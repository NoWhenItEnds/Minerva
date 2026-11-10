using System.Numerics;
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
public sealed record CampInfo(
    string Name, string Subclass, string? NeutralType, Vector3 Position,
    double FirstSpawnSeconds, double RespawnSeconds, double? TetherRadius);

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

    private MapData(string map, IReadOnlyList<CampInfo> camps, IReadOnlyList<ObjectiveInfo> objectives)
    {
        Map = map;
        Camps = camps;
        Objectives = objectives;
    }

    public string Map { get; }
    public IReadOnlyList<CampInfo> Camps { get; }
    public IReadOnlyList<ObjectiveInfo> Objectives { get; }

    /// <summary>Map data from another source (tests, cached data).</summary>
    public static MapData Create(string map, IReadOnlyList<CampInfo> camps, IReadOnlyList<ObjectiveInfo> objectives) =>
        new(map, camps, objectives);

    public static MapData Load(DeadlockInstall install, string map = DefaultMap)
    {
        var mapVpk = install.MapVpk(map);
        if (!File.Exists(mapVpk)) throw new FileNotFoundException($"Map package not found: {mapVpk}");

        Dictionary<string, KVObject> definitions;
        using (var main = new Package())
        {
            main.Read(install.MainVpk);
            definitions = VData.Read(main, CampDefinitions).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
        }

        using var package = new Package();
        package.Read(mapVpk);
        var entities = ReadEntities(package, $"maps/{map}/entities/default_ents.vents_c").ToList();

        var camps = new List<CampInfo>();
        var objectives = new List<ObjectiveInfo>();
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
                var name = NonEmpty(Str(e, "CampName"))
                           ?? NonEmpty(EntityLump.RemoveTargetnamePrefix(Str(e, "targetname") ?? ""))
                           ?? $"{subclass}@{position.X:0},{position.Y:0}";
                camps.Add(new CampInfo(Unique(name, camps.Select(c => c.Name)), subclass,
                    def == null ? null : InheritedString(definitions, def, "m_eNeutralType"),
                    position, first.Value, respawn.Value, Positive(Num(e, "TetherRadiusOverride"))));
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

        return new MapData(map, camps, objectives);
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
