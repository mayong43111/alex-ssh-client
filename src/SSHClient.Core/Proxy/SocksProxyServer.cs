using System.Net;
using System.Net.Sockets;
using System.Text;
using SSHClient.Core.Models;
using SSHClient.Core.Services;
using Serilog;

namespace SSHClient.Core.Proxy;

/// <summary>
/// Minimal SOCKS5/4 proxy server implementation (no UDP associate yet). Supports CONNECT.
/// Rule engine decides whether to route via proxy (SSH tunnel) or direct.
/// </summary>
public sealed class SocksProxyServer : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    private readonly ILogger _logger;
    private readonly IRuleEngine _rules;
    private readonly IProxyManager _proxyManager;
    private readonly IProxyConnector _proxyConnector;
    private readonly int _port;
    private readonly string? _routeProfileName;
    private readonly ProxyProfile? _routeProfile;
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;

    public SocksProxyServer(
        IRuleEngine rules,
        IProxyManager proxyManager,
        IProxyConnector proxyConnector,
        int port,
        ILogger? logger = null,
        string? routeProfileName = null,
        ProxyProfile? routeProfile = null)
    {
        _rules = rules;
        _proxyManager = proxyManager;
        _proxyConnector = proxyConnector;
        _port = port;
        _routeProfileName = routeProfileName;
        _routeProfile = routeProfile;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _logger = logger ?? Serilog.Log.Logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = ObserveBackgroundTaskAsync(AcceptLoopAsync(_cts.Token), "SOCKS 接收循环后台任务异常");
        _logger.Information("SOCKS 代理已监听 127.0.0.1:{Port}", _port);
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); }
        catch { /* ignore */ }
        try { _listener.Stop(); }
        catch (Exception ex) { _logger.Warning(ex, "SOCKS 代理监听停止失败"); }
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
                _ = ObserveBackgroundTaskAsync(HandleClientAsync(client, ct), "SOCKS 客户端处理后台任务异常");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "SOCKS 接收循环异常");
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var stream = client.GetStream();
        // Read auth methods
        int version = stream.ReadByte();
        if (version < 0)
        {
            return;
        }

        if (version == 0x04)
        {
            await HandleSocks4Async(stream, ct);
            return;
        }
        if (version != 0x05)
        {
            if (IsLikelyHttpMethodInitial(version))
            {
                _logger.Warning(
                    "SOCKS 端口收到疑似 HTTP 代理请求，首字节 {Version} ('{Ascii}')。请将该端口按 SOCKS5 代理配置。",
                    version,
                    (char)version);
                await TrySendHttpProxyHintAsync(stream, ct);
                return;
            }

            _logger.Warning("不支持的 SOCKS 版本 {Version}", version);
            return;
        }

        int nMethods = stream.ReadByte();
        if (nMethods <= 0)
        {
            _logger.Warning("SOCKS5 握手失败：认证方法数量无效 {MethodCount}", nMethods);
            return;
        }

        var methods = new byte[nMethods];
        await stream.ReadExactlyAsync(methods, ct);
        // No auth
        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

        // Parse request
        var header = new byte[4];
        await stream.ReadExactlyAsync(header.AsMemory(), ct);
        var cmd = header[1];
        var atyp = header[3];

        string host = string.Empty;
        byte[]? addrBytes = null;
        if (atyp == 0x01)
        {
            addrBytes = new byte[4];
            await stream.ReadExactlyAsync(addrBytes.AsMemory(), ct);
            host = new IPAddress(addrBytes).ToString();
        }
        else if (atyp == 0x03)
        {
            int len = stream.ReadByte();
            if (len <= 0)
            {
                _logger.Warning("SOCKS5 请求失败：域名长度无效 {Length}", len);
                await SendSocks5Reply(stream, 0x01).AsTask();
                return;
            }

            addrBytes = new byte[len];
            await stream.ReadExactlyAsync(addrBytes, ct);
            host = Encoding.ASCII.GetString(addrBytes);
        }
        else if (atyp == 0x04)
        {
            addrBytes = new byte[16];
            await stream.ReadExactlyAsync(addrBytes.AsMemory(), ct);
            host = new IPAddress(addrBytes).ToString();
        }
        else
        {
            _logger.Warning("未知 ATYP {Atyp}", atyp);
            return;
        }

        // Port
        var portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes.AsMemory(), ct);
        int port = (portBytes[0] << 8) | portBytes[1];

        if (cmd != 0x01)
        {
            await SendSocks5Reply(stream, 0x07).AsTask(); // Command not supported
            return;
        }

        TcpClient? remote = null;
        try
        {
            remote = await UpstreamRouteConnector.ConnectAsync(
                "SOCKS5",
                host,
                port,
                _rules,
                _proxyManager,
                _proxyConnector,
                _logger,
                _routeProfile,
                _routeProfileName,
                ct);

            await SendSocks5Reply(stream, 0x00).AsTask(); // succeeded

            // Bridge both streams and make sure both copy tasks are observed.
            var remoteStream = remote.GetStream();
            await BridgeStreamsAsync(stream, remoteStream, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SOCKS 连接 {Host}:{Port} 失败", host, port);
            await SendSocks5Reply(stream, 0x01).AsTask(); // general failure
        }
        finally
        {
            remote?.Dispose();
        }
    }

    private async Task HandleSocks4Async(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[7];
        await stream.ReadExactlyAsync(buf.AsMemory(), ct);
        var cmd = buf[0];
        var port = (buf[1] << 8) | buf[2];
        var ipBytes = new byte[] { buf[3], buf[4], buf[5], buf[6] };
        var host = new IPAddress(ipBytes).ToString();
        // UserId (null-terminated)
        var user = await ReadNullTerminatedAsync(stream, ct);
        // SOCKS4a host (optional)
        if (ipBytes[0] == 0 && ipBytes[1] == 0 && ipBytes[2] == 0 && ipBytes[3] != 0)
        {
            host = await ReadNullTerminatedAsync(stream, ct) ?? host;
        }

        if (cmd != 0x01)
        {
            await SendSocks4Reply(stream, success: false, port, ipBytes, ct).AsTask();
            return;
        }

        TcpClient? remote = null;
        try
        {
            remote = await UpstreamRouteConnector.ConnectAsync(
                "SOCKS4",
                host,
                port,
                _rules,
                _proxyManager,
                _proxyConnector,
                _logger,
                _routeProfile,
                _routeProfileName,
                ct);

            await SendSocks4Reply(stream, success: true, port, ipBytes, ct).AsTask();
            var remoteStream = remote.GetStream();
            await BridgeStreamsAsync(stream, remoteStream, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SOCKS4 连接 {Host}:{Port} 失败", host, port);
            await SendSocks4Reply(stream, success: false, port, ipBytes, ct).AsTask();
        }
        finally
        {
            remote?.Dispose();
        }
    }

    private static async Task<string?> ReadNullTerminatedAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        while (true)
        {
            int b = await ReadByteAsync(stream, ct);
            if (b <= 0) break;
            if (b == 0) break;
            bytes.Add((byte)b);
        }
        return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async ValueTask<byte[]> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
        return buffer;
    }

    private static async ValueTask<int> ReadByteAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, ct);
        return read == 0 ? -1 : buffer[0];
    }

    private static bool IsLikelyHttpMethodInitial(int value)
    {
        return value is (int)'G' // GET
            or (int)'P'          // POST/PUT/PATCH
            or (int)'C'          // CONNECT
            or (int)'H'          // HEAD
            or (int)'D'          // DELETE
            or (int)'O'          // OPTIONS
            or (int)'T';         // TRACE
    }

    private static async Task TrySendHttpProxyHintAsync(NetworkStream stream, CancellationToken ct)
    {
        const string response = "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nThis endpoint is SOCKS5, not HTTP proxy.\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        }
        catch
        {
            // Ignore write failures for non-HTTP clients.
        }
    }

    private static ValueTask SendSocks5Reply(NetworkStream stream, byte rep)
    {
        byte[] reply = new byte[10];
        reply[0] = 0x05;
        reply[1] = rep;
        reply[2] = 0x00;
        reply[3] = 0x01; // IPv4
        // IP + Port zeros
        return stream.WriteAsync(reply);
    }

    private static ValueTask SendSocks4Reply(NetworkStream stream, bool success, int port, byte[] ipBytes, CancellationToken ct)
    {
        byte[] reply = new byte[8];
        reply[0] = 0x00;
        reply[1] = success ? (byte)0x5a : (byte)0x5b;
        reply[2] = (byte)((port >> 8) & 0xFF);
        reply[3] = (byte)(port & 0xFF);
        Array.Copy(ipBytes, 0, reply, 4, 4);
        return stream.WriteAsync(reply, ct);
    }

    private static async Task BridgeStreamsAsync(NetworkStream clientStream, NetworkStream remoteStream, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cts = linkedCts.Token;

        var uplink = PumpStreamAsync(clientStream, remoteStream, cts);
        var downlink = PumpStreamAsync(remoteStream, clientStream, cts);

        await Task.WhenAny(uplink, downlink);
        linkedCts.Cancel();
        await Task.WhenAll(uplink, downlink);
    }

    private static async Task PumpStreamAsync(Stream source, Stream destination, CancellationToken ct)
    {
        try
        {
            await source.CopyToAsync(destination, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal during connection teardown.
        }
        catch (ObjectDisposedException)
        {
            // Normal during connection teardown.
        }
        catch (IOException)
        {
            // Normal during connection teardown.
        }
    }

    private async Task ObserveBackgroundTaskAsync(Task task, string context)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, context);
        }
    }
}
