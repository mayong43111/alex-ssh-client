using System.Text;

namespace SSHClient.App.Logging;

public sealed class RollingUiLogService : IUiLogService
{
    private readonly object _sync = new();
    private readonly Queue<string> _entries = new();
    private readonly int _maxEntries;

    public event EventHandler<string>? SnapshotChanged;

    public RollingUiLogService(int maxEntries = 1000)
    {
        _maxEntries = maxEntries > 0 ? maxEntries : 1000;
    }

    public string GetSnapshot()
    {
        lock (_sync)
        {
            return BuildSnapshotUnsafe();
        }
    }

    public void Append(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return;
        }

        string snapshot;
        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _maxEntries)
            {
                _entries.Dequeue();
            }

            snapshot = BuildSnapshotUnsafe();
        }

        SnapshotChanged?.Invoke(this, snapshot);
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }

        SnapshotChanged?.Invoke(this, string.Empty);
    }

    private string BuildSnapshotUnsafe()
    {
        var sb = new StringBuilder();
        foreach (var line in _entries)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append(line);
        }

        return sb.ToString();
    }
}
