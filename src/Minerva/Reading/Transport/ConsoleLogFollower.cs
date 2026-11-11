using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Minerva.Reading.Transport;

/// <param name="Channel">Console channel, e.g. <c>PanoramaScript</c>; null for lines without one.</param>
/// <param name="ReadUtc">When the companion read the line (for latency measurement).</param>
public sealed record ConsoleLine(string? Timestamp, string? Channel, string Message, DateTime ReadUtc)
{
    /// <summary>
    /// How long before it was read the game wrote this line, from the game's local "MM/dd HH:mm:ss" timestamp
    /// (no year: assumed to be the most recent such date). Null for lines without a timestamp.
    /// </summary>
    public TimeSpan? Age
    {
        get
        {
            if (Timestamp == null) return null;
            var read = ReadUtc.ToLocalTime();
            if (!DateTime.TryParseExact($"{read.Year}/{Timestamp}", "yyyy/MM/dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var written))
                return null;
            if (written > read.AddDays(1)) written = written.AddYears(-1); // read in January, written in December
            return read - written;
        }
    }
}

/// <summary>
/// Follows <c>game/citadel/console.log</c> (written when Deadlock runs with <c>-condebug</c>) and yields lines as the
/// game appends them. Survives the game restarting and truncating/recreating the file.
/// </summary>
public static partial class ConsoleLogFollower
{
    public static async IAsyncEnumerable<ConsoleLine> FollowAsync(
        string path,
        bool fromStart = false,
        TimeSpan? pollInterval = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(20);
        long position = -1;
        DateTime created = default;
        var decoder = new UTF8Encoding(false).GetDecoder();
        var pending = new StringBuilder();
        var bytes = new byte[1 << 16];
        var chars = new char[(1 << 16) + 4];

        while (!ct.IsCancellationRequested)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            // New file (game relaunched) or truncated: start over from the beginning.
            if (position < 0)
            {
                position = fromStart ? 0 : info.Length;
                created = info.CreationTimeUtc;
            }
            else if (info.Length < position || info.CreationTimeUtc != created)
            {
                position = 0;
                created = info.CreationTimeUtc;
                pending.Clear();
                decoder.Reset();
            }

            if (info.Length == position)
            {
                await Task.Delay(poll, ct).ConfigureAwait(false);
                continue;
            }

            var lines = new List<ConsoleLine>();
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                fs.Position = position;
                int n;
                while ((n = await fs.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
                {
                    position += n;
                    var c = decoder.GetChars(bytes, 0, n, chars, 0);
                    var now = DateTime.UtcNow;
                    for (var i = 0; i < c; i++)
                    {
                        var ch = chars[i];
                        if (ch == '\n')
                        {
                            lines.Add(Parse(pending.ToString().TrimEnd('\r').TrimStart('\uFEFF'), now));
                            pending.Clear();
                        }
                        else
                        {
                            pending.Append(ch);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // File briefly unavailable (being recreated); retry on the next poll.
            }

            foreach (var line in lines) yield return line;
        }
    }

    public static ConsoleLine Parse(string raw, DateTime readUtc)
    {
        var m = LineRegex().Match(raw);
        return m.Success
            ? new ConsoleLine(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, readUtc)
            : new ConsoleLine(null, null, raw, readUtc);
    }

    // "11/10 06:52:29 [PanoramaScript] message"
    [GeneratedRegex(@"^(\d\d/\d\d \d\d:\d\d:\d\d) \[([^\]]+)\] ?(.*)$")]
    private static partial Regex LineRegex();
}
