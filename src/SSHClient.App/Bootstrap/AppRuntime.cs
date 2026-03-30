using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using Serilog;
using SSHClient.App.Services;
using WpfApplication = System.Windows.Application;

namespace SSHClient.App.Bootstrap;

public static class AppRuntime
{
    public static void ShowMainWindow(IServiceProvider services)
    {
        var mainWindow = services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = services.GetRequiredService<ViewModels.MainViewModel>();

        if (WpfApplication.Current is not null)
        {
            WpfApplication.Current.MainWindow = mainWindow;
            mainWindow.Closed += (_, __) =>
            {
                StartupProbe.Log("主窗口关闭事件已触发");

                if (WpfApplication.Current is { } app)
                {
                    app.Shutdown();
                }

                // Final fallback: if shutdown flow hangs, force process exit.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    StartupProbe.Log("退出看门狗触发，强制 Environment.Exit(0)");
                    Environment.Exit(0);
                });
            };
        }

        mainWindow.Show();
        mainWindow.Activate();
        Log.Information("主窗口已显示");
    }

    public static async Task StartBackgroundServicesAsync(IServiceProvider services)
    {
        try
        {
            var proxyHost = services.GetService<ProxyHost>();
            if (proxyHost is not null)
            {
                await proxyHost.StartAsync();
            }
        }
        catch (Exception ex)
        {
            StartupProbe.Log($"代理宿主启动失败: {ex}");
            Log.Error(ex, "启动代理宿主失败");
        }
    }

    public static async Task StopBackgroundServicesAsync(IServiceProvider services, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var proxyHost = services.GetService<ProxyHost>();
            if (proxyHost is not null)
            {
                await proxyHost.StopAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            StartupProbe.Log($"停止后台服务异常: {ex}");
            Log.Error(ex, "停止服务失败");
        }
    }
}
