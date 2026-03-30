using System.Net.Sockets;
using SSHClient.Core.Models;
using SSHClient.Core.Services;
using Serilog;

namespace SSHClient.Core.Proxy;

public static class UpstreamRouteConnector
{
    public static async Task<TcpClient> ConnectAsync(
        string protocol,
        string host,
        int port,
        IRuleEngine rules,
        IProxyManager proxyManager,
        IProxyConnector proxyConnector,
        ILogger logger,
        ProxyProfile? preferredProfile,
        string? routeProfileName,
        CancellationToken cancellationToken)
    {
        var rule = rules.Match(host, port);
        var action = rule?.Action ?? RuleAction.Proxy;
        var shouldProxy = action == RuleAction.Proxy;
        var displayProfile = routeProfileName ?? "(当前配置)";

        logger.Information(
            "{Protocol} 命中 {Host}:{Port} => 规则={Rule}, 类型={RuleType}, 模式={Pattern}, 动作={Action}, 配置={Profile}, 路由={Route}",
            protocol,
            host,
            port,
            rule?.Name ?? "(无)",
            rule?.Type.ToString() ?? "(无)",
            rule?.Pattern ?? "(无)",
            action,
            displayProfile,
            shouldProxy ? "代理" : "直连");

        if (shouldProxy)
        {
            var profile = preferredProfile;
            if (profile is null)
            {
                var profiles = await proxyManager.GetProfilesAsync(cancellationToken);
                profile = !string.IsNullOrWhiteSpace(routeProfileName)
                    ? profiles.FirstOrDefault(p => p.Name.Equals(routeProfileName, StringComparison.OrdinalIgnoreCase))
                    : null;
                profile ??= profiles.FirstOrDefault();
            }

            if (profile is null)
            {
                throw new InvalidOperationException($"{protocol} 代理规则未找到配置");
            }

            var connected = await proxyManager.ConnectAsync(profile.Name, cancellationToken);
            if (!connected)
            {
                throw new InvalidOperationException($"{protocol} 代理配置 {profile.Name} 连接失败");
            }

            var upstream = await proxyConnector.ConnectAsync(profile, host, port, cancellationToken);
            logger.Information(
                "{Protocol} 上游通过 SSH 配置 {Profile} 连接成功 -> {Host}:{Port}",
                protocol,
                profile.Name,
                host,
                port);
            return upstream;
        }

        var directClient = new TcpClient();
        await directClient.ConnectAsync(host, port, cancellationToken);
        logger.Information("{Protocol} 上游直连成功 -> {Host}:{Port}", protocol, host, port);
        return directClient;
    }
}
