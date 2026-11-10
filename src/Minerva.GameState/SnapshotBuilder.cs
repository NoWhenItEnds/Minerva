using DemoFile;
using DemoFile.Game.Deadlock;
using Minerva.GameState.Model;
using Minerva.GameState.StaticData;

namespace Minerva.GameState;

/// <summary>Reads the parser's current entity state into a <see cref="GameSnapshot"/>.</summary>
public sealed class SnapshotBuilder(GameData data)
{
    /// <summary>Neutrals closer than this (2D, game units) are treated as one camp.</summary>
    public float CampRadius { get; init; } = 900f;

    /// <summary>Entity class name → kind. Class names come from the demo's own class table (see <c>probe census</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> ObjectiveKinds = new Dictionary<string, string>
    {
        ["CNPC_TrooperBoss"] = "Guardian",
        ["CNPC_Boss_Tier2"] = "Walker",
        ["CNPC_TrooperBarrackBoss"] = "BaseGuardian",
        ["CNPC_Boss_Tier3"] = "Patron",
        ["CNPC_BarrackBoss"] = "BaseGuardian",
        ["CCitadel_Destroyable_Building"] = "Shrine",
        ["CNPC_MidBoss"] = "MidBoss",
    };

    public static readonly IReadOnlyDictionary<string, string> PickupKinds = new Dictionary<string, string>
    {
        ["CCitadelItemPickupIdol"] = "SoulUrn",
        ["CCitadelIdolReturnTrigger"] = "UrnDropoff",
        ["CCitadelItemPickupRejuv"] = "Rejuvenator",
        ["CNPC_Neutral_SinnersSacrifice"] = "SinnersSacrifice",
        ["CItemCrate"] = "Crate",
        ["CCitadelItemPickup"] = "PowerUp",
        ["CCitadelItemPunchableNeutralGold"] = "GoldenStatue",
    };

    public static readonly IReadOnlySet<string> NeutralClasses = new HashSet<string>
    {
        "CNPC_TrooperNeutral",
        "CNPC_TrooperNeutralNodeMover",
    };

    public const string LaneTrooperClass = "CNPC_Trooper";

    public GameSnapshot Build(DeadlockDemoParser demo)
    {
        var now = demo.CurrentGameTime.Value;
        var rules = TryGetRules(demo);
        int? localSlot = demo.IsTvRecording || demo.ServerInfo == null ? null : demo.ServerInfo.PlayerSlot;

        var objectives = new List<UnitState>();
        var pickups = new List<UnitState>();
        var neutrals = new List<UnitState>();
        var troopers = new Dictionary<Team, int>();

        foreach (var entity in demo.Entities)
        {
            if (!entity.IsActive || entity is not CBaseEntity e) continue;
            var cls = entity.ServerClass.Name;

            if (cls == LaneTrooperClass)
            {
                if (IsAlive(e)) troopers[(Team)e.TeamNum] = troopers.GetValueOrDefault((Team)e.TeamNum) + 1;
            }
            else if (ObjectiveKinds.TryGetValue(cls, out var kind))
                objectives.Add(Unit(kind, cls, e));
            else if (PickupKinds.TryGetValue(cls, out kind))
                pickups.Add(Unit(kind, cls, e));
            else if (NeutralClasses.Contains(cls) && IsAlive(e))
                neutrals.Add(Unit("Neutral", cls, e));
        }

        return new GameSnapshot
        {
            Tick = demo.CurrentGameTick.Value is var t ? (int)t : 0,
            ServerTime = now,
            MatchClock = rules == null ? null : MatchClock(rules, demo),
            GameState = rules?.GameState.ToString().Replace("EGameState_", "") ?? "Unknown",
            Paused = rules?.ServerPaused ?? false,
            IsTvDemo = demo.IsTvRecording,
            LocalPlayerSlot = localSlot,
            Players = demo.PlayersIncludingDisconnected
                .Select(c => Player(c, now, localSlot))
                .Where(p => p.Team is Team.Amber or Team.Sapphire)
                .OrderBy(p => p.Team).ThenBy(p => p.Slot)
                .ToList(),
            Objectives = objectives.OrderBy(o => o.Kind).ThenBy(o => o.Team).ToList(),
            NeutralCamps = Cluster(neutrals),
            Pickups = pickups,
            Troopers = troopers.Select(kv => new LaneTroopers(kv.Key, kv.Value)).OrderBy(x => x.Team).ToList(),
        };
    }

