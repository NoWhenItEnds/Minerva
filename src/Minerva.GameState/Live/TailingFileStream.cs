using System.Diagnostics;

namespace Minerva.GameState.Live;

/// <summary>
/// Read-only stream over a file that another process (the game) is still appending to.
/// </summary>
/// <remarks>
/// While <see cref="Follow"/> is <c>false</c> this behaves like an ordinary <see cref="FileStream"/>
/// (reads return 0 at end of file). While <c>true</c>, a read at end of file waits for more data instead,
/// so a parser sitting on top of it simply "blocks" until the game writes the next network update.
/// Following stops when the file has not grown for <see cref="IdleTimeout"/> (recording stopped, game closed),
/// at which point reads return 0 and the parser sees a normal end of stream.
/// </remarks>
public sealed class TailingFileStream : Stream
{
    private readonly FileStream _inner;
    private readonly CancellationToken _cancellation;

    public TailingFileStream(string path, CancellationToken cancellation = default)
    {
        // FileShare.ReadWrite | Delete: the game holds the file open for writing while we read it.
        _inner = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 16, FileOptions.SequentialScan);
        _cancellation = cancellation;
    }

    /// <summary>When true, reads at end of file wait for the file to grow.</summary>
    public bool Follow { get; set; }

    /// <summary>Stop following after the file has not grown for this long.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often to re-check the file while waiting at end of file.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(4);

    /// <summary>True once following ended because the file stopped growing.</summary>
    public bool EndedByIdle { get; private set; }

    /// <summary>True while the most recent read had to wait at end of file, i.e. we are fully caught up with the game.</summary>
    public bool IsCaughtUp { get; private set; }

    /// <summary>Bytes written by the game that we have not consumed yet.</summary>
    public long BytesBehind => Math.Max(0, _inner.Length - _inner.Position);

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        Stopwatch? idle = null;
        while (true)
        {
            var n = _inner.Read(buffer);
            if (n > 0)
            {
                if (idle == null) IsCaughtUp = false;
                return n;
            }
            if (!Follow) return 0;

            IsCaughtUp = true;
            idle ??= Stopwatch.StartNew();
            if (idle.Elapsed > IdleTimeout)
            {
                EndedByIdle = true;
                return 0;
            }
            _cancellation.ThrowIfCancellationRequested();
            Thread.Sleep(PollInterval);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        Stopwatch? idle = null;
        CancellationTokenSource? linked = null;
        try
        {
            while (true)
            {
                var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (n > 0)
                {
                    if (idle == null) IsCaughtUp = false;
                    return n;
                }
                if (!Follow) return 0;

                IsCaughtUp = true;
                idle ??= Stopwatch.StartNew();
                if (idle.Elapsed > IdleTimeout)
                {
                    EndedByIdle = true;
                    return 0;
                }
                linked ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation);
                await Task.Delay(PollInterval, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            linked?.Dispose();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
