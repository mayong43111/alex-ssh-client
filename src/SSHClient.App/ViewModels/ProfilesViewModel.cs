using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SSHClient.Core.Models;
using SSHClient.Core.Services;

namespace SSHClient.App.ViewModels;

using System.Collections.Generic;

public partial class ProfilesViewModel : ObservableObject
{
    private readonly IProxyManager _proxyManager;
    private readonly IConfigService _configService;

    public ObservableCollection<ProxyProfile> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private ProxyProfile? _selectedProfile;

    public ProfilesViewModel(IProxyManager proxyManager, IConfigService configService)
    {
        _proxyManager = proxyManager;
        _configService = configService;
        _ = RefreshAsync();
    }

    private static ProxyProfile CreateDefaultProfile()
    {
        return new ProxyProfile
        {
            Name = "Default",
            Host = "127.0.0.1",
            Username = "user",
            Port = 22,
            LocalSocksPort = 1080,
            AuthMethod = SshAuthMethod.Password,
        };
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var profiles = (await _proxyManager.GetProfilesAsync()).ToList();
        var createdDefault = false;
        if (profiles.Count == 0)
        {
            profiles.Add(CreateDefaultProfile());
            createdDefault = true;
        }

        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault();

        if (createdDefault)
        {
            await SaveAsync();
        }
    }

    [RelayCommand]
    public async Task AddProfileAsync()
    {
        var newProfile = new ProxyProfile
        {
            Name = $"Profile-{Profiles.Count + 1}",
            Host = "host",
            Username = "user",
            Port = 22,
            LocalSocksPort = 1080,
            AuthMethod = SshAuthMethod.Password,
        };
        Profiles.Add(newProfile);
        await SaveAsync();
    }

    [RelayCommand]
    public async Task SaveProfileAsync()
    {
        await SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    public async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null) return;
        Profiles.Remove(SelectedProfile);
        await SaveAsync();
        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    public async Task ConnectAsync()
    {
        if (SelectedProfile is null) return;
        await _proxyManager.ConnectAsync(SelectedProfile.Name);
    }

    public string? JumpHostsDisplay => SelectedProfile is null ? null : string.Join(",", SelectedProfile.JumpHosts ?? new List<string>());


    private bool CanOperateOnSelection() => SelectedProfile is not null;

    private async Task SaveAsync()
    {
        if (Profiles.Count == 0)
        {
            Profiles.Add(CreateDefaultProfile());
        }

        var settings = await _configService.LoadAsync();
        settings.Profiles.Clear();
        foreach (var p in Profiles)
        {
            settings.Profiles.Add(p);
        }
        await _configService.SaveAsync(settings);

        if (SelectedProfile is null)
        {
            SelectedProfile = Profiles.FirstOrDefault();
        }
    }
}