    private PlayerState Player(CCitadelPlayerController c, float now, int? localSlot)
    {
        var pd = c.PlayerDataGlobal;
        var pawn = c.HeroPawn;
        var visible = pawn is { IsActive: true };
        var slot = (int)c.EntityIndex.Value - 1;
        var heroId = pd.HeroID.Value;

        var items = new List<ItemState>();
        foreach (var token in pd.Upgrades)
        {
            var info = data.Ability(token.Value);
            if (info is { IsItem: false }) continue; // Upgrades should only hold items; keep unknown tokens visible.
            items.Add(new ItemState(token.Value, info?.ClassName ?? $"0x{token.Value:x8}", info?.DisplayName ?? $"0x{token.Value:x8}",
                info?.ItemTier, info?.ItemSlot));
        }

        var respawn = pd.RespawnTime.Value - now;
        var ultLeft = pd.UltimateCooldownEnd.Value - now;

        return new PlayerState
        {
            Slot = slot,
            Name = c.PlayerName,
            Team = (Team)c.TeamNum,
            HeroId = heroId,
            Hero = data.HeroName(heroId),
            IsLocal = localSlot == slot,
            Level = pd.Level,
            NetWorth = pd.GoldNetWorth,
            Kills = pd.PlayerKills,
            Deaths = pd.Deaths,
            Assists = pd.PlayerAssists,
            LastHits = pd.LastHits,
            Denies = pd.Denies,
            Alive = pd.Alive,
            RespawnIn = !pd.Alive && respawn > 0 ? respawn : null,
            UltimateTrained = pd.UltimateTrained,
            UltimateCooldownRemaining = ultLeft > 0 ? ultLeft : null,
            AssignedLane = c.AssignedLane,
            Items = items,
            PawnVisible = visible,
            Position = visible ? ToVec(pawn!.Origin) : null,
            Health = visible ? pawn!.Health : null,
            MaxHealth = visible ? pawn!.MaxHealth : null,
            Currencies = visible ? pawn!.Currencies.ToArray() : null,
            InShopZone = visible ? pawn!.InItemShopZone : null,
        };
    }

    private static CCitadelGameRules? TryGetRules(DeadlockDemoParser demo)
    {
        try
        {
            return demo.GameRules;
        }
        catch (Exception) // rules proxy not networked yet
        {
            return null;
        }
    }

    /// <summary>
    /// The HUD clock: the server sends the clock value at its last update plus the tick of that update, and the
    /// client extrapolates from there unless paused.
    /// </summary>
    private static float? MatchClock(CCitadelGameRules rules, DeadlockDemoParser demo)
    {
        if (rules.GameState is not (EGameState.EGameState_GameInProgress or EGameState.EGameState_PostGame)) return null;
        var clock = rules.MatchClockAtLastUpdate;
        if (!rules.ServerPaused)
            clock += ((int)demo.CurrentGameTick.Value - rules.MatchClockUpdateTick) / (float)DeadlockDemoParser.TickRate;
        return clock;
    }

    private static bool IsAlive(CBaseEntity e) => e.LifeState == 0 && e.Health > 0;

    private static UnitState Unit(string kind, string cls, CBaseEntity e) => new()
    {
        Kind = kind,
        Class = cls,
        Team = (Team)e.TeamNum,
        Health = e.Health,
        MaxHealth = e.MaxHealth,
        Position = ToVec(e.Origin),
        Alive = IsAlive(e),
    };

    private static Vec3 ToVec(Vector v) => new(v.X, v.Y, v.Z);

    private List<NeutralCamp> Cluster(List<UnitState> neutrals)
    {
        var camps = new List<(Vec3 Sum, List<UnitState> Members)>();
        foreach (var n in neutrals)
        {
            var idx = camps.FindIndex(c => Centroid(c.Sum, c.Members.Count).Distance2D(n.Position) < CampRadius);
            if (idx < 0)
            {
                camps.Add((n.Position, [n]));
            }
            else
            {
                var (sum, members) = camps[idx];
                members.Add(n);
                camps[idx] = (new Vec3(sum.X + n.Position.X, sum.Y + n.Position.Y, sum.Z + n.Position.Z), members);
            }
        }

        return camps
            .Select(c => new NeutralCamp(Centroid(c.Sum, c.Members.Count), c.Members.Count,
                c.Members.Sum(m => m.Health), c.Members.Sum(m => m.MaxHealth)))
            .ToList();
    }

    private static Vec3 Centroid(Vec3 sum, int n) => new(sum.X / n, sum.Y / n, sum.Z / n);
}
