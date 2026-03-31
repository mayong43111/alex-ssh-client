using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace SSHClient.App.Services;

public interface IPacHttpHost
{
    int CurrentPort { get; }
    Task<int> EnsureStartedAsync(int preferredPort, Func<string> scriptProvider, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class PacHttpHost : IPacHttpHost, IAsyncDisposable
{
    private const string ScriptPath = "/proxy.pac";
    private const int MaxPortRetryAttempts = 20;
    private const int MinDynamicPort = 1024;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;

    private TcpListener? _listener;
    private CancellationTokenSource? _serveCts;
    private Task? _serveLoopTask;
    private Func<string>? _scriptProvider;
    private int _currentPort;

    public int CurrentPort => _currentPort;

    public PacHttpHost(ILogger? logger = null)
    {
        _logger = logger ?? Serilog.Log.Logger;
    }

    public async Task<int> EnsureStartedAsync(int preferredPort, Func<string> scriptProvider, CancellationToken cancellationToken = default)
    {
        if (preferredPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredPort), "PAC 脚本端口必须在 1-65535 之间。");
        }

        ArgumentNullException.ThrowIfNull(scriptProvider);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _scriptProvider = scriptProvider;

            if (_listener is not null && _currentPort == preferredPort)
            {
                return _currentPort;
            }

            await StopInternalAsync();

            var actualPort = StartServerWithRetry(preferredPort);
            if (actualPort != preferredPort)
            {
                _logger.Warning("PAC 首选端口 {PreferredPort} 被占用，已切换到 {ActualPort}", preferredPort, actualPort);
            }

            return actualPort;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(ShutdownTimeout);
            await StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("PAC HTTP Host 释放超时，跳过剩余等待");
        }

        _gate.Dispose();
    }

    private int StartServerWithRetry(int preferredPort)
    {
        for (var attempt = 0; attempt < MaxPortRetryAttempts; attempt++)
        {
            var candidatePort = GetCandidatePort(preferredPort, attempt);
            try
            {
                StartServer(candidatePort);
                return candidatePort;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _logger.Warning(
                    "PAC 端口 {Port} 被占用，继续尝试（{Attempt}/{MaxAttempts}）",
                    candidatePort,
                    attempt + 1,
                    MaxPortRetryAttempts);
            }
        }

        throw new InvalidOperationException(
            $"PAC 脚本服务启动失败：从端口 {preferredPort} 开始尝试 {MaxPortRetryAttempts} 次均不可用。");
    }

    private void StartServer(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        _listener = listener;
        _serveCts = new CancellationTokenSource();
        _serveLoopTask = Task.Run(() => ServeLoopAsync(listener, _serveCts.Token));
        _currentPort = port;

        _logger.Information("PAC 脚本服务已启动：{Url}", $"http://127.0.0.1:{port}{ScriptPath}");
    }

    private static int GetCandidatePort(int preferredPort, int offset)
    {
        var range = 65535 - MinDynamicPort + 1;
        var normalizedPreferred = preferredPort < MinDynamicPort ? MinDynamicPort : preferredPort;
        var shifted = (normalizedPreferred - MinDynamicPort + offset) % range;
        return MinDynamicPort + shifted;
    }

    private async Task StopInternalAsync()
    {
        var cts = _serveCts;
        var listener = _listener;
        var serveTask = _serveLoopTask;

        _serveCts = null;
        _listener = null;
        _serveLoopTask = null;
        _currentPort = 0;

        if (cts is not null)
        {
            cts.Cancel();
        }

        listener?.Stop();

        if (serveTask is not null)
        {
            var completed = await Task.WhenAny(serveTask, Task.Delay(ShutdownTimeout));
            if (!ReferenceEquals(completed, serveTask))
            {
                _logger.Warning("PAC 脚本服务停止等待超时，继续退出流程");
            }
            else
            {
                try
                {
                    await serveTask;
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation during shutdown.
                }
                catch (ObjectDisposedException)
                {
                    // Ignore listener disposal race.
                }
            }
        }

        cts?.Dispose();
    }

    private async Task ServeLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "PAC 脚本服务监听异常");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;

        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
            var requestedPath = ExtractRequestPath(requestLine);
            var isPacRequest = string.Equals(requestedPath, ScriptPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(requestedPath, "/", StringComparison.Ordinal);

            if (!isPacRequest)
            {
                var notFound = Encoding.UTF8.GetBytes("Not Found");
                var header404 = $"HTTP/1.1 404 Not Found\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {notFound.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header404), cancellationToken);
                await stream.WriteAsync(notFound, cancellationToken);
                return;
            }

            var script = _scriptProvider?.Invoke() ?? string.Empty;
            var body = Encoding.UTF8.GetBytes(script);
            var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/x-ns-proxy-autoconfig; charset=utf-8\r\nCache-Control: no-cache, no-store, must-revalidate\r\nPragma: no-cache\r\nExpires: 0\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Debug(ex, "PAC 脚本请求处理失败");
        }
    }

    private static string ExtractRequestPath(string requestLine)
    {
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        return parts[1].Trim();
    }
}
