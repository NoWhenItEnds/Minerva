using System.Numerics;

namespace Minerva.Match.Minimap;

/// <summary>A hero icon on the minimap.</summary>
/// <param name="Key">HUD panel key (stable within a match; icons are reused before the match starts).</param>
/// <param name="Team">Game team number (2 or 3), once calibration has matched minimap teams to game teams.</param>
/// <param name="Visible">Seen now. False for an enemy shown at its last known position.</param>
/// <param name="Elevation">+1 above the local player, -1 below, 0 level.</param>
/// <param name="Minimap">Icon centre in minimap pixels.</param>
/// <param name="World">World X/Y (no height), once calibrated.</param>
/// <param name="OnMap">Inside the minimap's bounds (icons sit off-map before the match starts).</param>
public sealed record HeroMarker(
    string Key, bool IsLocalPlayer, bool IsAlly, int? Team, bool Visible, bool Dead, int Elevation,
    Vector2 Minimap, Vector2? World, bool OnMap);

/// <summary>An objective icon (guardian, walker, patron, barracks, base building, mid-boss).</summary>
/// <param name="Lane">Lane colour (<c>yellow</c>, <c>blue</c>, ...), when the icon has one.</param>
/// <param name="Active">The icon's <c>active</c> class (the objective is up).</param>
public sealed record ObjectiveMarker(
    string Key, ObjectiveKind Kind, int? Team, string? Lane, bool Active, Vector2 Minimap, Vector2? World);

/// <summary>How minimap pixels relate to world coordinates for this match.</summary>
/// <param name="RmsPixels">Root-mean-square landmark error in minimap pixels.</param>
/// <param name="MinimapTeamToGameTeam">Minimap <c>teamN</c> number → game team number.</param>
public sealed record MinimapCalibration(
    Affine2 WorldToMinimap, Affine2 MinimapToWorld, double RmsPixels, int Landmarks,
    IReadOnlyDictionary<int, int> MinimapTeamToGameTeam)
{
    /// <summary>World units per minimap pixel.</summary>
    public double WorldUnitsPerPixel => 1 / WorldToMinimap.Scale;
}

/// <summary>Everything read from the minimap at one moment.</summary>
/// <param name="Size">The minimap's size in pixels.</param>
/// <param name="Warnings">What the reader expected but did not find (e.g. after a patch renamed a class).</param>
public sealed record MinimapState(
    IReadOnlyList<HeroMarker> Heroes,
    IReadOnlyList<ObjectiveMarker> Objectives,
    Vector2 Size,
    MinimapCalibration? Calibration,
    IReadOnlyList<string> Warnings)
{
    public static MinimapState Empty { get; } = new([], [], Vector2.Zero, null, []);

    public HeroMarker? LocalPlayer => Heroes.FirstOrDefault(h => h.IsLocalPlayer);
}
