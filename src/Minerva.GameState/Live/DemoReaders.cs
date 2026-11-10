using System.Reflection;
using DemoFile;

namespace Minerva.GameState.Live;

public enum LiveEndReason
{
    /// <summary>The game finished the recording (<c>stop</c>, disconnect, match end) and wrote a stop command.</summary>
    RecordingStopped,

    /// <summary>The file stopped growing for longer than the idle timeout without a stop command.</summary>
    Idle,

    Cancelled,
}

public sealed class LiveOptions
{
    /// <summary>Give up when the file has not grown for this long.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the game to write the demo's sign-on data before giving up.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Drives a <see cref="DeadlockDemoParser"/> from a finished demo file or from a demo the game is still recording.
/// Subscribe to the parser's events (e.g. <c>demo.EntityEvents</c>, <c>demo.Source1GameEvents</c>,
/// timers via <c>demo.CreateTimer</c>) before calling.
/// </summary>
public static class DemoReaders
{
    /// <summary>Parse a complete demo from start to end.</summary>
    public static async Task ReadFileAsync(DeadlockDemoParser demo, string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        var reader = DemoFileReader.Create(demo, fs);
        await reader.ReadAllAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Follow a demo that the game is currently recording (<c>record &lt;name&gt;</c> with <c>demo_flush 1</c>).
    /// Parses everything already written, then keeps parsing each network update as the game appends it.
    /// </summary>
    /// <param name="onStream">Called once the file is open, so callers can observe <see cref="TailingFileStream.IsCaughtUp"/> etc.</param>
    public static async Task<LiveEndReason> FollowAsync(
        DeadlockDemoParser demo,
        string path,
        LiveOptions? options = null,
        Action<TailingFileStream>? onStream = null,
        CancellationToken ct = default)
    {
        options ??= new LiveOptions();

        await WaitForSignonAsync(path, options.StartTimeout, ct).ConfigureAwait(false);

        await using var stream = new TailingFileStream(path, ct) { IdleTimeout = options.IdleTimeout, Follow = false };
        onStream?.Invoke(stream);
        var reader = DemoFileReader.Create(demo, stream);

        try
        {
            // Header + sign-on (string tables, send tables, class info) are already on disk; read them normally.
            await reader.StartReadingAsync(ct).ConfigureAwait(false);

            // A file still being written looks "incomplete" to DemoFile, which then refuses to read past the
            // last command that existed when we opened it. Lift that limit before we start following.
            IncompleteFileShim.Clear(reader);
            stream.Follow = true;

            while (await reader.MoveNextAsync(ct).ConfigureAwait(false))
            {
            }

            return LiveEndReason.RecordingStopped;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LiveEndReason.Cancelled;
        }
        catch (EndOfStreamException) when (stream.EndedByIdle)
        {
            return LiveEndReason.Idle;
        }
    }

    /// <summary>
    /// Wait until the game has written the demo header and all sign-on commands, i.e. the first command with a real
    /// tick. Before that point <see cref="DemoFileReader{T}.StartReadingAsync"/> would hit end of file.
    /// </summary>
    public static async Task WaitForSignonAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (HasSignon(path)) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"'{path}' did not receive demo sign-on data within {timeout.TotalSeconds:0}s.");
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private static bool HasSignon(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            if (length < 16) return false;
            Span<byte> magic = stackalloc byte[8];
            fs.ReadExactly(magic);
            if (!magic.SequenceEqual("PBDEMS2\0"u8)) throw new InvalidDataException($"'{path}' is not a Source 2 demo.");
            fs.Position = 16;

            while (true)
            {
                if (!TryReadUVarInt(fs, out _) || !TryReadUVarInt(fs, out var tick) || !TryReadUVarInt(fs, out var size))
                    return false;
                if (tick != uint.MaxValue) return true; // first command after pre-record
                if (fs.Position + size > length) return false; // sign-on command only partially written
                fs.Position += size;
            }
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryReadUVarInt(Stream s, out uint value)
    {
        value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = s.ReadByte();
            if (b < 0) return false;
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        throw new InvalidDataException("Malformed varint in demo command header.");
    }

    /// <summary>
    /// DemoFile.Net 0.44 marks a demo without a trailing file-info block as incomplete and stops at the last command
    /// it found while scanning at open time. For a live recording that is "now", so we reset it.
    /// TODO: upstream a public option for this (saul/demofile-net) and delete the reflection.
    /// </summary>
    private static class IncompleteFileShim
    {
        public static void Clear<T>(DemoFileReader<T> reader) where T : DemoParser<T>, new()
        {
            var type = reader.GetType();
            var incomplete = type.GetProperty(nameof(DemoFileReader<T>.IsIncompleteFile), BindingFlags.Public | BindingFlags.Instance)
                ?.GetSetMethod(nonPublic: true);
            var lastPos = type.GetProperty(nameof(DemoFileReader<T>.IncompleteFileLastStreamPosition), BindingFlags.Public | BindingFlags.Instance)
                ?.GetSetMethod(nonPublic: true);
            if (incomplete == null || lastPos == null)
                throw new MissingMemberException(
                    "DemoFile.Net changed its incomplete-file API; update IncompleteFileShim for live following.");

            incomplete.Invoke(reader, [false]);
            lastPos.Invoke(reader, [long.MaxValue]);
        }
    }
}
