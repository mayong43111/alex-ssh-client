using System.Net;
using System.Net.Sockets;
using System.Text;
using SSHClient.Core.Models;
using SSHClient.Core.Services;
using Serilog;

namespace SSHClient.Core.Proxy;

/// <summary>
/// Very minimal HTTP/HTTPS (CONNECT) proxy with rule routing.
/// </summary>
public sealed class HttpProxyServer : IAsyncDisposable
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

    public HttpProxyServer(
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
        _ = ObserveBackgroundTaskAsync(AcceptLoopAsync(_cts.Token), "HTTP 接收循环后台任务异常");
        _logger.Information("HTTP 代理已监听 127.0.0.1:{Port}", _port);
    }

    public async Task StopAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch { /* ignore */ }
        try
        {
            _listener.Stop();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "HTTP 代理监听停止失败");
        }
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
                _ = ObserveBackgroundTaskAsync(HandleClientAsync(client, ct), "HTTP 客户端处理后台任务异常");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "HTTP 接收循环异常");
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        // Read request line
        var requestLine = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(requestLine)) return;
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return;
        var method = parts[0];
        var target = parts[1];
        var httpVersion = parts.Length > 2 ? parts[2] : "HTTP/1.1";
        Uri? requestUri = null;

        string host;
        int port;
        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            var hp = target.Split(':');
            host = hp[0];
            port = hp.Length > 1 ? int.Parse(hp[1]) : 443;
        }
        else
        {
            requestUri = new Uri(target);
            host = requestUri.Host;
            port = requestUri.Port;
        }

        var protocol = $"HTTP[{method}]";

        // Read and preserve headers for non-CONNECT requests.
        var headers = new List<string>();
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            headers.Add(line);
        }

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

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("HTTP/1.1 200 Connection Established");
                await writer.WriteLineAsync("Proxy-Agent: SSHClientProxy");
                await writer.WriteLineAsync();
                await writer.FlushAsync();
                // Tunnel raw and ensure both copy tasks are observed.
                await BridgeStreamsAsync(stream, remoteStream, ct);
            }
            else
            {
                var requestPath = requestUri?.PathAndQuery;
                if (string.IsNullOrWhiteSpace(requestPath))
                {
                    requestPath = "/";
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

                var requestBuffer = Encoding.ASCII.GetBytes(outgoing.ToString());
                await remoteStream.WriteAsync(requestBuffer, ct);
                await remoteStream.FlushAsync(ct);
                await remoteStream.CopyToAsync(stream, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "HTTP 代理连接 {Host}:{Port} 失败", host, port);
            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("HTTP/1.1 502 Bad Gateway");
                await writer.WriteLineAsync();
            }
        }
        finally
        {
            remote?.Dispose();
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
