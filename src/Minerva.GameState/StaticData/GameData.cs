using System.Text.RegularExpressions;
using Minerva.GameState.Live;
using SteamDatabase.ValvePak;

namespace Minerva.GameState.StaticData;

public sealed record HeroInfo(int Id, string ClassName, string DisplayName);

public sealed record AbilityInfo(
    uint Token,
    string ClassName,
    string DisplayName,
    bool IsItem,
    string? ItemTier,
    string? ItemSlot);

/// <summary>
/// Static game definitions (heroes, items/abilities, display names) read straight from the installed game's VPK and
/// localization files, so they always match the build being played. No network access.
/// </summary>
public sealed partial class GameData
{
    private readonly Dictionary<int, HeroInfo> _heroes;
    private readonly Dictionary<uint, AbilityInfo> _abilitiesByToken;

    private GameData(Dictionary<int, HeroInfo> heroes, Dictionary<uint, AbilityInfo> abilities, int? build)
    {
        _heroes = heroes;
        _abilitiesByToken = abilities;
        Build = build;
    }

    /// <summary>Game build (<c>ClientVersion</c> in steam.inf) the data was read from.</summary>
    public int? Build { get; }

    public IReadOnlyCollection<HeroInfo> Heroes => _heroes.Values;
    public IReadOnlyCollection<AbilityInfo> Abilities => _abilitiesByToken.Values;

    public static GameData Empty { get; } = new([], [], null);

    public HeroInfo? Hero(int heroId) => _heroes.GetValueOrDefault(heroId);

    public string HeroName(int heroId) => Hero(heroId)?.DisplayName ?? (heroId == 0 ? "-" : $"hero#{heroId}");

    public AbilityInfo? Ability(uint token) => _abilitiesByToken.GetValueOrDefault(token);

    public static GameData Load(DeadlockInstall install)
    {
        var heroNames = ReadLocalization(Path.Combine(install.LocalizationDir, "citadel_gc_hero_names", "citadel_gc_hero_names_english.txt"));
        var modNames = ReadLocalization(Path.Combine(install.LocalizationDir, "citadel_gc_mod_names", "citadel_gc_mod_names_english.txt"));
        foreach (var (k, v) in ReadLocalization(Path.Combine(install.LocalizationDir, "citadel_mods", "citadel_mods_english.txt")))
            modNames.TryAdd(k, v);

        using var package = new Package();
        package.Read(install.MainVpk);

        var heroes = new Dictionary<int, HeroInfo>();
        foreach (var (className, def) in VData.Read(package, "scripts/heroes.vdata_c"))
        {
            if (!className.StartsWith("hero_", StringComparison.Ordinal)) continue;
            if (!def.TryGetValue("m_HeroID", out var idObj)) continue;
            var id = (int)idObj;
            heroes[id] = new HeroInfo(id, className, heroNames.GetValueOrDefault(className) ?? className["hero_".Length..]);
        }

        var abilities = new Dictionary<uint, AbilityInfo>();
        foreach (var (className, def) in VData.Read(package, "scripts/abilities.vdata_c"))
        {
            var type = VData.GetString(def, "m_eAbilityType");
            var isItem = type == "EAbilityType_Item";
            var info = new AbilityInfo(
                StringToken.Hash(className),
                className,
                modNames.GetValueOrDefault(className) ?? className,
                isItem,
                isItem ? VData.GetString(def, "m_iItemTier") : null,
                isItem ? VData.GetString(def, "m_eItemSlotType") : null);
            abilities[info.Token] = info;
        }

        return new GameData(heroes, abilities, ReadBuild(install));
    }

    private static Dictionary<string, string> ReadLocalization(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            var m = LocLineRegex().Match(line);
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            var colon = key.IndexOf(':');
            if (colon >= 0) key = key[..colon]; // "hero_inferno:n" -> "hero_inferno"
            result.TryAdd(key, m.Groups[2].Value);
        }
        return result;
    }

    private static int? ReadBuild(DeadlockInstall install)
    {
        var inf = Path.Combine(install.CitadelDir, "steam.inf");
        if (!File.Exists(inf)) return null;
        var m = BuildRegex().Match(File.ReadAllText(inf));
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    [GeneratedRegex("^\\s*\"([^\"]+)\"\\s+\"([^\"]*)\"")]
    private static partial Regex LocLineRegex();

    [GeneratedRegex("ClientVersion=(\\d+)")]
    private static partial Regex BuildRegex();
}
