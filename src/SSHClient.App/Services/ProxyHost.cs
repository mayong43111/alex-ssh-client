using SSHClient.App.Services;
using SSHClient.Core.Configuration;
using SSHClient.Core.Models;
using SSHClient.Core.Proxy;
using SSHClient.Core.Services;
using Serilog;

namespace SSHClient.App.Services;

/// <summary>
/// Hosts single-port mixed proxy listener based on configuration.
/// </summary>
public sealed class ProxyHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly IConfigService _configService;
    private readonly IProxyManager _proxyManager;
    private readonly ISystemProxyService _systemProxyService;
    private readonly ITrafficMonitor? _trafficMonitor;
    private readonly ILogger _logger;

    private MixedProxyServer? _mixed;

    private readonly IProxyConnector _proxyConnector;

    public ProxyHost(IConfigService configService,
                     IProxyManager proxyManager,
                     IProxyConnector proxyConnector,
                     ISystemProxyService systemProxyService,
                     ILogger? logger = null,
                     ITrafficMonitor? trafficMonitor = null)
    {
        _configService = configService;
        _proxyManager = proxyManager;
        _proxyConnector = proxyConnector;
        _systemProxyService = systemProxyService;
        _trafficMonitor = trafficMonitor;
        _logger = logger ?? Serilog.Log.Logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default, bool forceStart = false, string? activeProfileName = null)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await _configService.LoadAsync(cancellationToken);
            var proxySettings = settings.Proxy;

            if (_mixed is not null)
            {
                _logger.Information("代理监听器已在运行");
                return;
            }

            if (!forceStart && !proxySettings.EnableOnStartup)
            {
                _logger.Information("配置已禁用代理监听器");
                return;
            }

            await _proxyManager.ReloadAsync(cancellationToken);

            var activeProfile = !string.IsNullOrWhiteSpace(activeProfileName)
                ? settings.Profiles.FirstOrDefault(p => p.Name.Equals(activeProfileName, StringComparison.OrdinalIgnoreCase))
                : settings.Profiles.FirstOrDefault();
            var runtimeRuleEngine = new RuleEngine(BuildRuntimeRules(activeProfile?.Rules ?? Array.Empty<ProxyRule>()));
            var routeProfileName = activeProfile?.Name;

            var listenPort = proxySettings.ListenPort;

            var mixed = new MixedProxyServer(
                runtimeRuleEngine,
                _proxyManager,
                _proxyConnector,
                listenPort,
                _logger,
                routeProfileName: routeProfileName,
                routeProfile: activeProfile,
                trafficMonitor: _trafficMonitor);

            _mixed = mixed;
            _mixed.Start();

            if (proxySettings.ToggleSystemProxy)
            {
                await _systemProxyService.EnableAsync("127.0.0.1", listenPort, cancellationToken);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var mixed = _mixed;
            _mixed = null;

            // Stop listeners with cancellation support to avoid hangs
            if (mixed is not null)
            {
                try { await mixed.StopAsync(); }
                catch (OperationCanceledException) { /* ignorable */ }
                catch (Exception ex) { _logger.Warning(ex, "Mixed 代理停止异常"); }
                await mixed.DisposeAsync();
            }

            // Optionally disable system proxy on stop
            try
            {
                await _systemProxyService.DisableAsync(cancellationToken);
            }
            catch (OperationCanceledException) { /* ignorable */ }
            catch (Exception ex)
            {
                _logger.Warning(ex, "关闭系统代理失败");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private static IEnumerable<ProxyRuleEx> BuildRuntimeRules(IEnumerable<ProxyRule> rules)
    {
        var mapped = rules
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(MapRule)
            .ToList();

        // Always append a final direct fallback to avoid implicit proxy default.
        mapped.Add(new ProxyRuleEx
        {
            Name = "最终兜底直连",
            Pattern = "*",
            Action = RuleAction.Direct,
            Type = RuleMatchType.All,
        });

        return mapped;
    }

    private static ProxyRuleEx MapRule(ProxyRule rule)
    {
        var type = ParseRuleType(rule.Type, rule.Pattern);
        int? parsedPort = null;
        if (type == RuleMatchType.Port && int.TryParse(rule.Pattern, out var port))
        {
            parsedPort = port;
        }

        return new ProxyRuleEx
        {
            Name = rule.Name,
            Pattern = rule.Pattern,
            Action = rule.Action,
            Type = type,
            Port = parsedPort,
        };
    }

    private static RuleMatchType ParseRuleType(string? configuredType, string pattern)
    {
        if (string.Equals(configuredType, "All", StringComparison.OrdinalIgnoreCase))
        {
            return RuleMatchType.All;
        }

        if (string.Equals(configuredType, "IpCidr", StringComparison.OrdinalIgnoreCase))
        {
            return RuleMatchType.IpCidr;
        }

        if (string.Equals(configuredType, "DomainSuffix", StringComparison.OrdinalIgnoreCase))
        {
            return RuleMatchType.DomainSuffix;
        }

        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return RuleMatchType.All;
        }

        if (pattern.Contains('/'))
        {
            return RuleMatchType.IpCidr;
        }

        return RuleMatchType.DomainSuffix;
    }
}
