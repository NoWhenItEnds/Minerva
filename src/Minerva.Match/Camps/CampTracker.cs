using System.Numerics;
using Minerva.GameState.StaticData;

namespace Minerva.Match.Camps;

public enum CampStatus
{
    /// <summary>Before the camp's first spawn.</summary>
    NotSpawned,

    /// <summary>Spawned and not known to be cleared (possibly with some units already killed).</summary>
    Up,

    /// <summary>Cleared; respawns at <see cref="CampState.UpAt"/>.</summary>
    Cleared,
}

public enum CampClearer { You, Ally, Enemy }

/// <summary>How a clear was inferred.</summary>
public enum ClearEvidence
{
    /// <summary>Each of the camp's units matched one of the local player's soul gains while next to it.</summary>
    Kills,

    /// <summary>
    /// The local player's soul gains while at the camp covered what was left of it, after at least one exact kill there
    /// (or melee range of a Sinner's Sacrifice, which pays per hit): kills that arrived together with other souls.
    /// </summary>
    Souls,

    /// <summary>A hero stayed at the camp while their net worth rose by about the camp's value.</summary>
    NetWorth,
}

/// <summary>A camp's inferred state.</summary>
/// <param name="UpAt">When it spawns or respawns (null while up).</param>
/// <param name="UnitsLeft">Units not seen killed; a camp only respawns once all are dead.</param>
/// <param name="ClearedBy">Who made the last clear (kept after the camp respawns).</param>
/// <param name="EnemySeenAt">Last time a visible enemy was at the camp while it was up: it may have been taken unseen since.</param>
public sealed record CampState(
    CampInfo Camp, CampStatus Status, TimeSpan? UpAt, int UnitsLeft, double? SoulsLeft,
    CampClearer? ClearedBy, TimeSpan? ClearedAt, ClearEvidence? Evidence, TimeSpan? EnemySeenAt)
{
    public bool IsUp => Status == CampStatus.Up;
}

/// <summary>Something the tracker inferred in one update.</summary>
public sealed record CampEvent(TimeSpan Clock, CampInfo Camp, string Description);

/// <summary>
/// Infers neutral camp state. Camps have no HUD or minimap icons, so the tracker combines the map's camp data (position,
/// first spawn, respawn interval, which units spawn and what they pay) with what the HUD shows: the clock, hero
/// positions and souls. A camp's respawn timer starts when its last unit dies; a camp with a survivor doesn't respawn.
/// <list type="bullet">
/// <item>The local player's unspent souls are exact: a gain next to a camp that equals the reward of some of its
/// remaining units marks those units killed. Camp souls are split evenly between the allies who damaged the unit, so
/// with allies at the camp a gain can be a half or a third of the reward.</item>
/// <item>Kills often arrive in the same update as other souls (a lane trooper, a few souls of trickle), so the gains of
/// a whole visit are also counted: once the player leaves, a camp they killed at least one unit of is cleared if the
/// visit's other gains cover what was left.</item>
/// <item>A Sinner's Sacrifice pays a fraction of its value per melee hit and about half on the final hit, so it is
/// only ever cleared by the visit rule, and only when the player came within melee range.</item>
/// <item>Other heroes' souls are only the top bar's abbreviated net worth, so their clears are inferred when a hero
/// stays at a camp away from lanes while their net worth rises by most of its value.</item>
/// </list>
/// Positions come from the minimap, hence the generous radius.
/// </summary>
public sealed class CampTracker
{
    /// <summary>A hero this close to a camp (in the map plane) may be fighting it.</summary>
    public const float NearDistance = 900;

    /// <summary>Melee range of a Sinner's Sacrifice, allowing for minimap error.</summary>
    public const float MeleeDistance = 450;

    /// <summary>
    /// Other heroes are only watched at camps when at least this far from every lane path. Their net worth also rises
    /// from lane troopers, and in a recorded match 95% of hero positions were within 430 units of a lane (2% beyond
    /// 1200: rotations and camps), so closer than this a rise can't be told from laning.
    /// </summary>
    public const float LaneMargin = 600;

    /// <summary>Net worth rise, as a fraction of the camp's value, taken as another hero clearing it.</summary>
    public const double NetWorthClearFraction = 0.6;

