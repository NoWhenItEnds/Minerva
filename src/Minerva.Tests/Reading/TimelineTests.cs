using System.Numerics;
using Minerva.GameState.StaticData;
using Minerva.Hud;
using Minerva.Match;
using Minerva.Match.Bindings;
using Minerva.Match.Camps;
using Minerva.Match.Hud;
using Minerva.Match.Minimap;
using Minerva.Match.Timeline;

namespace Minerva.Tests;

public class TimelineTests
{
    // ---- Kill feed entries (read through a layout like the game's citadel_hud_data_feed_info) ----

    private const string FeedXml = """
        <root>
          <CitadelHudInfoFeed>
            <Panel id="TextContainer">
              <Panel id="KillerContainer">
                <Label class="killerInfo personaName" text="{s:killer_name}" />
                <Panel id="AssistsContainer" />
                <Label class="gold" text="{d:gold}" />
              </Panel>
              <Panel id="VictimContainer">
                <Panel class="soulBagAmount"><Label class="gold" text="{d:gold}" /></Panel>
                <Panel class="bossLaneIcon" />
                <Label class="victimInfo personaName" text="{s:victim_name}" />
              </Panel>
            </Panel>
          </CitadelHudInfoFeed>
        </root>
        """;

    [Fact]
    public void Feed_entries_give_killer_victim_bounty_and_assisting_heroes()
    {
        var catalog = LayoutCatalog.FromXml(new Dictionary<string, string> { [HudVocabulary.KillFeed] = FeedXml });
        const string e = "/Feed/CitadelHudInfoFeed#0";
        var panels = Panels(
            ("/Feed", "Panel", null, null, true),
            (e, "CitadelHudInfoFeed", null, HudVocabulary.KillFeed, true),
            ($"{e}/TextContainer", "Panel", null, null, true),
            ($"{e}/TextContainer/KillerContainer", "Panel", null, null, true),
            ($"{e}/TextContainer/KillerContainer/Label#0", "Label", "KHEPRI", null, true),
            ($"{e}/TextContainer/KillerContainer/AssistsContainer", "Panel", null, null, true),
            ($"{e}/TextContainer/KillerContainer/AssistsContainer/PlayerAssist13", "CitadelHeroImage", null, null, true),
            ($"{e}/TextContainer/KillerContainer/AssistsContainer/PlayerAssist84", "CitadelHeroImage", null, null, true),
            ($"{e}/TextContainer/KillerContainer/Label#2", "Label", "1,092", null, true),
            ($"{e}/TextContainer/VictimContainer", "Panel", null, null, true),
            ($"{e}/TextContainer/VictimContainer/Panel#0", "Panel", null, null, false),
            ($"{e}/TextContainer/VictimContainer/Panel#0/Label#0", "Label", "629", null, true), // hidden and stale
            ($"{e}/TextContainer/VictimContainer/Panel#1", "Panel", null, null, false),
            ($"{e}/TextContainer/VictimContainer/Label#2", "Label", "KELVIN", null, true));

        var entry = Assert.Single(new HudReader(catalog, GameData.Empty).Read(panels).Feed);

        Assert.Equal("KHEPRI", entry.Killer);
        Assert.Equal("KELVIN", entry.Victim);
        Assert.Equal(1092, entry.Souls); // the killer side's amount, not the hidden copy
        Assert.False(entry.VictimIsObjective);
        Assert.Equal([13, 84], entry.AssistHeroIds.Order());
    }

    [Fact]
    public void Feed_names_are_matched_to_heroes_by_player_name_ignoring_case()
    {
        var heroes = new[] { Hero(0, "HAZE", 13, player: "Khepri", local: true), Hero(2, "KELVIN", 12), Hero(5, "INFERNUS", 1) };
        var kill = MatchReader.Resolve(new FeedEntry("k", "KHEPRI", "Kelvin", 500, false, [1]), heroes);

        Assert.Equal(0, kill.KillerSlot);
        Assert.Equal(2, kill.VictimSlot);
        Assert.Equal([5], kill.AssistSlots);

        var byNpc = MatchReader.Resolve(new FeedEntry("k", "GUARDIAN", "KELVIN", 215, false, []), heroes);
        Assert.Null(byNpc.KillerSlot);
        Assert.Equal(2, byNpc.VictimSlot);
    }

    // ---- The timeline ----

    private static readonly MapData NoMap = MapData.Create("test", [], []);

    [Fact]
    public void A_kill_counts_once_however_long_it_stays_in_the_feed()
    {
        var t = new MatchTimeline(NoMap);
        var kill = new FeedEntry("/Feed/CitadelHudInfoFeed#0", "HAZE", "KELVIN", 514, false, []);
        var moved = kill with { Key = "/Feed/CitadelHudInfoFeed#1" }; // a newer entry pushed it down

        var events = new List<MatchEvent>();
        events.AddRange(t.Update(State(100, feed: [kill])));
        events.AddRange(t.Update(State(101.5, feed: [kill])));
        events.AddRange(t.Update(State(103, feed: [moved])));
        events.AddRange(t.Update(State(105, feed: [])));

        var k = Assert.Single(events.OfType<KillEvent>());
        Assert.Equal(TimeSpan.FromSeconds(101.5), k.Clock); // confirmed on its second read
        Assert.Equal("Haze killed Kelvin (514 souls)", k.Describe());
        Assert.Equal(1, t.Heroes.Single(h => h.Slot == 0).Kills);
    }

