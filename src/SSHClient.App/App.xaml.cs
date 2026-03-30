using System.Windows;
using Microsoft.Extensions.Hosting;
using Serilog;
using SSHClient.App.Bootstrap;

namespace SSHClient.App;

/// <summary>
/// Application bootstrapper with a DI host. Supports a "--minimal" fallback for smoke testing.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupProbe.Log("应用启动开始");

        var useMinimal = e.Args.Any(arg => string.Equals(arg, "--minimal", StringComparison.OrdinalIgnoreCase));
        var useDiag = e.Args.Any(arg => string.Equals(arg, "--diag", StringComparison.OrdinalIgnoreCase));
        if (useMinimal)
        {
            StartupProbe.Log("以 --minimal 模式运行");
            ShowSmokeTestWindow();
            return;
        }

        try
        {
            Log.Information("应用启动已初始化");
            GlobalExceptionHooks.Register(this);

            _host = AppHostFactory.Build(e.Args);
            StartupProbe.Log("正在启动宿主...");
            _host.Start();
            StartupProbe.Log("宿主已启动");

            if (useDiag)
            {
                Log.Information("诊断模式已启用（--diag）");
            }

            AppRuntime.ShowMainWindow(_host.Services);
        }
        catch (Exception ex)
        {
            StartupProbe.Log($"启动异常: {ex}");
            Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
            Log.Fatal(ex, "应用启动失败");
            MessageBox.Show($"启动失败：{ex.Message}", "SSH Client", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static void ShowSmokeTestWindow()
    {
        var w = new Window
        {
            Title = "冒烟测试窗口",
            Width = 800,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "冒烟测试 - 窗口已加载",
                FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        w.Show();
        w.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        if (_host is null)
        {
            return;
        }

        StartupProbe.Log("应用退出开始");

        _ = Task.Run(async () =>
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                await AppRuntime.StopBackgroundServicesAsync(_host.Services, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StartupProbe.Log($"应用退出清理异常: {ex}");
            }
            finally
            {
                _host.Dispose();
                StartupProbe.Log("应用退出清理完成");
            }
        });
    }
}