    /// <summary>Visit souls, as a fraction of the camp's remaining value (per sharer), taken as the local player clearing it.</summary>
    public const double VisitClearFraction = 0.8;

    /// <summary>
    /// Souls a gain may carry beyond its kills (trickle of a few souls lands in the same update; +7 seen). Kept below
    /// the 10–12 souls a lane trooper pays over a small camp's unit, so a trooper next to a camp isn't taken as a kill.
    /// </summary>
    public const int ExtraSouls = 8;

    /// <summary>The unit class that pays per hit instead of on death.</summary>
    public const string PaysPerHitClass = "npc_neutral_sinners_sacrifice";

    private static readonly TimeSpan MinDwell = TimeSpan.FromSeconds(3);

    private readonly MapData _map;
    private readonly List<Camp> _camps;
    private readonly Dictionary<int, Dwell> _dwells = [];
    private TimeSpan? _clock;
    private int? _souls;
    private Vector2? _position;
    private Visit? _visit;

    public CampTracker(MapData map)
    {
        _map = map;
        // The mid-boss is an objective with its own HUD timer, not a farm camp.
        _camps = map.Camps.Where(c => c.NeutralType != "NEUTRAL_SUPER").Select(c => new Camp(c)).ToList();
    }

    public IReadOnlyList<CampState> Camps => _camps.Select(c => c.State(_clock ?? TimeSpan.Zero)).ToList();

    private static bool PaysPerHit(CampUnit unit) => unit.Class == PaysPerHitClass;

    /// <summary>Feed one update; returns what changed.</summary>
    public IReadOnlyList<CampEvent> Update(CampObservation observation)
    {
        var now = observation.Clock;
        var events = new List<CampEvent>();
        // A clock going back means a new match (or a replay restarting): start over.
        if (_clock is { } previous && now < previous - TimeSpan.FromSeconds(10)) Reset();

        foreach (var camp in _camps)
            if (camp.Advance(now) is { } spawned) events.Add(new CampEvent(now, camp.Info, spawned));

        var local = observation.LocalPlayer;
        var alive = local is { Dead: false };
        var position = alive ? local!.World : null;
        var allies = observation.Heroes
            .Where(h => h is { IsAlly: true, IsLocalPlayer: false, Visible: true, Dead: false, World: not null })
            .Select(h => h.World!.Value).ToList();

        // Gains first, while the visit they belong to is still open (souls can show a moment after the player moves on).
        if (alive && observation.UnspentSouls is { } souls && _souls is { } before && souls > before)
            Attribute(now, souls - before, position, _position, allies, events);
        _souls = observation.UnspentSouls ?? _souls;
        _position = position;

        UpdateVisit(now, position, allies, events);

        foreach (var hero in observation.Heroes.Where(h => !h.IsLocalPlayer)) Watch(now, hero, events);

        _clock = now;
        return events;
    }

    private void Reset()
    {
        foreach (var camp in _camps) camp.Reset();
        _dwells.Clear();
        _souls = null;
        _position = null;
        _visit = null;
    }

    private IEnumerable<(Camp Camp, float Distance)> UpCampsNear(Vector2? position, Vector2? previous = null) =>
        _camps
            .Where(c => c.Status == CampStatus.Up && c.UnitsLeft.Count > 0)
            .Select(c => (Camp: c, Distance: Math.Min(Distance(c, position), Distance(c, previous))))
            .Where(c => c.Distance <= NearDistance)
            .OrderBy(c => c.Distance);

    /// <summary>Match a soul gain of the local player to units of a camp they are next to.</summary>
    private void Attribute(TimeSpan now, int gain, Vector2? position, Vector2? previous, List<Vector2> allies, List<CampEvent> events)
    {
        foreach (var (camp, _) in UpCampsNear(position, previous))
        {
            var sharers = 1 + allies.Count(a => Distance(camp, a) <= NearDistance);
            if (camp.MatchKills(now, gain, sharers) is not { } match) continue;
            camp.Kill(match.Units);
            if (_visit?.Camp == camp) _visit.Kills += match.Units.Count;
            var shared = match.Sharers > 1 ? $", shared {match.Sharers} ways" : "";
            if (camp.UnitsLeft.Count == 0)
            {
                camp.Clear(now, CampClearer.You, ClearEvidence.Kills);
                events.Add(new CampEvent(now, camp.Info, $"cleared by you (+{gain} souls{shared})"));
            }
            else
            {
                events.Add(new CampEvent(now, camp.Info, $"{match.Units.Count} unit(s) killed by you (+{gain} souls{shared}), {camp.UnitsLeft.Count} left"));
            }
            return;
        }

        if (_visit != null)
        {
            _visit.Souls += gain;
            _visit.LastGain = now;
        }
    }

