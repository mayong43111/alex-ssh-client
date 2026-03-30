using SSHClient.Core.Services;

namespace SSHClient.App.Services;

public interface IMinimizePreferenceService
{
    Task<bool?> GetMinimizeToTrayPreferenceAsync(CancellationToken cancellationToken = default);
    Task SetMinimizeToTrayPreferenceAsync(bool? minimizeToTray, CancellationToken cancellationToken = default);
}

public sealed class MinimizePreferenceService : IMinimizePreferenceService
{
    private readonly IConfigService _configService;
    private bool _loaded;
    private bool? _cachedPreference;

    public MinimizePreferenceService(IConfigService configService)
    {
        _configService = configService;
    }

    public async Task<bool?> GetMinimizeToTrayPreferenceAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            return _cachedPreference;
        }

        var settings = await _configService.LoadAsync(cancellationToken);
        _cachedPreference = settings.MinimizeToTray;
        _loaded = true;
        return _cachedPreference;
    }

    public async Task SetMinimizeToTrayPreferenceAsync(bool? minimizeToTray, CancellationToken cancellationToken = default)
    {
        _cachedPreference = minimizeToTray;
        _loaded = true;

        var settings = await _configService.LoadAsync(cancellationToken);
        settings.MinimizeToTray = minimizeToTray;
        await _configService.SaveAsync(settings, cancellationToken);
    }
}
