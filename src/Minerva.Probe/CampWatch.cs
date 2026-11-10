using Minerva.GameState.Live;
using Minerva.GameState.StaticData;
using Minerva.Hud;
using Minerva.Match;
using Minerva.Match.Camps;

namespace Minerva.Probe;

/// <summary>Runs the match reader and camp tracker over the mirrored HUD tree, for the probe's camp output.</summary>
internal sealed class CampWatch
{
    private readonly MatchReader _reader;
    private readonly CampTracker _tracker;

    public CampWatch(DeadlockInstall install, string? map)
    {
        var mapData = MapData.Load(install, map ?? MapData.DefaultMap);
        _reader = new MatchReader(LayoutCatalog.Load(install), GameData.Load(install), mapData);
        _tracker = new CampTracker(mapData);
    }

    /// <summary>Read the tree and feed the tracker; returns its events as lines (spawns only if asked: they're routine).</summary>
    public IEnumerable<string> Update(HudTree tree, bool spawns = false)
    {
        if (CampObservation.From(_reader.Read(tree.Panels)) is not { } observation) return [];
        return _tracker.Update(observation)
            .Where(e => spawns || !e.Description.EndsWith("spawned", StringComparison.Ordinal))
            .Select(e => $"{Clock(e.Clock)}  {e.Camp.Name} ({Size(e.Camp)}): {e.Description}")
            .ToList();
    }

    public void PrintTable()
    {
        Console.WriteLine("camp                              size     status      up at   souls  last cleared");
        foreach (var c in _tracker.Camps.OrderBy(c => c.Status switch { CampStatus.Up => 0, CampStatus.Cleared => 1, _ => 2 }).ThenBy(c => c.UpAt).ThenBy(c => c.Camp.Name))
            Console.WriteLine($"{c.Camp.Name,-33} {Size(c.Camp),-8} {c.Status,-11} {(c.UpAt is { } at ? Clock(at) : ""),6}  " +
                              $"{(c.SoulsLeft is { } s ? s.ToString("0") : ""),5}  " +
                              $"{(c.ClearedBy is { } by ? $"{by} at {Clock(c.ClearedAt!.Value)} ({c.Evidence})" : "")}" +
                              $"{(c.EnemySeenAt is { } seen ? $"  enemy seen {Clock(seen)}" : "")}");
    }

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private static string Size(CampInfo camp) => camp.NeutralType switch
    {
        "NEUTRAL_NPC_WEAK" => "small",
        "NEUTRAL_NPC_NORMAL" => "medium",
        "NEUTRAL_NPC_STRONG" => "large",
        "NEUTRAL_VAULT" => "vault",
        _ => camp.NeutralType ?? "?",
    };
}
