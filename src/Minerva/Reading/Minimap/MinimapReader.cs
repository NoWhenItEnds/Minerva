using System.Numerics;
using Minerva.GameState.StaticData;
using Minerva.Hud;
using V = Minerva.Match.Minimap.MinimapVocabulary;

namespace Minerva.Match.Minimap;

/// <summary>
/// Reads heroes and objectives from the minimap icons the mod reports (panels from the <c>hud_minimap</c> layout with
/// their probed classes), and places them in world coordinates using a per-match calibration.
/// </summary>
public sealed class MinimapReader(MapData map)
{
    private MinimapCalibration? _calibration;
    private string? _calibratedFor; // landmark set the calibration was fitted to

    public MinimapState Read(IReadOnlyDictionary<string, HudPanel> panels)
    {
        var warnings = new List<string>();
        var icons = panels.Values
            .Where(p => p.Layout == V.Layout && p.HasClass(V.Icon))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
        if (icons.Count == 0)
        {
            warnings.Add($"No minimap icons (panels from '{V.Layout}' with class '{V.Icon}'). Is the mod's class probe for the minimap installed and current?");
            return MinimapState.Empty with { Warnings = warnings };
        }

        // Icons are positioned inside the minimap panel (their parent).
        var minimapPanel = HudPanel.ParentKey(icons[0].Key) is { } parentKey ? panels.GetValueOrDefault(parentKey) : null;
        var size = new Vector2(minimapPanel?.Width ?? 0, minimapPanel?.Height ?? 0);

        static Vector2? Centre(HudPanel p) =>
            p is { X: { } x, Y: { } y, Width: { } w, Height: { } h } ? new Vector2(x + w / 2f, y + h / 2f) : null;
        static int? MinimapTeam(HudPanel p) =>
            p.ClassList.Where(c => c.StartsWith(V.TeamPrefix, StringComparison.Ordinal))
                .Select(c => int.TryParse(c.AsSpan(V.TeamPrefix.Length), out var n) ? n : (int?)null)
                .FirstOrDefault(n => n != null);
        static string? Lane(HudPanel p) =>
            p.ClassList.FirstOrDefault(c => c.EndsWith(V.LaneSuffix, StringComparison.Ordinal) && c.Length > V.LaneSuffix.Length) is { } c
                ? c[..^V.LaneSuffix.Length]
                : null;
        static ObjectiveKind? Kind(HudPanel p) =>
            p.HasClass(V.MidBoss) ? ObjectiveKind.MidBoss
            : p.ClassList.Select(c => V.ObjectiveIcons.TryGetValue(c, out var k) ? k : (ObjectiveKind?)null).FirstOrDefault(k => k != null);

        // Calibrate from landmark icons; refit only when the set of landmarks changes.
        var landmarks = icons
            .Where(p => p.HasClass(V.Boss) && Kind(p) is { } k && V.LandmarkEntities.ContainsKey(k) && MinimapTeam(p) != null && Centre(p) != null)
            .Select(p => new LandmarkIcon(Kind(p)!.Value, MinimapTeam(p)!.Value, Lane(p), Centre(p)!.Value))
            .ToList();
        var signature = string.Join(';', landmarks.Select(l => $"{l.Kind}{l.MinimapTeam}{l.Lane}{l.Minimap}"));
        if (signature != _calibratedFor)
        {
            _calibratedFor = signature;
            _calibration = MinimapCalibrator.Calibrate(landmarks, map.Objectives, Math.Max(size.X, size.Y));
        }
        if (_calibration == null)
            warnings.Add($"Minimap not calibrated: {landmarks.Count} landmark icons did not fit {map.Map}'s objectives (world positions unavailable).");

        Vector2? World(Vector2 m) => _calibration?.MinimapToWorld.Apply(m);
        int? GameTeam(HudPanel p) => MinimapTeam(p) is { } t && _calibration?.MinimapTeamToGameTeam.TryGetValue(t, out var g) == true ? g : null;
        bool OnMap(Vector2 m) => size.X > 0 && m.X >= 0 && m.Y >= 0 && m.X <= size.X && m.Y <= size.Y;

        var heroes = new List<HeroMarker>();
        var objectives = new List<ObjectiveMarker>();
        foreach (var icon in icons)
        {
            if (Centre(icon) is not { } centre) continue; // not laid out / hidden
            if (icon.HasClass(V.Hero))
            {
                var ally = icon.HasClass(V.Ally);
                heroes.Add(new HeroMarker(
                    icon.Key, icon.HasClass(V.LocalPlayer), ally, GameTeam(icon),
                    Visible: ally || icon.HasClass(V.Active),
                    Dead: icon.HasClass(V.Dead),
                    Elevation: icon.HasClass(V.Above) ? 1 : icon.HasClass(V.Below) ? -1 : 0,
                    centre, World(centre), OnMap(centre)));
            }
            else if (Kind(icon) is { } kind)
            {
                objectives.Add(new ObjectiveMarker(icon.Key, kind, GameTeam(icon), Lane(icon),
                    icon.HasClass(V.Active) || icon.HasClass(V.MidBossSpawned), centre, World(centre)));
            }
        }

        if (heroes.Count == 0) warnings.Add($"No hero icons on the minimap (class '{V.Hero}').");
        else if (heroes.All(h => !h.IsLocalPlayer)) warnings.Add($"No hero icon is the local player (class '{V.LocalPlayer}').");
        if (heroes.Count > 0 && heroes.All(h => !h.IsAlly) ) warnings.Add($"No allied hero icons (class '{V.Ally}').");

        return new MinimapState(heroes, objectives, size, _calibration, warnings);
    }
}
