using Minerva.GameState.StaticData;

namespace Minerva.Tests;

/// <summary>
/// Map data read from the installed game. Asserts invariants (not exact values) so a balance patch that moves a camp
/// or changes a timer doesn't break the tests, while a broken reader does.
/// </summary>
public class MapDataTests
{
    private static readonly Lazy<MapData> Map = new(() => MapData.Load(GameFiles.Install));

    [Fact]
    public void Camps_come_with_positions_and_timings()
    {
        var camps = Map.Value.Camps;

        Assert.True(camps.Count >= 20, $"only {camps.Count} camps");
        Assert.All(camps, c =>
        {
            Assert.True(c.FirstSpawnSeconds >= 0 && c.RespawnSeconds > 0, $"{c.Name}: {c.FirstSpawnSeconds}/{c.RespawnSeconds}");
            Assert.NotEqual(System.Numerics.Vector3.Zero, c.Position);
            Assert.NotNull(c.NeutralType);
        });
        Assert.Equal(camps.Count, camps.Select(c => c.Name).Distinct().Count());
        // Several camp sizes, each with its own timing from the game's definitions.
        Assert.True(camps.Select(c => c.Subclass).Distinct().Count() >= 3);
        Assert.True(camps.Select(c => c.RespawnSeconds).Distinct().Count() >= 3);
    }

    [Fact]
    public void Camp_names_come_from_the_map_not_from_generated_fallbacks()
    {
        // Entity keys are lowercased by the reader; a case mismatch would leave every name generated.
        Assert.True(Map.Value.Camps.Count(c => !c.Name.Contains('@')) > Map.Value.Camps.Count / 2);
        Assert.Contains(Map.Value.Camps, c => c.TetherRadius > 0);
    }

    [Fact]
    public void Camps_know_their_troopers_and_what_they_pay()
    {
        var camps = Map.Value.Camps.Where(c => c.NeutralType != "NEUTRAL_SUPER").ToList(); // the mid-boss isn't a camp

        // Spawn points name their camp with a prefix; a matching bug would leave camps without units.
        Assert.All(camps, c => Assert.True(c.Units.Count > 0, $"{c.Name} ({c.Subclass}) has no spawn points"));
        Assert.True(camps.Count(c => c.SoulsAt(TimeSpan.Zero) != null) > camps.Count * 3 / 4);
        // Rewards grow with match time, and stronger camps pay more.
        Assert.All(camps.Where(c => c.SoulsAt(TimeSpan.Zero) != null), c => Assert.True(c.SoulsAt(TimeSpan.FromMinutes(20)) > c.SoulsAt(TimeSpan.Zero)));
        double Mean(string type) => camps.Where(c => c.NeutralType == type).Average(c => c.SoulsAt(TimeSpan.Zero) ?? 0);
        Assert.True(Mean("NEUTRAL_NPC_WEAK") < Mean("NEUTRAL_NPC_NORMAL") && Mean("NEUTRAL_NPC_NORMAL") < Mean("NEUTRAL_NPC_STRONG"));
    }

    [Fact]
    public void Objectives_are_placed_symmetrically_for_both_teams()
    {
        var objectives = Map.Value.Objectives;

        Assert.All(objectives, o => Assert.Contains(o.Team, new[] { 2, 3 }));
        foreach (var byClass in objectives.GroupBy(o => o.ClassName))
            Assert.Equal(byClass.Count(o => o.Team == 2), byClass.Count(o => o.Team == 3));

        var walkers = objectives.Where(o => o.ClassName == "npc_boss_tier2").ToList();
        Assert.True(walkers.Count >= 4);
        Assert.All(walkers, w => Assert.NotNull(w.Lane));
    }
}
