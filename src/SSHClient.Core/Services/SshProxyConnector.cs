using System.Net.Sockets;
using SSHClient.Core.Models;
using Serilog;

namespace SSHClient.Core.Services;

/// <summary>
/// SSH-based connector using SSH.NET when available; otherwise falls back to direct TcpClient.
/// </summary>
public sealed class SshProxyConnector : IProxyConnector
{
    private readonly ISshTunnelService _tunnelService;
    private readonly ILogger _logger;

    public SshProxyConnector(ISshTunnelService tunnelService, ILogger? logger = null)
    {
        _tunnelService = tunnelService;
        _logger = logger ?? Serilog.Log.Logger;
    }

    public async Task<TcpClient> ConnectAsync(ProxyProfile profile, string host, int port, CancellationToken cancellationToken = default)
    {
#if SSHNET
        // Ensure tunnel is up
        var ok = await _tunnelService.StartAsync(profile, cancellationToken);
        if (!ok)
        {
            throw new InvalidOperationException($"Failed to start SSH tunnel for profile {profile.Name}");
        }
        // Create a local forwarded port to the target, then connect to it locally
        var localPort = await EnsureLocalForwardAsync(profile, host, port);
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", localPort, cancellationToken);
        return client;
#else
        // fallback: direct
        var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        return client;
#endif
    }

#if SSHNET
    private async Task<int> EnsureLocalForwardAsync(ProxyProfile profile, string host, int port)
    {
        // Expose method on tunnel service for reusability
        if (_tunnelService is ILocalForwardManager mgr)
        {
            return await mgr.EnsureLocalForwardAsync(host, port);
        }
        // fallback: direct connect fallback
        _logger.Warning("Tunnel service does not implement ILocalForwardManager; falling back to direct connect");
        return port;

    }
#endif
}
