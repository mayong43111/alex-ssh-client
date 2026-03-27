using SSHClient.Core.Models;

namespace SSHClient.Core.Services;

public interface ISshTunnelService : IAsyncDisposable
{
    Task<bool> StartAsync(ProxyProfile profile, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    bool IsConnected { get; }
}

/// <summary>
/// Optional interface for allocating per-target local forwards over SSH.
/// </summary>
public interface ILocalForwardManager
{
    Task<int> EnsureLocalForwardAsync(string host, int port, CancellationToken cancellationToken = default);
}
