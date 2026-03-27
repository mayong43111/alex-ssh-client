using SSHClient.Core.Models;
using Serilog;
using System.Collections.Concurrent;

namespace SSHClient.Core.Services;

/// <summary>
/// Minimal SSH tunnel wrapper. Supports dynamic (SOCKS) forwarding initially.
/// If the SSHNET symbol is defined and Renci.SshNet is referenced, it will use the real implementation.
/// Otherwise, a stub is used to keep the scaffold buildable offline.
/// </summary>
public sealed class SshTunnelService : ISshTunnelService, ILocalForwardManager
{
    private readonly ILogger _logger;
    private readonly object _sync = new();

#if SSHNET
    private Renci.SshNet.SshClient? _client;
    private readonly ConcurrentDictionary<string, Renci.SshNet.ForwardedPortLocal> _localForwards = new();
    private Renci.SshNet.ForwardedPortDynamic? _dynamicPort;
    public bool IsConnected => _client?.IsConnected == true;
#else
    private bool _isConnected;
    public bool IsConnected => _isConnected;
#endif

    public SshTunnelService(ILogger? logger = null)
    {
        _logger = logger ?? Serilog.Log.Logger;
    }

    public async Task<bool> StartAsync(ProxyProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

#if SSHNET
        lock (_sync)
        {
            if (_client?.IsConnected == true)
            {
                _logger.Information("SSH tunnel already connected for {Profile}", profile.Name);
                return true;
            }
        }

        var connectionInfo = BuildConnectionInfo(profile);
        var client = new Renci.SshNet.SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };

        try
        {
            client.Connect();
            if (!client.IsConnected)
            {
                _logger.Error("Failed to connect SSH client for {Profile}", profile.Name);
                return false;
            }

            var listenAddress = string.IsNullOrWhiteSpace(profile.LocalListenAddress)
                ? "127.0.0.1"
                : profile.LocalListenAddress;

            var dynamicPort = new Renci.SshNet.ForwardedPortDynamic(listenAddress, (uint)profile.LocalSocksPort);
            client.AddForwardedPort(dynamicPort);
            dynamicPort.Exception += (_, e) => _logger.Error(e.Exception, "Forwarded port error");
            dynamicPort.Start();

            lock (_sync)
            {
                _client = client;
                _dynamicPort = dynamicPort;
            }

            _logger.Information("SSH SOCKS tunnel started on {Address}:{Port} for profile {Profile}", listenAddress, profile.LocalSocksPort, profile.Name);
            return true;
        }
        catch (Renci.SshNet.Common.SshAuthenticationException ex)
        {
            _logger.Error(ex, "SSH authentication failed for profile {Profile}", profile.Name);
            client.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SSH connection failed for profile {Profile}", profile.Name);
            client.Dispose();
            return false;
        }
#else
        // Stub path for offline/initial scaffolding.
        _logger.Warning("SSHNET not enabled; starting stub tunnel for profile {Profile}", profile.Name);
        _isConnected = true;
        await Task.CompletedTask;
        return true;
#endif
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if SSHNET
        lock (_sync)
        {
            try
            {
                _dynamicPort?.Stop();
                _dynamicPort?.Dispose();
                foreach (var kvp in _localForwards)
                {
                    try
                    {
                        kvp.Value.Stop();
                        kvp.Value.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Error stopping local forward {Key}", kvp.Key);
                    }
                }
                _localForwards.Clear();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error while stopping forwarded port");
            }
            finally
            {
                _dynamicPort = null;
            }

            _client?.Disconnect();
            _client?.Dispose();
            _client = null;
        }
#else
        _isConnected = false;
#endif
        return Task.CompletedTask;
    }

#if SSHNET
    private static Renci.SshNet.ConnectionInfo BuildConnectionInfo(ProxyProfile profile)
    {
        var methods = new List<Renci.SshNet.AuthenticationMethod>();
        switch (profile.AuthMethod)
        {
            case SshAuthMethod.Password:
                if (string.IsNullOrEmpty(profile.Password))
                    throw new InvalidOperationException("Password auth selected but password is empty.");
                methods.Add(new Renci.SshNet.PasswordAuthenticationMethod(profile.Username, profile.Password));
                break;
            case SshAuthMethod.PrivateKey:
                if (string.IsNullOrEmpty(profile.PrivateKeyPath))
                    throw new InvalidOperationException("PrivateKey auth selected but key path is empty.");
                var keyFile = string.IsNullOrEmpty(profile.PrivateKeyPassphrase)
                    ? new Renci.SshNet.PrivateKeyFile(profile.PrivateKeyPath)
                    : new Renci.SshNet.PrivateKeyFile(profile.PrivateKeyPath, profile.PrivateKeyPassphrase);
                methods.Add(new Renci.SshNet.PrivateKeyAuthenticationMethod(profile.Username, keyFile));
                break;
            case SshAuthMethod.KeyboardInteractive:
                var kbAuth = new Renci.SshNet.KeyboardInteractiveAuthenticationMethod(profile.Username);
                kbAuth.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                    {
                        if (prompt.Request.Contains("Password", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(profile.Password))
                        {
                            prompt.Response = profile.Password;
                        }
                    }
                };
                methods.Add(kbAuth);
                break;
        }

        return new Renci.SshNet.ConnectionInfo(profile.Host, profile.Port, profile.Username, methods.ToArray());
    }
#endif

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    public async Task<int> EnsureLocalForwardAsync(string host, int port, CancellationToken cancellationToken = default)
    {
#if SSHNET
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("SSH client not connected");

        var key = $"{host}:{port}";
        if (_localForwards.TryGetValue(key, out var existing))
        {
            return (int)existing.BoundPort;
        }

        // Allocate ephemeral local port
        uint boundPort = 0;
        var fwd = new Renci.SshNet.ForwardedPortLocal("127.0.0.1", boundPort, host, (uint)port);
        _client.AddForwardedPort(fwd);
        fwd.Exception += (_, e) => _logger.Error(e.Exception, "Forwarded port error (local forward)");
        fwd.Start();
        boundPort = fwd.BoundPort;

        _localForwards[key] = fwd;

        await Task.CompletedTask;
        return (int)boundPort;
#else
        // Stub: just return target port (direct connect path will be used)
        await Task.CompletedTask;
        return port;
#endif
    }

}
