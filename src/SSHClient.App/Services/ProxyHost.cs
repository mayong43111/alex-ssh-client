using SSHClient.App.Services;
using SSHClient.Core.Configuration;
using SSHClient.Core.Proxy;
using SSHClient.Core.Services;
using Serilog;

namespace SSHClient.App.Services;

/// <summary>
/// Hosts HTTP + SOCKS proxies based on configuration.
/// </summary>
public sealed class ProxyHost : IAsyncDisposable
{
    private readonly IConfigService _configService;
    private readonly IRuleEngine _ruleEngine;
    private readonly IProxyManager _proxyManager;
    private readonly ISystemProxyService _systemProxyService;
    private readonly ILogger _logger;

    private HttpProxyServer? _http;
    private SocksProxyServer? _socks;

    private readonly IProxyConnector _proxyConnector;

    public ProxyHost(IConfigService configService,
                     IRuleEngine ruleEngine,
                     IProxyManager proxyManager,
                     IProxyConnector proxyConnector,
                     ISystemProxyService systemProxyService,
                     ILogger? logger = null)
    {
        _configService = configService;
        _ruleEngine = ruleEngine;
        _proxyManager = proxyManager;
        _proxyConnector = proxyConnector;
        _systemProxyService = systemProxyService;
        _logger = logger ?? Serilog.Log.Logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _configService.LoadAsync(cancellationToken);
        var proxySettings = settings.Proxy;
        if (!proxySettings.EnableOnStartup)
        {
            _logger.Information("Proxy listeners disabled by config");
            return;
        }

        _http = new HttpProxyServer(_ruleEngine, _proxyManager, _proxyConnector, proxySettings.HttpPort, _logger);
        _socks = new SocksProxyServer(_ruleEngine, _proxyManager, _proxyConnector, proxySettings.SocksPort, _logger);
        _http.Start();
        _socks.Start();

        if (proxySettings.ToggleSystemProxy)
        {
            await _systemProxyService.EnableAsync("127.0.0.1", proxySettings.HttpPort, proxySettings.SocksPort, cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Stop listeners with cancellation support to avoid hangs
        if (_http is not null)
        {
            try { await _http.StopAsync(); }
            catch (OperationCanceledException) { /* ignorable */ }
            catch (Exception ex) { _logger.Warning(ex, "HTTP proxy stop error"); }
            await _http.DisposeAsync();
        }
        if (_socks is not null)
        {
            try { await _socks.StopAsync(); }
            catch (OperationCanceledException) { /* ignorable */ }
            catch (Exception ex) { _logger.Warning(ex, "SOCKS proxy stop error"); }
            await _socks.DisposeAsync();
        }

        // Optionally disable system proxy on stop
        try
        {
            await _systemProxyService.DisableAsync(cancellationToken);
        }
        catch (OperationCanceledException) { /* ignorable */ }
        catch (Exception ex)
        {
            _logger.Warning(ex, "System proxy disable failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
