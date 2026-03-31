using SSHClient.Core.Models;
using SSHClient.Core.Services;

namespace SSHClient.App.Services;

public enum SystemProxyApplyStatus
{
    Success,
    Cancelled,
    InvalidListenPort,
    InvalidPacScriptPort,
}

public sealed record SystemProxyApplyResult(SystemProxyApplyStatus Status, string? ScriptUrl = null, int ProxyRuleCount = 0);

public enum SystemProxyRestoreStatus
{
    Success,
    Cancelled,
}

public sealed record SystemProxyRestoreResult(SystemProxyRestoreStatus Status);

public interface ISystemProxyApplicationService
{
    Task<SystemProxyApplyResult> ApplyPacAsync(IEnumerable<ProxyRule> rules, CancellationToken cancellationToken = default);
    Task<SystemProxyRestoreResult> RestorePacAsync(CancellationToken cancellationToken = default);
}

public sealed class SystemProxyApplicationService : ISystemProxyApplicationService
{
    private readonly IConfigService _configService;
    private readonly ISystemProxyService _systemProxyService;
    private readonly IAutoProxyScriptService _autoProxyScriptService;

    public SystemProxyApplicationService(
        IConfigService configService,
        ISystemProxyService systemProxyService,
        IAutoProxyScriptService autoProxyScriptService)
    {
        _configService = configService;
        _systemProxyService = systemProxyService;
        _autoProxyScriptService = autoProxyScriptService;
    }

    public async Task<SystemProxyApplyResult> ApplyPacAsync(IEnumerable<ProxyRule> rules, CancellationToken cancellationToken = default)
    {
        var snapshot = (rules ?? Array.Empty<ProxyRule>()).ToList();
        var settings = await _configService.LoadAsync();

        var listenPort = settings.Proxy.ListenPort;
        if (listenPort <= 0)
        {
            return new SystemProxyApplyResult(SystemProxyApplyStatus.InvalidListenPort);
        }

        var pacScriptPort = settings.Proxy.PacScriptPort;
        if (pacScriptPort <= 0)
        {
            return new SystemProxyApplyResult(SystemProxyApplyStatus.InvalidPacScriptPort);
        }

        var scriptUrl = await _autoProxyScriptService.PublishAsync(pacScriptPort, listenPort, snapshot, cancellationToken);
        var applied = await _systemProxyService.EnableAutoConfigScriptWithElevationAsync(scriptUrl);
        if (!applied)
        {
            return new SystemProxyApplyResult(SystemProxyApplyStatus.Cancelled);
        }

        var proxyRuleCount = snapshot.Count(r => r.Action == RuleAction.Proxy);
        return new SystemProxyApplyResult(SystemProxyApplyStatus.Success, scriptUrl, proxyRuleCount);
    }

    public async Task<SystemProxyRestoreResult> RestorePacAsync(CancellationToken cancellationToken = default)
    {
        var restored = await _systemProxyService.DisableAutoConfigScriptWithElevationAsync();
        if (!restored)
        {
            return new SystemProxyRestoreResult(SystemProxyRestoreStatus.Cancelled);
        }

        await _autoProxyScriptService.StopAsync(cancellationToken);
        return new SystemProxyRestoreResult(SystemProxyRestoreStatus.Success);
    }
}
