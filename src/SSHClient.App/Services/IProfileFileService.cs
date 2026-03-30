using System.IO;
using System.Text.Json;
using SSHClient.Core.Models;

namespace SSHClient.App.Services;

public interface IProfileFileService
{
    string GetDefaultProfileFilePath();
    string NormalizePathOrNull(string? filePath);
    string GetCurrentExportDirectory(string? activeProfileFilePath);
    string GetCurrentExportFileName(string? activeProfileFilePath, string? selectedProfileName);
    Task WriteProfileAsync(string filePath, ProxyProfile profile, CancellationToken cancellationToken = default);
    Task<ProxyProfile?> ReadProfileAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed class ProfileFileService : IProfileFileService
{
    private const string DefaultProfileFileName = "default.profile.json";

    private static readonly JsonSerializerOptions ProfileFileJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string GetDefaultProfileFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, DefaultProfileFileName);
    }

    public string NormalizePathOrNull(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        return Path.GetFullPath(filePath);
    }

    public string GetCurrentExportDirectory(string? activeProfileFilePath)
    {
        if (!string.IsNullOrWhiteSpace(activeProfileFilePath))
        {
            var dir = Path.GetDirectoryName(activeProfileFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }

        return AppContext.BaseDirectory;
    }

    public string GetCurrentExportFileName(string? activeProfileFilePath, string? selectedProfileName)
    {
        if (!string.IsNullOrWhiteSpace(activeProfileFilePath))
        {
            var currentName = Path.GetFileName(activeProfileFilePath);
            if (!string.IsNullOrWhiteSpace(currentName))
            {
                return currentName;
            }
        }

        var profileName = string.IsNullOrWhiteSpace(selectedProfileName) ? "profile" : selectedProfileName;
        return $"{profileName}.profile.json";
    }

    public async Task WriteProfileAsync(string filePath, ProxyProfile profile, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = filePath + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, profile, ProfileFileJsonOptions, cancellationToken);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public async Task<ProxyProfile?> ReadProfileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePathOrNull(filePath);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<ProxyProfile>(stream, ProfileFileJsonOptions, cancellationToken);
    }
}
