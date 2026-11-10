using System.Diagnostics;
using System.Text.RegularExpressions;
using Minerva.GameState.Live;
using SteamDatabase.ValvePak;

namespace Minerva.Mod;

public enum ModState
{
    NotInstalled,
    UpToDate,

    /// <summary>Built for another game build / host layout / script version; rebuild before playing.</summary>
    Stale,
}

public sealed record ModStatus(
    ModState State,
    string? InstalledPath,
    ModManifest? Installed,
    ModManifest Expected,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Installs the generated addon into <c>game/citadel/addons</c> and keeps it in sync with the game build.
/// Only ever writes or deletes files that carry a Minerva manifest.
/// </summary>
public sealed partial class ModInstaller(DeadlockInstall install)
{
    /// <summary>Preferred addon slot. Deadlock Mod Manager fills slots from pak01 upwards, so stay high.</summary>
    public const int PreferredSlot = 90;

    public string AddonsDir => Path.Combine(install.CitadelDir, "addons");

    public string? FindInstalled() =>
        Directory.Exists(AddonsDir)
            ? Directory.EnumerateFiles(AddonsDir, "pak*_dir.vpk").FirstOrDefault(f => ModBuilder.ReadManifest(f) != null)
            : null;

    public ModStatus Status()
    {
        var expected = ModBuilder.ExpectedManifest(install);
        var path = FindInstalled();
        var installed = path == null ? null : ModBuilder.ReadManifest(path);

        var reasons = new List<string>();
        if (installed != null)
        {
            if (installed.GameBuild != expected.GameBuild) reasons.Add($"game build changed ({installed.GameBuild} -> {expected.GameBuild})");
            if (installed.HostLayout != expected.HostLayout || installed.HostLayoutCrc != expected.HostLayoutCrc)
                reasons.Add($"game's {expected.HostLayout} changed");
            if (installed.ScriptSha256 != expected.ScriptSha256) reasons.Add("mod script changed");
        }

        var state = installed == null ? ModState.NotInstalled : reasons.Count == 0 ? ModState.UpToDate : ModState.Stale;
        return new ModStatus(state, path, installed, expected, reasons, Warnings(expected.HostLayout, path));
    }

    /// <summary>Build and install if missing or stale. Returns the status after the operation.</summary>
    public ModStatus Ensure(bool force = false)
    {
        var status = Status();
        if (!force && status.State == ModState.UpToDate) return status;
        Install(status.InstalledPath);
        return Status();
    }

    public string Install(string? existing = null)
    {
        existing ??= FindInstalled();
        var target = existing ?? FreeSlot();
        Directory.CreateDirectory(AddonsDir);

        // Build next to the target, then swap in, so a failed build never leaves a half-written addon.
        var temp = Path.Combine(AddonsDir, $"minerva_build_{Environment.ProcessId}_dir.vpk");
        try
        {
            ModBuilder.Build(install, temp);
            try
            {
                File.Move(temp, target, overwrite: true);
            }
            catch (IOException ex) when (IsGameRunning())
            {
                throw new IOException($"Deadlock has {Path.GetFileName(target)} open. Close the game and run again.", ex);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return target;
    }

    public bool Uninstall()
    {
        var path = FindInstalled();
        if (path == null) return false;
        File.Delete(path);
        return true;
    }

    public static bool IsGameRunning() => Process.GetProcessesByName("deadlock").Length > 0;

    private string FreeSlot()
    {
        for (var slot = PreferredSlot; slot <= 99; slot++)
        {
            var p = Path.Combine(AddonsDir, $"pak{slot:00}_dir.vpk");
            if (!File.Exists(p)) return p;
        }
        throw new IOException("No free addon slot between pak90 and pak99.");
    }

    private IReadOnlyList<string> Warnings(string hostLayout, string? ours)
    {
        var warnings = new List<string>();

        // The game only loads addons when gameinfo.gi mounts citadel/addons (Deadlock Mod Manager adds this; Steam
        // restores the stock file after updates and "verify integrity").
        var gameInfo = Path.Combine(install.CitadelDir, "gameinfo.gi");
        if (!File.Exists(gameInfo) || !AddonsSearchPathRegex().IsMatch(File.ReadAllText(gameInfo)))
            warnings.Add("gameinfo.gi does not mount citadel/addons, so no addons load. Re-apply it with Deadlock Mod Manager.");

        // Another addon overriding the same layout would shadow ours (or be shadowed).
        if (Directory.Exists(AddonsDir))
        {
            foreach (var vpk in Directory.EnumerateFiles(AddonsDir, "pak*_dir.vpk"))
            {
                if (string.Equals(vpk, ours, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var package = new Package();
                    package.Read(vpk);
                    if (package.FindEntry(hostLayout) != null)
                        warnings.Add($"{Path.GetFileName(vpk)} also overrides {hostLayout}; only one of them will load.");
                }
                catch (Exception)
                {
                    // Not a readable VPK (e.g. a chunk archive); ignore.
                }
            }
        }

        if (!LaunchOptionsHaveCondebug())
            warnings.Add("Deadlock launch options do not include -condebug, so console.log is not written and the companion receives nothing.");

        return warnings;
    }

    private static bool LaunchOptionsHaveCondebug()
    {
        // Best effort: Steam keeps per-user launch options in userdata/<id>/config/localconfig.vdf.
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (steam == null) return true; // unknown; don't warn
            var userdata = Path.Combine(steam, "userdata");
            if (!Directory.Exists(userdata)) return true;
            foreach (var config in Directory.EnumerateFiles(userdata, "localconfig.vdf", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(config);
                var key = $"\"{DeadlockInstall.SteamAppId}\"";
                for (var at = text.IndexOf(key, StringComparison.Ordinal); at >= 0; at = text.IndexOf(key, at + 1, StringComparison.Ordinal))
                {
                    // Only look inside this key's own { ... } block.
                    var open = text.IndexOf('{', at);
                    if (open < 0 || text.AsSpan(at + key.Length, open - at - key.Length).Trim().Length != 0) continue;
                    var depth = 0;
                    var close = open;
                    for (; close < text.Length; close++)
                    {
                        if (text[close] == '{') depth++;
                        else if (text[close] == '}' && --depth == 0) break;
                    }
                    var m = LaunchOptionsRegex().Match(text[open..Math.Min(close, text.Length)]);
                    if (m.Success && m.Groups[1].Value.Contains("-condebug", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    [GeneratedRegex(@"^\s*Game\s+citadel/addons\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AddonsSearchPathRegex();

    [GeneratedRegex("\"LaunchOptions\"\\s+\"([^\"]*)\"")]
    private static partial Regex LaunchOptionsRegex();
}
