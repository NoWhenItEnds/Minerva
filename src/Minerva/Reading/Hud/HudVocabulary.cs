namespace Minerva.Reading.Hud;

/// <summary>
/// The layouts, dialog variables and element ids the HUD reader relies on, in one place. All of them are the game's
/// own names from its layout files (e.g. <c>{i:kills}</c> in <c>citadel_hud_top_bar_player</c>); the reader checks
/// them against the installed layouts and reports any a patch removed.
/// </summary>
public static class HudVocabulary
{
    public const string TopBar = "citadel_hud_top_bar";
    public const string GameClock = "game_clock";
    public const string MidBossTimer = "midboss_timer";
    public const string FriendlyKills = "friendly_kills";
    public const string EnemyKills = "enemy_kills";
    public const string TeamNetworth = "team_networth";
    public const string FriendlyScore = "TeamScoreFriendly";
    public const string EnemyScore = "TeamScoreEnemy";

    /// <summary>Attribute on the top bar's team containers giving their game team number.</summary>
    public const string TeamAttribute = "team";

    public const string TopBarPlayer = "citadel_hud_top_bar_player";
    public const string HeroName = "hero_name";
    public const string PlayerName = "player_name";
    public const string Souls = "gold";
    public const string Level = "player_level";
    public const string Kills = "kills";
    public const string Deaths = "deaths";
    public const string Assists = "assists";
    public const string ObjectiveDamage = "objective_damage";
    public const string RespawnTime = "respawn_time";
    public const string HealthFill = "HealthBar_Fill";
    public const string HealthContents = "HealthBar_Contents";

    public const string PlayerDetails = "citadel_hud_top_bar_player_details";
    public const string MaxHealth = "stat_max_health";
    public const string BulletResist = "stat_bullet_resist";
    public const string SpiritResist = "stat_tech_resist";

    public const string SoulsContainer = "hud_gold_and_ap_container";
    public const string UnspentSouls = "hud_cur_gold";

    /// <summary>One kill feed entry (also used for objective kills, under an element named <c>BossDied</c>).</summary>
    public const string KillFeed = "citadel_hud_data_feed_info";
    /// <summary>Persona names: a player's name (for bots, the hero's), or an NPC's localized name.</summary>
    public const string KillerName = "killer_name";
    public const string VictimName = "victim_name";
    /// <summary>The kill's soul bounty, shown on the killer's side (the victim side's copy is hidden and goes stale).</summary>
    public const string FeedSouls = "gold";
    public const string KillerContainer = "KillerContainer";
    public const string AssistsContainer = "AssistsContainer";
    /// <summary>Shown when the victim is an objective (guardian, walker, ...), so no localized names are needed.</summary>
    public const string ObjectiveVictimClass = "bossLaneIcon";
    /// <summary>
    /// The game adds one hero image per assisting hero to <see cref="AssistsContainer"/>, with id
    /// <c>PlayerAssist&lt;hero id&gt;</c> (seen: Haze, hero 13, as <c>PlayerAssist13</c>). Created at runtime, so not in
    /// the layout files and not checked at start.
    /// </summary>
    public const string AssistPrefix = "PlayerAssist";

    /// <summary>Every (layout, dialog variable) the reader uses.</summary>
    public static readonly IReadOnlyList<(string Layout, string Variable)> Variables =
    [
        (TopBar, GameClock), (TopBar, MidBossTimer), (TopBar, FriendlyKills), (TopBar, EnemyKills), (TopBar, TeamNetworth),
        (TopBarPlayer, HeroName), (TopBarPlayer, PlayerName), (TopBarPlayer, Souls), (TopBarPlayer, Level),
        (TopBarPlayer, Kills), (TopBarPlayer, Deaths), (TopBarPlayer, Assists), (TopBarPlayer, ObjectiveDamage), (TopBarPlayer, RespawnTime),
        (PlayerDetails, MaxHealth), (PlayerDetails, BulletResist), (PlayerDetails, SpiritResist),
        (SoulsContainer, UnspentSouls),
        (KillFeed, KillerName), (KillFeed, VictimName), (KillFeed, FeedSouls),
    ];

    /// <summary>Every (layout, class) the reader uses.</summary>
    public static readonly IReadOnlyList<(string Layout, string Class)> Classes =
    [
        (KillFeed, ObjectiveVictimClass),
    ];

    /// <summary>Every (layout, element id) the reader uses.</summary>
    public static readonly IReadOnlyList<(string Layout, string Id)> Ids =
    [
        (TopBar, FriendlyScore), (TopBar, EnemyScore),
        (TopBarPlayer, HealthFill), (TopBarPlayer, HealthContents),
        (KillFeed, KillerContainer), (KillFeed, AssistsContainer),
    ];
}
