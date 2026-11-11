using System.Numerics;
using System.Text.Json;
using Minerva.GameState.StaticData;
using Minerva.Match.Camps;

namespace Minerva.Tests;

public class CampTrackerTests
{
    // A weak camp of three 45-soul units (growing 1.08%/min), far from the one lane, which runs along y = 0.
    private static readonly CampInfo Weak = new("weak_camp", "neutral_camp_weak", "NEUTRAL_NPC_WEAK", new Vector3(0, 3000, 0),
        120, 85, 130, [Unit(45), Unit(45), Unit(45)]);

    private static readonly CampInfo Strong = new("strong_camp", "neutral_camp_strong", "NEUTRAL_NPC_STRONG", new Vector3(5000, 3000, 0),
        480, 335, null, [Unit(199), Unit(199), Unit(199)]);

    private static readonly LanePath Lane = new(1, 0, [new Vector3(-10000, 0, 0), new Vector3(10000, 0, 0)]);

    private static CampUnit Unit(double souls) => new("neutral_unit", souls, 1.08);

    private static CampTracker Tracker() => new(MapData.Create("test", [Weak, Strong], [], [Lane]));

    private static CampObservation At(double seconds, int? souls, params HeroSighting[] others) =>
        new(TimeSpan.FromSeconds(seconds), souls, others);

    private static HeroSighting Me(float x, float y) => new(0, true, true, true, false, new Vector2(x, y), null);

    private static HeroSighting Hero(int slot, bool ally, float x, float y, int netWorth) =>
        new(slot, false, ally, true, false, new Vector2(x, y), netWorth);

    private static CampState State(CampTracker t, string name) => t.Camps.Single(c => c.Camp.Name == name);

    [Fact]
    public void Camps_spawn_at_their_first_spawn_time()
    {
        var t = Tracker();
        t.Update(At(119, 600, Me(0, 0)));
        Assert.Equal(CampStatus.NotSpawned, State(t, "weak_camp").Status);
        Assert.Equal(TimeSpan.FromSeconds(120), State(t, "weak_camp").UpAt);

        t.Update(At(121, 600, Me(0, 0)));
        Assert.True(State(t, "weak_camp").IsUp);
        Assert.Equal(CampStatus.NotSpawned, State(t, "strong_camp").Status);
    }

    [Fact]
    public void Soul_gains_matching_unit_rewards_next_to_a_camp_clear_it_and_start_its_respawn()
    {
        var t = Tracker();
        t.Update(At(200, 798, Me(100, 2800)));
        t.Update(At(204, 844, Me(100, 2800))); // 45 × (1 + 1.08% × 3.4 min) ≈ 46.7
        Assert.Equal(2, State(t, "weak_camp").UnitsLeft);

        var events = t.Update(At(208, 936, Me(150, 2900))); // two at once
        var camp = State(t, "weak_camp");
        Assert.Equal(CampStatus.Cleared, camp.Status);
        Assert.Equal(CampClearer.You, camp.ClearedBy);
        Assert.Equal(ClearEvidence.Kills, camp.Evidence);
        Assert.Equal(TimeSpan.FromSeconds(208 + 85), camp.UpAt);
        Assert.Contains(events, e => e.Description.StartsWith("cleared by you"));

        t.Update(At(208 + 86, 936, Me(0, 0)));
        Assert.True(State(t, "weak_camp").IsUp);
        Assert.Equal(3, State(t, "weak_camp").UnitsLeft);
    }

    [Fact]
    public void Gains_that_dont_match_or_happen_away_from_camps_are_ignored()
    {
        var t = Tracker();
        t.Update(At(200, 500, Me(100, 2800)));
        t.Update(At(202, 557, Me(100, 2800))); // a lane trooper's 57, not a 46-soul unit
        t.Update(At(204, 500, Me(100, 2800))); // spent souls
        t.Update(At(205, 500, Me(0, 0)));
        t.Update(At(206, 546, Me(0, 0)));      // the right amount, but in lane (the previous position counts too)
        Assert.Equal(3, State(t, "weak_camp").UnitsLeft);
    }

    [Fact]
    public void A_partly_killed_camp_stays_up_with_fewer_units()
    {
        var t = Tracker();
        t.Update(At(200, 798, Me(100, 2800)));
        t.Update(At(204, 844, Me(100, 2800)));
        t.Update(At(300, 844, Me(0, 0))); // left, long after

        var camp = State(t, "weak_camp");
        Assert.True(camp.IsUp);
        Assert.Equal(2, camp.UnitsLeft);
        Assert.InRange(camp.SoulsLeft!.Value, 90, 100);
    }

