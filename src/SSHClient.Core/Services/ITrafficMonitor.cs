using SSHClient.Core.Models;

namespace SSHClient.Core.Services;

/// <summary>
/// 单条连接的实时流量快照。
/// </summary>
public sealed class ConnectionSnapshot
{
    public required string Id { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Protocol { get; init; }
    public required DateTime ConnectedAt { get; init; }
    public required RuleAction RouteAction { get; init; }
    public DateTime? DisconnectedAt { get; set; }
    public long TotalUpBytes { get; set; }
    public long TotalDownBytes { get; set; }
    /// <summary>上行速率 bytes/s（最近一次采样）</summary>
    public double UpBytesPerSecond { get; set; }
    /// <summary>下行速率 bytes/s（最近一次采样）</summary>
    public double DownBytesPerSecond { get; set; }
    public bool IsActive => DisconnectedAt is null;
}

/// <summary>
/// 全局带宽时间序列的一个点（用于折线图）。
/// </summary>
public sealed class BandwidthPoint
{
    public DateTime Timestamp { get; init; }
    public double UpBytesPerSecond { get; init; }
    public double DownBytesPerSecond { get; init; }
}

public interface ITrafficMonitor
{
    /// <summary>注册一条新连接，返回不透明的连接 ID。</summary>
    string RegisterConnection(string protocol, string host, int port, RuleAction routeAction);

    /// <summary>报告该连接已传输的累计字节数（由 CountingStream 调用）。</summary>
    void ReportBytes(string connectionId, long upBytes, long downBytes);

    /// <summary>标记连接已断开。</summary>
    void CompleteConnection(string connectionId);

    /// <summary>获取当前活跃连接快照列表（线程安全副本）。</summary>
    IReadOnlyList<ConnectionSnapshot> GetConnections();

    /// <summary>获取最近 N 个带宽采样点（用于折线图）。</summary>
    IReadOnlyList<BandwidthPoint> GetBandwidthHistory(int maxPoints = 60);

    /// <summary>当带宽统计刷新时触发（约每秒一次）。</summary>
    event EventHandler? Refreshed;
}
