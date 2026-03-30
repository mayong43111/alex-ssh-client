using FluentAssertions;
using Serilog;
using SSHClient.Core.Configuration;
using SSHClient.Core.Models;
using SSHClient.Core.Proxy;
using SSHClient.Core.Services;
using System.Net;
using System.Net.Sockets;

namespace SSHClient.Tests;

[Trait("Category", "CriticalPath")]
public class UpstreamRouteConnectorTests
{
    [Fact]
    public async Task ConnectAsync_Should_Use_ProxyConnector_When_Rule_Is_Proxy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var remotePort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var settings = new AppSettings
        {
            Profiles =
            {
                new ProxyProfile { Name = "P1", Host = "example.com", Username = "u", AuthMethod = SshAuthMethod.Password, Password = "p" }
            }
        };

        var rules = new[]
        {
            new ProxyRuleEx { Name = "AllProxy", Pattern = "*", Action = RuleAction.Proxy, Type = RuleMatchType.All }
        };

        var proxyManager = new ProxyManager(new InMemoryConfigService(settings), () => new FakeSshTunnelService());
        var connector = new FakeProxyConnector();

        using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _ = listener.AcceptTcpClientAsync(acceptCts.Token);

        var client = await UpstreamRouteConnector.ConnectAsync(
            protocol: "TEST",
            host: IPAddress.Loopback.ToString(),
            port: remotePort,
            rules: new RuleEngine(rules),
            proxyManager: proxyManager,
            proxyConnector: connector,
            logger: Log.Logger,
            preferredProfile: settings.Profiles[0],
            routeProfileName: "P1",
            cancellationToken: CancellationToken.None);

        connector.Calls.Should().ContainSingle();
        connector.Calls[0].profile.Name.Should().Be("P1");
        client.Dispose();
        listener.Stop();
    }

    [Fact]
    public async Task ConnectAsync_Should_DirectConnect_When_Rule_Is_Direct()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var remotePort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var rules = new[]
        {
            new ProxyRuleEx { Name = "AllDirect", Pattern = "*", Action = RuleAction.Direct, Type = RuleMatchType.All }
        };

        var proxyManager = new ProxyManager(new InMemoryConfigService(new AppSettings()), () => new FakeSshTunnelService());
        var connector = new FakeProxyConnector();

        using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _ = listener.AcceptTcpClientAsync(acceptCts.Token);

        var client = await UpstreamRouteConnector.ConnectAsync(
            protocol: "TEST",
            host: IPAddress.Loopback.ToString(),
            port: remotePort,
            rules: new RuleEngine(rules),
            proxyManager: proxyManager,
            proxyConnector: connector,
            logger: Log.Logger,
            preferredProfile: null,
            routeProfileName: null,
            cancellationToken: CancellationToken.None);

        connector.Calls.Should().BeEmpty();
        client.Connected.Should().BeTrue();
        client.Dispose();
        listener.Stop();
    }

    [Fact]
    public async Task ConnectAsync_DirectRoute_Should_Handle_Concurrent_Load_Sample()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var remotePort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var rules = new[]
        {
            new ProxyRuleEx { Name = "AllDirect", Pattern = "*", Action = RuleAction.Direct, Type = RuleMatchType.All }
        };

        var proxyManager = new ProxyManager(new InMemoryConfigService(new AppSettings()), () => new FakeSshTunnelService());
        var connector = new FakeProxyConnector();

        var acceptedCount = 0;
        using var acceptCts = new CancellationTokenSource();
        var acceptLoop = Task.Run(async () =>
        {
            while (!acceptCts.Token.IsCancellationRequested)
            {
                TcpClient? incoming = null;
                try
                {
                    incoming = await listener.AcceptTcpClientAsync(acceptCts.Token);
                    Interlocked.Increment(ref acceptedCount);
                    incoming.Dispose();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                finally
                {
                    incoming?.Dispose();
                }
            }
        });

        const int concurrentConnections = 60;
        var tasks = Enumerable.Range(0, concurrentConnections)
            .Select(async _ =>
            {
                using var client = await UpstreamRouteConnector.ConnectAsync(
                    protocol: "LOAD",
                    host: IPAddress.Loopback.ToString(),
                    port: remotePort,
                    rules: new RuleEngine(rules),
                    proxyManager: proxyManager,
                    proxyConnector: connector,
                    logger: Log.Logger,
                    preferredProfile: null,
                    routeProfileName: null,
                    cancellationToken: CancellationToken.None);

                client.Connected.Should().BeTrue();
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Give accept loop a short window to drain queued sockets.
        await Task.Delay(100);
        Volatile.Read(ref acceptedCount).Should().BeGreaterOrEqualTo(concurrentConnections);

        acceptCts.Cancel();
        listener.Stop();
        await acceptLoop;
    }

    private sealed class InMemoryConfigService : IConfigService
    {
        private readonly AppSettings _settings;

        public InMemoryConfigService(AppSettings settings)
        {
            _settings = settings;
        }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_settings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSshTunnelService : ISshTunnelService
    {
        public bool IsConnected { get; private set; }

        public Task<bool> StartAsync(ProxyProfile profile, CancellationToken cancellationToken = default)
        {
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
