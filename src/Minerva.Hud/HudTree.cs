using System.Text.Json;

namespace Minerva.Hud;

/// <summary>One HUD panel as reported by the mod.</summary>
/// <param name="Key">Id path from the top panel, e.g. <c>/Hud/Panel#0/TopBar/Panel#2/GameTime</c>.</param>
/// <param name="Visible">The panel's own visibility flag (an ancestor may still hide it).</param>
/// <param name="X">Layout position and size; null when the panel is not on screen (hidden or not laid out).</param>
public sealed record HudPanel(string Key, string? Type, bool Visible, int? X, int? Y, int? Width, int? Height, string? Text, double? Value)
{
    /// <summary>Parse a delta row: <c>[key, type, visible, x, y, w, h, text, value]</c>, trailing nulls omitted.</summary>
    public static HudPanel FromRow(JsonElement row) =>
        FromFields(row.GetArrayLength() > 0 ? row[0].GetString() ?? "" : "", row, 1, t => Str(t));

    /// <summary>Parse the fields after the key, starting at <paramref name="start"/> (type, visible, x, y, w, h, text, value).</summary>
    internal static HudPanel FromFields(string key, JsonElement row, int start, Func<JsonElement, string?> type)
    {
        var n = row.GetArrayLength();
        JsonElement At(int i) => start + i < n ? row[start + i] : default;
        return new HudPanel(
            key,
            type(At(0)),
            At(1).ValueKind == JsonValueKind.Number && At(1).GetDouble() != 0,
            Coord(At(2)), Coord(At(3)), Coord(At(4)), Coord(At(5)),
            Str(At(6)),
            At(7).ValueKind == JsonValueKind.Number ? At(7).GetDouble() : null);
    }

    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int? Coord(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.GetDouble() is var d && Math.Abs(d) < 1e7 ? (int)Math.Round(d) : null;
}

/// <summary>The companion's mirror of the game's HUD panel tree, kept current from full and delta messages.</summary>
public sealed class HudTree
{
    private readonly Dictionary<string, HudPanel> _panels = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, HudPanel> Panels => _panels;

    /// <summary>True once a full dump has been applied (deltas before that are partial).</summary>
    public bool Complete { get; private set; }

    public DateTime? LastUpdateUtc { get; private set; }

    /// <summary>
    /// Apply a message; returns the panels that changed (all panels for a full dump). Panels inside hidden subtrees keep
    /// their last reported state between the mod's complete walks.
    /// </summary>
    public IReadOnlyList<HudPanel> Apply(HudMessage message)
    {
        var changed = new List<HudPanel>();
        switch (message.Type)
        {
            case "full":
                _panels.Clear();
                ApplyFull(message.Body, changed);
                Complete = true;
                break;
            case "delta":
                foreach (var row in message.Body.GetProperty("set").EnumerateArray())
                {
                    var p = HudPanel.FromRow(row);
                    _panels[p.Key] = p;
                    changed.Add(p);
                }
                // A removed panel takes everything below it.
                var removed = message.Body.GetProperty("del").EnumerateArray().Select(k => k.GetString() ?? "").ToList();
                if (removed.Count > 0)
                {
                    var prefixes = removed.Select(k => k + "/").ToList();
                    foreach (var key in _panels.Keys.Where(k => removed.Contains(k) || prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).ToList())
                        _panels.Remove(key);
                }
                break;
            default:
                return changed;
        }
        LastUpdateUtc = message.ReceivedUtc;
        return changed;
    }

    /// <summary>Full rows: <c>[parent index, name, type index, visible, x, y, w, h, text, value]</c>, in walk order.</summary>
    private void ApplyFull(JsonElement body, List<HudPanel> changed)
    {
        var types = body.GetProperty("types").EnumerateArray().Select(t => t.GetString()).ToArray();
        var keys = new List<string>();
        foreach (var row in body.GetProperty("panels").EnumerateArray())
        {
            var parent = row[0].GetInt32();
            var name = row.GetArrayLength() > 1 ? row[1].GetString() ?? "" : "";
            var key = parent >= 0 && parent < keys.Count ? keys[parent] + "/" + name : name;
            keys.Add(key);
            var p = HudPanel.FromFields(key, row, 2,
                t => t.ValueKind == JsonValueKind.Number && t.GetInt32() is var i && i >= 0 && i < types.Length ? types[i] : null);
            _panels[key] = p;
            changed.Add(p);
        }
    }
}
