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
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;

    public HttpProxyServer(IRuleEngine rules, IProxyManager proxyManager, IProxyConnector proxyConnector, int port, ILogger? logger = null)
    {
        _rules = rules;
        _proxyManager = proxyManager;
        _proxyConnector = proxyConnector;
        _port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _logger = logger ?? Serilog.Log.Logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
        _logger.Information("HTTP proxy listening on 127.0.0.1:{Port}", _port);
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
            _logger.Warning(ex, "HTTP proxy listener stop failed");
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
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "HTTP proxy accept loop error");
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
            var uri = new Uri(target);
            host = uri.Host;
            port = uri.Port;
        }

        var rule = _rules.Match(host, port);
        var action = rule?.Action ?? RuleAction.Proxy;
        var targetProfileName = rule?.Profile;
        var shouldProxy = action == RuleAction.Proxy && !string.IsNullOrWhiteSpace(targetProfileName);
        _logger.Information("HTTP proxy {Method} {Host}:{Port} -> action {Action}", method, host, port, action);

        // Consume headers until empty line
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }

        TcpClient? remote = null;
        try
        {
            if (shouldProxy && targetProfileName is not null)
            {
                var profiles = await _proxyManager.GetProfilesAsync(ct);
                var profile = profiles.FirstOrDefault(p => p.Name.Equals(targetProfileName, StringComparison.OrdinalIgnoreCase)) ?? profiles.FirstOrDefault();
                if (profile is null)
                {
                    throw new InvalidOperationException("Profile not found for HTTP proxy rule");
                }
                await _proxyManager.ConnectAsync(profile.Name, ct);
                remote = await _proxyConnector.ConnectAsync(profile, host, port, ct);
            }
            else
            {
                remote = new TcpClient();
                await remote.ConnectAsync(host, port, ct);
            }
            var remoteStream = remote.GetStream();

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("HTTP/1.1 200 Connection Established");
                await writer.WriteLineAsync("Proxy-Agent: SSHClientProxy");
                await writer.WriteLineAsync();
                await writer.FlushAsync();
                // Tunnel raw
                await Task.WhenAny(
                    stream.CopyToAsync(remoteStream, ct),
                    remoteStream.CopyToAsync(stream, ct));
            }
            else
            {
                // Forward original request (with absolute URI already provided by browser)
                var requestBuffer = Encoding.ASCII.GetBytes(requestLine + "\r\n\r\n");
                await remoteStream.WriteAsync(requestBuffer, ct);
                await remoteStream.FlushAsync(ct);
                await remoteStream.CopyToAsync(stream, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "HTTP proxy connect failed to {Host}:{Port}", host, port);
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
}
