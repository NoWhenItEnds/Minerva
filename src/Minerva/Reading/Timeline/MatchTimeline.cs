using System.Numerics;
using Minerva.GameState.StaticData;
using Minerva.Match.Camps;
using Minerva.Match.Minimap;

namespace Minerva.Match.Timeline;

/// <summary>Something that happened in the match, as inferred from successive HUD reads.</summary>
public abstract record MatchEvent(TimeSpan Clock)
{
    public abstract string Describe();
}

/// <summary>A kill from the kill feed.</summary>
/// <param name="Killer">Hero name for a hero, else the feed's name (an NPC such as a guardian).</param>
/// <param name="Victim">Hero name for a hero, else the feed's name (an objective).</param>
public sealed record KillEvent(TimeSpan Clock, FeedKill Kill, string Killer, string Victim) : MatchEvent(Clock)
{
    public override string Describe() =>
        $"{Killer} killed {Victim}{(Kill.Entry.Souls is { } s ? $" ({s} souls)" : "")}" +
        (Kill.AssistSlots.Count > 0 ? $", assisted by {Kill.AssistSlots.Count}" : "");
}

/// <summary>A hero's respawn timer appeared (they died) or went away (they respawned).</summary>
public sealed record LifeEvent(TimeSpan Clock, int Slot, string Hero, bool Died, double? RespawnSeconds) : MatchEvent(Clock)
{
    public override string Describe() => Died
        ? $"{Hero} died{(RespawnSeconds is { } r ? $", respawns in {r:0} s" : "")}"
        : $"{Hero} respawned";
}

/// <summary>An objective's minimap icon went from up to down or back.</summary>
public sealed record ObjectiveEvent(TimeSpan Clock, ObjectiveMarker Objective, bool Up) : MatchEvent(Clock)
{
    public override string Describe() =>
        $"team {Objective.Team?.ToString() ?? "?"} {Objective.Kind}{(Objective.Lane is { } l ? $" ({l})" : "")} {(Up ? "up" : "down")}";
}

/// <summary>A camp tracker inference.</summary>
public sealed record CampChangeEvent(CampEvent Camp) : MatchEvent(Camp.Clock)
{
    public override string Describe() => $"{Camp.Camp.Name}: {Camp.Description}";
}

/// <summary>One hero over the match so far.</summary>
/// <param name="Kills">Hero kills from the kill feed (the top bar's K/D/A isn't kept up to date).</param>
/// <param name="Deaths">Deaths, from the respawn timer (counts deaths the feed missed, e.g. while it was hidden).</param>
/// <param name="LastSeenAt">Match time an enemy was last seen (allies are always seen).</param>
/// <param name="LastPosition">Where they were last seen.</param>
/// <param name="RespawnsAt">While dead: when they're back.</param>
public sealed record HeroTimeline(
    int Slot, HeroState Latest, int Kills, int Deaths, int Assists,
    TimeSpan? LastSeenAt, Vector2? LastPosition, TimeSpan? RespawnsAt);

/// <summary>
/// The match over time: feeds each <see cref="MatchState"/> to the trackers that need history (camps, kill feed,
/// deaths, objectives) and keeps the events and per-hero running totals. The advisor reads from here.
/// </summary>
public sealed class MatchTimeline
{
    /// <summary>A feed entry must be on screen for this many consecutive reads before it counts as a kill.</summary>
    private const int FeedConfirmReads = 2;

    /// <summary>The same entry (killer, victim, bounty) seen again within this time is the same kill.</summary>
    private static readonly TimeSpan FeedMemory = TimeSpan.FromSeconds(30);

    private readonly MapData _map;
    private readonly List<MatchEvent> _events = [];
    private readonly Dictionary<int, Hero> _heroes = [];
    private readonly Dictionary<string, int> _pendingFeed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _recentFeed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _objectives = new(StringComparer.Ordinal);
    private CampTracker _camps;
    private TimeSpan? _clock;
    private bool _localKillLastRead;

    public MatchTimeline(MapData map)
    {
        _map = map;
        _camps = new CampTracker(map);
    }

    public MatchState? Current { get; private set; }

    public IReadOnlyList<MatchEvent> Events => _events;

    public IReadOnlyList<CampState> Camps => _camps.Camps;

    public IReadOnlyList<HeroTimeline> Heroes =>
        _heroes.Values.OrderBy(h => h.Slot)
            .Select(h => new HeroTimeline(h.Slot, h.Latest, h.Kills, h.Deaths, h.Assists, h.LastSeenAt, h.LastPosition, h.RespawnsAt))
            .ToList();

    /// <summary>Feed one read of the HUD; returns the events it produced.</summary>
    public IReadOnlyList<MatchEvent> Update(MatchState state)
    {
        if (state.Clock is not { } now) return [];
        // The clock going back means a new match (or a replay restarting).
        if (_clock is { } previous && now < previous - TimeSpan.FromSeconds(10)) Reset();
        _clock = now;
        Current = state;

        var events = new List<MatchEvent>();
        UpdateHeroes(now, state, events);
        var localKill = UpdateFeed(now, state, events);
        UpdateObjectives(now, state, events);

        // A kill's souls can show in the read after the entry, so a local kill covers this read and the next.
        if (CampObservation.From(state) is { } observation)
            foreach (var e in _camps.Update(observation with { LocalGainFromKill = localKill || _localKillLastRead }))
                events.Add(new CampChangeEvent(e));
        _localKillLastRead = localKill;

        _events.AddRange(events);
        return events;
    }

