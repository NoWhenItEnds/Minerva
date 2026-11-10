using System.Globalization;
using System.Text.RegularExpressions;
using Minerva.GameState.StaticData;
using Minerva.Hud;
using Minerva.Match.Bindings;
using V = Minerva.Match.Hud.HudVocabulary;

namespace Minerva.Match.Hud;

/// <summary>One hero's row in the top bar.</summary>
/// <param name="Team">
/// Game team number, from the <c>team</c> attribute of the row's container in the layout. The containers' ids
/// (<c>TeamFriendly</c>/<c>TeamEnemy</c>) are not relative to the local player: in a recorded match the local player's
/// row was under <c>TeamEnemy</c>. Whether a row is an ally is decided by comparing with the local player's team.
/// </param>
/// <param name="Slot">Player slot, from the row's name (<c>TopBarPlayer3</c> → 3); minimap hero icons follow the same order.</param>
/// <param name="Souls">Net worth as the top bar shows it, which abbreviates (<c>2.1k</c>): approximate above 1000.</param>
/// <param name="HealthFraction">Width of the health bar fill relative to the bar, when on screen.</param>
/// <param name="MaxHealth">From the row's stat tooltip, which is hidden, so refreshed only on the mod's complete walks.</param>
public sealed record HeroRow(
    string Key, int? Team, int? Slot, string? HeroName, HeroInfo? Hero, string? PlayerName,
    int? Souls, int? Level, int? Kills, int? Deaths, int? Assists, int? ObjectiveDamage,
    double? RespawnSeconds, bool Dead, double? HealthFraction, int? MaxHealth, int? BulletResist, int? SpiritResist);

/// <summary>Match-level HUD values.</summary>
/// <param name="Clock">Match clock; negative before the match starts.</param>
/// <param name="UnspentSouls">The local player's unspent souls (exact).</param>
/// <param name="FriendlyKills">
/// The score labels are relative to the local player (unlike the hero rows' containers): in a recorded match the
/// <c>TeamScoreFriendly</c> net worth matched the sum of the local player's team's rows.
/// </param>
public sealed record HudState(
    TimeSpan? Clock, int? UnspentSouls,
    int? FriendlyKills, int? EnemyKills, int? FriendlyNetworth, int? EnemyNetworth, string? MidBossTimer,
    IReadOnlyList<HeroRow> Heroes, IReadOnlyList<string> Warnings);

/// <summary>Reads named values from the HUD through the game's own layout bindings.</summary>
public sealed partial class HudReader
{
    private readonly LayoutCatalog _catalog;
    private readonly Dictionary<string, HeroInfo> _heroesByName;
    private readonly IReadOnlyList<string> _vocabularyWarnings;

