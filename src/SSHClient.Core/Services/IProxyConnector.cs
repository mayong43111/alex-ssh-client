using System.Net.Sockets;
using SSHClient.Core.Models;

namespace SSHClient.Core.Services;

public interface IProxyConnector
{
    /// <summary>
    /// Obtain a connected TcpClient to target host:port via the given profile (SSH or direct fallback).
    /// </summary>
    Task<TcpClient> ConnectAsync(ProxyProfile profile, string host, int port, CancellationToken cancellationToken = default);
}