    /// <summary>Follow which camp the local player is at; when they leave one, check whether the visit cleared it.</summary>
    private void UpdateVisit(TimeSpan now, Vector2? position, List<Vector2> allies, List<CampEvent> events)
    {
        var (at, distance) = UpCampsNear(position).FirstOrDefault();
        if (_visit != null && _visit.Camp != at)
        {
            EndVisit(_visit, events);
            _visit = null;
        }
        if (at == null) return;
        _visit ??= new Visit(at);
        _visit.Closest = Math.Min(_visit.Closest, distance);
        _visit.Sharers = Math.Max(_visit.Sharers, 1 + allies.Count(a => Distance(at, a) <= NearDistance));
    }

    private static void EndVisit(Visit visit, List<CampEvent> events)
    {
        var camp = visit.Camp;
        if (camp.Status != CampStatus.Up || camp.UnitsLeft.Count == 0 || visit.LastGain is not { } last) return;
        // Proof the player fought this camp, not just passed it while earning elsewhere.
        var fought = visit.Kills > 0 || (camp.UnitsLeft.Any(PaysPerHit) && visit.Closest <= MeleeDistance);
        var left = camp.SoulsLeft(last);
        if (!fought || left <= 0 || visit.Souls < VisitClearFraction * left / visit.Sharers) return;
        camp.Clear(last, CampClearer.You, ClearEvidence.Souls);
        events.Add(new CampEvent(last, camp.Info, $"cleared by you (+{visit.Souls} souls while there for {left:0} left)"));
    }

    /// <summary>Follow another hero's stay at a camp and their net worth while there.</summary>
    private void Watch(TimeSpan now, HeroSighting hero, List<CampEvent> events)
    {
        var camp = hero is { Visible: true, Dead: false, World: { } at } && _map.DistanceToLane(at) >= LaneMargin
            ? UpCampsNear(at).Select(c => c.Camp).FirstOrDefault()
            : null;
        if (camp == null)
        {
            _dwells.Remove(hero.Slot);
            return;
        }
        if (!hero.IsAlly) camp.EnemySeenAt = now;

        if (!_dwells.TryGetValue(hero.Slot, out var dwell) || dwell.Camp != camp)
        {
            _dwells[hero.Slot] = new Dwell(camp, now, hero.NetWorth);
            return;
        }
        if (now - dwell.Since < MinDwell || dwell.NetWorth is not { } start || hero.NetWorth is not { } current) return;
        if (camp.Info.SoulsAt(now) is null) return; // unknown rewards: no way to tell a clear from passing by

        // The top bar rounds to 100 above 1000 souls ("2.1k"): a real rise of x shows as x ± 50.
        var rise = current - start;
        var rounding = current >= 1000 || start >= 1000 ? 50 : 0;
        if (rise > 0 && rise + rounding >= NetWorthClearFraction * camp.SoulsLeft(now))
        {
            var by = hero.IsAlly ? CampClearer.Ally : CampClearer.Enemy;
            camp.Clear(now, by, ClearEvidence.NetWorth);
            _dwells.Remove(hero.Slot);
            if (_visit?.Camp == camp) _visit = null;
            events.Add(new CampEvent(now, camp.Info, $"probably cleared by {(hero.IsAlly ? "an ally" : "an enemy")} (slot {hero.Slot}, net worth +{rise} at the camp)"));
        }
    }

    private static float Distance(Camp camp, Vector2? at) =>
        at is { } p ? Vector2.Distance(new Vector2(camp.Info.Position.X, camp.Info.Position.Y), p) : float.MaxValue;

    private sealed record Dwell(Camp Camp, TimeSpan Since, int? NetWorth);

