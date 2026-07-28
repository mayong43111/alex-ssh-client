using SSHClient.Core.Models;

namespace SSHClient.Core.Services;

public interface ISshTunnelService : IAsyncDisposable
{
    event EventHandler<SshTunnelDisconnectedEventArgs>? Disconnected;
    Task<bool> StartAsync(ProxyProfile profile, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    bool IsConnected { get; }
}

public sealed class SshTunnelDisconnectedEventArgs : EventArgs
{
    public SshTunnelDisconnectedEventArgs(string profileName, Exception exception)
    {
        ProfileName = profileName;
        Exception = exception;
    }

    public string ProfileName { get; }
    public Exception Exception { get; }
}

/// <summary>
/// Optional interface for allocating per-target local forwards over SSH.
/// </summary>
public interface ILocalForwardManager
{
    Task<int> EnsureLocalForwardAsync(string host, int port, CancellationToken cancellationToken = default);
}
