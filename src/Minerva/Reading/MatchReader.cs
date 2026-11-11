using System.Numerics;
using Minerva.GameState.StaticData;
using Minerva.Hud;
using Minerva.Match.Bindings;
using Minerva.Match.Hud;
using Minerva.Match.Minimap;

namespace Minerva.Match;

/// <summary>Everything known about one hero: their top bar row joined with their minimap icon.</summary>
/// <param name="Visible">Seen now (allies always; enemies when not in fog). False: <see cref="World"/> is last known.</param>
public sealed record HeroState(
    int? Slot, int? Team, bool IsLocalPlayer, bool IsAlly, HeroInfo? Hero, string? HeroName,
    HeroRow? Row, HeroMarker? Icon)
{
    public bool Dead => Row?.Dead == true || Icon?.Dead == true;
    public bool Visible => Icon?.Visible ?? IsAlly;
    public Vector2? World => Icon is { OnMap: true } i ? i.World : null;
}

/// <summary>A kill feed entry with its heroes identified.</summary>
/// <param name="KillerSlot">The killer's player slot; null for an NPC (or a name that matched no row).</param>
/// <param name="VictimSlot">The victim's slot; null for an objective.</param>
public sealed record FeedKill(FeedEntry Entry, int? KillerSlot, int? VictimSlot, IReadOnlyList<int> AssistSlots)
{
    public bool Involves(int slot) => KillerSlot == slot || AssistSlots.Contains(slot);
}

/// <summary>The match as the local player's HUD shows it, at one moment.</summary>
public sealed record MatchState(
    TimeSpan? Clock, int? LocalTeam, int? UnspentSouls,
    IReadOnlyList<HeroState> Heroes, IReadOnlyList<ObjectiveMarker> Objectives,
    HudState Hud, MinimapState Minimap, IReadOnlyList<string> Warnings)
{
    public HeroState? LocalPlayer => Heroes.FirstOrDefault(h => h.IsLocalPlayer);

    /// <summary>Kill feed entries on screen, with their heroes' slots.</summary>
    public IReadOnlyList<FeedKill> Feed { get; init; } = [];
    public IEnumerable<HeroState> Allies => Heroes.Where(h => h.IsAlly);
    public IEnumerable<HeroState> Enemies => Heroes.Where(h => !h.IsAlly);
}

/// <summary>Reads the whole match state from the mirrored HUD tree.</summary>
public sealed class MatchReader(LayoutCatalog catalog, GameData data, MapData map)
{
    private readonly HudReader _hud = new(catalog, data);
    private readonly MinimapReader _minimap = new(map);

    public MatchState Read(IReadOnlyDictionary<string, HudPanel> panels)
    {
        var hud = _hud.Read(new HudBindings(panels, catalog));
        var minimap = _minimap.Read(panels);
        var warnings = hud.Warnings.Concat(minimap.Warnings).ToList();

        var links = Link(hud.Heroes, minimap.Heroes, warnings);
        var localIcon = minimap.LocalPlayer;
        var localRow = localIcon != null ? links.FirstOrDefault(l => l.Icon == localIcon).Row : null;
        var localTeam = localRow?.Team ?? localIcon?.Team;

        var heroes = new List<HeroState>();
        foreach (var (row, icon) in links)
        {
            var team = row?.Team ?? icon?.Team;
            heroes.Add(new HeroState(row?.Slot, team, icon?.IsLocalPlayer == true,
                IsAlly: icon?.IsAlly ?? (team != null && team == localTeam),
                row?.Hero, row?.HeroName, row, icon));
        }

        return new MatchState(hud.Clock, localTeam, hud.UnspentSouls, heroes, minimap.Objectives, hud, minimap, warnings)
        {
            Feed = hud.Feed.Select(e => Resolve(e, heroes)).ToList(),
        };
    }

    /// <summary>
    /// Ties a feed entry's names to heroes. The feed shows persona names: the top bar row's player name (a bot's is its
    /// hero's name), compared ignoring case since the feed may differ in case. Names that match no hero are NPCs
    /// (a guardian killing a hero, say). Assists come as hero ids.
    /// </summary>
    public static FeedKill Resolve(FeedEntry entry, IReadOnlyList<HeroState> heroes)
    {
        HeroState? ByName(string? name) =>
            string.IsNullOrEmpty(name) ? null
            : heroes.FirstOrDefault(h => string.Equals(h.Row?.PlayerName, name, StringComparison.OrdinalIgnoreCase))
              ?? heroes.FirstOrDefault(h => string.Equals(h.HeroName, name, StringComparison.OrdinalIgnoreCase));
        var killer = ByName(entry.Killer);
        var victim = entry.VictimIsObjective ? null : ByName(entry.Victim);
        var assists = entry.AssistHeroIds
            .Select(id => heroes.FirstOrDefault(h => h.Hero?.Id == id)?.Slot)
            .OfType<int>().ToList();
        return new FeedKill(entry, killer?.Slot, victim?.Slot, assists);
    }

    /// <summary>
    /// Pair top bar rows with minimap hero icons. Both list players in slot order (in a recorded match, every death on
    /// the top bar coincided with the icon at the same rank), so rows sorted by slot are paired with icons sorted by
    /// panel index; the pairing is only kept if every pair agrees on team.
    /// </summary>
    internal static List<(HeroRow? Row, HeroMarker? Icon)> Link(IReadOnlyList<HeroRow> rows, IReadOnlyList<HeroMarker> icons, List<string> warnings)
    {
        static int Index(string key) =>
            key[(key.LastIndexOf('/') + 1)..] is var name && name.LastIndexOf('#') is var h and > 0 && int.TryParse(name.AsSpan(h + 1), out var i) ? i : int.MaxValue;
        var orderedRows = rows.OrderBy(r => r.Slot ?? int.MaxValue).ToList();
        var orderedIcons = icons.OrderBy(i => Index(i.Key)).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();

        if (orderedRows.Count == orderedIcons.Count && orderedRows.Count > 0)
        {
            var pairs = orderedRows.Zip(orderedIcons).ToList();
            if (pairs.All(p => p.First.Team == null || p.Second.Team == null || p.First.Team == p.Second.Team))
                return pairs.Select(p => ((HeroRow?)p.First, (HeroMarker?)p.Second)).ToList();
            warnings.Add("Top bar rows and minimap hero icons are not in the same order (teams disagree); hero names are not linked to map positions.");
        }
        else if (orderedRows.Count > 0 && orderedIcons.Count > 0)
        {
            warnings.Add($"{orderedRows.Count} top bar rows but {orderedIcons.Count} minimap hero icons; hero names are not linked to map positions.");
        }

        return orderedRows.Select(r => ((HeroRow?)r, (HeroMarker?)null))
            .Concat(orderedIcons.Select(i => ((HeroRow?)null, (HeroMarker?)i)))
            .ToList();
    }
}
