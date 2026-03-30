using System.Text.Json;
using SSHClient.Core.Configuration;

namespace SSHClient.Core.Services;

public sealed class FileConfigService : IConfigService
{
    private readonly string _configPath;
    private readonly string _legacyConfigPath;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public FileConfigService(string? configPath = null)
    {
        _legacyConfigPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        _configPath = configPath ?? Path.Combine(GetAppDataDirectory(), "appsettings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_configPath))
        {
            await using var stream = File.OpenRead(_configPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                           ?? new AppSettings();
            return settings;
        }

        if (File.Exists(_legacyConfigPath))
        {
            await using var stream = File.OpenRead(_legacyConfigPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                           ?? new AppSettings();
            return settings;
        }

        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        await using var stream = File.Create(_configPath);
        await JsonSerializer.SerializeAsync(stream, settings, _options, cancellationToken);
    }

    private static string GetAppDataDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "AlexSSHClient");
    }
}
