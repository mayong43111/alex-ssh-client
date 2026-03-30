namespace SSHClient.Core.Proxy;

/// <summary>
/// 包裹 NetworkStream，统计实际传输的字节数并周期上报给 ITrafficMonitor。
/// 注意：只统计写入方向（下行 = remote 写入 client，上行 = client 写入 remote）。
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onWrite;

    private long _written;

    public CountingStream(Stream inner, Action<long> onWrite)
    {
        _inner = inner;
        _onWrite = onWrite;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _inner.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => _inner.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        AddBytes(count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await _inner.WriteAsync(buffer, offset, count, ct);
        AddBytes(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _inner.WriteAsync(buffer, ct);
        AddBytes(buffer.Length);
    }

    private void AddBytes(int count)
    {
        var total = Interlocked.Add(ref _written, count);
        _onWrite(total);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
