using System.Text.Json;
using Minerva.GameState.StaticData;
using Minerva.Hud;
using Minerva.Match;
using Minerva.Match.Bindings;
using Minerva.Match.Hud;

namespace Minerva.Tests;

public class HudReaderTests
{
    private const string TopBarXml = """
        <root>
          <styles><include src="s2r://panorama/styles/x.vcss" /></styles>
          <CitadelHudTopBar>
            <Panel class="GameClock"><Label id="GameTime" text="{s:game_clock}" /></Panel>
            <Panel id="TeamScoreFriendly"><Label text="{s:team_networth}" /></Panel>
            <Panel id="TeamScoreEnemy"><Label text="{s:team_networth}" /></Panel>
            <Panel id="TeamsContainer">
              <CitadelHudTopBarTeam id="TeamFriendly" team="2" />
              <CitadelHudTopBarTeam id="TeamEnemy" team="3" />
            </Panel>
          </CitadelHudTopBar>
        </root>
        """;

    private const string TeamXml = """
        <root><CitadelHudTopBarTeam><Panel id="PlayersContainer" /></CitadelHudTopBarTeam></root>
        """;

    private const string PlayerXml = """
        <root>
          <CitadelHudTopBarPlayer>
            <Panel id="HeroContents">
              <Label id="SoulsValue" text="{s:gold}" />
              <Panel class="DeathStatus"><Label class="RespawnTimer" text="{d:respawn_time}" /></Panel>
            </Panel>
            <Panel id="Names">
              <Label class="PlayerName" text="{s:player_name}" />
              <Label class="HeroName" text="{s:hero_name}" />
              <Label text="#Citadel_Static" />
              <Label class="Resist" text="{s:stat_bullet_resist}%" />
            </Panel>
          </CitadelHudTopBarPlayer>
        </root>
        """;

    [Fact]
    public void Live_panels_get_their_layout_variables_by_id_then_type_and_order()
    {
        var catalog = LayoutCatalog.FromXml(new Dictionary<string, string> { ["top"] = TopBarXml, ["player"] = PlayerXml, ["team"] = TeamXml });
        var panels = Panels(
            ("/TopBar", "CitadelHudTopBar", null, "top"),
            ("/TopBar/Panel#1", "Panel", null, null),   // GameClock (no id): matched by type and order
            ("/TopBar/Panel#1/GameTime", "Label", "12:34", null),
            ("/TopBar/TeamsContainer", "Panel", null, null),
            ("/TopBar/TeamsContainer/TeamEnemy", "CitadelHudTopBarTeam", null, "team"), // loads its own layout, as in the game
            ("/TopBar/TeamsContainer/TeamEnemy/P0", "CitadelHudTopBarPlayer", null, "player"),
            ("/TopBar/TeamsContainer/TeamEnemy/P0/Names", "Panel", null, null),
            ("/TopBar/TeamsContainer/TeamEnemy/P0/Names/Label#0", "Label", "LocalPlayer", null),
            ("/TopBar/TeamsContainer/TeamEnemy/P0/Names/Label#1", "Label", "HAZE", null),
            ("/TopBar/TeamsContainer/TeamEnemy/P0/Names/Label#2", "Label", "Static", null),
            ("/TopBar/TeamsContainer/TeamEnemy/P0/Names/Label#3", "Label", "15%", null));

        var hud = new HudBindings(panels, catalog);

        Assert.Equal("12:34", hud.Instances("top").Single().Field("game_clock"));
        var row = hud.Instances("player").Single();
        Assert.Equal("LocalPlayer", row.Field("player_name"));
        Assert.Equal("HAZE", row.Field("hero_name"));
        Assert.Equal("15", row.Field("stat_bullet_resist")); // template "{s:stat_bullet_resist}%"
        Assert.Null(row.Field("gold")); // not on the HUD
        Assert.Equal("3", hud.Placement("/TopBar/TeamsContainer/TeamEnemy")?.Attributes["team"]);
    }

