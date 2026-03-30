using SSHClient.Core.Models;
using Serilog;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

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
                _logger.Information("SSH 隧道已连接，配置 {Profile}", profile.Name);
                return true;
            }
        }

        var candidates = ResolveConnectionCandidates(profile).ToList();
        if (candidates.Count == 0)
        {
            _logger.Warning("配置 {Profile} 缺少可用的认证材料", profile.Name);
            return false;
        }

        foreach (var candidate in candidates)
        {
            Renci.SshNet.SshClient? client = null;
            try
            {
                var connectionInfo = BuildConnectionInfo(candidate);

                if (candidate.AuthMethod == SshAuthMethod.PrivateKey)
                {
                    _logger.Information(
                        "正在为配置 {Profile} 打开 SSH 隧道到 {Host}:{Port}，用户名 {Username}，认证方式 {AuthMethod}，密钥 {KeyPath}",
                        candidate.Name,
                        candidate.Host,
                        candidate.Port,
                        candidate.Username,
                        ToZhAuthMethod(candidate.AuthMethod),
                        candidate.PrivateKeyPath);
                }
                else
                {
                    _logger.Information(
                        "正在为配置 {Profile} 打开 SSH 隧道到 {Host}:{Port}，用户名 {Username}，认证方式 {AuthMethod}",
                        candidate.Name,
                        candidate.Host,
                        candidate.Port,
                        candidate.Username,
                        ToZhAuthMethod(candidate.AuthMethod));
                }

                client = new Renci.SshNet.SshClient(connectionInfo)
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(30),
                };

                await Task.Run(() => client.Connect(), cancellationToken);
                if (!client.IsConnected)
                {
                    _logger.Error("配置 {Profile} 的 SSH 客户端连接失败", candidate.Name);
                    client.Dispose();
                    continue;
                }

                lock (_sync)
                {
                    _client = client;
                    _dynamicPort = null;
                }

                _logger.Information("配置 {Profile} 的 SSH 隧道已建立（本地代理端口由应用监听）", candidate.Name);
                return true;
            }
            catch (Renci.SshNet.Common.SshAuthenticationException ex)
            {
                if (candidate.AuthMethod == SshAuthMethod.PrivateKey)
                {
                    _logger.Warning(ex, "配置 {Profile} 使用密钥 {KeyPath} 认证失败", candidate.Name, candidate.PrivateKeyPath);
                }
                else
                {
                    _logger.Error(ex, "配置 {Profile} SSH 认证失败", candidate.Name);
                }

                client?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "配置 {Profile} SSH 连接失败", candidate.Name);
                client?.Dispose();
                return false;
            }
        }

        _logger.Error("配置 {Profile} SSH 认证失败，已尝试 {CandidateCount} 个认证候选", profile.Name, candidates.Count);
        return false;
#else
        // Stub path for offline/initial scaffolding.
        _logger.Warning("未启用 SSHNET，正在为配置 {Profile} 启动桩隧道", profile.Name);
        _isConnected = true;
        await Task.CompletedTask;
        return true;
#endif
    }

    private IEnumerable<ProxyProfile> ResolveConnectionCandidates(ProxyProfile profile)
    {
        if (profile.AuthMethod != SshAuthMethod.PrivateKey)
        {
            return new[] { profile };
        }

        if (!string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            return new[] { profile };
        }

        var keyPaths = GetDefaultPrivateKeyPaths().ToList();
        if (keyPaths.Count > 0)
        {
            _logger.Information("配置 {Profile} 使用自动发现的 {Count} 个默认 SSH 密钥候选", profile.Name, keyPaths.Count);
            return keyPaths.Select(path => profile with { PrivateKeyPath = path });
        }

        _logger.Warning("配置 {Profile} 选择了公钥认证，但在用户 .ssh 目录未找到私钥", profile.Name);
        return Array.Empty<ProxyProfile>();
    }

    private static IEnumerable<string> GetDefaultPrivateKeyPaths()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userHome))
        {
            userHome = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(userHome))
        {
            return Array.Empty<string>();
        }

        var sshDir = Path.Combine(userHome, ".ssh");
        var candidates = new[]
        {
            "id_ed25519",
            "id_ecdsa",
            "id_rsa",
            "id_dsa",
            "id_ed25519_sk",
            "id_ecdsa_sk",
        };

        return candidates
            .Select(name => Path.Combine(sshDir, name))
            .Where(File.Exists)
            .ToList();
    }

    private static string ToZhAuthMethod(SshAuthMethod method)
    {
        return method switch
        {
            SshAuthMethod.Password => "密码",
            SshAuthMethod.PrivateKey => "公钥",
            SshAuthMethod.KeyboardInteractive => "键盘交互",
            _ => method.ToString(),
        };
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
                        _logger.Warning(ex, "停止本地转发 {Key} 时出错", kvp.Key);
                    }
                }
                _localForwards.Clear();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "停止转发端口时出错");
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
                    throw new InvalidOperationException("已选择密码认证，但密码为空。");
                methods.Add(new Renci.SshNet.PasswordAuthenticationMethod(profile.Username, profile.Password));
                break;
            case SshAuthMethod.PrivateKey:
                if (string.IsNullOrEmpty(profile.PrivateKeyPath))
                    throw new InvalidOperationException("已选择私钥认证，但密钥路径为空。");
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
            throw new InvalidOperationException("SSH 客户端未连接");

        var key = $"{host}:{port}";
        if (_localForwards.TryGetValue(key, out var existing))
        {
            return (int)existing.BoundPort;
        }

        // Allocate ephemeral local port
        uint boundPort = 0;
        var fwd = new Renci.SshNet.ForwardedPortLocal("127.0.0.1", boundPort, host, (uint)port);
        _client.AddForwardedPort(fwd);
        fwd.Exception += (_, e) => _logger.Error(e.Exception, "转发端口异常（本地转发）");
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
