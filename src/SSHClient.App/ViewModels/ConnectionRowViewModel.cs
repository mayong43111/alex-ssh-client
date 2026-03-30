using CommunityToolkit.Mvvm.ComponentModel;
using SSHClient.App.Models;
using SSHClient.Core.Models;
using SSHClient.Core.Services;

namespace SSHClient.App.ViewModels;

/// <summary>单条连接在列表中展示的 ViewModel 行。</summary>
public sealed partial class ConnectionRowViewModel : ObservableObject
{
    [ObservableProperty] private string _upRate = "0 B/s";
    [ObservableProperty] private string _downRate = "0 B/s";

    public string Id { get; }
    public string Host { get; }
    public int Port { get; }
    public string ConnectedAt { get; }
    public string RouteColor { get; }
    public string RouteLabel { get; }

    public string HostPort => $"{Host}:{Port}";

    public ConnectionRowViewModel(ConnectionSnapshot snap)
    {
        Id = snap.Id;
        Host = snap.Host;
        Port = snap.Port;
        ConnectedAt = snap.ConnectedAt.ToString("HH:mm:ss");
        (RouteColor, RouteLabel) = snap.RouteAction switch
        {
            RuleAction.Direct => ("#22C55E", "直连"),
            RuleAction.Reject => ("#EF4444", "拒绝"),
            _                 => ("#3B82F6", "代理"),
        };
        Update(snap);
    }

    public void Update(ConnectionSnapshot snap)
    {
        UpRate = ByteRateFormatter.Format(snap.UpBytesPerSecond);
        DownRate = ByteRateFormatter.Format(snap.DownBytesPerSecond);
    }
}
