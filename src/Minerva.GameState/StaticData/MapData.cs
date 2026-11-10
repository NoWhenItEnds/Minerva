using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Minerva.GameState.Live;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Minerva.GameState.StaticData;

/// <summary>A neutral camp placed in the map.</summary>
/// <param name="Name">The camp's name in the map (or a generated one when the map leaves it empty); unique per map.</param>
/// <param name="Subclass">Camp definition in <c>scripts/misc.vdata</c>, e.g. <c>neutral_camp_medium</c>.</param>
/// <param name="NeutralType">The definition's <c>m_eNeutralType</c>, e.g. <c>NEUTRAL_NPC_NORMAL</c>.</param>
/// <param name="FirstSpawnSeconds">Match time of the first spawn.</param>
/// <param name="RespawnSeconds">Respawn interval.</param>
/// <param name="TetherRadius">How far the camp's troopers chase (world units), when the map sets it.</param>
/// <param name="Units">The troopers the camp spawns, from the map's spawn points for it.</param>
public sealed record CampInfo(
    string Name, string Subclass, string? NeutralType, Vector3 Position,
    double FirstSpawnSeconds, double RespawnSeconds, double? TetherRadius, IReadOnlyList<CampUnit> Units)
{
    /// <summary>Souls for clearing the whole camp at a match time, when every unit's reward is known.</summary>
    public double? SoulsAt(TimeSpan clock) =>
        Units.Count > 0 && Units.All(u => u.Souls != null) ? Units.Sum(u => u.SoulsAt(clock)!.Value) : null;
}

/// <summary>One trooper a camp spawns.</summary>
/// <param name="Subclass">Unit definition in <c>scripts/npc_units.vdata</c>, e.g. <c>neutral_drain_creature_weak</c>.</param>
/// <param name="Souls">Souls for killing it at 0:00 (<c>m_flGoldReward</c>), when the definition has one.</param>
/// <param name="BonusPercentPerMinute">
/// Reward growth (<c>m_flGoldRewardBonusPercentPerMinute</c>): linear, in percent of the base per minute of match time.
/// Checked against a recorded match: a 45-soul unit paid 46 at 3:24.
/// </param>
/// <param name="Class">The unit's entity class (<c>_class</c>), e.g. <c>npc_trooper_neutral</c>.</param>
public sealed record CampUnit(string Subclass, double? Souls, double BonusPercentPerMinute, string? Class = null)
{
    public double? SoulsAt(TimeSpan clock) =>
        Souls * (1 + BonusPercentPerMinute / 100 * Math.Max(0, clock.TotalMinutes));
}

/// <summary>A lane's trooper path (<c>lane_marker_path</c>), as world points along it.</summary>
/// <param name="Lane">The map's lane number (as on objectives).</param>
/// <param name="Slot">Lanes have several parallel paths.</param>
public sealed record LanePath(int Lane, int Slot, IReadOnlyList<Vector3> Points)
{
    /// <summary>Distance in the map plane from a point to the path.</summary>
    public float DistanceTo(Vector2 p)
    {
        var best = float.MaxValue;
        for (var i = 1; i < Points.Count; i++)
        {
            var a = new Vector2(Points[i - 1].X, Points[i - 1].Y);
            var b = new Vector2(Points[i].X, Points[i].Y);
            var ab = b - a;
            var t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            best = Math.Min(best, Vector2.Distance(p, a + t * ab));
        }
        return Points.Count == 1 ? Vector2.Distance(p, new Vector2(Points[0].X, Points[0].Y)) : best;
    }
}

/// <summary>A team-owned NPC placed in the map (walker, patron, barracks guardian, base sentry).</summary>
/// <param name="ClassName">Entity class, e.g. <c>npc_boss_tier2</c>.</param>
/// <param name="Team">Game team number (2 or 3).</param>
/// <param name="Lane">The map's lane number, when set.</param>
/// <param name="Name">The NPC's name in the map, e.g. <c>combine_t2_boss_yellow</c> (often names the lane colour).</param>
public sealed record ObjectiveInfo(string ClassName, string? Subclass, int Team, int? Lane, string Name, Vector3 Position);

