using System.Windows;
using Serilog;

namespace SSHClient.App.Bootstrap;

public static class GlobalExceptionHooks
{
    public static void Register(Application application)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            StartupProbe.Log($"[应用域未处理异常] {args.ExceptionObject}");
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "应用域致命异常");
            }
        };

        application.DispatcherUnhandledException += (_, args) =>
        {
            StartupProbe.Log($"[UI线程未处理异常] {args.Exception}");
            Log.Error(args.Exception, "UI 线程未处理异常");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            StartupProbe.Log($"[任务未观察异常] {args.Exception}");
            Log.Error(args.Exception, "任务未观察到异常");
            args.SetObserved();
        };
    }
}