    [Fact]
    public void A_feed_entry_seen_once_is_not_a_kill()
    {
        // While the feed reflows a read can mix two entries' labels; a real entry stays for seconds.
        var t = new MatchTimeline(NoMap);
        t.Update(State(100, feed: [new FeedEntry("e", "HAZE", "INFERNUS", 514, false, [])]));
        Assert.Empty(t.Update(State(101.5)).OfType<KillEvent>());
    }

    [Fact]
    public void Deaths_and_respawns_come_from_the_respawn_timer()
    {
        var t = new MatchTimeline(NoMap);
        t.Update(State(100));
        var died = Assert.Single(t.Update(State(101.5, kelvinRespawn: 12)).OfType<LifeEvent>());
        Assert.True(died.Died);
        Assert.Equal(TimeSpan.FromSeconds(113.5), t.Heroes.Single(h => h.Slot == 2).RespawnsAt);

        t.Update(State(103, kelvinRespawn: 10));
        var back = Assert.Single(t.Update(State(114)).OfType<LifeEvent>());
        Assert.False(back.Died);
        Assert.Equal(1, t.Heroes.Single(h => h.Slot == 2).Deaths);
    }

    [Fact]
    public void Objectives_are_followed_by_identity_not_by_the_icon_panel_showing_them()
    {
        // When a guardian dies the minimap's icon panels shift: Panel#5 then shows another guardian.
        var t = new MatchTimeline(NoMap);
        ObjectiveMarker Guardian(string key, string lane, bool active, float x) =>
            new(key, ObjectiveKind.Guardian, 2, lane, active, new Vector2(x, 100), null);
        t.Update(State(200, objectives: [Guardian("Panel#5", "yellow", true, 50), Guardian("Panel#6", "blue", true, 250)]));
        var events = t.Update(State(202, objectives: [Guardian("Panel#5", "blue", true, 250), Guardian("Panel#6", "yellow", false, 50)]));

        var down = Assert.Single(events.OfType<ObjectiveEvent>());
        Assert.Equal("yellow", down.Objective.Lane);
        Assert.False(down.Up);
    }

    [Fact]
    public void Souls_from_the_local_players_hero_kill_are_not_taken_for_camp_kills()
    {
        // A 45-soul camp unit next to the player; the gain equals a unit's value but the feed shows a kill of ours.
        var camp = new CampInfo("camp", "neutral_camp_weak", "NEUTRAL_NPC_WEAK", new Vector3(0, 3000, 0), 120, 85, null,
            [new CampUnit("u", 45, 0), new CampUnit("u", 45, 0), new CampUnit("u", 45, 0)]);
        var t = new MatchTimeline(MapData.Create("test", [camp], []));
        var kill = new FeedEntry("e", "HAZE", "KELVIN", 45, false, []);

        t.Update(State(200, souls: 500));
        t.Update(State(202, souls: 545, feed: [kill]));
        t.Update(State(204, souls: 545, feed: [kill]));

        Assert.Equal(3, t.Camps.Single().UnitsLeft);
    }

    private static HeroState Hero(int slot, string name, int id, string? player = null, bool local = false, double? respawn = null)
    {
        var row = new HeroRow($"/TopBar/TopBarPlayer{slot}", local ? 3 : 2, slot, name, new HeroInfo(id, "hero_" + name.ToLowerInvariant(), name[0] + name[1..].ToLowerInvariant()),
            player ?? name, 1000, 5, null, null, null, null, respawn, respawn > 0, 1, 1000, 0, 0);
        var icon = new HeroMarker($"/Map/Panel#{slot + 4}", local, local, local ? 3 : 2, true, respawn > 0, 0, Vector2.Zero,
            local ? new Vector2(0, 3000) : new Vector2(5000, -5000), true);
        return new HeroState(slot, local ? 3 : 2, local, local, row.Hero, name, row, icon);
    }

    private static MatchState State(double seconds, int souls = 500, IReadOnlyList<FeedEntry>? feed = null, double? kelvinRespawn = null,
        IReadOnlyList<ObjectiveMarker>? objectives = null)
    {
        var heroes = new List<HeroState> { Hero(0, "HAZE", 13, player: "Khepri", local: true), Hero(2, "KELVIN", 12, respawn: kelvinRespawn) };
        var clock = TimeSpan.FromSeconds(seconds);
        var hud = new HudState(clock, souls, null, null, null, null, null, heroes.Select(h => h.Row!).ToList(), []);
        return new MatchState(clock, 3, souls, heroes, objectives ?? [], hud, MinimapState.Empty, [])
        {
            Feed = (feed ?? []).Select(e => MatchReader.Resolve(e, heroes)).ToList(),
        };
    }

    private static Dictionary<string, HudPanel> Panels(params (string Key, string Type, string? Text, string? Layout, bool Visible)[] rows)
    {
        var panels = new Dictionary<string, HudPanel>(StringComparer.Ordinal);
        foreach (var (key, type, text, layout, visible) in rows)
        {
            var parentLayout = HudPanel.ParentKey(key) is { } pk && panels.TryGetValue(pk, out var parent) ? parent.Layout : null;
            panels[key] = new HudPanel(key, type, visible, 0, 0, 10, 10, text, null, layout ?? parentLayout);
        }
        return panels;
    }
}
