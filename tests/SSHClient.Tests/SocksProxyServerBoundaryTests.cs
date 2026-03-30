using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SSHClient.Core.Configuration;
using SSHClient.Core.Models;
using SSHClient.Core.Proxy;
using SSHClient.Core.Services;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SSHClient.Tests;

[Trait("Category", "CriticalPath")]
public class SocksProxyServerBoundaryTests
{
    [Fact]
    public async Task SocksProxy_Should_Not_Log_BackgroundTaskException_When_Client_Closes_After_Version_Byte()
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var rules = new[]
        {
            new ProxyRuleEx
            {
                Name = "AllDirect",
                Pattern = "*",
                Action = RuleAction.Direct,
                Type = RuleMatchType.All,
            }
        };

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var remotePort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var proxyMgr = new ProxyManager(new InMemoryConfigService(new AppSettings()), () => new FakeSshTunnelService());
        var connector = new FakeProxyConnector();
        var server = new SocksProxyServer(new RuleEngine(rules), proxyMgr, connector, 0, logger);
        server.Start();

        try
        {
            // Send only SOCKS5 version byte, then close to simulate EOF during method negotiation.
            using (var malformedClient = new TcpClient())
            {
                await malformedClient.ConnectAsync(IPAddress.Loopback, server.Port);
                var malformedStream = malformedClient.GetStream();
                await malformedStream.WriteAsync(new byte[] { 0x05 });
            }

            await Task.Delay(200);

            // Verify server still handles a valid SOCKS5 request.
            var acceptTask = listener.AcceptTcpClientAsync();
            using var validClient = new TcpClient();
            await validClient.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = validClient.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
            var greetReply = new byte[2];
            await stream.ReadExactlyAsync(greetReply.AsMemory());
            greetReply.Should().Equal(new byte[] { 0x05, 0x00 });

            var hostBytes = IPAddress.Loopback.GetAddressBytes();
            var request = new List<byte> { 0x05, 0x01, 0x00, 0x01 };
            request.AddRange(hostBytes);
            request.Add((byte)(remotePort >> 8));
            request.Add((byte)(remotePort & 0xFF));
            await stream.WriteAsync(request.ToArray());

            var connectReply = new byte[10];
            await stream.ReadExactlyAsync(connectReply.AsMemory());
            connectReply[1].Should().Be(0x00);

            using var accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(2));

            sink.Messages.Should().NotContain(message =>
                message.Contains("SOCKS 客户端处理后台任务异常", StringComparison.Ordinal));
        }
        finally
        {
            await server.StopAsync();
            listener.Stop();
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public void Emit(LogEvent logEvent)
        {
            _messages.Enqueue(logEvent.MessageTemplate.Text);
        }
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
        public async Task<TcpClient> ConnectAsync(ProxyProfile profile, string host, int port, CancellationToken cancellationToken = default)
        {
            var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            return client;
        }
    }
}
