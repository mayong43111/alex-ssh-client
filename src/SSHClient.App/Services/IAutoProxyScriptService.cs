using SSHClient.Core.Models;
using Serilog;

namespace SSHClient.App.Services;

public interface IAutoProxyScriptService
{
    Task<string> PublishAsync(int scriptPort, int proxyPort, IEnumerable<ProxyRule> rules, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    string GeneratePacScript(int proxyPort, IEnumerable<ProxyRule> rules);
}

public sealed class AutoProxyScriptService : IAutoProxyScriptService, IAsyncDisposable
{
    private const string ScriptPath = "/proxy.pac";
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;
    private readonly IPacHttpHost _pacHttpHost;
    private readonly IPacScriptBuilder _pacScriptBuilder;

    private string _currentScript;

    public AutoProxyScriptService(IPacHttpHost pacHttpHost, IPacScriptBuilder pacScriptBuilder, ILogger? logger = null)
    {
        _pacHttpHost = pacHttpHost;
        _pacScriptBuilder = pacScriptBuilder;
        _logger = logger ?? Serilog.Log.Logger;
        _currentScript = _pacScriptBuilder.Build(proxyPort: 1080, Array.Empty<ProxyRule>());
    }

    public async Task<string> PublishAsync(int scriptPort, int proxyPort, IEnumerable<ProxyRule> rules, CancellationToken cancellationToken = default)
    {
        if (scriptPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(scriptPort), "PAC 脚本端口必须在 1-65535 之间。");
        }

        if (proxyPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(proxyPort), "代理端口必须在 1-65535 之间。");
        }

        var script = _pacScriptBuilder.Build(proxyPort, rules ?? Array.Empty<ProxyRule>());

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _currentScript = script;
            var actualPort = await _pacHttpHost.EnsureStartedAsync(scriptPort, () => _currentScript, cancellationToken);
            return $"http://127.0.0.1:{actualPort}{ScriptPath}";
        }
        finally
        {
            _gate.Release();
        }
    }

    public string GeneratePacScript(int proxyPort, IEnumerable<ProxyRule> rules)
    {
        return _pacScriptBuilder.Build(proxyPort, rules ?? Array.Empty<ProxyRule>());
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _pacHttpHost.StopAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(ShutdownTimeout);
            await StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("PAC 脚本服务释放超时，跳过剩余等待");
        }
        _gate.Dispose();
    }

}
