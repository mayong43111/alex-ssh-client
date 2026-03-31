using SSHClient.Core.Models;
using SSHClient.Core.Services;

namespace SSHClient.App.Services;

public interface IPacPreviewService
{
    Task<string> BuildPreviewScriptAsync(IEnumerable<ProxyRule> rules, CancellationToken cancellationToken = default);
}

public sealed class PacPreviewService : IPacPreviewService
{
    private readonly IConfigService _configService;
    private readonly IAutoProxyScriptService _autoProxyScriptService;

    public PacPreviewService(IConfigService configService, IAutoProxyScriptService autoProxyScriptService)
    {
        _configService = configService;
        _autoProxyScriptService = autoProxyScriptService;
    }

    public async Task<string> BuildPreviewScriptAsync(IEnumerable<ProxyRule> rules, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = await _configService.LoadAsync();
        var listenPort = settings.Proxy.ListenPort;
        if (listenPort <= 0)
        {
            throw new InvalidOperationException("监听端口无效，无法生成 PAC 预览。");
        }

        return _autoProxyScriptService.GeneratePacScript(listenPort, rules ?? Array.Empty<ProxyRule>());
    }
}
