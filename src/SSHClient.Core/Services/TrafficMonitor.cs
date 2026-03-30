using System.Collections.Concurrent;
using SSHClient.Core.Models;

namespace SSHClient.Core.Services;

/// <summary>
/// 线程安全的流量监控实现，内置 1 秒定时采样。
/// </summary>
public sealed class TrafficMonitor : ITrafficMonitor, IDisposable
{
    private readonly ConcurrentDictionary<string, ConnectionEntry> _entries = new();
    private readonly object _historyLock = new();
    private readonly Queue<BandwidthPoint> _history = new();
    private readonly int _maxHistory;
    private readonly Timer _timer;

    public event EventHandler? Refreshed;

    public TrafficMonitor(int maxHistory = 60)
    {
        _maxHistory = maxHistory;
        _timer = new Timer(Tick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public string RegisterConnection(string protocol, string host, int port, RuleAction routeAction)
    {
        var id = Guid.NewGuid().ToString("N");
        var entry = new ConnectionEntry(id, protocol, host, port, routeAction);
        _entries[id] = entry;
        return id;
    }

    public void ReportBytes(string connectionId, long upBytes, long downBytes)
    {
        if (_entries.TryGetValue(connectionId, out var entry))
        {
            Interlocked.Exchange(ref entry.TotalUp, upBytes);
            Interlocked.Exchange(ref entry.TotalDown, downBytes);
        }
    }

    public void CompleteConnection(string connectionId)
    {
        if (_entries.TryGetValue(connectionId, out var entry))
        {
            entry.DisconnectedAt = DateTime.Now;
        }
    }

    public void Clear()
    {
        _entries.Clear();
        lock (_historyLock)
        {
            _history.Clear();
        }

        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ConnectionSnapshot> GetConnections()
    {
        var now = DateTime.Now;
        return _entries.Values
            .OrderByDescending(e => e.ConnectedAt)
            .Take(200)
            .Select(e => new ConnectionSnapshot
            {
                Id = e.Id,
                Host = e.Host,
                Port = e.Port,
                Protocol = e.Protocol,
                RouteAction = e.RouteAction,
                ConnectedAt = e.ConnectedAt,
                DisconnectedAt = e.DisconnectedAt,
                TotalUpBytes = Interlocked.Read(ref e.TotalUp),
                TotalDownBytes = Interlocked.Read(ref e.TotalDown),
                UpBytesPerSecond = e.CurrentUpRate,
                DownBytesPerSecond = e.CurrentDownRate,
            })
            .ToList();
    }

    public IReadOnlyList<BandwidthPoint> GetBandwidthHistory(int maxPoints = 60)
    {
        lock (_historyLock)
        {
            return _history.TakeLast(maxPoints).ToList();
        }
    }

    private void Tick(object? _)
    {
        double totalUp = 0;
        double totalDown = 0;

        foreach (var entry in _entries.Values)
        {
            var up = Interlocked.Read(ref entry.TotalUp);
            var down = Interlocked.Read(ref entry.TotalDown);

            var upRate = Math.Max(0, up - entry.LastSampledUp);
            var downRate = Math.Max(0, down - entry.LastSampledDown);

            entry.LastSampledUp = up;
            entry.LastSampledDown = down;
            entry.CurrentUpRate = upRate;
            entry.CurrentDownRate = downRate;

            totalUp += upRate;
            totalDown += downRate;
        }

        // 清理 2 分钟前已断开的连接
        var cutoff = DateTime.Now.AddMinutes(-2);
        var toRemove = _entries
            .Where(kv => kv.Value.DisconnectedAt.HasValue && kv.Value.DisconnectedAt.Value < cutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in toRemove)
        {
            _entries.TryRemove(key, out var _discarded);
            _ = _discarded;
        }

        // 硬上限：单表记录最多 1000 条（居4 高量连接场景）
        const int maxEntries = 1000;
        if (_entries.Count > maxEntries)
        {
            var overflow = _entries.Values
                .Where(e => e.DisconnectedAt.HasValue)
                .OrderBy(e => e.DisconnectedAt)
                .Take(_entries.Count - maxEntries)
                .Select(e => e.Id)
                .ToList();
            foreach (var id in overflow)
            {
                _entries.TryRemove(id, out var _d);
                _ = _d;
            }
        }

        lock (_historyLock)
        {
            _history.Enqueue(new BandwidthPoint
            {
                Timestamp = DateTime.Now,
                UpBytesPerSecond = totalUp,
                DownBytesPerSecond = totalDown,
            });
            while (_history.Count > _maxHistory)
            {
                _history.Dequeue();
            }
        }

        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _timer.Dispose();

    internal sealed class ConnectionEntry(string id, string protocol, string host, int port, RuleAction routeAction)
    {
        public string Id { get; } = id;
        public string Protocol { get; } = protocol;
        public string Host { get; } = host;
        public int Port { get; } = port;
        public RuleAction RouteAction { get; } = routeAction;
        public DateTime ConnectedAt { get; } = DateTime.Now;
        public DateTime? DisconnectedAt { get; set; }
        public long TotalUp;
        public long TotalDown;
        public long LastSampledUp;
        public long LastSampledDown;
        public double CurrentUpRate { get; set; }
        public double CurrentDownRate { get; set; }
    }
}
