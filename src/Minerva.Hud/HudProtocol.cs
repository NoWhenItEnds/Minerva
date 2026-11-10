using System.Text;
using System.Text.Json;

namespace Minerva.Hud;

/// <summary>A complete message from the in-game mod.</summary>
/// <param name="Type"><c>hello</c>, <c>full</c>, <c>delta</c> or <c>error</c>.</param>
/// <param name="SentUnixMs">Game-side <c>Date.now()</c> when the message was sent.</param>
/// <param name="ReceivedUtc">When the last chunk was read from console.log.</param>
public sealed record HudMessage(string Type, int Seq, long SentUnixMs, DateTime ReceivedUtc, JsonElement Body)
{
    /// <summary>Mod → companion delay (same machine, so both clocks agree).</summary>
    public TimeSpan Latency => ReceivedUtc - DateTimeOffset.FromUnixTimeMilliseconds(SentUnixMs).UtcDateTime;
}

/// <summary>
/// Reassembles chunked messages written by the mod as console lines:
/// <c>MNRV3|&lt;seq&gt;|&lt;index&gt;|&lt;count&gt;|&lt;json chunk&gt;</c> on the <c>PanoramaScript</c> channel.
/// </summary>
public sealed class HudProtocol
{
    public const int Version = 3;
    public const string Prefix = "MNRV3|";

    private readonly Dictionary<int, (string?[] Parts, int Received)> _partial = new();

    /// <summary>Lines that looked like ours but could not be decoded (truncation, corruption).</summary>
    public int Rejected { get; private set; }

    /// <summary>Protocol version of the last line from a different version of the mod (stale install), if any.</summary>
    public int? ForeignVersion { get; private set; }

    /// <summary>Feed one console line; returns a message when its last chunk arrives.</summary>
    public HudMessage? Accept(ConsoleLine line)
    {
        var text = line.Message;
        var at = text.IndexOf(Prefix, StringComparison.Ordinal);
        if (at < 0)
        {
            var other = text.IndexOf("MNRV", StringComparison.Ordinal);
            var bar = other < 0 ? -1 : text.IndexOf('|', other);
            if (bar > other + 4 && int.TryParse(text.AsSpan(other + 4, bar - other - 4), out var v) && v != Version)
                ForeignVersion = v;
            return null;
        }
        text = text[(at + Prefix.Length)..];

        var parts = text.Split('|', 4);
        if (parts.Length != 4 || !int.TryParse(parts[0], out var seq) || !int.TryParse(parts[1], out var index) ||
            !int.TryParse(parts[2], out var count) || count <= 0 || index < 0 || index >= count)
        {
            Rejected++;
            return null;
        }

        if (seq == 1 && index == 0) _partial.Clear(); // the mod (re)started: sequence numbers begin again
        if (!_partial.TryGetValue(seq, out var entry) || entry.Parts.Length != count) entry = (new string?[count], 0);
        if (entry.Parts[index] == null) entry.Received++;
        entry.Parts[index] = parts[3];
        _partial[seq] = entry;
        if (entry.Received < count) return null;

        _partial.Remove(seq);
        // Drop stale partial messages (lost chunks) so memory cannot grow.
        foreach (var old in _partial.Keys.Where(k => k < seq - 50).ToList()) _partial.Remove(old);

        var json = new StringBuilder();
        foreach (var p in entry.Parts) json.Append(p);
        try
        {
            using var doc = JsonDocument.Parse(json.ToString());
            var root = doc.RootElement.Clone();
            return new HudMessage(
                root.TryGetProperty("t", out var t) ? t.GetString() ?? "?" : "?",
                seq,
                root.TryGetProperty("ms", out var ms) && ms.ValueKind == JsonValueKind.Number ? (long)ms.GetDouble() : 0,
                line.ReadUtc,
                root);
        }
        catch (JsonException)
        {
            Rejected++;
            return null;
        }
    }
}