    /// <summary>The local player's stay at one camp.</summary>
    private sealed class Visit(Camp camp)
    {
        public Camp Camp { get; } = camp;
        public int Kills { get; set; }
        /// <summary>Gains during the visit not matched to kills.</summary>
        public int Souls { get; set; }
        public TimeSpan? LastGain { get; set; }
        public float Closest { get; set; } = float.MaxValue;
        public int Sharers { get; set; } = 1;
    }

    private sealed record KillMatch(List<CampUnit> Units, int Sharers);

    private sealed class Camp(CampInfo info)
    {
        public CampInfo Info { get; } = info;
        public CampStatus Status { get; private set; } = CampStatus.NotSpawned;
        public TimeSpan UpAt { get; private set; } = TimeSpan.FromSeconds(info.FirstSpawnSeconds);
        public List<CampUnit> UnitsLeft { get; private set; } = [.. info.Units];
        public CampClearer? ClearedBy { get; private set; }
        public TimeSpan? ClearedAt { get; private set; }
        public ClearEvidence? Evidence { get; private set; }
        public TimeSpan? EnemySeenAt { get; set; }

        public void Reset()
        {
            Status = CampStatus.NotSpawned;
            UpAt = TimeSpan.FromSeconds(Info.FirstSpawnSeconds);
            UnitsLeft = [.. Info.Units];
            ClearedBy = null;
            ClearedAt = null;
            Evidence = null;
            EnemySeenAt = null;
        }

        /// <summary>Spawn or respawn when due; returns a description when it happened.</summary>
        public string? Advance(TimeSpan now)
        {
            if (Status == CampStatus.Up || now < UpAt) return null;
            var first = Status == CampStatus.NotSpawned;
            Status = CampStatus.Up;
            UnitsLeft = [.. Info.Units];
            EnemySeenAt = null;
            return first ? "spawned" : "respawned";
        }

        public double SoulsLeft(TimeSpan now) => UnitsLeft.Sum(u => u.SoulsAt(now) ?? 0);

        /// <summary>
        /// The remaining units whose rewards add up to <paramref name="gain"/> (one gain can be several kills), split
        /// between up to <paramref name="maxSharers"/> allies, or null. Rewards are rounded per unit (seen up to 2 souls
        /// under the formula), and other souls can land in the same update.
        /// </summary>
        public KillMatch? MatchKills(TimeSpan now, int gain, int maxSharers)
        {
            var units = UnitsLeft.Where(u => !PaysPerHit(u) && u.Souls != null).ToList();
            var values = units.Select(u => u.SoulsAt(now)!.Value).ToList();
            for (var sharers = 1; sharers <= Math.Min(maxSharers, 4); sharers++)
            {
                List<CampUnit>? best = null;
                for (var mask = 1; mask < 1 << units.Count; mask++)
                {
                    double sum = 0;
                    var subset = new List<CampUnit>();
                    for (var i = 0; i < units.Count; i++)
                    {
                        if ((mask & (1 << i)) == 0) continue;
                        sum += values[i] / sharers;
                        subset.Add(units[i]);
                    }
                    var extra = gain - sum;
                    if (extra >= -Math.Max(2, sum * 0.04) && extra <= ExtraSouls && (best == null || subset.Count < best.Count)) best = subset;
                }
                if (best != null) return new KillMatch(best, sharers);
            }
            return null;
        }

        public void Kill(List<CampUnit> units)
        {
            foreach (var u in units) UnitsLeft.Remove(u);
        }

        public void Clear(TimeSpan now, CampClearer by, ClearEvidence evidence)
        {
            Status = CampStatus.Cleared;
            UnitsLeft = [];
            ClearedBy = by;
            ClearedAt = now;
            Evidence = evidence;
            UpAt = now + TimeSpan.FromSeconds(Info.RespawnSeconds);
        }

        public CampState State(TimeSpan now) => new(
            Info, Status, Status == CampStatus.Up ? null : UpAt,
            Status == CampStatus.Up ? UnitsLeft.Count : 0,
            Status == CampStatus.Up && UnitsLeft.All(u => u.Souls != null) ? SoulsLeft(now) : null,
            ClearedBy, ClearedAt, Evidence, EnemySeenAt);
    }
}
