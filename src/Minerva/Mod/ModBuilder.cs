using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minerva.GameData;
using Minerva.Mod.Formats;
using SteamDatabase.ValvePak;

namespace Minerva.Mod;

/// <summary>What a built mod was generated from; stored inside the VPK as <see cref="ModBuilder.ManifestPath"/>.</summary>
public sealed record ModManifest
{
    public required string Tool { get; init; }
    public required int? GameBuild { get; init; }
    public required string HostLayout { get; init; }

    /// <summary>CRC32 of the game's own compiled host layout the override was derived from.</summary>
    public required uint HostLayoutCrc { get; init; }

    public required string ScriptSha256 { get; init; }
    public required DateTime BuiltUtc { get; init; }
}

/// <summary>
/// Builds the Minerva addon VPK from the installed game's files plus the embedded mod script. Nothing game-specific
/// is stored in this repository: the host layout override is regenerated from whatever layout the current build
/// ships, so a patch only requires re-running the build (which <see cref="ModInstaller"/> does automatically).
/// </summary>
public static class ModBuilder
{
    public const string AddonName = "minerva";
    public const string ScriptSourcePath = "panorama/scripts/minerva/minerva.js";
    public const string ManifestPath = "minerva/manifest.json";

    /// <summary>
    /// Layouts that host our script include, in preference order. <c>base_hud</c> is the root of the in-match HUD
    /// and is tiny, so overriding it carries the least risk of shadowing Valve changes.
    /// </summary>
    public static readonly IReadOnlyList<string> HostLayouts = ["panorama/layout/base_hud.vxml_c"];

    public static string CompiledScriptReference => PanoramaCompiler.CompiledName(ScriptSourcePath); // .../minerva.vjs

    /// <summary>Placeholder in the script template that <see cref="WithProbes"/> replaces with generated data.</summary>
    public const string ProbesPlaceholder = "/*@probes*/{}";

    /// <summary>The script template (no generated data: probes empty).</summary>
    public static string Script
    {
        get
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Minerva.Mod.minerva.js")
                               ?? throw new InvalidOperationException("Embedded mod script missing.");
            return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
        }
    }

    /// <summary>Fill the template's probe table.</summary>
    public static string WithProbes(string script, IEnumerable<ClassProbe> probes)
    {
        if (!script.Contains(ProbesPlaceholder, StringComparison.Ordinal))
            throw new InvalidOperationException($"Mod script has no {ProbesPlaceholder} placeholder.");
        var table = probes.ToDictionary(p => p.Layout, p => new { gate = p.Gate, classes = p.Classes });
        return script.Replace(ProbesPlaceholder, JsonSerializer.Serialize(table), StringComparison.Ordinal);
    }

    /// <summary>The script as installed: the template plus data derived from the installed game's files.</summary>
    public static (string Script, IReadOnlyList<ClassProbe> Probes, IReadOnlyList<string> Warnings) GenerateScript(DeadlockInstall install)
    {
        using var package = OpenGame(install);
        var (_, host) = ReadHostLayout(package, install);
        var warnings = new List<string>();
        var shared = LayoutTree.Includes(PanoramaCompiler.ReadLayoutTree(host));
        var probes = ClassProbes.Derive(package, shared, warnings);
        return (WithProbes(Script, probes), probes, warnings);
    }

    /// <summary>The game's current compiled host layout (path and bytes).</summary>
    public static (string Path, byte[] Bytes) ReadHostLayout(DeadlockInstall install)
    {
        using var package = OpenGame(install);
        return ReadHostLayout(package, install);
    }

    private static Package OpenGame(DeadlockInstall install)
    {
        var package = new Package();
        package.Read(install.MainVpk);
        return package;
    }

    private static (string Path, byte[] Bytes) ReadHostLayout(Package package, DeadlockInstall install)
    {
        foreach (var path in HostLayouts)
        {
            if (package.FindEntry(path) is not { } entry) continue;
            package.ReadEntry(entry, out var bytes);
            return (path, bytes);
        }
        throw new FileNotFoundException(
            $"None of the host layouts ({string.Join(", ", HostLayouts)}) exist in {install.MainVpk}. The HUD layout was renamed; update ModBuilder.HostLayouts.");
    }

    public static ModManifest ExpectedManifest(DeadlockInstall install) => ExpectedManifest(install, GenerateScript(install).Script);

    private static ModManifest ExpectedManifest(DeadlockInstall install, string script)
    {
        var (path, bytes) = ReadHostLayout(install);
        return new ModManifest
        {
            Tool = $"Minerva.Mod {typeof(ModBuilder).Assembly.GetName().Version}",
            GameBuild = ReadGameBuild(install),
            HostLayout = path,
            HostLayoutCrc = Crc32.Compute(bytes),
            // Covers the template and the data derived from game files (e.g. stylesheet classes).
            ScriptSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))),
            BuiltUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Build the addon into <paramref name="outputVpk"/> (a <c>*_dir.vpk</c> path).</summary>
    public static ModManifest Build(DeadlockInstall install, string outputVpk)
    {
        var (hostPath, hostBytes) = ReadHostLayout(install);

        var tree = PanoramaCompiler.ReadLayoutTree(hostBytes);
        LayoutTree.AddScriptInclude(tree, CompiledScriptReference);
        var hostOverride = PanoramaCompiler.ReplaceLayoutTree(hostBytes, tree);

        var script = GenerateScript(install).Script;
        var compiledScript = PanoramaCompiler.CompileScript(AddonName, ScriptSourcePath, script);

        var manifest = ExpectedManifest(install, script);

        using var package = new Package();
        package.AddFile(hostPath, hostOverride);
        package.AddFile(CompiledScriptReference + "_c", compiledScript);
        package.AddFile(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputVpk))!);
        package.Write(outputVpk);
        return manifest;
    }

    /// <summary>Read the manifest from a VPK, or null if the VPK is not a Minerva build.</summary>
    public static ModManifest? ReadManifest(string vpkPath)
    {
        try
        {
            using var package = new Package();
            package.Read(vpkPath);
            if (package.FindEntry(ManifestPath) is not { } entry) return null;
            package.ReadEntry(entry, out var bytes);
            return JsonSerializer.Deserialize<ModManifest>(bytes, ManifestJson);
        }
        catch (Exception) when (File.Exists(vpkPath))
        {
            return null;
        }
    }

    private static int? ReadGameBuild(DeadlockInstall install)
    {
        var inf = Path.Combine(install.CitadelDir, "steam.inf");
        if (!File.Exists(inf)) return null;
        foreach (var line in File.ReadLines(inf))
        {
            if (line.StartsWith("ClientVersion=", StringComparison.Ordinal) && int.TryParse(line["ClientVersion=".Length..], out var v))
                return v;
        }
        return null;
    }

    internal static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };
}
