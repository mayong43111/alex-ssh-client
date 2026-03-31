using System.Net;
using System.Net.Sockets;
using System.Text;
using SSHClient.Core.Models;
using SSHClient.Core.Services;
using Serilog;

namespace SSHClient.Core.Proxy;

/// <summary>
/// Single-port mixed proxy server.
/// It accepts SOCKS4/5 and HTTP proxy requests on the same listening port.
/// </summary>
public sealed class MixedProxyServer : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private readonly ILogger _logger;
    private readonly IRuleEngine _rules;
    private readonly IProxyManager _proxyManager;
    private readonly IProxyConnector _proxyConnector;
    private readonly ITrafficMonitor? _trafficMonitor;
    private readonly int _port;
    private readonly string? _routeProfileName;
    private readonly ProxyProfile? _routeProfile;
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;

    public MixedProxyServer(
        IRuleEngine rules,
        IProxyManager proxyManager,
        IProxyConnector proxyConnector,
        int port,
        ILogger? logger = null,
        string? routeProfileName = null,
        ProxyProfile? routeProfile = null,
        ITrafficMonitor? trafficMonitor = null)
    {
        _rules = rules;
        _proxyManager = proxyManager;
        _proxyConnector = proxyConnector;
        _port = port;
        _routeProfileName = routeProfileName;
        _routeProfile = routeProfile;
        _trafficMonitor = trafficMonitor;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _logger = logger ?? Serilog.Log.Logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = ObserveBackgroundTaskAsync(AcceptLoopAsync(_cts.Token), "Mixed 代理接收循环后台任务异常");
        _logger.Information("Mixed 代理已监听 127.0.0.1:{Port}（HTTP + SOCKS）", _port);
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); }
        catch { /* ignore */ }

        try { _listener.Stop(); }
        catch (Exception ex) { _logger.Warning(ex, "Mixed 代理监听停止失败"); }

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
                _ = ObserveBackgroundTaskAsync(HandleClientAsync(client, ct), "Mixed 客户端处理后台任务异常");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Mixed 接收循环异常");
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var stream = client.GetStream();

        var firstByte = await ReadByteAsync(stream, ct);
        if (firstByte < 0)
        {
            return;
        }

        if (firstByte == 0x04)
        {
            await HandleSocks4Async(stream, ct);
            return;
        }

        if (firstByte == 0x05)
        {
            await HandleSocks5Async(stream, ct);
            return;
        }

        if (IsLikelyHttpMethodInitial(firstByte))
        {
            await HandleHttpAsync(stream, (byte)firstByte, ct);
            return;
        }

        _logger.Warning("Mixed 端口收到不支持协议首字节 {FirstByte}", firstByte);
    }

    private async Task HandleSocks5Async(NetworkStream stream, CancellationToken ct)
    {
        var nMethods = await ReadByteAsync(stream, ct);
        if (nMethods <= 0)
        {
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

        string host;
        byte[]? addrBytes;
        if (atyp == 0x01)
        {
            addrBytes = new byte[4];
            await stream.ReadExactlyAsync(addrBytes.AsMemory(), ct);
            host = new IPAddress(addrBytes).ToString();
        }
        else if (atyp == 0x03)
        {
            var len = await ReadByteAsync(stream, ct);
            if (len <= 0)
            {
                await SendSocks5Reply(stream, 0x01);
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
            _logger.Warning("未知 SOCKS5 ATYP {Atyp}", atyp);
            await SendSocks5Reply(stream, 0x08); // Address type not supported
            return;
        }

        var portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes.AsMemory(), ct);
        var port = (portBytes[0] << 8) | portBytes[1];

        if (cmd != 0x01)
        {
            await SendSocks5Reply(stream, 0x07); // Command not supported
            return;
        }

        if (IsSelfProxyEndpoint(host, port))
        {
            _logger.Warning("阻止潜在代理回环：SOCKS5 请求目标为本地代理端点 {Host}:{Port}", host, port);
            await SendSocks5Reply(stream, 0x02); // Connection not allowed by ruleset
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

            await SendSocks5Reply(stream, 0x00);

            var remoteStream = remote.GetStream();
            var routeAction = _rules.Match(host, port)?.Action ?? RuleAction.Proxy;
            await BridgeTrackedAsync("SOCKS5", host, port, routeAction, stream, remoteStream, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SOCKS5 连接 {Host}:{Port} 失败", host, port);
            await SendSocks5Reply(stream, 0x01);
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

        _ = await ReadNullTerminatedAsync(stream, ct); // user id

        // SOCKS4a host (optional)
        if (ipBytes[0] == 0 && ipBytes[1] == 0 && ipBytes[2] == 0 && ipBytes[3] != 0)
        {
            host = await ReadNullTerminatedAsync(stream, ct) ?? host;
        }

        if (cmd != 0x01)
        {
            await SendSocks4Reply(stream, success: false, port, ipBytes, ct);
            return;
        }

        if (IsSelfProxyEndpoint(host, port))
        {
            _logger.Warning("阻止潜在代理回环：SOCKS4 请求目标为本地代理端点 {Host}:{Port}", host, port);
            await SendSocks4Reply(stream, success: false, port, ipBytes, ct);
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

            await SendSocks4Reply(stream, success: true, port, ipBytes, ct);
            var remoteStream = remote.GetStream();
            var routeAction = _rules.Match(host, port)?.Action ?? RuleAction.Proxy;
            await BridgeTrackedAsync("SOCKS4", host, port, routeAction, stream, remoteStream, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SOCKS4 连接 {Host}:{Port} 失败", host, port);
            await SendSocks4Reply(stream, success: false, port, ipBytes, ct);
        }
        finally
        {
            remote?.Dispose();
        }
    }

    private async Task HandleHttpAsync(NetworkStream stream, byte firstByte, CancellationToken ct)
    {
        var requestLine = await ReadAsciiLineAsync(stream, ct, firstByte);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return;
        }

        var method = parts[0];
        var target = parts[1];
        var httpVersion = parts.Length > 2 ? parts[2] : "HTTP/1.1";

        var headers = new List<string>();
        string? line;
        while (!string.IsNullOrEmpty(line = await ReadAsciiLineAsync(stream, ct)))
        {
            headers.Add(line);
        }

        if (!TryResolveHttpTarget(method, target, headers, out var host, out var port, out var requestPath, out var isConnect))
        {
            _logger.Warning("HTTP 请求无法解析目标：{RequestLine}", requestLine);
            await TrySendHttpBadRequestAsync(stream, ct);
            return;
        }

        if (IsSelfProxyEndpoint(host, port))
        {
            _logger.Warning("阻止潜在代理回环：HTTP 请求目标为本地代理端点 {Host}:{Port}", host, port);
            await TrySendHttpLoopDetectedAsync(stream, ct);
            return;
        }

        var protocol = $"HTTP[{method}]";

        TcpClient? remote = null;
        try
        {
            remote = await UpstreamRouteConnector.ConnectAsync(
                protocol,
                host,
                port,
                _rules,
                _proxyManager,
                _proxyConnector,
                _logger,
                _routeProfile,
                _routeProfileName,
                ct);

            var remoteStream = remote.GetStream();

            if (isConnect)
            {
                var success = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\nProxy-Agent: SSHClientProxy\r\n\r\n");
                await stream.WriteAsync(success, ct);
                var routeAction = _rules.Match(host, port)?.Action ?? RuleAction.Proxy;
                await BridgeTrackedAsync($"HTTP[{method}]", host, port, routeAction, stream, remoteStream, ct);
                return;
            }

            var outgoing = new StringBuilder();
            outgoing.Append(method).Append(' ').Append(requestPath).Append(' ').Append(httpVersion).Append("\r\n");
            foreach (var header in headers)
            {
                if (header.StartsWith("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                outgoing.Append(header).Append("\r\n");
            }
            outgoing.Append("\r\n");

            await remoteStream.WriteAsync(Encoding.ASCII.GetBytes(outgoing.ToString()), ct);
            await remoteStream.FlushAsync(ct);
            await remoteStream.CopyToAsync(stream, ct);
        }
        catch (Exception ex)
        {
            if (IsExpectedShutdownException(ex, ct))
            {
                return;
            }

            _logger.Warning(ex, "HTTP 代理连接 {Host}:{Port} 失败", host, port);
            await TrySendHttpBadGatewayAsync(stream, ct, isConnect);
        }
        finally
        {
            remote?.Dispose();
        }
    }

    private static bool TryResolveHttpTarget(
        string method,
        string target,
        IReadOnlyList<string> headers,
        out string host,
        out int port,
        out string requestPath,
        out bool isConnect)
    {
        isConnect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
        requestPath = "/";
        host = string.Empty;
        port = 80;

        if (isConnect)
        {
            var hp = target.Split(':', 2);
            host = hp[0];
            port = hp.Length > 1 && int.TryParse(hp[1], out var connectPort) ? connectPort : 443;
            return !string.IsNullOrWhiteSpace(host);
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
        {
            host = absoluteUri.Host;
            port = absoluteUri.Port;
            requestPath = string.IsNullOrWhiteSpace(absoluteUri.PathAndQuery) ? "/" : absoluteUri.PathAndQuery;
            return !string.IsNullOrWhiteSpace(host);
        }

        requestPath = string.IsNullOrWhiteSpace(target) ? "/" : target;

        var hostHeader = headers
            .FirstOrDefault(h => h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return false;
        }

        var hostValue = hostHeader[5..].Trim();
        if (string.IsNullOrWhiteSpace(hostValue))
        {
            return false;
        }

        var hostPortParts = hostValue.Split(':', 2);
        host = hostPortParts[0].Trim();
        port = hostPortParts.Length > 1 && int.TryParse(hostPortParts[1], out var parsedPort) ? parsedPort : 80;

        return !string.IsNullOrWhiteSpace(host);
    }

    private static async Task<string?> ReadAsciiLineAsync(NetworkStream stream, CancellationToken ct, byte? firstByte = null)
    {
        var bytes = new List<byte>();
        if (firstByte.HasValue)
        {
            bytes.Add(firstByte.Value);
        }

        while (true)
        {
            var b = await ReadByteAsync(stream, ct);
            if (b < 0)
            {
                break;
            }

            if (b == '\n')
            {
                break;
            }

            if (b != '\r')
            {
                bytes.Add((byte)b);
            }
        }

        if (bytes.Count == 0)
        {
            return null;
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static bool IsLikelyHttpMethodInitial(int value)
    {
        return value is (int)'G'
            or (int)'P'
            or (int)'C'
            or (int)'H'
            or (int)'D'
            or (int)'O'
            or (int)'T'
            or (int)'g'
            or (int)'p'
            or (int)'c'
            or (int)'h'
            or (int)'d'
            or (int)'o'
            or (int)'t';
    }

    private static async Task<string?> ReadNullTerminatedAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = await ReadByteAsync(stream, ct);
            if (b <= 0)
            {
                break;
            }

            if (b == 0)
            {
                break;
            }

            bytes.Add((byte)b);
        }

        return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static ValueTask SendSocks5Reply(NetworkStream stream, byte rep)
    {
        var reply = new byte[10];
        reply[0] = 0x05;
        reply[1] = rep;
        reply[2] = 0x00;
        reply[3] = 0x01;
        return stream.WriteAsync(reply);
    }

    private static ValueTask SendSocks4Reply(NetworkStream stream, bool success, int port, byte[] ipBytes, CancellationToken ct)
    {
        var reply = new byte[8];
        reply[0] = 0x00;
        reply[1] = success ? (byte)0x5a : (byte)0x5b;
        reply[2] = (byte)((port >> 8) & 0xFF);
        reply[3] = (byte)(port & 0xFF);
        Array.Copy(ipBytes, 0, reply, 4, 4);
        return stream.WriteAsync(reply, ct);
    }

    private static async Task TrySendHttpBadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        const string response = "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task TrySendHttpBadGatewayAsync(NetworkStream stream, CancellationToken ct, bool isConnect)
    {
        if (!isConnect)
        {
            return;
        }

        const string response = "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        }
        catch
        {
            // ignore
        }
    }

    private async Task TrySendHttpLoopDetectedAsync(NetworkStream stream, CancellationToken ct)
    {
        var response = "HTTP/1.1 508 Loop Detected\r\nConnection: close\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nBlocked proxy self-loop target: 127.0.0.1:" + _port + "\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        }
        catch
        {
            // ignore
        }
    }

    private bool IsSelfProxyEndpoint(string host, int port)
    {
        if (port != _port || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    private async Task BridgeTrackedAsync(
        string protocol, string host, int port, RuleAction routeAction,
        NetworkStream clientStream, NetworkStream remoteStream,
        CancellationToken ct)
    {
        if (_trafficMonitor is null)
        {
            await BridgeStreamsAsync(clientStream, remoteStream, ct);
            return;
        }

        var connId = _trafficMonitor.RegisterConnection(protocol, host, port, routeAction);
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tunnelToken = linkedCts.Token;

            // 用数组规避闭包捕获局部变量问题：[0]=upBytes [1]=downBytes
            var counters = new long[2];

            // client -> remote (上行)，用 CountingStream 包裹目标流
            var countingRemote = new CountingStream(remoteStream, total =>
            {
                Interlocked.Exchange(ref counters[0], total);
                _trafficMonitor.ReportBytes(connId, counters[0], Interlocked.Read(ref counters[1]));
            });
            // remote -> client (下行)
            var countingClient = new CountingStream(clientStream, total =>
            {
                Interlocked.Exchange(ref counters[1], total);
                _trafficMonitor.ReportBytes(connId, Interlocked.Read(ref counters[0]), counters[1]);
            });

            var uplink = PumpStreamAsync(clientStream, countingRemote, tunnelToken);
            var downlink = PumpStreamAsync(remoteStream, countingClient, tunnelToken);

            await Task.WhenAny(uplink, downlink);
            linkedCts.Cancel();
            await Task.WhenAll(uplink, downlink);
        }
        finally
        {
            _trafficMonitor.CompleteConnection(connId);
        }
    }

    private static async Task BridgeStreamsAsync(NetworkStream clientStream, NetworkStream remoteStream, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tunnelToken = linkedCts.Token;

        var uplink = PumpStreamAsync(clientStream, remoteStream, tunnelToken);
        var downlink = PumpStreamAsync(remoteStream, clientStream, tunnelToken);

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
            // Normal during tunnel close.
        }
        catch (ObjectDisposedException)
        {
            // Normal during tunnel close.
        }
        catch (IOException)
        {
            // Normal during tunnel close.
        }
    }

    private static async ValueTask<int> ReadByteAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, ct);
        return read == 0 ? -1 : buffer[0];
    }

    private static bool IsExpectedShutdownException(Exception ex, CancellationToken ct)
    {
        return ex is OperationCanceledException
            || ex is ObjectDisposedException
            || (ct.IsCancellationRequested && ex is IOException);
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
