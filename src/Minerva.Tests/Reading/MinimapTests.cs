using System.Numerics;
using System.Text.Json;
using Minerva.GameState.StaticData;
using Minerva.Hud;
using Minerva.Match.Minimap;

namespace Minerva.Tests;

public class MinimapTests
{
    private const string MinimapKey = "/Hud/minimap";
    private const int MinimapSize = 504;

    /// <summary>Icons recorded in a bot match on build 6860 (out/minimap.json from <c>probe hud watch</c>).</summary>
    [Fact]
    public void Recorded_minimap_calibrates_against_the_installed_map()
    {
        var map = MapData.Load(GameFiles.Install);
        var panels = LoadFixture("minimap_icons_build6860.json");

        var state = new MinimapReader(map).Read(panels);

        Assert.Empty(state.Warnings);
        var calibration = Assert.IsType<MinimapCalibration>(state.Calibration);
        Assert.True(calibration.Landmarks >= 10, $"{calibration.Landmarks} landmarks");
        Assert.True(calibration.RmsPixels < 15, $"rms {calibration.RmsPixels:0.0} px");
        Assert.InRange(calibration.WorldUnitsPerPixel, 30, 60);
        Assert.Equal(2, calibration.MinimapTeamToGameTeam[1]); // as fitted by hand from the same data

        Assert.Equal(12, state.Heroes.Count);
        var me = Assert.IsType<HeroMarker>(state.LocalPlayer);
        Assert.True(me.IsAlly);
        Assert.Equal(6, state.Heroes.Count(h => h.IsAlly));
        Assert.All(state.Heroes, h => Assert.Equal(me.Team, h.IsAlly ? h.Team : 5 - h.Team));
        Assert.All(state.Heroes.Where(h => h.OnMap), h =>
        {
            Assert.InRange(h.World!.Value.X, -12000, 12000);
            Assert.InRange(h.World!.Value.Y, -12000, 12000);
        });

        // Every walker icon lands near the walker of the same team and lane in the map data.
        foreach (var walker in state.Objectives.Where(o => o.Kind == ObjectiveKind.Walker))
        {
            var entity = map.Objectives.Single(o => o.ClassName == "npc_boss_tier2" && o.Team == walker.Team &&
                                                    o.Name.Contains(walker.Lane!, StringComparison.OrdinalIgnoreCase));
            var distance = Vector2.Distance(walker.World!.Value, new Vector2(entity.Position.X, entity.Position.Y));
            Assert.True(distance < 15 * calibration.WorldUnitsPerPixel, $"{walker.Lane} walker off by {distance:0} units");
        }
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)] // inverted minimap, and minimap team1 = game team 3
    public void Calibration_recovers_scale_orientation_and_teams(bool inverted, int teamOne)
    {
        var flip = inverted ? -1 : 1;
        var truth = new Affine2(0.024 * flip, 0, 252, 0, -0.024 * flip, 252);
        var objectives = new List<ObjectiveInfo>
        {
            Walker(2, "yellow", -6000, -4700), Walker(2, "blue", -1300, -3500), Walker(2, "purple", 5600, -5000),
            Walker(3, "yellow", -5400, 5000), Walker(3, "blue", 1300, 3500), Walker(3, "purple", 6400, 4800),
            new("npc_boss_tier3", null, 2, null, "npc_boss_tier3", new Vector3(-1280, -8000, 0)),
            new("npc_boss_tier3", null, 3, null, "npc_boss_tier3", new Vector3(1280, 8000, 0)),
        };
        var map = MapDataFor(objectives);

        var panels = new Dictionary<string, HudPanel> { [MinimapKey] = new(MinimapKey, "Panel", true, 42, 42, MinimapSize, MinimapSize, null, null, "hud_minimap") };
        var n = 0;
        void Icon(string classes, Vector2 world)
        {
            var m = truth.Apply(world);
            var key = $"{MinimapKey}/Panel#{n++}";
            panels[key] = new HudPanel(key, "Panel", true, (int)Math.Round(m.X - 20), (int)Math.Round(m.Y - 20), 40, 40, null, null,
                "hud_minimap", ["map_button", .. classes.Split(' ')]);
        }
        string TeamClass(int gameTeam) => gameTeam == teamOne ? "team1" : "team2";
        foreach (var o in objectives)
            Icon($"active boss {(o.ClassName == "npc_boss_tier2" ? "boss_icon_t2" : "boss_icon_t3")} {TeamClass(o.Team)}" +
                 (o.Name.Contains('_') && o.ClassName == "npc_boss_tier2" ? $" {o.Name.Split('_')[^1]}Lane" : ""),
                new Vector2(o.Position.X, o.Position.Y));
        Icon($"active friend localplayer player {TeamClass(3)}", new Vector2(1000, 2000));
        Icon($"enemy player {TeamClass(2)}", new Vector2(-3000, 500));

        var state = new MinimapReader(map).Read(panels);

        Assert.Empty(state.Warnings);
        Assert.Equal(teamOne, state.Calibration!.MinimapTeamToGameTeam[1]);
        var me = state.LocalPlayer!;
        Assert.Equal(3, me.Team);
        Assert.True(Vector2.Distance(new Vector2(1000, 2000), me.World!.Value) < 50, $"local player at {me.World}");
        var enemy = state.Heroes.Single(h => !h.IsAlly);
        Assert.False(enemy.Visible); // no 'active': last known position
        Assert.Equal(2, enemy.Team);
        Assert.True(Vector2.Distance(new Vector2(-3000, 500), enemy.World!.Value) < 50);
    }

    [Fact]
    public void Missing_classes_are_reported_instead_of_returning_nothing_silently()
    {
        var panels = new Dictionary<string, HudPanel>
        {
            [MinimapKey] = new(MinimapKey, "Panel", true, 0, 0, MinimapSize, MinimapSize, null, null, "hud_minimap"),
            [MinimapKey + "/Panel#0"] = new(MinimapKey + "/Panel#0", "Panel", true, 10, 10, 20, 20, null, null, "hud_minimap", ["map_button", "renamed_hero_class"]),
        };

        var state = new MinimapReader(MapDataFor([])).Read(panels);

        Assert.Contains(state.Warnings, w => w.Contains("not calibrated"));
        Assert.Contains(state.Warnings, w => w.Contains("No hero icons"));
        Assert.Contains(new MinimapReader(MapDataFor([])).Read(new Dictionary<string, HudPanel>()).Warnings, w => w.Contains("No minimap icons"));
    }

    private static ObjectiveInfo Walker(int team, string lane, float x, float y) =>
        new("npc_boss_tier2", null, team, null, $"t{team}_boss_{lane}", new Vector3(x, y, 0));

    private static MapData MapDataFor(IReadOnlyList<ObjectiveInfo> objectives) => MapData.Create("test", [], objectives);

    private static Dictionary<string, HudPanel> LoadFixture(string name)
    {
        var rows = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))).RootElement;
        var panels = new Dictionary<string, HudPanel>(StringComparer.Ordinal);
        foreach (var r in rows.EnumerateArray())
        {
            int? Int(string p) => r.GetProperty(p).ValueKind == JsonValueKind.Number ? r.GetProperty(p).GetInt32() : null;
            var key = r.GetProperty("Key").GetString()!;
            panels[key] = new HudPanel(key, "Panel", r.GetProperty("Visible").GetBoolean(), Int("X"), Int("Y"), Int("Width"), Int("Height"),
                null, null, r.GetProperty("Layout").GetString(), r.GetProperty("Classes").GetString()!.Split(' '));
            // The minimap panel itself (the icons' parent) is 504 px square in the recorded HUD.
            var parent = HudPanel.ParentKey(key)!;
            panels.TryAdd(parent, new HudPanel(parent, "HudMinimap", true, 42, 42, MinimapSize, MinimapSize, null, null, "hud_minimap"));
        }
        return panels;
    }
}
