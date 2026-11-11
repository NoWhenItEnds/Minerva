using Minerva.Mod.Formats;
using SteamDatabase.ValvePak;

namespace Minerva.Mod;

/// <summary>
/// Classes the in-game script tests on panels from one layout. Panorama has no way to list a panel's classes, only
/// <c>BHasClass(name)</c>, so the candidates come from the layout's own stylesheets.
/// </summary>
/// <param name="Layout">Layout base name as the script sees it, e.g. <c>hud_minimap</c>.</param>
/// <param name="Gate">
/// The class most selectors in the layout's stylesheets are built on (<c>map_button</c> for minimap icons). Only panels
/// that have it are probed further, which keeps the cost to the panels the stylesheet is about.
/// </param>
/// <param name="Classes">Every class that appears in a compound selector together with <paramref name="Gate"/>.</param>
public sealed record ClassProbe(string Layout, string Gate, IReadOnlyList<string> Classes);

public static class ClassProbes
{
    /// <summary>
    /// Layouts whose panels carry meaning in their classes. Minimap icons: what an icon is (hero, camp, boss, ...) and
    /// its state (enemy, dead, camp up) are only expressed as classes.
    /// </summary>
    public static readonly IReadOnlyList<string> Layouts = ["panorama/layout/hud_minimap.vxml_c"];

    /// <summary>Derive probes from the installed game's files.</summary>
    /// <param name="sharedStyles">Stylesheets to ignore because every HUD layout includes them (the host layout's).</param>
    public static IReadOnlyList<ClassProbe> Derive(Package package, IEnumerable<string> sharedStyles, ICollection<string> warnings)
    {
        var shared = sharedStyles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var probes = new List<ClassProbe>();
        foreach (var layoutPath in Layouts)
        {
            if (Read(package, layoutPath) is not { } layout)
            {
                warnings.Add($"{layoutPath} is missing from the game files, so its panels' classes are not read (renamed in a patch? update ClassProbes.Layouts).");
                continue;
            }

            var sets = new List<IReadOnlySet<string>>();
            foreach (var style in LayoutTree.Includes(PanoramaCompiler.ReadLayoutTree(layout))
                         .Where(s => s.EndsWith(".vcss", StringComparison.OrdinalIgnoreCase) && !shared.Contains(s)))
            {
                if (Read(package, style + "_c") is { } css) sets.AddRange(CssSelectors.CompoundClassSets(PanoramaCompiler.Decompile(css)));
                else warnings.Add($"{style}_c (included by {layoutPath}) is missing from the game files.");
            }

            var gate = sets.SelectMany(s => s).GroupBy(c => c, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;
            if (gate == null)
            {
                warnings.Add($"{layoutPath}'s stylesheets select on no classes; nothing to probe.");
                continue;
            }

            var classes = sets.Where(s => s.Contains(gate)).SelectMany(s => s).Where(c => c != gate)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            probes.Add(new ClassProbe(BaseName(layoutPath), gate, classes));
        }
        return probes;
    }

    /// <summary>
    /// <c>panorama/layout/hud_minimap.vxml_c</c> → <c>hud_minimap</c>, as the script derives it from <c>layoutfile</c>
    /// (which the game reports as <c>panorama\layout\hud_minimap</c>).
    /// </summary>
    public static string BaseName(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static byte[]? Read(Package package, string path)
    {
        if (package.FindEntry(path) is not { } entry) return null;
        package.ReadEntry(entry, out var bytes);
        return bytes;
    }
}