    [Fact]
    public void Another_hero_staying_at_a_camp_while_their_net_worth_jumps_probably_cleared_it()
    {
        var t = Tracker();
        t.Update(At(500, null, Hero(7, false, 5100, 3000, 3200)));
        t.Update(At(503, null, Hero(7, false, 5100, 3000, 3300)));
        Assert.True(State(t, "strong_camp").IsUp);
        Assert.Equal(TimeSpan.FromSeconds(503), State(t, "strong_camp").EnemySeenAt); // last seen there

        t.Update(At(506, null, Hero(7, false, 5000, 2950, 3800))); // +600 ≈ the camp's value
        var camp = State(t, "strong_camp");
        Assert.Equal(CampStatus.Cleared, camp.Status);
        Assert.Equal(CampClearer.Enemy, camp.ClearedBy);
        Assert.Equal(ClearEvidence.NetWorth, camp.Evidence);
    }

    [Fact]
    public void Net_worth_rises_next_to_a_lane_are_not_taken_as_camp_clears()
    {
        // A camp beside the lane: heroes there are usually laning, and lane troopers raise net worth too.
        var laneside = Weak with { Name = "lane_camp", Position = new Vector3(0, 300, 0) };
        var t = new CampTracker(MapData.Create("test", [laneside], [], [Lane]));
        t.Update(At(200, null, Hero(3, true, 0, 200, 1100)));
        t.Update(At(204, null, Hero(3, true, 0, 250, 1200)));
        t.Update(At(208, null, Hero(3, true, 50, 200, 1400)));
        Assert.True(t.Camps.Single().IsUp);
        Assert.Null(t.Camps.Single().EnemySeenAt);
    }

    [Fact]
    public void A_clock_going_back_starts_a_new_match()
    {
        var t = Tracker();
        t.Update(At(200, 798, Me(100, 2800)));
        t.Update(At(204, 936, Me(100, 2800)));
        t.Update(At(206, 982, Me(100, 2800)));
        Assert.Equal(CampStatus.Cleared, State(t, "weak_camp").Status);

        t.Update(At(-20, 0, Me(0, 0)));
        Assert.Equal(CampStatus.NotSpawned, State(t, "weak_camp").Status);
    }

    [Fact]
    public void Kills_shared_with_an_ally_at_the_camp_pay_a_share()
    {
        // Camp souls are split between the allies who damaged the unit.
        var withAlly = Tracker();
        withAlly.Update(At(200, 500, Me(100, 2800), Hero(4, true, -100, 2900, 1500)));
        withAlly.Update(At(202, 523, Me(100, 2800), Hero(4, true, -100, 2900, 1500))); // 46.7 / 2
        Assert.Equal(2, State(withAlly, "weak_camp").UnitsLeft);

        var alone = Tracker();
        alone.Update(At(200, 500, Me(100, 2800), Hero(4, true, 0, 0, 1500)));
        alone.Update(At(202, 523, Me(100, 2800), Hero(4, true, 0, 0, 1500))); // the ally is in lane: not a share
        Assert.Equal(3, State(alone, "weak_camp").UnitsLeft);
    }

    [Fact]
    public void A_kill_can_arrive_with_a_few_other_souls()
    {
        var t = Tracker();
        t.Update(At(200, 500, Me(100, 2800)));
        t.Update(At(202, 553, Me(100, 2800))); // 46.7 + 6
        Assert.Equal(2, State(t, "weak_camp").UnitsLeft);
    }

    [Fact]
    public void Leaving_a_camp_after_killing_part_of_it_clears_it_when_the_visit_souls_cover_the_rest()
    {
        // In a recorded match the last kill arrived together with a lane trooper's souls (+107 = 47 + 60).
        var t = Tracker();
        t.Update(At(300, 500, Me(100, 2800)));
        t.Update(At(302, 547, Me(100, 2800)));  // a kill
        t.Update(At(304, 594, Me(100, 2800)));  // a kill
        t.Update(At(306, 701, Me(150, 2900)));  // the last kill plus something else
        Assert.True(State(t, "weak_camp").IsUp);  // still there: undecided
        t.Update(At(310, 701, Me(0, 0)));        // left

        var camp = State(t, "weak_camp");
        Assert.Equal(CampStatus.Cleared, camp.Status);
        Assert.Equal(ClearEvidence.Souls, camp.Evidence);
        Assert.Equal(TimeSpan.FromSeconds(306), camp.ClearedAt); // the last gain there
    }

