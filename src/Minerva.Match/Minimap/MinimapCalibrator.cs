using System.Numerics;
using Minerva.GameState.StaticData;

namespace Minerva.Match.Minimap;

/// <summary>A landmark icon: an objective whose world position the map's entity lump gives.</summary>
/// <param name="MinimapTeam">The icon's <c>teamN</c> number.</param>
/// <param name="Lane">Lane colour from the icon's <c>&lt;colour&gt;Lane</c> class.</param>
public sealed record LandmarkIcon(ObjectiveKind Kind, int MinimapTeam, string? Lane, Vector2 Minimap);

/// <summary>
/// Fits the minimap ↔ world transform for a match by matching landmark icons (walkers, patrons, barracks) to the
/// same objectives in the map's entity lump. Nothing about the transform is hardcoded: scale, offset, orientation
/// (the minimap can be inverted per team) and which minimap team is which game team all come from the fit.
/// </summary>
public static class MinimapCalibrator
{
    /// <summary>Fits worse than this (as a fraction of the minimap's size) are rejected.</summary>
    public const double MaxRmsFraction = 0.05;

    public static MinimapCalibration? Calibrate(IReadOnlyList<LandmarkIcon> icons, IReadOnlyList<ObjectiveInfo> objectives, float minimapSize)
    {
        var colours = icons.Select(i => i.Lane).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? ColourOf(string name) => colours.FirstOrDefault(c => name.Contains(c, StringComparison.OrdinalIgnoreCase));

        // Several entities can share one icon (a barracks is two guardians): use their centre.
        var groups = objectives
            .Where(o => MinimapVocabulary.LandmarkEntities.Values.Contains(o.ClassName))
            .GroupBy(o => (o.ClassName, o.Team, Lane: ColourOf(o.Name)))
            .Select(g => (g.Key.ClassName, g.Key.Team, g.Key.Lane,
                Position: new Vector2(g.Average(o => o.Position.X), g.Average(o => o.Position.Y))))
            .ToList();

        MinimapCalibration? best = null;
        // Which minimap team is which game team isn't known in advance; try both and keep the better fit.
        foreach (var teamOne in new[] { 2, 3 })
        {
            var teams = new Dictionary<int, int> { [1] = teamOne, [2] = 5 - teamOne };
            var world = new List<Vector2>();
            var minimap = new List<Vector2>();
            var used = new HashSet<int>();
            foreach (var icon in icons)
            {
                if (!MinimapVocabulary.LandmarkEntities.TryGetValue(icon.Kind, out var entityClass)) continue;
                if (!teams.TryGetValue(icon.MinimapTeam, out var team)) continue;
                var candidates = groups
                    .Select((g, index) => (g, index))
                    .Where(x => x.g.ClassName == entityClass && x.g.Team == team &&
                                string.Equals(x.g.Lane, icon.Lane, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (candidates.Count != 1 || !used.Add(candidates[0].index)) continue; // ambiguous: skip
                world.Add(candidates[0].g.Position);
                minimap.Add(icon.Minimap);
            }

            if (Affine2.Fit(world, minimap) is not { } fit) continue;
            var rms = Math.Sqrt(world.Select((w, i) => Vector2.DistanceSquared(fit.Apply(w), minimap[i])).Average());
            if (rms > MaxRmsFraction * minimapSize) continue;
            if (best == null || rms < best.RmsPixels)
                best = new MinimapCalibration(fit, fit.Inverse(), rms, world.Count, teams);
        }
        return best;
    }
}
