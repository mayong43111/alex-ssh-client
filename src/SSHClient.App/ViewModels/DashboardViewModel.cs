using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SSHClient.App.Services;
using SSHClient.Core.Services;

namespace SSHClient.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly ProxyHost? _proxyHost;
    private readonly IConfigService _configService;

    [ObservableProperty]
    private string _status = "Unknown";

    [ObservableProperty]
    private int _httpPort;

    [ObservableProperty]
    private int _socksPort;

    [ObservableProperty]
    private bool _systemProxyEnabled;

    [ObservableProperty]
    private bool _isSystemProxyToggleLocked = true;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private Brush _statusColor = Brushes.Gray;

    public string HttpEndpoint => $"127.0.0.1:{HttpPort}";
    public string SocksEndpoint => $"127.0.0.1:{SocksPort}";

    public DashboardViewModel(ProxyHost? proxyHost, IConfigService configService)
    {
        _proxyHost = proxyHost;
        _configService = configService;
        _ = LoadAsync();
    }

    [ObservableProperty]
    private List<(int HttpPort, int SocksPort)> _portBindings = new();
    private async Task LoadAsync()
    {
        try
        {
            var settings = await _configService.LoadAsync();
            HttpPort = settings.Proxy.HttpPort;
            SocksPort = settings.Proxy.SocksPort;
            SystemProxyEnabled = settings.Proxy.ToggleSystemProxy;
            IsSystemProxyToggleLocked = !settings.Proxy.ToggleSystemProxy; // UI hint only
            Status = settings.Proxy.EnableOnStartup ? "Running" : "Stopped";
            StatusColor = settings.Proxy.EnableOnStartup ? Brushes.LimeGreen : Brushes.Gray;
            // simplistic port bindings list (single entry for now)
            PortBindings = new List<(int HttpPort, int SocksPort)> { (HttpPort, SocksPort) };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "Error";
            StatusColor = Brushes.OrangeRed;
        }
    }

    [RelayCommand]
    private async Task StartProxiesAsync()
    {
        if (_proxyHost is null) return;
        try
        {
            await _proxyHost.StartAsync();
            Status = "Running";
            StatusColor = Brushes.LimeGreen;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "Error";
            StatusColor = Brushes.OrangeRed;
        }
    }

    [RelayCommand]
    private async Task StopProxiesAsync()
    {
        if (_proxyHost is null) return;
        try
        {
            await _proxyHost.StopAsync();
            Status = "Stopped";
            StatusColor = Brushes.Gray;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = "Error";
            StatusColor = Brushes.OrangeRed;
        }
    }

    [RelayCommand]
    private void OpenConfig()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (System.IO.File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SavePortsAsync()
    {
        var settings = await _configService.LoadAsync();
        settings.Proxy.HttpPort = HttpPort;
        settings.Proxy.SocksPort = SocksPort;
        await _configService.SaveAsync(settings);
    }
}
