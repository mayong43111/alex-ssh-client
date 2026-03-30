using FluentAssertions;
using SSHClient.Core.Configuration;
using SSHClient.Core.Services;

namespace SSHClient.Tests;

[Trait("Category", "CriticalPath")]
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

    [Fact]
    public async Task LoadAsync_Should_Fallback_To_Default_When_Config_Is_Invalid_Json()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(tempPath, "{ invalid json }");
        var service = new FileConfigService(tempPath);

        var loaded = await service.LoadAsync();

        loaded.Should().NotBeNull();
        loaded.Profiles.Should().BeEmpty();
        loaded.ActiveProfileName.Should().BeNull();
        loaded.MinimizeToTray.Should().BeNull();
    }

        [Fact]
        public async Task LoadAsync_Should_Ignore_Downlined_Legacy_Profile_Fields()
        {
                var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
                var legacyJson = """
                {
                    "profiles": [
                        {
                            "name": "Legacy",
                            "host": "example.com",
                            "port": 22,
                            "username": "alice",
                            "jumpHosts": ["jump1.example.com"],
                            "strictHostKeyChecking": true,
                            "rules": [
                                {
                                    "name": "默认",
                                    "priority": 9999,
                                    "pattern": "*",
                                    "type": "All",
                                    "action": 1
                                }
                            ]
                        }
                    ]
                }
                """;

                await File.WriteAllTextAsync(tempPath, legacyJson);
                var service = new FileConfigService(tempPath);

                var loaded = await service.LoadAsync();

                loaded.Profiles.Should().ContainSingle();
                loaded.Profiles[0].Name.Should().Be("Legacy");
                loaded.Profiles[0].Host.Should().Be("example.com");
                loaded.Profiles[0].Rules.Should().ContainSingle();
        }
}
