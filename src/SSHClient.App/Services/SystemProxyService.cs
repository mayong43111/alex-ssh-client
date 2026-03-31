using Microsoft.Win32;
using Serilog;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SSHClient.App.Services;

/// <summary>
/// Minimal system proxy toggler for Windows. Sets WinINET + WinHTTP proxy to localhost ports.
/// </summary>
public interface ISystemProxyService
{
    Task EnableAsync(string host, int port, CancellationToken cancellationToken = default);
    Task<bool> EnableWithElevationAsync(string host, int port, CancellationToken cancellationToken = default);
    Task DisableAsync(CancellationToken cancellationToken = default);
    Task<bool> DisableWithElevationAsync(CancellationToken cancellationToken = default);
    Task<bool> EnableAutoConfigScriptWithElevationAsync(string scriptUrl, CancellationToken cancellationToken = default);
    Task<bool> DisableAutoConfigScriptWithElevationAsync(CancellationToken cancellationToken = default);
    string? GetCurrentAutoConfigScriptUrl();
}

public sealed class SystemProxyService : ISystemProxyService
{
    private readonly ILogger _logger;

    private readonly record struct WinInetSettingsSnapshot(
        bool HasProxyEnable,
        int ProxyEnable,
        bool HasProxyServer,
        string? ProxyServer,
        bool HasAutoConfigUrl,
        string? AutoConfigUrl);

    public SystemProxyService(ILogger? logger = null)
    {
        _logger = logger ?? Serilog.Log.Logger;
    }

