using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace SSHClient.App.Services;

public interface ITrayBehaviorService : IDisposable
{
    bool IsTaskbarModeSelected { get; }
    bool IsTrayModeSelected { get; }

    Task AttachAsync(Window window, CancellationToken cancellationToken = default);
    Task HandleWindowStateChangedAsync(CancellationToken cancellationToken = default);
    Task SetTaskbarModeAsync(CancellationToken cancellationToken = default);
    Task SetTrayModeAsync(CancellationToken cancellationToken = default);
}

public sealed class TrayBehaviorService : ITrayBehaviorService
{
    private readonly IMinimizePreferenceService _preferenceService;
    private readonly Forms.NotifyIcon _trayIcon;

    private Window? _window;
    private bool _trayHintShown;
    private bool? _minimizeToTray;
    private bool _disposed;

    public bool IsTaskbarModeSelected => _minimizeToTray == false;
    public bool IsTrayModeSelected => _minimizeToTray == true;

    public TrayBehaviorService(IMinimizePreferenceService preferenceService)
    {
        _preferenceService = preferenceService;
        _trayIcon = CreateTrayIcon();
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.ContextMenuStrip = BuildTrayContextMenu();
    }

    public async Task AttachAsync(Window window, CancellationToken cancellationToken = default)
    {
        _window = window;
        _minimizeToTray = await _preferenceService.GetMinimizeToTrayPreferenceAsync(cancellationToken);
    }

    public async Task HandleWindowStateChangedAsync(CancellationToken cancellationToken = default)
    {
        if (_window is null || _window.WindowState != WindowState.Minimized)
        {
            return;
        }

        var behavior = _minimizeToTray;
        if (!behavior.HasValue)
        {
            var result = MessageBox.Show(
                _window,
                "首次最小化请选择行为：\n是：最小化到托盘（后台运行）\n否：仅最小化到任务栏\n取消：本次仅最小化，稍后再选\n\n后续可通过右上角齿轮图标修改。",
                "最小化行为",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                behavior = true;
                await _preferenceService.SetMinimizeToTrayPreferenceAsync(true, cancellationToken);
                _minimizeToTray = true;
            }
            else if (result == MessageBoxResult.No)
            {
                behavior = false;
                await _preferenceService.SetMinimizeToTrayPreferenceAsync(false, cancellationToken);
                _minimizeToTray = false;
            }
            else
            {
                behavior = false;
            }
        }

        if (behavior == true)
        {
            MinimizeToTray();
        }
    }

    public async Task SetTaskbarModeAsync(CancellationToken cancellationToken = default)
    {
        _minimizeToTray = false;
        await _preferenceService.SetMinimizeToTrayPreferenceAsync(false, cancellationToken);
        RestoreFromTray();
    }

    public async Task SetTrayModeAsync(CancellationToken cancellationToken = default)
    {
        _minimizeToTray = true;
        await _preferenceService.SetMinimizeToTrayPreferenceAsync(true, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private static Forms.NotifyIcon CreateTrayIcon()
    {
        Icon icon;
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            icon = Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
        }
        else
        {
            icon = SystemIcons.Application;
        }

        return new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "SSH 客户端",
            Visible = false,
        };
    }

    private Forms.ContextMenuStrip BuildTrayContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add("退出", null, (_, _) => _window?.Dispatcher.Invoke(() => _window.Close()));
        return menu;
    }

    private void MinimizeToTray()
    {
        if (_window is null)
        {
            return;
        }

        _window.ShowInTaskbar = false;
        _window.Hide();

        _trayIcon.Visible = true;
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _trayIcon.BalloonTipTitle = "SSH 客户端";
        _trayIcon.BalloonTipText = "程序已最小化到托盘，双击图标可恢复窗口。";
        _trayIcon.ShowBalloonTip(1500);
    }

    private void RestoreFromTray()
    {
        if (_window is null)
        {
            return;
        }

        if (!_window.Dispatcher.CheckAccess())
        {
            _ = _window.Dispatcher.InvokeAsync(RestoreFromTray);
            return;
        }

        _window.Show();
        _window.ShowInTaskbar = true;
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _trayIcon.Visible = false;
    }
}
