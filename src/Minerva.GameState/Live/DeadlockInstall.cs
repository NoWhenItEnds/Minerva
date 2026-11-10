using System.Text.RegularExpressions;

namespace Minerva.GameState.Live;

/// <summary>Locates the local Deadlock install and the demos it writes.</summary>
public sealed partial class DeadlockInstall
{
    public const int SteamAppId = 1422450;

    private DeadlockInstall(string root) => Root = root;

    /// <summary><c>...\steamapps\common\Deadlock</c></summary>
    public string Root { get; }

    /// <summary><c>...\Deadlock\game\citadel</c> — the mod folder; <c>record foo</c> writes <c>foo.dem</c> here.</summary>
    public string CitadelDir => Path.Combine(Root, "game", "citadel");

    public string MainVpk => Path.Combine(CitadelDir, "pak01_dir.vpk");

    public string LocalizationDir => Path.Combine(CitadelDir, "resource", "localization");

    public string MapsDir => Path.Combine(CitadelDir, "maps");

    /// <summary>The package holding a map's world and entities, e.g. <c>maps\dl_midtown.vpk</c>.</summary>
    public string MapVpk(string map) => Path.Combine(MapsDir, map + ".vpk");

    public static DeadlockInstall FromPath(string path)
    {
        // Accept the install root, game\ or game\citadel\.
        var dir = new DirectoryInfo(path);
        for (var d = dir; d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "game", "citadel", "pak01_dir.vpk")))
                return new DeadlockInstall(d.FullName);
        }
        throw new DirectoryNotFoundException($"No Deadlock install found at or above '{path}'.");
    }

    /// <summary>Find Deadlock through Steam's library folders.</summary>
    public static DeadlockInstall Locate()
    {
        foreach (var library in SteamLibraries())
        {
            var root = Path.Combine(library, "steamapps", "common", "Deadlock");
            if (File.Exists(Path.Combine(root, "game", "citadel", "pak01_dir.vpk")))
                return new DeadlockInstall(root);
        }
        throw new DirectoryNotFoundException("Could not find Deadlock in any Steam library. Pass --game-dir.");
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var steamRoots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string p)
                steamRoots.Add(p.Replace('/', Path.DirectorySeparatorChar));
            steamRoots.Add(@"C:\Program Files (x86)\Steam");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            steamRoots.Add(Path.Combine(home, ".steam", "steam"));
            steamRoots.Add(Path.Combine(home, ".local", "share", "Steam"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steam in steamRoots)
        {
            if (seen.Add(steam)) yield return steam;
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in LibraryPathRegex().Matches(File.ReadAllText(vdf)))
            {
                var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                if (seen.Add(lib)) yield return lib;
            }
        }
    }

    /// <summary>
    /// Wait for the game to start writing a new demo in <see cref="CitadelDir"/> (or a subfolder), i.e. a <c>.dem</c>
    /// file created or growing after <paramref name="since"/>. Downloaded replays in <c>replays\</c> are ignored.
    /// </summary>
    public async Task<string> WaitForNewDemoAsync(DateTime since, CancellationToken ct)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            foreach (var file in EnumerateRecordedDemos())
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < since.ToUniversalTime()) continue;
                // Require the file to be growing so a demo that just finished is not mistaken for a live one.
                if (sizes.TryGetValue(file, out var prev) && info.Length > prev) return file;
                sizes[file] = info.Length;
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    public IEnumerable<string> EnumerateRecordedDemos()
    {
        var replays = Path.Combine(CitadelDir, "replays") + Path.DirectorySeparatorChar;
        var addons = Path.Combine(CitadelDir, "addons") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(CitadelDir, "*.dem", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
            .Where(f => !f.StartsWith(replays, StringComparison.OrdinalIgnoreCase) && !f.StartsWith(addons, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPathRegex();
}