    [Fact]
    public void Souls_earned_near_a_camp_without_killing_any_of_it_dont_clear_it()
    {
        var t = Tracker();
        t.Update(At(300, 500, Me(100, 2800)));
        t.Update(At(302, 700, Me(100, 2800))); // a hero kill, say
        t.Update(At(310, 700, Me(0, 0)));
        Assert.True(State(t, "weak_camp").IsUp);
        Assert.Equal(3, State(t, "weak_camp").UnitsLeft);
    }

    [Fact]
    public void A_sinners_sacrifice_pays_per_hit_and_is_cleared_by_visiting_it_in_melee_range()
    {
        // Seen in a recorded match: +34, +34, +68 ... while next to a 170-soul machine.
        var vault = new CampInfo("vault", "neutral_camp_vaults", "NEUTRAL_VAULT", new Vector3(-5000, 3000, 0), 480, 300, null,
            [new CampUnit("neutral_sinners_sacrifice", 155, 1.08, CampTracker.PaysPerHitClass)]);
        CampTracker Run(float distance)
        {
            var t = new CampTracker(MapData.Create("test", [vault], [], [Lane]));
            var souls = 500;
            t.Update(At(540, souls, Me(-5000 + distance, 3000)));
            foreach (var gain in new[] { 34, 34, 68, 100 })
                t.Update(At(540 + gain, souls += gain, Me(-5000 + distance, 3000)));
            t.Update(At(700, souls, Me(0, 0)));
            return t;
        }

        Assert.Equal(CampStatus.Cleared, Run(100).Camps.Single().Status);
        Assert.True(Run(800).Camps.Single().IsUp); // near, but never in melee range: someone else's souls
    }

    /// <summary>The local player's timeline from a recorded bot match on build 6860 (positions, souls; no names).</summary>
    [Fact]
    public void Recorded_match_finds_the_one_camp_the_player_cleared()
    {
        var observations = JsonSerializer.Deserialize<List<CampObservation>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "camp_observations_build6860.json")),
            new JsonSerializerOptions { IncludeFields = true })!;
        var t = new CampTracker(MapData.Load(GameFiles.Install));

        var events = observations.SelectMany(t.Update).ToList();

        // At 3:24–3:31 the player killed park_club_camp's three weak units for +46 each; everything else was laning.
        var cleared = Assert.Single(t.Camps, c => c.Status == CampStatus.Cleared);
        Assert.Equal("park_club_camp", cleared.Camp.Name);
        Assert.Equal(CampClearer.You, cleared.ClearedBy);
        Assert.InRange(cleared.ClearedAt!.Value.TotalSeconds, 205, 215);
        Assert.Equal(cleared.ClearedAt + TimeSpan.FromSeconds(cleared.Camp.RespawnSeconds), cleared.UpAt);
        Assert.Equal(3, events.Count(e => e.Camp.Name == "park_club_camp" && e.Description.Contains("by you")));
        Assert.DoesNotContain(events, e => e.Description.Contains("probably"));
    }

    /// <summary>A 10-minute recorded bot match: two small-camp clears, a medium and a large in the theater, a vault.</summary>
    [Fact]
    public void Recorded_ten_minute_match_finds_the_players_clears()
    {
        var observations = JsonSerializer.Deserialize<List<CampObservation>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "camp_observations_build6860_10min.json")),
            new JsonSerializerOptions { IncludeFields = true })!;
        var t = new CampTracker(MapData.Load(GameFiles.Install));

        var clears = observations.SelectMany(t.Update).Where(e => e.Description.StartsWith("cleared by you")).ToList();

        TimeSpan[] Cleared(string camp) => clears.Where(e => e.Camp.Name == camp).Select(e => e.Clock).ToArray();
        // park_club_camp twice: three exact kills at 3:22–3:31; after its 1:25 respawn, two exact kills and the last
        // one arriving with a lane trooper's souls (5:17–5:26).
        var club = Cleared("park_club_camp");
        Assert.Equal(2, club.Length);
        Assert.InRange(club[0].TotalSeconds, 3 * 60 + 25, 3 * 60 + 35);
        Assert.InRange(club[1].TotalSeconds, 5 * 60 + 20, 5 * 60 + 30);
        Assert.Single(Cleared("theater_stage_01_camp")); // 82 + 218 + 80: the large camp's three units
        Assert.Single(Cleared("park_club_vault"));       // per-hit payouts of 34 at melee range
        Assert.DoesNotContain(t.Camps, c => c.ClearedBy == CampClearer.Enemy);
    }
}