    public async Task EnableAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var proxyString = $"{host}:{port}"; // WinINET expects host:port; mixed listener accepts HTTP and SOCKS on one port.
        SetWinInetProxy(proxyString);
        await SetWinHttpProxy(proxyString, cancellationToken, elevate: false);
        _logger.Information("系统代理已启用：{Proxy}", proxyString);
    }

    public async Task<bool> EnableWithElevationAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var proxyString = $"{host}:{port}";
        var snapshot = CaptureWinInetSettings();
        SetWinInetProxy(proxyString);

        try
        {
            await SetWinHttpProxy(proxyString, cancellationToken, elevate: true);
            _logger.Information("系统代理已通过管理员权限启用：{Proxy}", proxyString);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            RestoreWinInetSettings(snapshot);
            _logger.Information("用户取消了管理员授权，系统代理保持不变");
            return false;
        }
        catch
        {
            RestoreWinInetSettings(snapshot);
            throw;
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        SetWinInetProxy(null);
        await SetWinHttpProxy("", cancellationToken, elevate: false);
        _logger.Information("系统代理已关闭");
    }

    public async Task<bool> DisableWithElevationAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureWinInetSettings();
        SetWinInetProxy(null);

        try
        {
            await SetWinHttpProxy("", cancellationToken, elevate: true);
            _logger.Information("系统代理已通过管理员权限恢复默认设置");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            RestoreWinInetSettings(snapshot);
            _logger.Information("用户取消了管理员授权，系统代理恢复未完成");
            return false;
        }
        catch
        {
            RestoreWinInetSettings(snapshot);
            throw;
        }
    }

    public async Task<bool> EnableAutoConfigScriptWithElevationAsync(string scriptUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scriptUrl))
        {
            throw new ArgumentException("自动脚本地址不能为空", nameof(scriptUrl));
        }

        var snapshot = CaptureWinInetSettings();
        SetWinInetAutoConfigScript(scriptUrl.Trim());

        try
        {
            await RunNetshWinHttpAsync("winhttp import proxy source=ie", cancellationToken, elevate: true);
            _logger.Information("系统自动代理脚本已启用：{ScriptUrl}", scriptUrl);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            RestoreWinInetSettings(snapshot);
            _logger.Information("用户取消了管理员授权，自动代理脚本保持不变");
            return false;
        }
        catch
        {
            RestoreWinInetSettings(snapshot);
            throw;
        }
    }

    public async Task<bool> DisableAutoConfigScriptWithElevationAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureWinInetSettings();
        ClearWinInetAutoConfigScript();

        try
        {
            await RunNetshWinHttpAsync("winhttp reset proxy", cancellationToken, elevate: true);
            _logger.Information("系统自动代理脚本已恢复默认设置");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            RestoreWinInetSettings(snapshot);
            _logger.Information("用户取消了管理员授权，自动代理脚本恢复未完成");
            return false;
        }
        catch
        {
            RestoreWinInetSettings(snapshot);
            throw;
        }
    }

    public string? GetCurrentAutoConfigScriptUrl()
    {
        const string keyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue("AutoConfigURL") as string;
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

        // Ensure fixed-proxy mode does not keep stale PAC URL.
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        RefreshWinInetSettings();
    }

    private static void SetWinInetAutoConfigScript(string scriptUrl)
    {
        const string keyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!;
        key.SetValue("AutoConfigURL", scriptUrl, RegistryValueKind.String);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        RefreshWinInetSettings();
    }

    private static void ClearWinInetAutoConfigScript()
    {
        const string keyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!;
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        RefreshWinInetSettings();
    }

    private static async Task SetWinHttpProxy(string proxy, CancellationToken ct, bool elevate)
    {
        var arguments = string.IsNullOrWhiteSpace(proxy) ? "winhttp reset proxy" : $"winhttp set proxy {proxy}";
        await RunNetshWinHttpAsync(arguments, ct, elevate);
    }

    private static async Task RunNetshWinHttpAsync(string arguments, CancellationToken ct, bool elevate)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = elevate,
            Verb = elevate ? "runas" : string.Empty,
            RedirectStandardOutput = !elevate,
            RedirectStandardError = !elevate,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 netsh 命令。");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"netsh 执行失败，参数：{arguments}，退出码：{proc.ExitCode}");
        }
    }

    private static void RefreshWinInetSettings()
    {
        const int internetOptionSettingsChanged = 39;
        const int internetOptionRefresh = 37;
        _ = InternetSetOption(IntPtr.Zero, internetOptionSettingsChanged, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, internetOptionRefresh, IntPtr.Zero, 0);
    }

    private static WinInetSettingsSnapshot CaptureWinInetSettings()
    {
        const string keyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false)!;

        var proxyEnableRaw = key.GetValue("ProxyEnable");
        var hasProxyEnable = proxyEnableRaw is not null;
        var proxyEnable = hasProxyEnable ? Convert.ToInt32(proxyEnableRaw) : 0;

        var proxyServerRaw = key.GetValue("ProxyServer") as string;
        var hasProxyServer = proxyServerRaw is not null;

        var autoConfigRaw = key.GetValue("AutoConfigURL") as string;
        var hasAutoConfigUrl = autoConfigRaw is not null;

        return new WinInetSettingsSnapshot(
            HasProxyEnable: hasProxyEnable,
            ProxyEnable: proxyEnable,
            HasProxyServer: hasProxyServer,
            ProxyServer: proxyServerRaw,
            HasAutoConfigUrl: hasAutoConfigUrl,
            AutoConfigUrl: autoConfigRaw);
    }

    private static void RestoreWinInetSettings(WinInetSettingsSnapshot snapshot)
    {
        const string keyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!;

        if (snapshot.HasProxyEnable)
        {
            key.SetValue("ProxyEnable", snapshot.ProxyEnable, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("ProxyEnable", throwOnMissingValue: false);
        }

        if (snapshot.HasProxyServer)
        {
            key.SetValue("ProxyServer", snapshot.ProxyServer ?? string.Empty, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }

        if (snapshot.HasAutoConfigUrl)
        {
            key.SetValue("AutoConfigURL", snapshot.AutoConfigUrl ?? string.Empty, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        }

        RefreshWinInetSettings();
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