    private void Reset()
    {
        _events.Clear();
        _heroes.Clear();
        _pendingFeed.Clear();
        _recentFeed.Clear();
        _objectives.Clear();
        _camps = new CampTracker(_map);
        _localKillLastRead = false;
    }

    private void UpdateHeroes(TimeSpan now, MatchState state, List<MatchEvent> events)
    {
        foreach (var h in state.Heroes)
        {
            if (h.Slot is not { } slot) continue;
            if (!_heroes.TryGetValue(slot, out var hero)) _heroes[slot] = hero = new Hero(slot, h);
            var wasDead = hero.Latest.Dead;
            hero.Latest = h;
            if (h.Visible && !h.Dead && h.World is { } at)
            {
                hero.LastSeenAt = now;
                hero.LastPosition = at;
            }
            if (h.Dead && !wasDead)
            {
                hero.Deaths++;
                var respawn = h.Row?.RespawnSeconds;
                hero.RespawnsAt = respawn is { } r ? now + TimeSpan.FromSeconds(r) : null;
                events.Add(new LifeEvent(now, slot, Name(h), true, respawn));
            }
            else if (!h.Dead && wasDead)
            {
                hero.RespawnsAt = null;
                events.Add(new LifeEvent(now, slot, Name(h), false, null));
            }
        }
    }

    /// <summary>New kills from the feed; returns whether one involved the local player (as killer or assist).</summary>
    private bool UpdateFeed(TimeSpan now, MatchState state, List<MatchEvent> events)
    {
        foreach (var key in _recentFeed.Where(kv => now - kv.Value > FeedMemory).Select(kv => kv.Key).ToList()) _recentFeed.Remove(key);

        var local = state.LocalPlayer?.Slot;
        var localKill = false;
        var onScreen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kill in state.Feed)
        {
            var signature = kill.Entry.Signature;
            if (!onScreen.Add(signature)) continue;
            if (local is { } me && kill.Involves(me) && !_recentFeed.ContainsKey(signature)) localKill = true;
            if (_recentFeed.ContainsKey(signature))
            {
                _recentFeed[signature] = now;
                continue;
            }
            // Entries are read while the feed reflows; one seen twice in a row is real, not a mix of two entries.
            var reads = _pendingFeed.GetValueOrDefault(signature) + 1;
            if (reads < FeedConfirmReads)
            {
                _pendingFeed[signature] = reads;
                continue;
            }
            _pendingFeed.Remove(signature);
            _recentFeed[signature] = now;
            Count(kill);
            events.Add(new KillEvent(now, kill, NameOf(kill.KillerSlot, kill.Entry.Killer), kill.Entry.VictimIsObjective
                ? $"{kill.Entry.Victim ?? "an objective"} (objective)"
                : NameOf(kill.VictimSlot, kill.Entry.Victim)));
        }
        foreach (var key in _pendingFeed.Keys.Where(k => !onScreen.Contains(k)).ToList()) _pendingFeed.Remove(key);
        return localKill;
    }

    private void Count(FeedKill kill)
    {
        // Only hero kills count toward K/A; objective kills are events of their own.
        if (kill.Entry.VictimIsObjective || kill.VictimSlot == null) return;
        if (kill.KillerSlot is { } k && _heroes.TryGetValue(k, out var killer)) killer.Kills++;
        foreach (var a in kill.AssistSlots.Where(a => a != kill.KillerSlot))
            if (_heroes.TryGetValue(a, out var assist)) assist.Assists++;
    }

    /// <summary>
    /// Objectives going down (or back up), from their icons' <c>active</c> class. Icons are identified by what they are
    /// (kind, team, lane), not by panel: the minimap reuses icon panels, so after a guardian dies its panel can show
    /// another one. Several of a kind without a lane (base buildings) are told apart by position on the minimap, which is
    /// fixed.
    /// </summary>
    private void UpdateObjectives(TimeSpan now, MatchState state, List<MatchEvent> events)
    {
        foreach (var group in state.Objectives.GroupBy(o => (o.Kind, o.Team, o.Lane)))
        {
            var ordered = group.OrderBy(o => o.Minimap.X).ThenBy(o => o.Minimap.Y).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var o = ordered[i];
                var identity = $"{o.Kind}|{o.Team}|{o.Lane}|{i}";
                if (_objectives.TryGetValue(identity, out var wasUp) && wasUp != o.Active)
                    events.Add(new ObjectiveEvent(now, o, o.Active));
                _objectives[identity] = o.Active;
            }
        }
    }

    private string NameOf(int? slot, string? fallback) =>
        slot is { } s && _heroes.TryGetValue(s, out var h) ? Name(h.Latest) : fallback ?? "?";

    private static string Name(HeroState h) => h.Hero?.DisplayName ?? h.HeroName ?? $"slot {h.Slot}";

    private sealed class Hero(int slot, HeroState latest)
    {
        public int Slot { get; } = slot;
        public HeroState Latest { get; set; } = latest;
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Assists { get; set; }
        public TimeSpan? LastSeenAt { get; set; }
        public Vector2? LastPosition { get; set; }
        public TimeSpan? RespawnsAt { get; set; }
    }
}