    public HudReader(LayoutCatalog catalog, GameData data)
    {
        _catalog = catalog;
        _heroesByName = new Dictionary<string, HeroInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in data.Heroes) _heroesByName.TryAdd(h.DisplayName, h);
        _vocabularyWarnings = CheckVocabulary(catalog);
    }

    /// <summary>
    /// Every variable and id the reader uses, checked against the installed layouts: a patch that renames one shows up
    /// here (once, at start) rather than as a silently missing value.
    /// </summary>
    public static IReadOnlyList<string> CheckVocabulary(LayoutCatalog catalog)
    {
        var warnings = new List<string>();
        static IEnumerable<LayoutNode> All(LayoutNode n) => n.Children.SelectMany(All).Prepend(n);
        foreach (var layout in V.Variables.Select(v => v.Layout).Concat(V.Ids.Select(i => i.Layout)).Distinct())
        {
            if (catalog.Get(layout) is not { } file)
            {
                warnings.Add($"Layout '{layout}' is not in the game files.");
                continue;
            }
            var nodes = All(file.Root).ToList();
            foreach (var (_, variable) in V.Variables.Where(v => v.Layout == layout))
                if (!nodes.Any(n => n.Variables.Contains(variable))) warnings.Add($"'{layout}' has no {{:{variable}}} label any more.");
            foreach (var (_, id) in V.Ids.Where(i => i.Layout == layout))
                if (!nodes.Any(n => n.Id == id)) warnings.Add($"'{layout}' has no element '{id}' any more.");
        }
        return warnings;
    }

    public HudState Read(IReadOnlyDictionary<string, HudPanel> panels) => Read(new HudBindings(panels, _catalog));

    public HudState Read(HudBindings hud)
    {
        // Unknown layouts in general are expected (engine layouts live outside the game package); only the ones the
        // reader uses matter, and CheckVocabulary covers those.
        var warnings = new List<string>(_vocabularyWarnings);

        var top = hud.Instances(V.TopBar).FirstOrDefault();
        if (top == null) warnings.Add($"No '{V.TopBar}' on the HUD (not in a match?).");

        var heroes = new List<HeroRow>();
        foreach (var row in hud.Instances(V.TopBarPlayer))
        {
            if (TeamOf(hud, row.Key) is not { } team) continue; // not in a team container (e.g. a spectator view)
            var rowName = row.Key[(row.Key.LastIndexOf('/') + 1)..];
            var digits = rowName.Length - rowName.AsSpan().TrimEnd("0123456789").Length;
            int? slot = digits > 0 ? int.Parse(rowName.AsSpan(rowName.Length - digits)) : null;
            var name = Clean(row.Field(V.HeroName));
            var respawn = row.Bound(V.RespawnTime);
            var respawnSeconds = Number(respawn?.Value(V.RespawnTime));
            var details = row.Nested(V.PlayerDetails).FirstOrDefault();
            var fill = row.ById(V.HealthFill)?.Panel;
            var bar = row.ById(V.HealthContents)?.Panel;
            heroes.Add(new HeroRow(
                row.Key, team, slot, name, name != null ? _heroesByName.GetValueOrDefault(name) : null, Clean(row.Field(V.PlayerName)),
                Souls(row.Field(V.Souls)), Integer(row.Field(V.Level)),
                Integer(row.Field(V.Kills)), Integer(row.Field(V.Deaths)), Integer(row.Field(V.Assists)), Integer(row.Field(V.ObjectiveDamage)),
                respawnSeconds,
                Dead: respawn != null && hud.IsShown(respawn.Panel.Key) && respawnSeconds > 0,
                HealthFraction: fill?.Width is { } fw && bar?.Width is { } bw && bw > 0 && hud.IsShown(fill.Key) ? Math.Clamp((double)fw / bw, 0, 1) : null,
                MaxHealth: Integer(details?.Field(V.MaxHealth)),
                BulletResist: Integer(details?.Field(V.BulletResist)),
                SpiritResist: Integer(details?.Field(V.SpiritResist))));
        }
        if (top != null && heroes.Count == 0) warnings.Add($"No hero rows ('{V.TopBarPlayer}' inside an element with a '{V.TeamAttribute}' attribute).");
        heroes.Sort((a, b) => (a.Slot ?? int.MaxValue).CompareTo(b.Slot ?? int.MaxValue));
        foreach (var h in heroes.Where(h => h.HeroName != null && h.Hero == null))
            warnings.Add($"Hero '{h.HeroName}' doesn't match any hero name in the game's localization.");

        return new HudState(
            Clock(top?.Field(V.GameClock)),
            Integer(hud.Instances(V.SoulsContainer).FirstOrDefault()?.Field(V.UnspentSouls)),
            Integer(top?.Field(V.FriendlyKills)), Integer(top?.Field(V.EnemyKills)),
            Souls(top?.Field(V.TeamNetworth, V.FriendlyScore)), Souls(top?.Field(V.TeamNetworth, V.EnemyScore)),
            Clean(top?.Field(V.MidBossTimer)),
            heroes, warnings);
    }

    /// <summary>The game team of the nearest enclosing layout element with a <c>team</c> attribute.</summary>
    private static int? TeamOf(HudBindings hud, string key)
    {
        for (var k = HudPanel.ParentKey(key); k != null; k = HudPanel.ParentKey(k))
        {
            foreach (var node in new[] { hud.Placement(k), hud.Bound.GetValueOrDefault(k)?.Node })
                if (node != null && node.Attributes.TryGetValue(V.TeamAttribute, out var t) &&
                    int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var team))
                    return team;
            if (k.Length == 0) break;
        }
        return null;
    }

    /// <summary><c>3:53</c>, <c>1:02:03</c>; the HUD shows pregame time as <c>0:-23</c>.</summary>
    public static TimeSpan? Clock(string? text)
    {
        text = Clean(text);
        if (string.IsNullOrEmpty(text)) return null;
        var negative = text.Contains('-');
        var seconds = 0.0;
        foreach (var part in text.Replace("-", "").Split(':'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return null;
            seconds = seconds * 60 + v;
        }
        return TimeSpan.FromSeconds(negative ? -seconds : seconds);
    }

    /// <summary><c>600</c>, <c>2.1k</c>, <c>12,345</c>, <c>1.2m</c>.</summary>
    public static int? Souls(string? text)
    {
        text = Clean(text)?.Replace(",", "").Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var multiplier = char.ToLowerInvariant(text[^1]) switch { 'k' => 1_000, 'm' => 1_000_000, _ => 1 };
        if (multiplier != 1) text = text[..^1];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)Math.Round(v * multiplier) : null;
    }

    public static int? Integer(string? text)
    {
        text = Clean(text);
        if (string.IsNullOrEmpty(text)) return null;
        var m = NumberRegex().Match(text.Replace(",", ""));
        return m.Success && int.TryParse(m.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? Number(string? text)
    {
        text = Clean(text);
        if (string.IsNullOrEmpty(text)) return null;
        var m = DecimalRegex().Match(text);
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Labels with <c>html="true"</c> can carry markup.</summary>
    private static string? Clean(string? text) => text == null ? null : TagRegex().Replace(text, "").Trim();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"-?\d+")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"-?\d+(\.\d+)?")]
    private static partial Regex DecimalRegex();
}