/// <summary>
/// Static map data (camps, objectives) read from the map's own package, so it matches the installed build: camp
/// positions and timings and objective positions come from the map's entity lump and the game's vdata, not from
/// hand-entered tables.
/// </summary>
public sealed class MapData
{
    public const string DefaultMap = "dl_midtown";
    private const string CampClass = "info_neutral_trooper_camp";
    private const string CampDefinitions = "scripts/misc.vdata_c";
    private const string SpawnClass = "info_neutral_trooper_spawn";
    private const string UnitDefinitions = "scripts/npc_units.vdata_c";

    private const string LaneClass = "lane_marker_path";

    private MapData(string map, IReadOnlyList<CampInfo> camps, IReadOnlyList<ObjectiveInfo> objectives, IReadOnlyList<LanePath> lanes)
    {
        Map = map;
        Camps = camps;
        Objectives = objectives;
        Lanes = lanes;
    }

    public string Map { get; }
    public IReadOnlyList<CampInfo> Camps { get; }
    public IReadOnlyList<ObjectiveInfo> Objectives { get; }
    public IReadOnlyList<LanePath> Lanes { get; }

    /// <summary>Distance in the map plane from a point to the nearest lane path (infinite without lane data).</summary>
    public float DistanceToLane(Vector2 p) => Lanes.Count == 0 ? float.PositiveInfinity : Lanes.Min(l => l.DistanceTo(p));

    /// <summary>Map data from another source (tests, cached data).</summary>
    public static MapData Create(string map, IReadOnlyList<CampInfo> camps, IReadOnlyList<ObjectiveInfo> objectives, IReadOnlyList<LanePath>? lanes = null) =>
        new(map, camps, objectives, lanes ?? []);

    public static MapData Load(DeadlockInstall install, string map = DefaultMap)
    {
        var mapVpk = install.MapVpk(map);
        if (!File.Exists(mapVpk)) throw new FileNotFoundException($"Map package not found: {mapVpk}");

        Dictionary<string, KVObject> definitions, units;
        using (var main = new Package())
        {
            main.Read(install.MainVpk);
            definitions = VData.Read(main, CampDefinitions).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            units = VData.Read(main, UnitDefinitions).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
        }

        using var package = new Package();
        package.Read(mapVpk);
        var entities = ReadEntities(package, $"maps/{map}/entities/default_ents.vents_c").ToList();

        // Spawn points name their camp (with the map's "[PR#]"-style prefix) and the unit they spawn.
        var campUnits = new Dictionary<string, List<CampUnit>>(StringComparer.Ordinal);
        foreach (var e in entities.Where(e => Str(e, "classname") == SpawnClass))
        {
            if (NonEmpty(EntityLump.RemoveTargetnamePrefix(Str(e, "CampName") ?? "")) is not { } campName ||
                NonEmpty(Str(e, "NeutralSubclass")) is not { } unit)
                continue;
            if (!campUnits.TryGetValue(campName, out var list)) campUnits[campName] = list = [];
            var def = units.GetValueOrDefault(unit);
            list.Add(new CampUnit(unit, Inherited(units, def, "m_flGoldReward"), Inherited(units, def, "m_flGoldRewardBonusPercentPerMinute") ?? 0,
                InheritedString(units, def, "_class")));
        }

        var camps = new List<CampInfo>();
        var objectives = new List<ObjectiveInfo>();
        var lanes = new List<LanePath>();
        foreach (var e in entities)
        {
            var className = Str(e, "classname");
            if (className == null) continue;
            var position = e.GetVector3Property("origin", Vector3.Zero);

            if (className == CampClass)
            {
                var subclass = Str(e, "subclass_name") ?? CampClass;
                var def = definitions.GetValueOrDefault(subclass);
                // The camp entity can override its definition's timing.
                var first = Positive(Num(e, "InitialSpawnDelayInSeconds")) ?? Inherited(definitions, def, "m_iInitialSpawnDelayInSeconds");
                var respawn = Positive(Num(e, "SpawnIntervalInSeconds")) ?? Inherited(definitions, def, "m_iSpawnIntervalInSeconds");
                if (first == null || respawn == null) continue; // not a timed camp (no definition)
                var target = NonEmpty(EntityLump.RemoveTargetnamePrefix(Str(e, "targetname") ?? ""));
                var name = NonEmpty(Str(e, "CampName")) ?? target ?? $"{subclass}@{position.X:0},{position.Y:0}";
                // Spawn points refer to the camp's targetname; some camps also carry a different display name.
                var spawned = (target != null ? campUnits.GetValueOrDefault(target) : null) ?? campUnits.GetValueOrDefault(name) ?? [];
                camps.Add(new CampInfo(Unique(name, camps.Select(c => c.Name)), subclass,
                    def == null ? null : InheritedString(definitions, def, "m_eNeutralType"),
                    position, first.Value, respawn.Value, Positive(Num(e, "TetherRadiusOverride")),
                    spawned));
            }
            else if (className == LaneClass && Num(e, "LaneNum") is { } laneNum && PathPoints(Str(e, "pathNodes"), position) is { Count: > 0 } points)
            {
                lanes.Add(new LanePath((int)laneNum, (int)(Num(e, "LaneSlot") ?? 0), points));
            }
            else if (className.StartsWith("npc_", StringComparison.Ordinal) && Num(e, "teamnumber") is { } team and (2 or 3))
            {
                var name = NonEmpty(Str(e, "BossName"))
                           ?? NonEmpty(EntityLump.RemoveTargetnamePrefix(Str(e, "targetname") ?? ""))
                           ?? className;
                objectives.Add(new ObjectiveInfo(className, Str(e, "subclass_name"), (int)team,
                    Num(e, "LaneNum") is { } lane ? (int)lane : null, name, position));
            }
        }

        return new MapData(map, camps, objectives, lanes);
    }

