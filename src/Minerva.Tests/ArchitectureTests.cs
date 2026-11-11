using System.Text.RegularExpressions;

namespace Minerva.Tests;

/// <summary>
/// The library is one project with a folder per layer, so the compiler doesn't keep layers apart; this does. Each
/// layer may only use the namespaces listed for it: what the game files say (GameData) is at the bottom, the in-game
/// mod and the HUD reading build on it, and the advisor sees only the reading layer's results, never its panels.
/// </summary>
public partial class ArchitectureTests
{
    /// <summary>Namespaces each layer may use; <c>.*</c> includes everything under it.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["GameData"] = ["Minerva.GameData.*"],
        ["Mod"] = ["Minerva.GameData.*", "Minerva.Mod.*"],
        ["Reading"] = ["Minerva.GameData.*", "Minerva.Reading.*"],
        // The match state and its trackers; not the panel tree, bindings or minimap internals.
        ["Advisor"] = ["Minerva.GameData.*", "Minerva.Advisor.*", "Minerva.Reading", "Minerva.Reading.Camps", "Minerva.Reading.Timeline"],
    };

    [Fact]
    public void Every_layer_uses_only_the_layers_below_it()
    {
        var library = Path.Combine(RepositoryRoot(), "src", "Minerva");
        var layers = Directory.GetDirectories(library).Select(d => Path.GetFileName(d)!).Where(d => d is not ("bin" or "obj")).ToList();
        foreach (var layer in layers)
            Assert.True(Allowed.ContainsKey(layer), $"New folder '{layer}' in src/Minerva: add it to {nameof(Allowed)} with what it may use.");

        var files = layers.SelectMany(l => Directory.EnumerateFiles(Path.Combine(library, l), "*.cs", SearchOption.AllDirectories).Select(f => (Layer: l, File: f))).ToList();
        // References are matched to the namespaces the library declares (the longest that fits), so a type name after
        // the namespace doesn't matter and new sub-namespaces are covered without changing this test.
        var declared = files.SelectMany(f => File.ReadLines(f.File))
            .Select(l => NamespaceRegex().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Distinct().OrderByDescending(n => n.Length).ToList();

        var violations = new List<string>();
        foreach (var (layer, file) in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (NamespaceRegex().IsMatch(lines[i])) continue;
                foreach (Match m in ReferenceRegex().Matches(lines[i]))
                {
                    var ns = declared.FirstOrDefault(d => m.Value == d || m.Value.StartsWith(d + ".", StringComparison.Ordinal));
                    if (ns != null && !Allowed[layer].Any(a => Covers(a, ns)))
                        violations.Add($"{Path.GetRelativePath(library, file)}:{i + 1} ({layer}) uses {ns}");
                }
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static bool Covers(string allowed, string ns) =>
        allowed.EndsWith(".*", StringComparison.Ordinal)
            ? ns == allowed[..^2] || ns.StartsWith(allowed[..^1], StringComparison.Ordinal)
            : ns == allowed;

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)")]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"\bMinerva(\.\w+)+")]
    private static partial Regex ReferenceRegex();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Minerva.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Minerva.sln not found above the test output.");
    }
}
