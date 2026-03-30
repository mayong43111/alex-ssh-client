using System.Text.Json;
using Serilog;
using SSHClient.Core.Configuration;

namespace SSHClient.Core.Services;

public sealed class FileConfigService : IConfigService
{
    private readonly string _configPath;
    private readonly string _legacyConfigPath;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public FileConfigService(string? configPath = null, ILogger? logger = null)
    {
        _legacyConfigPath = AppConfigPaths.GetPackagedConfigPath();
        _configPath = NormalizePathOrFallback(configPath, AppConfigPaths.GetUserConfigPath());
        _logger = logger ?? Serilog.Log.Logger;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var userSettings = await TryLoadFromPathAsync(_configPath, "用户配置", cancellationToken);
        if (userSettings is not null)
        {
            return userSettings;
        }

        if (!string.Equals(_legacyConfigPath, _configPath, StringComparison.OrdinalIgnoreCase))
        {
            var packagedSettings = await TryLoadFromPathAsync(_legacyConfigPath, "内置配置", cancellationToken);
            if (packagedSettings is not null)
            {
                return packagedSettings;
            }
        }

        _logger.Information("未找到可用配置文件，使用默认配置。用户路径 {UserPath}", _configPath);
        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var configDirectory = Path.GetDirectoryName(_configPath);
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            throw new InvalidOperationException($"配置文件路径无效：{_configPath}");
        }

        Directory.CreateDirectory(configDirectory);

        var tempPath = _configPath + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, _options, cancellationToken);
            }

            File.Move(tempPath, _configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task<AppSettings?> TryLoadFromPathAsync(string path, string sourceName, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                ?? new AppSettings();
            return settings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "读取{SourceName}失败，路径 {Path}，将尝试下一个来源", sourceName, path);
            return null;
        }
    }

    private static string NormalizePathOrFallback(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return fallback;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return fallback;
        }
    }
}