    /// <summary>
    /// A path's nodes: a text array of nodes, each <c>[x, y, z, in-tangent xyz, out-tangent xyz]</c> relative to the
    /// entity's origin. Only the node positions are used (the path between them is treated as straight).
    /// </summary>
    internal static List<Vector3> PathPoints(string? nodes, Vector3 origin)
    {
        var points = new List<Vector3>();
        if (string.IsNullOrWhiteSpace(nodes)) return points;
        foreach (Match node in Regex.Matches(nodes, @"\[([^\[\]]*)\]"))
        {
            var numbers = node.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (numbers.Length < 3) continue;
            if (!float.TryParse(numbers[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(numbers[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(numbers[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                continue;
            points.Add(origin + new Vector3(x, y, z));
        }
        return points;
    }

    /// <summary>All entities of a lump and its child lumps.</summary>
    private static IEnumerable<EntityLump.Entity> ReadEntities(Package package, string path)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(VData.ReadEntry(package, path)));
        if (resource.DataBlock is not EntityLump lump) throw new InvalidDataException($"'{path}' is not an entity lump.");
        var entities = lump.GetEntities().ToList();
        foreach (var child in lump.GetChildEntityNames())
        {
            var childPath = child.EndsWith("_c", StringComparison.Ordinal) ? child : child + "_c";
            if (package.FindEntry(childPath) != null) entities.AddRange(ReadEntities(package, childPath));
        }
        return entities;
    }

    // vdata definitions name their parent in _base; compiled files are usually flattened, but follow the chain anyway.
    private static double? Inherited(Dictionary<string, KVObject> definitions, KVObject? def, string key)
    {
        for (var d = def; d != null; d = VData.GetString(d, "_base") is { } b ? definitions.GetValueOrDefault(b) : null)
            if (VData.GetNumber(d, key) is { } v) return v;
        return null;
    }

    private static string? InheritedString(Dictionary<string, KVObject> definitions, KVObject? def, string key)
    {
        for (var d = def; d != null; d = VData.GetString(d, "_base") is { } b ? definitions.GetValueOrDefault(b) : null)
            if (VData.GetString(d, key) is { } v) return v;
        return null;
    }

    // The entity lump reader lowercases keys; look them up by the names the map editor shows.
    private static string? Str(EntityLump.Entity e, string key) => VData.GetString(e, key.ToLowerInvariant());

    private static double? Num(EntityLump.Entity e, string key) => VData.GetNumber(e, key.ToLowerInvariant());

    private static double? Positive(double? v) => v > 0 ? v : null;

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Unique(string name, IEnumerable<string> taken)
    {
        var set = taken.ToHashSet(StringComparer.Ordinal);
        if (!set.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!set.Contains($"{name}#{i}")) return $"{name}#{i}";
    }
}