    [Fact]
    public void Hero_rows_take_their_team_from_the_layout_attribute_not_the_container_name()
    {
        var catalog = LayoutCatalog.FromXml(new Dictionary<string, string> { ["citadel_hud_top_bar"] = TopBarXml, ["citadel_hud_top_bar_player"] = PlayerXml });
        var panels = Panels(
            ("/TopBar", "CitadelHudTopBar", null, "citadel_hud_top_bar"),
            ("/TopBar/Panel#1", "Panel", null, null),
            ("/TopBar/Panel#1/GameTime", "Label", "0:-23", null),
            ("/TopBar/TeamsContainer", "Panel", null, null),
            ("/TopBar/TeamsContainer/TeamFriendly", "CitadelHudTopBarTeam", null, null),
            ("/TopBar/TeamsContainer/TeamFriendly/TopBarPlayer3", "CitadelHudTopBarPlayer", null, "citadel_hud_top_bar_player"),
            ("/TopBar/TeamsContainer/TeamFriendly/TopBarPlayer3/HeroContents", "Panel", null, null),
            ("/TopBar/TeamsContainer/TeamFriendly/TopBarPlayer3/HeroContents/SoulsValue", "Label", "2.1k", null),
            ("/TopBar/TeamsContainer/TeamFriendly/TopBarPlayer3/Names", "Panel", null, null),
            ("/TopBar/TeamsContainer/TeamFriendly/TopBarPlayer3/Names/Label#1", "Label", "HAZE", null));

        var state = new HudReader(catalog, GameData.Empty).Read(panels);

        Assert.Equal(TimeSpan.FromSeconds(-23), state.Clock);
        var row = Assert.Single(state.Heroes);
        Assert.Equal(2, row.Team); // TeamFriendly's team="2", whoever the local player is
        Assert.Equal(3, row.Slot);
        Assert.Equal(2100, row.Souls);
        Assert.Equal("HAZE", row.HeroName);
    }

    [Theory]
    [InlineData("3:53", 233)]
    [InlineData("0:-23", -23)]
    [InlineData("1:02:03", 3723)]
    public void Clock_parses_the_HUD_formats(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), HudReader.Clock(text));

    [Theory]
    [InlineData("600", 600)]
    [InlineData("2.1k", 2100)]
    [InlineData("12,345", 12345)]
    [InlineData("<font color='#fff'>1.5k</font>", 1500)]
    public void Souls_parse_abbreviations_and_markup(string text, int souls) => Assert.Equal(souls, HudReader.Souls(text));

    [Fact]
    public void Every_variable_and_id_the_reader_uses_exists_in_the_installed_layouts()
    {
        // The patch detector: fails when Valve renames a dialog variable or element the reader relies on.
        Assert.Empty(HudReader.CheckVocabulary(LayoutCatalog.Load(GameFiles.Install)));
    }

    /// <summary>The top bar, souls and minimap of a recorded bot match on build 6860 (local player's name replaced).</summary>
    [Fact]
    public void Recorded_match_reads_completely_through_the_installed_layouts()
    {
        var install = GameFiles.Install;
        var panels = JsonSerializer.Deserialize<List<HudPanel>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hud_match_build6860.json")))!
            .ToDictionary(p => p.Key, StringComparer.Ordinal);

        var state = new MatchReader(LayoutCatalog.Load(install), GameData.Load(install), MapData.Load(install)).Read(panels);

        // If this fails right after a game patch, the HUD changed: re-record the fixture with `probe hud watch`.
        Assert.Empty(state.Warnings);
        Assert.Equal(TimeSpan.FromSeconds(233), state.Clock);
        Assert.Equal(1048, state.UnspentSouls);
        Assert.Equal(3, state.LocalTeam);
        Assert.Equal(12, state.Heroes.Count);
        var me = Assert.IsType<HeroState>(state.LocalPlayer);
        Assert.Equal("Haze", me.Hero?.DisplayName);
        Assert.Equal(0, me.Slot);
        Assert.All(state.Heroes, h =>
        {
            Assert.NotNull(h.Hero);
            Assert.NotNull(h.Row);
            Assert.NotNull(h.Icon);
            Assert.Equal(h.Row!.Team, h.Icon!.Team);
            Assert.Equal(h.IsAlly, h.Team == state.LocalTeam);
            Assert.True(h.Row.Souls > 0 && h.Row.MaxHealth > 0, $"{h.HeroName}: souls {h.Row.Souls}, max health {h.Row.MaxHealth}");
        });
        Assert.Equal(6, state.Allies.Count());
        Assert.True(state.Hud.FriendlyNetworth > state.Hud.EnemyNetworth); // the local team was ahead (16k vs 12.7k in rows)
    }

    private static Dictionary<string, HudPanel> Panels(params (string Key, string Type, string? Text, string? Layout)[] rows)
    {
        var panels = new Dictionary<string, HudPanel>(StringComparer.Ordinal);
        foreach (var (key, type, text, layout) in rows)
        {
            var parentLayout = HudPanel.ParentKey(key) is { } pk && panels.TryGetValue(pk, out var parent) ? parent.Layout : null;
            panels[key] = new HudPanel(key, type, true, 0, 0, 10, 10, text, null, layout ?? parentLayout);
        }
        return panels;
    }
}
