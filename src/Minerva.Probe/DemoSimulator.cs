using System.Diagnostics;

namespace Minerva.Probe;

/// <summary>
/// Re-writes a finished demo into a new file at real-time pace, the way the game appends to a demo while recording
/// with <c>demo_flush 1</c>. The header's size/offset fields are zeroed so the output looks like an in-progress
/// recording. Lets us exercise the live follower end-to-end without launching the game.
/// </summary>
internal static class DemoSimulator
{
    private const int TickRate = 64;

    public static async Task RunAsync(string src, string dst, float speed, float skipSeconds, CancellationToken ct)
    {
        await using var input = File.OpenRead(src);
        await using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var header = new byte[16];
        input.ReadExactly(header);
        Array.Clear(header, 8, 8); // no file-info offset yet: "still recording"
        output.Write(header);
        output.Flush();

        uint? firstTick = null;
        var skipTicks = (uint)(skipSeconds * TickRate);
        var clock = Stopwatch.StartNew();
        var commands = 0L;
        var lastReport = Stopwatch.StartNew();

        Console.WriteLine($"Simulating recording {src} -> {dst} at {speed}x{(skipSeconds > 0 ? $", fast-forwarding the first {skipSeconds}s" : "")}");
        var buffer = new MemoryStream();
        while (true)
        {
            buffer.SetLength(0);
            if (!CopyVarInt(input, buffer, out _) || !CopyVarInt(input, buffer, out var tick) || !CopyVarInt(input, buffer, out var size))
                break;
            var body = new byte[size];
            input.ReadExactly(body);
            buffer.Write(body);

            if (tick != uint.MaxValue)
            {
                firstTick ??= tick;
                var elapsedTicks = tick - firstTick.Value;
                if (elapsedTicks > skipTicks)
                {
                    var due = TimeSpan.FromSeconds((elapsedTicks - skipTicks) / (double)TickRate / speed);
                    var wait = due - clock.Elapsed;
                    if (wait > TimeSpan.FromMilliseconds(1)) await Task.Delay(wait, ct);
                }
                else
                {
                    clock.Restart(); // keep the pace anchored to the end of the fast-forward
                }
            }

            output.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
            output.Flush(flushToDisk: false);
            commands++;

            if (lastReport.Elapsed > TimeSpan.FromSeconds(10))
            {
                lastReport.Restart();
                Console.WriteLine($"  wrote {commands} commands, demo time {(tick - (firstTick ?? tick)) / (float)TickRate / 60:0.0} min, {output.Length / 1e6:0.0} MB");
            }
        }

        Console.WriteLine($"Done: {commands} commands, {output.Length / 1e6:0.0} MB");
    }

    private static bool CopyVarInt(Stream input, Stream copy, out uint value)
    {
        value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = input.ReadByte();
            if (b < 0) return false;
            copy.WriteByte((byte)b);
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        throw new InvalidDataException("Malformed varint");
    }
}
