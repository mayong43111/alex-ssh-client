using Microsoft.Win32;
using Serilog;

namespace SSHClient.App.Services;

/// <summary>
/// Minimal system proxy toggler for Windows. Sets WinINET + WinHTTP proxy to localhost ports.
/// </summary>
public interface ISystemProxyService
{
    Task EnableAsync(string host, int port, CancellationToken cancellationToken = default);
    Task DisableAsync(CancellationToken cancellationToken = default);
}

public sealed class SystemProxyService : ISystemProxyService
{
    private readonly ILogger _logger;

    public SystemProxyService(ILogger? logger = null)
    {
        _logger = logger ?? Serilog.Log.Logger;
    }

    public async Task EnableAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var proxyString = $"{host}:{port}"; // WinINET expects host:port; mixed listener accepts HTTP and SOCKS on one port.
        SetWinInetProxy(proxyString);
        await SetWinHttpProxy(proxyString, cancellationToken);
        _logger.Information("系统代理已启用：{Proxy}", proxyString);
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        SetWinInetProxy(null);
        await SetWinHttpProxy("", cancellationToken);
        _logger.Information("系统代理已关闭");
    }

    private static void SetWinInetProxy(string? proxy)
    {
        const string keyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!;
        key.SetValue("ProxyEnable", proxy is not null ? 1 : 0, RegistryValueKind.DWord);
        if (proxy is not null)
        {
            key.SetValue("ProxyServer", proxy, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }
    }

    private static async Task SetWinHttpProxy(string proxy, CancellationToken ct)
    {
        // netsh winhttp set proxy 127.0.0.1:8888
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = string.IsNullOrWhiteSpace(proxy) ? "winhttp reset proxy" : $"winhttp set proxy {proxy}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync(ct);
    }
}
