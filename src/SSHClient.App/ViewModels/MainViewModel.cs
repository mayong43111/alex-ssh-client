using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SSHClient.App.Logging;
using System.Reflection;
using System.Windows.Threading;

namespace SSHClient.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IUiLogService _uiLogService;
    private readonly DispatcherTimer _logClearTimer;

    public ProfilesViewModel ProfilesVM { get; }
    public MonitorViewModel MonitorVM { get; }

    [ObservableProperty]
    private string _liveLog = string.Empty;

    public string AppVersion { get; } = ResolveAppVersion();

    public MainViewModel(ProfilesViewModel profiles,
                         MonitorViewModel monitorVm,
                         IUiLogService uiLogService)
    {
        ProfilesVM = profiles;
        MonitorVM = monitorVm;
        _uiLogService = uiLogService;

        LiveLog = _uiLogService.GetSnapshot();
        _uiLogService.SnapshotChanged += OnSnapshotChanged;

        _logClearTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _logClearTimer.Tick += OnLogClearTimerTick;
        _logClearTimer.Start();
    }

    [RelayCommand]
    private void ClearLog() => _uiLogService.Clear();

    public void Dispose()
    {
        _logClearTimer.Stop();
        _logClearTimer.Tick -= OnLogClearTimerTick;
        _uiLogService.SnapshotChanged -= OnSnapshotChanged;
    }

    private void OnLogClearTimerTick(object? sender, EventArgs e) => _uiLogService.Clear();

    private void OnSnapshotChanged(object? sender, string snapshot)
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            LiveLog = snapshot;
            return;
        }

        _ = dispatcher.InvokeAsync(() => LiveLog = snapshot);
    }

    private static string ResolveAppVersion()
    {
        var assembly = typeof(MainViewModel).Assembly;
        var infoVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return infoVersion;
        }

        return "v.0.0.1";
    }
}
