using SSHClient.Core.Models;
using Serilog;

namespace SSHClient.Core.Services;

public interface IProxyManager
{
    Task<bool> ConnectAsync(string profileName, CancellationToken cancellationToken = default);
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

    private async Task EnsureProfilesLoadedAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (_profiles.Count == 0)
            {
                var settings = await _configService.LoadAsync(cancellationToken);
                foreach (var profile in settings.Profiles)
                {
                    _profiles[profile.Name] = profile;
                }
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<ProxyProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureProfilesLoadedAsync(cancellationToken);
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
        await EnsureProfilesLoadedAsync(cancellationToken);
        await _mutex.WaitAsync(cancellationToken);
        try
        {

            if (!_profiles.TryGetValue(profileName, out var profile))
            {
                _logger.Warning("Profile {Profile} not found", profileName);
                return false;
            }

            if (_activeTunnels.TryGetValue(profileName, out var existing) && existing.IsConnected)
            {
                _logger.Information("Profile {Profile} already connected", profileName);
                return true;
            }

            var tunnel = _tunnelFactory();
            var success = await tunnel.StartAsync(profile, cancellationToken);
            if (success)
            {
                _activeTunnels[profileName] = tunnel;
            }
            else
            {
                await tunnel.DisposeAsync();
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
                await tunnel.StopAsync(cancellationToken);
                await tunnel.DisposeAsync();
                _activeTunnels.Remove(profileName);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }
}
