using System.Text;
using Minerva.GameState.Model;

namespace Minerva.Probe;

internal static class Fmt
{
    public static string Clock(float seconds) => seconds < 0 ? "-" : $"{(int)seconds / 60:00}:{(int)seconds % 60:00}";
}

internal static class Render
{
    public static void Snapshot(GameSnapshot s)
    {
        var sb = new StringBuilder();
        var clock = s.MatchClock is { } c ? Fmt.Clock(c) : "--:--";
        var pov = s.IsTvDemo ? "TV (all-seeing)" : $"POV slot {s.LocalPlayerSlot} ({s.LocalPlayer?.Name} / {s.LocalPlayer?.Hero})";
        sb.AppendLine($"=== {clock}  {s.GameState}{(s.Paused ? " PAUSED" : "")}  tick {s.Tick}  {pov}");

        foreach (var team in s.Players.GroupBy(p => p.Team))
        {
            sb.AppendLine($"  {team.Key.ToString().ToUpperInvariant(),-9} {"hero",-14} {"lvl",3} {"souls",6} {"K/D/A",8} {"health",11} {"position",-20} items");
            foreach (var p in team)
            {
                var hp = !p.Alive ? $"dead {p.RespawnIn:0}s" : p.Health is { } h ? $"{h}/{p.MaxHealth}" : "?";
                var pos = p.Position?.ToString() ?? (p.PawnVisible ? "?" : "(not visible)");
                var items = string.Join(", ", p.Items.Select(i => i.Name));
                var me = p.IsLocal ? "*" : " ";
                sb.AppendLine($" {me}{Trim(p.Name, 9),-9} {Trim(p.Hero, 14),-14} {p.Level,3} {p.NetWorth,6} {$"{p.Kills}/{p.Deaths}/{p.Assists}",8} {hp,11} {pos,-20} [{p.Items.Count}] {items}");
            }
        }

        if (s.Objectives.Count > 0)
        {
            var obj = s.Objectives
                .GroupBy(o => (o.Kind, o.Team))
                .Select(g => $"{g.Key.Kind}/{g.Key.Team}: {g.Count(o => o.Alive)}/{g.Count()} up" +
                             (g.Key.Kind is "Patron" or "MidBoss" ? $" ({string.Join(",", g.Select(o => $"{o.Health}/{o.MaxHealth}"))})" : ""));
            sb.AppendLine($"  objectives  {string.Join(" | ", obj)}");
        }

        sb.AppendLine($"  neutrals    {s.NeutralCamps.Count} camps networked, {s.NeutralCamps.Sum(c => c.Alive)} creeps" +
                      (s.NeutralCamps.Count > 0 ? $"  e.g. {string.Join(" ", s.NeutralCamps.Take(4).Select(c => $"{c.Center}x{c.Alive}"))}" : ""));
        if (s.Pickups.Count > 0)
            sb.AppendLine($"  pickups     {string.Join(" | ", s.Pickups.GroupBy(p => p.Kind).Select(g => $"{g.Key} x{g.Count()}" + (g.Count() <= 2 ? " @ " + string.Join(" ", g.Select(p => p.Position)) : "")))}");
        sb.AppendLine($"  troopers    {string.Join("  ", s.Troopers.Select(t => $"{t.Team}: {t.Count}"))}");

        Console.Write(sb.ToString());
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
