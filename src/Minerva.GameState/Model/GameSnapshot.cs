namespace Minerva.GameState.Model;

public readonly record struct Vec3(float X, float Y, float Z)
{
    public float Distance2D(Vec3 other) => MathF.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
    public override string ToString() => $"({X:0},{Y:0},{Z:0})";
}

public enum Team
{
    Unassigned = 0,
    Spectator = 1,
    Amber = 2,
    Sapphire = 3,
    Neutral = 4,
}

/// <summary>A point-in-time view of the match as reconstructed from the demo stream.</summary>
public sealed record GameSnapshot
{
    public required int Tick { get; init; }

    /// <summary>Server time in seconds since map load.</summary>
    public required float ServerTime { get; init; }

    /// <summary>The in-game match clock (what the HUD shows), in seconds; null before the match starts.</summary>
    public required float? MatchClock { get; init; }

    public required string GameState { get; init; }
    public required bool Paused { get; init; }

    /// <summary>True for SourceTV/broadcast demos (all-seeing); false for a player's own POV recording.</summary>
    public required bool IsTvDemo { get; init; }

    /// <summary>The recording player's slot in a POV demo.</summary>
    public required int? LocalPlayerSlot { get; init; }

    public required IReadOnlyList<PlayerState> Players { get; init; }
    public required IReadOnlyList<UnitState> Objectives { get; init; }
    public required IReadOnlyList<NeutralCamp> NeutralCamps { get; init; }
    public required IReadOnlyList<UnitState> Pickups { get; init; }
    public required IReadOnlyList<LaneTroopers> Troopers { get; init; }

    public PlayerState? LocalPlayer => Players.FirstOrDefault(p => p.IsLocal);
}

public sealed record PlayerState
{
    public required int Slot { get; init; }
    public required string Name { get; init; }
    public required Team Team { get; init; }
    public required int HeroId { get; init; }
    public required string Hero { get; init; }
    public required bool IsLocal { get; init; }

    // Scoreboard data (networked for every player).
    public required int Level { get; init; }
    public required int NetWorth { get; init; }
    public required int Kills { get; init; }
    public required int Deaths { get; init; }
    public required int Assists { get; init; }
    public required int LastHits { get; init; }
    public required int Denies { get; init; }
    public required bool Alive { get; init; }
    public required float? RespawnIn { get; init; }
    public required bool UltimateTrained { get; init; }
    public required float? UltimateCooldownRemaining { get; init; }
    public required int AssignedLane { get; init; }
    public required IReadOnlyList<ItemState> Items { get; init; }

    // Hero pawn data; null when the pawn is not networked to us (out of PVS / fog in a POV demo).
    public required bool PawnVisible { get; init; }
    public required Vec3? Position { get; init; }
    public required int? Health { get; init; }
    public required int? MaxHealth { get; init; }

    /// <summary>Raw <c>m_nCurrencies</c> from the pawn; index meaning to be confirmed (souls / ability points / ...).</summary>
    public required IReadOnlyList<int>? Currencies { get; init; }

    public required bool? InShopZone { get; init; }
}

public sealed record ItemState(uint Token, string ClassName, string Name, string? Tier, string? Slot);

public sealed record UnitState
{
    public required string Kind { get; init; }
    public required string Class { get; init; }
    public required Team Team { get; init; }
    public required int Health { get; init; }
    public required int MaxHealth { get; init; }
    public required Vec3 Position { get; init; }
    public required bool Alive { get; init; }
}

/// <summary>Neutral creeps currently networked, clustered by position.</summary>
public sealed record NeutralCamp(Vec3 Center, int Alive, int Health, int MaxHealth);

public sealed record LaneTroopers(Team Team, int Count);
