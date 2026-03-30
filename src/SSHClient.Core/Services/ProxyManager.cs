using SSHClient.Core.Models;
using Serilog;

namespace SSHClient.Core.Services;

public interface IProxyManager
{
    Task<bool> ConnectAsync(string profileName, CancellationToken cancellationToken = default);
    Task<bool> ConnectAsync(ProxyProfile profile, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string profileName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxyProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task ReloadAsync(CancellationToken cancellationToken = default);
}

public sealed class ProxyManager : IProxyManager
{
    private readonly IConfigService _configService;
    private readonly Func<ISshTunnelService> _tunnelFactory;
    private readonly ILogger _logger;
    private readonly Dictionary<string, ISshTunnelService> _activeTunnels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProxyProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public ProxyManager(IConfigService configService, Func<ISshTunnelService> tunnelFactory, ILogger? logger = null)
    {
        _configService = configService;
        _tunnelFactory = tunnelFactory;
        _logger = logger ?? Serilog.Log.Logger;
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            _profiles.Clear();
            var settings = await _configService.LoadAsync(cancellationToken);
            foreach (var profile in settings.Profiles)
            {
                _profiles[profile.Name] = profile;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<ProxyProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await ReloadAsync(cancellationToken);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            return _profiles.Values.ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> ConnectAsync(string profileName, CancellationToken cancellationToken = default)
    {
        await ReloadAsync(cancellationToken);
        await _mutex.WaitAsync(cancellationToken);
        try
        {

            if (!_profiles.TryGetValue(profileName, out var profile))
            {
                _logger.Warning("未找到配置 {Profile}", profileName);
                return false;
            }

            if (_activeTunnels.TryGetValue(profileName, out var existing) && existing.IsConnected)
            {
                _logger.Information("配置 {Profile} 已连接", profileName);
                return true;
            }

            _logger.Information(
                "正在连接配置 {Profile} 到 SSH {Host}:{Port}，用户名 {Username}，认证方式 {AuthMethod}",
                profile.Name,
                profile.Host,
                profile.Port,
                profile.Username,
                ToZhAuthMethod(profile.AuthMethod));

            var tunnel = _tunnelFactory();
            var success = await tunnel.StartAsync(profile, cancellationToken);
            if (success)
            {
                _activeTunnels[profileName] = tunnel;
                _logger.Information("配置 {Profile} 连接成功", profileName);
            }
            else
            {
                await tunnel.DisposeAsync();
                _logger.Warning("配置 {Profile} 连接失败", profileName);
            }

            return success;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> ConnectAsync(ProxyProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
        {
            _logger.Warning("登录失败：界面配置为空或名称无效");
            return false;
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            _profiles[profile.Name] = profile;

            if (_activeTunnels.TryGetValue(profile.Name, out var existing))
            {
                _logger.Information("配置 {Profile} 已连接，正在按当前界面配置重新连接", profile.Name);
                await existing.StopAsync(cancellationToken);
                await existing.DisposeAsync();
                _activeTunnels.Remove(profile.Name);
            }

            _logger.Information(
                "正在连接配置 {Profile} 到 SSH {Host}:{Port}，用户名 {Username}，认证方式 {AuthMethod}",
                profile.Name,
                profile.Host,
                profile.Port,
                profile.Username,
                ToZhAuthMethod(profile.AuthMethod));

            var tunnel = _tunnelFactory();
            var success = await tunnel.StartAsync(profile, cancellationToken);
            if (success)
            {
                _activeTunnels[profile.Name] = tunnel;
                _logger.Information("配置 {Profile} 连接成功", profile.Name);
            }
            else
            {
                await tunnel.DisposeAsync();
                _logger.Warning("配置 {Profile} 连接失败", profile.Name);
            }

            return success;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DisconnectAsync(string profileName, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (_activeTunnels.TryGetValue(profileName, out var tunnel))
            {
                _logger.Information("正在断开配置 {Profile}", profileName);
                await tunnel.StopAsync(cancellationToken);
                await tunnel.DisposeAsync();
                _activeTunnels.Remove(profileName);
                _logger.Information("配置 {Profile} 已断开", profileName);
            }
        }
        finally
        {
            _mutex.Release();
        }
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
}
