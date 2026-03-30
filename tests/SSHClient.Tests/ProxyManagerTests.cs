using FluentAssertions;
using SSHClient.Core.Configuration;
using SSHClient.Core.Models;
using SSHClient.Core.Services;
using System.Net;
using System.Net.Sockets;

namespace SSHClient.Tests;

public class ProxyManagerTests
{
    [Fact]
    public async Task ConnectAsync_Should_Start_Tunnel_When_Profile_Exists()
    {
        // Arrange
        var appSettings = new AppSettings
        {
            Profiles =
            {
                new ProxyProfile { Name = "Test", Host = "example.com", Username = "user", AuthMethod = SshAuthMethod.Password, Password = "pwd" }
            }
        };
        var configService = new InMemoryConfigService(appSettings);
        var fakeTunnel = new FakeSshTunnelService();
        var proxyManager = new ProxyManager(configService, () => fakeTunnel);

        // Act
        var success = await proxyManager.ConnectAsync("Test");

        // Assert
        success.Should().BeTrue();
        fakeTunnel.StartedProfiles.Should().Contain("Test");
    }

    [Fact]
    public void RuleEngine_Should_Match_DomainSuffix()
    {
        var rules = new[]
        {
            new SSHClient.Core.Proxy.ProxyRuleEx { Name = "GitHub", Pattern = "github.com", Action = RuleAction.Proxy, Profile = "Test", Type = SSHClient.Core.Proxy.RuleMatchType.DomainSuffix }
        };
        var engine = new SSHClient.Core.Proxy.RuleEngine(rules);
        var match = engine.Match("api.github.com", 443);
        match.Should().NotBeNull();
        match!.Profile.Should().Be("Test");
    }

    [Fact]
    public async Task SocksProxy_Should_Handle_Simple_Connect()
    {
        // Arrange: start a dummy TCP listener to act as remote server
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rules = new[] { new SSHClient.Core.Proxy.ProxyRuleEx { Name = "All", Pattern = "*", Action = RuleAction.Direct, Type = SSHClient.Core.Proxy.RuleMatchType.All } };
        var engine = new SSHClient.Core.Proxy.RuleEngine(rules);
        var proxyMgr = new ProxyManager(new InMemoryConfigService(new AppSettings()), () => new FakeSshTunnelService());
        var fakeConnector = new FakeProxyConnector();
        var socks = new SSHClient.Core.Proxy.SocksProxyServer(engine, proxyMgr, fakeConnector, 0);
        socks.Start();

        var proxyPort = socks.Port;

        // Act: open SOCKS5 connection
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort);
        var stream = client.GetStream();
        // greeting
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }); // v5, 1 method, no auth
        var resp = new byte[2];
        await stream.ReadExactlyAsync(resp.AsMemory());
        resp.Should().Equal(new byte[] { 0x05, 0x00 });

        // connect request to dummy server
        var hostBytes = IPAddress.Loopback.GetAddressBytes();
        var request = new List<byte> { 0x05, 0x01, 0x00, 0x01 };
        request.AddRange(hostBytes);
        request.Add((byte)(port >> 8));
        request.Add((byte)(port & 0xFF));
        await stream.WriteAsync(request.ToArray());

        var reply = new byte[10];
        await stream.ReadExactlyAsync(reply.AsMemory());
        reply[1].Should().Be(0x00); // succeeded

        // Clean up
        await socks.StopAsync();
        listener.Stop();
    }

    [Fact]
    public async Task SocksProxy_Should_Use_ProxyConnector_When_Rule_Proxy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rules = new[] { new SSHClient.Core.Proxy.ProxyRuleEx { Name = "ProxyRule", Pattern = "*", Action = RuleAction.Proxy, Profile = "Test", Type = SSHClient.Core.Proxy.RuleMatchType.All } };
        var engine = new SSHClient.Core.Proxy.RuleEngine(rules);
        var appSettings = new AppSettings
        {
            Profiles = { new ProxyProfile { Name = "Test", Host = "example.com", Username = "user" } }
        };
        var proxyMgr = new ProxyManager(new InMemoryConfigService(appSettings), () => new FakeSshTunnelService());
        var fakeConnector = new FakeProxyConnector();
        var socks = new SSHClient.Core.Proxy.SocksProxyServer(engine, proxyMgr, fakeConnector, 0);
        socks.Start();
        var proxyPort = socks.Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var resp = new byte[2];
        await stream.ReadExactlyAsync(resp.AsMemory());
        var hostBytes = IPAddress.Loopback.GetAddressBytes();
        var request = new List<byte> { 0x05, 0x01, 0x00, 0x01 };
        request.AddRange(hostBytes);
        request.Add((byte)(port >> 8));
        request.Add((byte)(port & 0xFF));
        await stream.WriteAsync(request.ToArray());
        var reply = new byte[10];
        await stream.ReadExactlyAsync(reply.AsMemory());
        reply[1].Should().Be(0x00);

        fakeConnector.Calls.Should().ContainSingle();
        fakeConnector.Calls.Single().host.Should().Be(IPAddress.Loopback.ToString());

        await socks.StopAsync();
        listener.Stop();
    }

    [Fact]
    public async Task ConnectAsync_Should_ReturnFalse_When_Profile_Not_Found()
    {
        var configService = new InMemoryConfigService(new AppSettings());
        var proxyManager = new ProxyManager(configService, () => new FakeSshTunnelService());

        var success = await proxyManager.ConnectAsync("NotExists");

        success.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectAsync_Should_NotReload_Config_On_Every_Call()
    {
        var appSettings = new AppSettings
        {
            Profiles =
            {
                new ProxyProfile { Name = "P1", Host = "example.com", Username = "user", AuthMethod = SshAuthMethod.Password, Password = "pwd" }
            }
        };

        var configService = new InMemoryConfigService(appSettings);
        var tunnel = new FakeSshTunnelService();
        var proxyManager = new ProxyManager(configService, () => tunnel);

        var first = await proxyManager.ConnectAsync("P1");
        var second = await proxyManager.ConnectAsync("P1");

        first.Should().BeTrue();
        second.Should().BeTrue();
        configService.LoadCount.Should().Be(1);
    }

    private sealed class InMemoryConfigService : IConfigService
    {
        private readonly AppSettings _settings;
        public InMemoryConfigService(AppSettings settings) => _settings = settings;
        public int LoadCount { get; private set; }
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(_settings);
        }
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSshTunnelService : ISshTunnelService
    {
        public List<string> StartedProfiles { get; } = new();
        public bool IsConnected { get; private set; }

        public Task<bool> StartAsync(ProxyProfile profile, CancellationToken cancellationToken = default)
        {
            StartedProfiles.Add(profile.Name);
            IsConnected = true;
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProxyConnector : IProxyConnector
    {
        public List<(ProxyProfile profile, string host, int port)> Calls { get; } = new();
        public async Task<TcpClient> ConnectAsync(ProxyProfile profile, string host, int port, CancellationToken cancellationToken = default)
        {
            Calls.Add((profile, host, port));
            var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            return client;
        }
    }
}
