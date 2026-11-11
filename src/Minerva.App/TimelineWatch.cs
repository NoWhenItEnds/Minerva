using Minerva.GameData;
using Minerva.Reading.Transport;
using Minerva.Reading;
using Minerva.Reading.Camps;
using Minerva.Reading.Timeline;

namespace Minerva.App;

/// <summary>Runs the match reader and timeline over the mirrored HUD tree, for the probe's event output.</summary>
internal sealed class TimelineWatch
{
    private readonly MatchReader _reader;

    public TimelineWatch(DeadlockInstall install, string? map)
    {
        var mapData = MapData.Load(install, map ?? MapData.DefaultMap);
        _reader = new MatchReader(LayoutCatalog.Load(install), GameCatalog.Load(install), mapData);
        Timeline = new MatchTimeline(mapData);
    }

    public MatchTimeline Timeline { get; }

    /// <summary>Read the tree and feed the timeline; returns its events as lines (camp spawns only if asked: they're routine).</summary>
    public IEnumerable<string> Update(HudTree tree, bool spawns = false) =>
        Timeline.Update(_reader.Read(tree.Panels))
            .Where(e => spawns || e is not CampChangeEvent c || !c.Camp.Description.EndsWith("spawned", StringComparison.Ordinal))
            .Select(e => $"{Clock(e.Clock)}  {Kind(e),-9} {e.Describe()}")
            .ToList();

    public void PrintHeroes()
    {
        Console.WriteLine("      slot hero             K  D  A  souls  last seen        status");
        foreach (var h in Timeline.Heroes.OrderByDescending(h => h.Latest.IsAlly).ThenBy(h => h.Slot))
        {
            var who = h.Latest.IsLocalPlayer ? "you  " : h.Latest.IsAlly ? "ally " : "enemy";
            var seen = h.LastSeenAt is { } at && h.LastPosition is { } p ? $"{Clock(at)} ({p.X,6:0},{p.Y,6:0})" : "";
            var status = h.Latest.Dead ? $"dead{(h.RespawnsAt is { } r ? $", back {Clock(r)}" : "")}" : "alive";
            Console.WriteLine($"{who} {h.Slot,4} {h.Latest.Hero?.DisplayName ?? h.Latest.HeroName,-15} {h.Kills,2} {h.Deaths,2} {h.Assists,2} {h.Latest.Row?.Souls,6}  {seen,-22} {status}");
        }
    }

    public void PrintCamps()
    {
        Console.WriteLine("camp                              size     status      up at   souls  last cleared");
        foreach (var c in Timeline.Camps.OrderBy(c => c.Status switch { CampStatus.Up => 0, CampStatus.Cleared => 1, _ => 2 }).ThenBy(c => c.UpAt).ThenBy(c => c.Camp.Name))
            Console.WriteLine($"{c.Camp.Name,-33} {Size(c.Camp),-8} {c.Status,-11} {(c.UpAt is { } at ? Clock(at) : ""),6}  " +
                              $"{(c.SoulsLeft is { } s ? s.ToString("0") : ""),5}  " +
                              $"{(c.ClearedBy is { } by ? $"{by} at {Clock(c.ClearedAt!.Value)} ({c.Evidence})" : "")}" +
                              $"{(c.EnemySeenAt is { } seen ? $"  enemy seen {Clock(seen)}" : "")}");
    }

    private static string Kind(MatchEvent e) => e switch
    {
        KillEvent => "kill",
        LifeEvent l => l.Died ? "death" : "respawn",
        ObjectiveEvent => "objective",
        CampChangeEvent => "camp",
        _ => e.GetType().Name,
    };

    private static string Clock(TimeSpan t) => t < TimeSpan.Zero ? $"-{Clock(-t)}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private static string Size(CampInfo camp) => camp.NeutralType switch
    {
        "NEUTRAL_NPC_WEAK" => "small",
        "NEUTRAL_NPC_NORMAL" => "medium",
        "NEUTRAL_NPC_STRONG" => "large",
        "NEUTRAL_VAULT" => "vault",
        _ => camp.NeutralType ?? "?",
    };
}
