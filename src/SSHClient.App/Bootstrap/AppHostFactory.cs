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

namespace SSHClient.App.Bootstrap;

public static class AppHostFactory
{
    public static IHost Build(string[] args)
    {
        var diagMode = args.Any(arg => string.Equals(arg, "--diag", StringComparison.OrdinalIgnoreCase));
        var uiLogService = new RollingUiLogService(maxEntries: 1000);

        return Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .UseSerilog((ctx, _, serilogConfig) =>
            {
                var section = ctx.Configuration.GetSection("SSHClient:Logging");
                var configuredMinLevel = ParseLogLevel(section["MinimumLevel"] ?? "Information");
                var minLevel = diagMode ? LogEventLevel.Debug : configuredMinLevel;
                var logPath = section["LogPath"];
                var consoleTemplate = diagMode
                    ? "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"
                    : "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

                serilogConfig
                    .MinimumLevel.Is(minLevel)
                    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .WriteTo.Console(outputTemplate: consoleTemplate)
                    .WriteTo.Sink(new UiLogSink(uiLogService));

                if (!string.IsNullOrWhiteSpace(logPath))
                {
                    serilogConfig.WriteTo.File(
                        logPath,
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} {Message:lj}{NewLine}");
                }
            })
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<IConfigService>(_ => new FileConfigService());
                services.AddSingleton<ISshTunnelService, SshTunnelService>();
                services.AddSingleton<Func<ISshTunnelService>>(sp => () => sp.GetRequiredService<ISshTunnelService>());
                services.AddSingleton<IProxyConnector, SshProxyConnector>();
                services.AddSingleton<IProxyManager, ProxyManager>();
                services.AddSingleton<ProxyHost>();
                services.AddSingleton<ISystemProxyService, SystemProxyService>();
                services.AddSingleton<IMinimizePreferenceService, MinimizePreferenceService>();
                services.AddSingleton<IProfileFileService, ProfileFileService>();
                services.AddSingleton<IRuleNormalizationService, RuleNormalizationService>();
                services.AddSingleton<ITrayBehaviorService, TrayBehaviorService>();
                services.AddSingleton<IProfileFileDialogService, ProfileFileDialogService>();
                services.AddSingleton<IMainWindowActionService, MainWindowActionService>();
                services.AddSingleton<IUiLogService>(uiLogService);
                services.AddSingleton<ITrafficMonitor, TrafficMonitor>();

                services.AddSingleton<ProfilesViewModel>();
                services.AddSingleton<MonitorViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    private static LogEventLevel ParseLogLevel(string level)
    {
        return Enum.TryParse<LogEventLevel>(level, true, out var result)
            ? result
            : LogEventLevel.Information;
    }
}
