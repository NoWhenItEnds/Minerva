namespace Minerva.Reading.Minimap;

/// <summary>
/// The Deadlock style classes the minimap reader interprets, in one place. They come from the game's
/// <c>hud_minimap</c> stylesheet (the mod probes every class that stylesheet uses), so a rename in a patch shows up as
/// a <see cref="MinimapState.Warnings"/> entry and is a one-line change here.
/// </summary>
public static class MinimapVocabulary
{
    /// <summary>Layout the minimap's icons come from.</summary>
    public const string Layout = "hud_minimap";

    /// <summary>Every minimap icon has this class.</summary>
    public const string Icon = "map_button";

    public const string Hero = "player";
    public const string Ally = "friend";
    public const string Enemy = "enemy";
    public const string LocalPlayer = "localplayer";

    /// <summary>On enemy heroes: seen now. Without it the icon shows the last known position.</summary>
    public const string Active = "active";

    public const string Dead = "PlayerDead";
    public const string Above = "arrowUp";
    public const string Below = "arrowDown";

    public const string Boss = "boss";
    public const string MidBoss = "mid_boss";
    public const string MidBossSpawned = "midboss_spawned";

    /// <summary><c>team1</c>/<c>team2</c>; which game team number each is, is worked out by calibration.</summary>
    public const string TeamPrefix = "team";

    /// <summary><c>yellowLane</c>, <c>blueLane</c>, ...; the colour also appears in the map's objective names.</summary>
    public const string LaneSuffix = "Lane";

    /// <summary>Objective icon classes and the map entity class each stands for.</summary>
    public static readonly IReadOnlyDictionary<string, ObjectiveKind> ObjectiveIcons = new Dictionary<string, ObjectiveKind>(StringComparer.Ordinal)
    {
        ["boss_icon_t1"] = ObjectiveKind.Guardian,
        ["boss_icon_t2"] = ObjectiveKind.Walker,
        ["boss_icon_t3"] = ObjectiveKind.Patron,
        ["boss_barracks_icon"] = ObjectiveKind.Barracks,
        ["boss_buildingzip"] = ObjectiveKind.Building,
    };

    /// <summary>Map entity classes of objectives that serve as calibration landmarks (fixed and in the entity lump).</summary>
    public static readonly IReadOnlyDictionary<ObjectiveKind, string> LandmarkEntities = new Dictionary<ObjectiveKind, string>
    {
        [ObjectiveKind.Walker] = "npc_boss_tier2",
        [ObjectiveKind.Patron] = "npc_boss_tier3",
        [ObjectiveKind.Barracks] = "npc_barrack_boss",
    };
}

public enum ObjectiveKind
{
    Guardian,
    Walker,
    Patron,
    Barracks,
    Building,
    MidBoss,
}
