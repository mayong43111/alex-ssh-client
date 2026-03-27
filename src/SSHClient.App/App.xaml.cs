using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using SSHClient.App.Logging;
using SSHClient.App.Services;
using SSHClient.App.ViewModels;
using SSHClient.Core.Proxy;
using SSHClient.Core.Services;

namespace SSHClient.App;

/// <summary>
/// Application bootstrapper with a DI host. Supports a "--minimal" fallback for smoke testing.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private bool _useMinimal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupProbe.Log("OnStartup begin");

        _useMinimal = e.Args.Any(arg => string.Equals(arg, "--minimal", StringComparison.OrdinalIgnoreCase));
        if (_useMinimal)
        {
            StartupProbe.Log("Running in --minimal mode");
            ShowSmokeTestWindow();
            return;
        }

        try
        {
            Log.Information("Application startup initiated");

            // Global exception handlers for diagnostics
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                StartupProbe.Log($"[DomainUnhandled] {args.ExceptionObject}");
                if (args.ExceptionObject is Exception ex)
                {
                    Log.Fatal(ex, "Fatal domain exception");
                }
            };
            DispatcherUnhandledException += (_, args) =>
            {
                StartupProbe.Log($"[DispatcherUnhandled] {args.Exception}");
                Log.Error(args.Exception, "Dispatcher unhandled exception");
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                StartupProbe.Log($"[TaskUnhandled] {args.Exception}");
                Log.Error(args.Exception, "Task unobserved exception");
            };

            _host = BuildHost(e.Args);
            StartupProbe.Log("Starting host...");
            _host.Start();
            StartupProbe.Log("Host started");

            // Resolve MainWindow + VM from DI
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = _host.Services.GetRequiredService<MainViewModel>();
            mainWindow.Show();
            mainWindow.Activate();
            Log.Information("Main window shown");

            // Optionally start proxy listeners in background
            _ = StartProxyHostAsync(_host.Services);

            // Hook Application.Exit to ensure background stop
            Exit += async (_, __) => await StopServicesAsync();
        }
        catch (Exception ex)
        {
            StartupProbe.Log($"Startup exception: {ex}");
            Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
            Log.Fatal(ex, "Failed to start application");
            MessageBox.Show($"Startup failed: {ex.Message}", "SSH Client", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static async Task StartProxyHostAsync(IServiceProvider services)
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
            StartupProbe.Log($"ProxyHost start failed: {ex}");
            Log.Error(ex, "Failed to start proxy host");
        }
    }

    private async Task StopServicesAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var proxyHost = _host?.Services.GetService<ProxyHost>();
            if (proxyHost is not null)
            {
                await proxyHost.StopAsync(cts.Token);
            }
        }
        catch (Exception ex)
        {
            StartupProbe.Log($"StopServicesAsync exception: {ex}");
            Log.Error(ex, "Stop services failed");
        }
    }

    private static IHost BuildHost(string[] args)
    {
        var uiLogService = new RollingUiLogService(maxEntries: 1000);

        var builder = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                // Ensure our appsettings.json (with SSHClient section) is loaded
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .UseSerilog((ctx, services, serilogConfig) =>
            {
                var section = ctx.Configuration.GetSection("SSHClient:Logging");
                var minLevel = ParseLogLevel(section["MinimumLevel"] ?? "Information");
                var logPath = section["LogPath"];

                serilogConfig
                    .MinimumLevel.Is(minLevel)
                    .Enrich.FromLogContext()
                    .WriteTo.Console()
                    .WriteTo.Sink(new UiLogSink(uiLogService));

                if (!string.IsNullOrWhiteSpace(logPath))
                {
                    serilogConfig.WriteTo.File(logPath, rollingInterval: RollingInterval.Day);
                }
            })
            .ConfigureServices((ctx, services) =>
            {
                // Core services
                services.AddSingleton<IConfigService>(_ => new FileConfigService());
                services.AddSingleton<ISshTunnelService, SshTunnelService>();
                services.AddSingleton<Func<ISshTunnelService>>(sp => () => sp.GetRequiredService<ISshTunnelService>());
                services.AddSingleton<IProxyConnector, SshProxyConnector>();
                services.AddSingleton<IProxyManager, ProxyManager>();
                services.AddSingleton<IRuleEngine, RuleEngine>();
                services.AddSingleton<ProxyHost>();
                services.AddSingleton<ISystemProxyService, SystemProxyService>();
                services.AddSingleton<IUiLogService>(uiLogService);

                // ViewModels
                services.AddSingleton<DashboardViewModel>();
                // converters (for design-time/DI convenience)
                services.AddSingleton<Converters.NullToVisibilityConverter>();
                services.AddSingleton<ProfilesViewModel>();
                services.AddSingleton<RulesViewModel>();
                services.AddSingleton<ConnectionsViewModel>();
                services.AddSingleton<MainViewModel>();

                // Views / dialogs
                services.AddSingleton<MainWindow>();
                services.AddTransient<RulesWindow>();
                services.AddTransient<PortBindingsWindow>();
            });

        return builder.Build();
    }

    private static LogEventLevel ParseLogLevel(string level)
    {
        return Enum.TryParse<LogEventLevel>(level, true, out var result)
            ? result
            : LogEventLevel.Information;
    }

    private static void ShowSmokeTestWindow()
    {
        var w = new Window
        {
            Title = "Smoke Test Window",
            Width = 800,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "Smoke Test - Window Loaded",
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
        if (_host is not null)
        {
            try
            {
                // Request stop but with a short timeout to avoid hang
                _host.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                StartupProbe.Log($"OnExit StopAsync exception: {ex}");
            }
            finally
            {
                // Ensure async disposables are disposed in a fire-and-forget fashion to avoid blocking UI thread
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var proxyHost = _host.Services.GetService<ProxyHost>();
                        if (proxyHost is not null)
                        {
                            await proxyHost.StopAsync(cts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        StartupProbe.Log($"ProxyHost StopAsync exception: {ex}");
                    }
                    finally
                    {
                        _host.Dispose();
                    }
                });
            }
        }
    }
}
