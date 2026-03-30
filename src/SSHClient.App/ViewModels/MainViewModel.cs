using CommunityToolkit.Mvvm.ComponentModel;
using SSHClient.App.Logging;

namespace SSHClient.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IUiLogService _uiLogService;

    public ProfilesViewModel ProfilesVM { get; }

    [ObservableProperty]
    private string _liveLog = string.Empty;

    public MainViewModel(ProfilesViewModel profiles,
                         IUiLogService uiLogService)
    {
        ProfilesVM = profiles;
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
}
