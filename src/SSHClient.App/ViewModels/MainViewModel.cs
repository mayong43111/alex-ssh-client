using CommunityToolkit.Mvvm.ComponentModel;
using SSHClient.App.Logging;

namespace SSHClient.App.ViewModels;

using CommunityToolkit.Mvvm.Input;

public partial class MainViewModel : ObservableObject
{
    private readonly IUiLogService _uiLogService;

    public ProfilesViewModel ProfilesVM { get; }
    public RulesViewModel RulesVM { get; }
    public ConnectionsViewModel ConnectionsVM { get; }
    public DashboardViewModel DashboardVM { get; }

    [ObservableProperty]
    private string _liveLog = string.Empty;

    public MainViewModel(DashboardViewModel dashboard,
                         ProfilesViewModel profiles,
                         RulesViewModel rules,
                         ConnectionsViewModel connections,
                         IUiLogService uiLogService)
    {
        DashboardVM = dashboard;
        ProfilesVM = profiles;
        RulesVM = rules;
        ConnectionsVM = connections;
        _uiLogService = uiLogService;

        LiveLog = _uiLogService.GetSnapshot();
        _uiLogService.SnapshotChanged += OnSnapshotChanged;
    }

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

    // Commands for dialogs
    [RelayCommand]
    private void ShowRulesDialog()
    {
        var window = new RulesWindow { DataContext = RulesVM };
        window.Owner = App.Current.MainWindow;
        window.ShowDialog();
    }

    [RelayCommand]
    private void ShowPortBindingsDialog()
    {
        var window = new PortBindingsWindow { DataContext = DashboardVM };
        window.Owner = App.Current.MainWindow;
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenLogs()
    {
        // reuse DashboardVM open config/logs if needed
    }

    [RelayCommand]
    private void ClearLog()
    {
        _uiLogService.Clear();
    }

    [RelayCommand]
    private void SwitchProfile()
    {
        if (ProfilesVM.Profiles.Count == 0)
        {
            return;
        }

        if (ProfilesVM.SelectedProfile is null)
        {
            ProfilesVM.SelectedProfile = ProfilesVM.Profiles[0];
            return;
        }

        var index = ProfilesVM.Profiles.IndexOf(ProfilesVM.SelectedProfile);
        if (index < 0 || index >= ProfilesVM.Profiles.Count - 1)
        {
            ProfilesVM.SelectedProfile = ProfilesVM.Profiles[0];
            return;
        }

        ProfilesVM.SelectedProfile = ProfilesVM.Profiles[index + 1];
    }
}
