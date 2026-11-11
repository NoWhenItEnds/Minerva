using System.Numerics;

namespace Minerva.Reading.Camps;

/// <summary>What the camp tracker needs from one hero in one update.</summary>
/// <param name="Slot">Player slot: identifies the hero across updates.</param>
/// <param name="Visible">Seen now (allies always; enemies outside fog). Only seen positions count.</param>
/// <param name="NetWorth">Souls from the top bar (abbreviated above 1000, e.g. <c>2.1k</c>).</param>
public sealed record HeroSighting(int Slot, bool IsLocalPlayer, bool IsAlly, bool Visible, bool Dead, Vector2? World, int? NetWorth);

/// <summary>One update of the match, reduced to what the camp tracker uses.</summary>
/// <param name="UnspentSouls">The local player's unspent souls (exact).</param>
/// <param name="LocalGainFromKill">
/// The kill feed shows a hero kill the local player made or assisted just now: this update's soul gain is (mostly) the
/// kill's bounty, so it isn't matched to camps.
/// </param>
public sealed record CampObservation(TimeSpan Clock, int? UnspentSouls, IReadOnlyList<HeroSighting> Heroes, bool LocalGainFromKill = false)
{
    private const int LocalSlot = -1;

    public HeroSighting? LocalPlayer => Heroes.FirstOrDefault(h => h.IsLocalPlayer);

    public static CampObservation? From(MatchState state)
    {
        if (state.Clock is not { } clock) return null;
        // Without a slot (rows and icons not linked) a hero can't be followed across updates; the local player still can.
        var heroes = state.Heroes
            .Where(h => h.Slot != null || h.IsLocalPlayer)
            .Select(h => new HeroSighting(h.Slot ?? LocalSlot, h.IsLocalPlayer, h.IsAlly, h.Visible, h.Dead, h.World, h.Row?.Souls))
            .ToList();
        return new CampObservation(clock, state.UnspentSouls, heroes);
    }
}
