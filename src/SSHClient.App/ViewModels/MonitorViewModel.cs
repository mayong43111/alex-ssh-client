using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SSHClient.App.Models;
using SSHClient.Core.Services;

namespace SSHClient.App.ViewModels;

public sealed partial class MonitorViewModel : ObservableObject
{
    private readonly ITrafficMonitor _monitor;

    // ── 折线图数据 ──────────────────────────────────────────────
    /// <summary>上行速率历史点（用于折线图控件绑定）</summary>
    public ObservableCollection<double> UpHistory { get; } = new();
    /// <summary>下行速率历史点</summary>
    public ObservableCollection<double> DownHistory { get; } = new();

    [ObservableProperty] private string _currentUp = "0 B/s";
    [ObservableProperty] private string _currentDown = "0 B/s";

    // ── 连接列表 ────────────────────────────────────────────────
    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = new();

    public MonitorViewModel(ITrafficMonitor monitor)
    {
        _monitor = monitor;
        _monitor.Refreshed += OnRefreshed;
    }

    private void OnRefreshed(object? sender, EventArgs e)
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Refresh();
            return;
        }
        _ = dispatcher.InvokeAsync(Refresh);
    }

    private void Refresh()
    {
        // 折线图历史
        var history = _monitor.GetBandwidthHistory(60);
        RebuildHistory(UpHistory, history, p => p.UpBytesPerSecond);
        RebuildHistory(DownHistory, history, p => p.DownBytesPerSecond);

        if (history.Count > 0)
        {
            var last = history[^1];
            CurrentUp = ByteRateFormatter.Format(last.UpBytesPerSecond);
            CurrentDown = ByteRateFormatter.Format(last.DownBytesPerSecond);
        }

        // 连接列表（合并更新，避免全量重建导致列表闪烁）
        var snapshots = _monitor.GetConnections();
        var snapMap = snapshots.ToDictionary(s => s.Id);

        // 移除已消失的行
        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            if (!snapMap.ContainsKey(Connections[i].Id))
                Connections.RemoveAt(i);
        }

        // 更新或追加
        var existingIds = Connections.Select(c => c.Id).ToHashSet();
        foreach (var snap in snapshots)
        {
            if (existingIds.Contains(snap.Id))
            {
                var row = Connections.First(c => c.Id == snap.Id);
                row.Update(snap);
            }
            else
            {
                Connections.Add(new ConnectionRowViewModel(snap));
            }
        }
    }

    private static void RebuildHistory(ObservableCollection<double> col,
        IReadOnlyList<BandwidthPoint> history,
        Func<BandwidthPoint, double> selector)
    {
        // 补齐到 60 个点，不足则前面填 0
        const int maxPoints = 60;
        var values = history.Select(selector).ToList();

        while (col.Count > maxPoints) col.RemoveAt(0);

        // 确保列表 = 60 个点
        if (col.Count < maxPoints)
        {
            var pad = maxPoints - col.Count - values.Count;
            for (int i = 0; i < pad; i++) col.Add(0);
        }

        foreach (var v in values)
        {
            if (col.Count >= maxPoints) col.RemoveAt(0);
            col.Add(v);
        }
    }
}
