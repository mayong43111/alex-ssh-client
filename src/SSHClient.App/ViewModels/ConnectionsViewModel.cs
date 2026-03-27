using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SSHClient.Core.Services;

namespace SSHClient.App.ViewModels;

public partial class ConnectionsViewModel : ObservableObject
{
    public record ConnectionItem(string ProfileName, DateTime ConnectedAt, string Endpoint);

    private readonly IProxyManager _proxyManager;

    public ObservableCollection<ConnectionItem> ConnectionItems { get; } = new();

    public ConnectionsViewModel(IProxyManager proxyManager)
    {
        _proxyManager = proxyManager;
    }

    [RelayCommand]
    private async Task ConnectAsync(string profileName)
    {
        if (await _proxyManager.ConnectAsync(profileName))
        {
            if (!ConnectionItems.Any(x => x.ProfileName == profileName))
            {
                ConnectionItems.Add(new ConnectionItem(profileName, DateTime.Now, "127.0.0.1"));
            }
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync(object? itemObj)
    {
        if (itemObj is ConnectionItem item)
        {
            await _proxyManager.DisconnectAsync(item.ProfileName);
            _ = ConnectionItems.Remove(item);
        }
        else if (itemObj is string profileName)
        {
            await _proxyManager.DisconnectAsync(profileName);
            var existing = ConnectionItems.FirstOrDefault(x => x.ProfileName == profileName);
            if (existing is not null) ConnectionItems.Remove(existing);
        }
    }
}
