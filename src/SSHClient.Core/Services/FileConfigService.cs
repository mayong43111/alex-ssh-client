using System.Text.Json;
using SSHClient.Core.Configuration;

namespace SSHClient.Core.Services;

public sealed class FileConfigService : IConfigService
{
    private readonly string _configPath;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public FileConfigService(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configPath))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(_configPath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                       ?? new AppSettings();
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        await using var stream = File.Create(_configPath);
        await JsonSerializer.SerializeAsync(stream, settings, _options, cancellationToken);
    }
}
