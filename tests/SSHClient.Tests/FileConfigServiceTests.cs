using FluentAssertions;
using SSHClient.Core.Configuration;
using SSHClient.Core.Services;

namespace SSHClient.Tests;

public class FileConfigServiceTests
{
    [Fact]
    public async Task SaveAndLoad_Should_Persist_Settings()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var service = new FileConfigService(tempPath);
        var settings = new AppSettings
        {
            Profiles =
            {
                new Core.Models.ProxyProfile { Name = "TestProfile", Host = "host", Username = "user" }
            }
        };

        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();

        loaded.Profiles.Should().ContainSingle(p => p.Name == "TestProfile");

        File.Exists(tempPath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAndLoad_Should_Persist_AppState_Fields()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var service = new FileConfigService(tempPath);
        var settings = new AppSettings
        {
            ActiveProfileName = "P1",
            ActiveProfileFilePath = @"C:\\profiles\\p1.profile.json",
            MinimizeToTray = true,
        };

        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();

        loaded.ActiveProfileName.Should().Be("P1");
        loaded.ActiveProfileFilePath.Should().Be(@"C:\\profiles\\p1.profile.json");
        loaded.MinimizeToTray.Should().BeTrue();
    }
}
