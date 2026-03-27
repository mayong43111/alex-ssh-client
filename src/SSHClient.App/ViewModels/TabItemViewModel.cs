using CommunityToolkit.Mvvm.ComponentModel;

namespace SSHClient.App.ViewModels;

public partial class TabItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private object? _content; // Could be another VM or a simple string placeholder.

    [ObservableProperty]
    private string _icon = string.Empty; // emoji or glyph
}
