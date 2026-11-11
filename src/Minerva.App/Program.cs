using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DemoFile.Game.Deadlock;
using Minerva.GameState;
using Minerva.GameState.Live;
using Minerva.GameState.Model;
using Minerva.GameState.StaticData;
using Minerva.Probe;

var cli = new Args(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return cli.Command switch
    {
        "gamedata" => GameDataCommand(),
        "census" => await CensusCommand(),
        "replay" => await ReplayCommand(),
        "live" => await LiveCommand(),
        "simulate" => await SimulateCommand(),
        "vpk" => VpkCommand(),
        "mod" => ModCommand(),
        "hud" => await HudCommand(),
        "map" => MapCommand(),
        "minimap" => MinimapCommand(),
        "state" => StateCommand(),
        "timeline" => TimelineCommand(),
        _ => Usage(),
    };
}
catch (OperationCanceledException)
{
    return 130;
}

int Usage()
{
    Console.WriteLine("""
        Minerva probe — development CLI.

        Live HUD (in-game mod + console.log):
          probe mod status                     Is the addon installed and built for the current game build?
          probe mod install [--force true]     Build the addon from the current game files and install it (pak90).
          probe mod uninstall                  Remove the addon.
          probe mod preview                    Show the generated host layout override as XML.
          probe mod build [--out <x_dir.vpk>]  Build the addon without installing.
          probe hud watch [--grep <regex>] [--dump <tree.json>] [--from-start false] [--log <console.log>]
                                               Follow console.log and mirror the HUD tree the mod reports.

        Game files:
          probe vpk ls [<vpk>] [<regex>]       List entries (default: the game's pak01_dir.vpk).
          probe vpk cat [<vpk>] <entry>        Print an entry, decompiling compiled resources.
          probe gamedata                       Load heroes/items from the local install and print a summary.
          probe map [--map <name>] [--camps true]
                                               Load a map's camps and objectives (default dl_midtown) and print a summary.

        Interpretation:
          probe minimap [--tree out/hud_tree.json] [--map <name>]
                                               Read heroes and objectives from a HUD tree dump (written by hud watch)
                                               and place them in world coordinates.
          probe state [--tree out/hud_tree.json]
                                               Read named HUD values (clock, souls, each hero's row) from a HUD tree
                                               dump through the game's layout bindings.
          probe timeline [--log <console.log>] [--spawns true]
                                               Replay a session's console.log through the match timeline: kills,
                                               deaths, objectives and camp inferences as they happened, then each
                                               hero's K/D/A and every camp's state. (hud watch prints the same events
                                               live.)

        Demos (offline analysis; `record` is cheat-protected, so not live in matchmaking):
          probe census <demo.dem> [--at <s>]   List entity classes present at match clock <s> (default 600).
          probe replay <demo.dem> [--every <s>] [--json <out.jsonl>]
                                               Parse a finished demo and print a snapshot every <s> match seconds (default 300).
          probe live [--file <demo.dem>] [--every <s>] [--json <out.jsonl>]
                                               Wait for the game to start recording (or follow --file) and print
                                               live snapshots every <s> wall-clock seconds (default 2).
          probe simulate <src.dem> <dst.dem> [--speed <x>] [--skip <s>]
                                               Re-write a demo into <dst> at real-time pace, as the game would while
                                               recording, to test `live` without the game.

        Common options: --game-dir <path to Deadlock>
        """);
    return 1;
}

int MapCommand()
{
    var sw = Stopwatch.StartNew();
    var map = MapData.Load(Install(), cli.Option("map") ?? MapData.DefaultMap);
    Console.WriteLine($"{map.Map}: {map.Camps.Count} camps, {map.Objectives.Count} objectives ({sw.ElapsedMilliseconds} ms)");
    Console.WriteLine();
    Console.WriteLine("camps by type        count  first spawn  respawn");
    foreach (var g in map.Camps.GroupBy(c => (c.Subclass, c.FirstSpawnSeconds, c.RespawnSeconds)).OrderBy(g => g.Key.Subclass).ThenBy(g => g.Key.FirstSpawnSeconds))
        Console.WriteLine($"  {g.Key.Subclass,-20} {g.Count(),3}  {TimeSpan.FromSeconds(g.Key.FirstSpawnSeconds):m\\:ss}  {TimeSpan.FromSeconds(g.Key.RespawnSeconds),10:m\\:ss}");
    if (cli.Option("camps") == "true")
        foreach (var c in map.Camps.OrderBy(c => c.Subclass).ThenBy(c => c.Name))
            Console.WriteLine($"    {c.Name,-40} {c.Subclass,-20} ({c.Position.X,7:0}, {c.Position.Y,7:0}, {c.Position.Z,6:0})  tether {c.TetherRadius?.ToString("0") ?? "-"}");
    Console.WriteLine();
    Console.WriteLine("objectives");
    foreach (var o in map.Objectives.OrderBy(o => o.ClassName).ThenBy(o => o.Team).ThenBy(o => o.Name))
        Console.WriteLine($"  {o.ClassName,-24} team {o.Team}  lane {o.Lane?.ToString() ?? "-"}  {o.Name,-28} ({o.Position.X,7:0}, {o.Position.Y,7:0})");
    return 0;
}

int StateCommand()
{
    var treePath = cli.Option("tree") ?? Path.Combine(Environment.CurrentDirectory, "out", "hud_tree.json");
    var panels = JsonSerializer.Deserialize<List<Minerva.Hud.HudPanel>>(File.ReadAllText(treePath))!
        .ToDictionary(p => p.Key, StringComparer.Ordinal);
    var install = Install();
    var sw = Stopwatch.StartNew();
    var reader = new Minerva.Match.MatchReader(LayoutCatalog.Load(install), GameData.Load(install), MapData.Load(install, cli.Option("map") ?? MapData.DefaultMap));
    var loadMs = sw.ElapsedMilliseconds;
    sw.Restart();
    var state = reader.Read(panels);
    Console.WriteLine($"{panels.Count} panels (load {loadMs} ms, read {sw.ElapsedMilliseconds} ms)");
    foreach (var w in state.Warnings) Console.WriteLine($"warning: {w}");
    var hud = state.Hud;
    Console.WriteLine($"clock {state.Clock}  your team {state.LocalTeam}  unspent souls {state.UnspentSouls}  " +
                      $"kills {hud.FriendlyKills}-{hud.EnemyKills}  networth {hud.FriendlyNetworth} vs {hud.EnemyNetworth}  mid-boss '{hud.MidBossTimer}'");
    Console.WriteLine();
    Console.WriteLine("      slot team hero            souls lvl  K/ D/ A   obj  health      state  position");
    foreach (var h in state.Heroes.OrderByDescending(h => h.IsAlly).ThenBy(h => h.Slot))
    {
        var r = h.Row;
        Console.WriteLine($"{(h.IsLocalPlayer ? "you  " : h.IsAlly ? "ally " : "enemy")} {h.Slot,4} {h.Team,4} {h.HeroName,-14} {r?.Souls,6} {r?.Level,3} " +
                          $"{r?.Kills,2}/{r?.Deaths,2}/{r?.Assists,2} {r?.ObjectiveDamage,5}  " +
                          $"{(r?.HealthFraction is { } f ? $"{f:P0}" : "-"),4} of {r?.MaxHealth?.ToString() ?? "?",4}  " +
                          $"{(h.Dead ? "dead " : h.Visible ? "seen " : "last ")}  " +
                          $"{(h.World is { } w ? $"({w.X,6:0}, {w.Y,6:0})" : "-")}");
    }
    return 0;
}

int TimelineCommand()
{
    // probe timeline [--log <console.log>]: replay a session's log through the match timeline.
    var log = cli.Option("log") ?? Path.Combine(Install().CitadelDir, "console.log");
    var protocol = new Minerva.Hud.HudProtocol();
    var tree = new Minerva.Hud.HudTree();
    var watch = new TimelineWatch(Install(), cli.Option("map"));
    foreach (var raw in File.ReadLines(log))
    {
        if (protocol.Accept(Minerva.Hud.ConsoleLogFollower.Parse(raw, DateTime.UtcNow)) is not { } msg) continue;
        tree.Apply(msg);
        if (msg.Type is "full" or "delta" && tree.Complete)
            foreach (var e in watch.Update(tree, spawns: cli.Option("spawns") == "true")) Console.WriteLine(e);
    }
    Console.WriteLine();
    watch.PrintHeroes();
    Console.WriteLine();
    watch.PrintCamps();
    return 0;
}

int MinimapCommand()
{
    var treePath = cli.Option("tree") ?? Path.Combine(Environment.CurrentDirectory, "out", "hud_tree.json");
    var panels = JsonSerializer.Deserialize<List<Minerva.Hud.HudPanel>>(File.ReadAllText(treePath))!
        .ToDictionary(p => p.Key, StringComparer.Ordinal);
    var map = MapData.Load(Install(), cli.Option("map") ?? MapData.DefaultMap);
    var state = new Minerva.Match.Minimap.MinimapReader(map).Read(panels);

    foreach (var w in state.Warnings) Console.WriteLine($"warning: {w}");
    if (state.Calibration is { } cal)
        Console.WriteLine($"calibrated from {cal.Landmarks} landmarks: rms {cal.RmsPixels:0.0} px, {cal.WorldUnitsPerPixel:0} world units/px, " +
                          $"minimap team1 = game team {cal.MinimapTeamToGameTeam[1]}");

    string Near(System.Numerics.Vector2? world)
    {
        if (world is not { } w) return "";
        var camp = map.Camps.MinBy(c => System.Numerics.Vector2.Distance(w, new(c.Position.X, c.Position.Y)))!;
        return $"  near {camp.Name} ({System.Numerics.Vector2.Distance(w, new(camp.Position.X, camp.Position.Y)):0} u)";
    }
    string At(System.Numerics.Vector2? world) => world is { } w ? $"({w.X,6:0}, {w.Y,6:0})" : "(uncalibrated)";

    Console.WriteLine($"\nheroes ({state.Heroes.Count})");
    foreach (var h in state.Heroes.OrderByDescending(h => h.IsAlly).ThenByDescending(h => h.IsLocalPlayer))
        Console.WriteLine($"  {(h.IsLocalPlayer ? "you  " : h.IsAlly ? "ally " : "enemy")} team {h.Team?.ToString() ?? "?"}  " +
                          $"{(h.Dead ? "dead " : h.Visible ? "seen " : "last ")}{(h.Elevation > 0 ? "above" : h.Elevation < 0 ? "below" : "     ")} " +
                          $"{At(h.World)}{(h.OnMap ? Near(h.World) : "  off map")}");
    Console.WriteLine($"\nobjectives ({state.Objectives.Count})");
    foreach (var o in state.Objectives.OrderBy(o => o.Kind).ThenBy(o => o.Team).ThenBy(o => o.Lane))
        Console.WriteLine($"  {o.Kind,-9} team {o.Team?.ToString() ?? "-"}  {o.Lane ?? "",-7} {(o.Active ? "up  " : "down")}  {At(o.World)}");
    return 0;
}

DeadlockInstall Install() =>
    cli.Option("game-dir") is { } dir ? DeadlockInstall.FromPath(dir) : DeadlockInstall.Locate();

GameData LoadData()
{
    var sw = Stopwatch.StartNew();
    try
    {
        var data = GameData.Load(Install());
        Console.Error.WriteLine($"[gamedata] build {data.Build}: {data.Heroes.Count} heroes, {data.Abilities.Count(a => a.IsItem)} items, {data.Abilities.Count} abilities ({sw.ElapsedMilliseconds} ms)");
        return data;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[gamedata] could not load static data ({ex.Message}); names will be raw ids.");
        return GameData.Empty;
    }
}

int GameDataCommand()
{
    var data = LoadData();
    foreach (var h in data.Heroes.OrderBy(h => h.Id)) Console.WriteLine($"hero {h.Id,3} {h.ClassName,-24} {h.DisplayName}");
    foreach (var g in data.Abilities.Where(a => a.IsItem).GroupBy(a => (a.ItemSlot, a.ItemTier)).OrderBy(g => g.Key.ItemSlot).ThenBy(g => g.Key.ItemTier))
        Console.WriteLine($"items {g.Key.ItemSlot}/{g.Key.ItemTier}: {string.Join(", ", g.Select(a => a.DisplayName).Order())}");
    return 0;
}

async Task<int> CensusCommand()
{
    var path = cli.Positional(0) ?? throw new ArgumentException("census <demo.dem>");
    var at = cli.FloatOption("at") ?? 600f;
    var session = new MatchSession(LoadData());
    var done = false;
    var unresolvedTokens = new HashSet<uint>();

    session.EntitiesUpdated += () =>
    {
        if (done) return;
        var snap = session.Snapshot();
        if (snap.MatchClock is not { } clock || clock < at) return;
        done = true;

        Console.WriteLine($"== entity classes active at match clock {Fmt.Clock(clock)} (tick {snap.Tick}, tv={snap.IsTvDemo})");
        var byClass = session.Parser.Entities.Where(e => e.IsActive).GroupBy(e => e.ServerClass.Name).OrderByDescending(g => g.Count());
        foreach (var g in byClass)
        {
            var teams = string.Join(" ", g.OfType<CBaseEntity>().GroupBy(e => (Team)e.TeamNum).Select(t => $"{t.Key}:{t.Count()}"));
            Console.WriteLine($"{g.Count(),5}  {g.Key,-48} {teams}");
        }
        foreach (var p in snap.Players)
        foreach (var i in p.Items.Where(i => i.Tier == null))
            unresolvedTokens.Add(i.Token);
    };

    await session.ReadFileAsync(path, cts.Token);
    Console.WriteLine(unresolvedTokens.Count == 0
        ? "== all item tokens resolved to item definitions"
        : $"== {unresolvedTokens.Count} unresolved item tokens: {string.Join(", ", unresolvedTokens.Select(t => $"0x{t:x8}"))}");
    return 0;
}

async Task<int> ReplayCommand()
{
    var path = cli.Positional(0) ?? throw new ArgumentException("replay <demo.dem>");
    var every = cli.FloatOption("every") ?? 300f;
    using var json = JsonOut();
    var session = new MatchSession(LoadData());
    var next = 0f;
    var snapshots = 0;

    session.EntitiesUpdated += () =>
    {
        var snap = session.Snapshot();
        if (snap.MatchClock is not { } clock || clock < next) return;
        next = clock + every;
        snapshots++;
        Render.Snapshot(snap);
        json?.Write(snap);
    };

    var sw = Stopwatch.StartNew();
    await session.ReadFileAsync(path, cts.Token);
    var final = session.Snapshot();
    Render.Snapshot(final);
    json?.Write(final);
    Console.WriteLine($"== parsed {new FileInfo(path).Length / 1e6:0} MB in {sw.Elapsed.TotalSeconds:0.0}s ({snapshots + 1} snapshots)");
    return 0;
}

async Task<int> LiveCommand()
{
    var every = TimeSpan.FromSeconds(cli.FloatOption("every") ?? 2f);
    using var json = JsonOut();
    var data = LoadData();

    string path;
    if (cli.Option("file") is { } file)
    {
        path = file;
    }
    else
    {
        var install = Install();
        Console.WriteLine($"Waiting for a new recording in {install.CitadelDir} ...");
        Console.WriteLine("In the game console run:  demo_flush 1; record minerva");
        path = await install.WaitForNewDemoAsync(DateTime.Now.AddSeconds(-5), cts.Token);
    }
    Console.WriteLine($"Following {path}");

    var session = new MatchSession(data);
    TailingFileStream? stream = null;
    var lastPrint = Stopwatch.StartNew();
    var wall = Stopwatch.StartNew();
    var caughtUpAt = (TimeSpan?)null;
    var updates = 0L;

    session.EntitiesUpdated += () =>
    {
        updates++;
        if (stream == null) return;
        if (!stream.IsCaughtUp && stream.BytesBehind > 64 * 1024) return; // still catching up on the backlog
        if (caughtUpAt == null)
        {
            caughtUpAt = wall.Elapsed;
            Console.WriteLine($"== caught up with the game after {caughtUpAt.Value.TotalSeconds:0.0}s ({updates} updates replayed); now live");
        }
        if (lastPrint.Elapsed < every) return;
        lastPrint.Restart();

        var file = new FileInfo(path);
        var lagMs = (DateTime.UtcNow - file.LastWriteTimeUtc).TotalMilliseconds;
        var snap = session.Snapshot();
        Render.Snapshot(snap);
        Console.WriteLine($"   [live] {stream.BytesBehind} bytes behind, file last written {lagMs:0} ms ago, {updates} updates");
        json?.Write(snap);
    };

    var reason = await session.FollowAsync(path, new LiveOptions(), s => stream = s, cts.Token);
    Console.WriteLine($"== stopped: {reason}");
    return 0;
}

async Task<int> SimulateCommand()
{
    var src = cli.Positional(0) ?? throw new ArgumentException("simulate <src.dem> <dst.dem>");
    var dst = cli.Positional(1) ?? throw new ArgumentException("simulate <src.dem> <dst.dem>");
    await DemoSimulator.RunAsync(src, dst, cli.FloatOption("speed") ?? 1f, cli.FloatOption("skip") ?? 0f, cts.Token);
    return 0;
}

async Task<int> HudCommand()
{
    // probe hud watch [--log <console.log>] [--from-start false] [--dump <tree.json>] [--grep <regex>]
    if (cli.Positional(0) != "watch") return Usage();
    var log = cli.Option("log") ?? Path.Combine(Install().CitadelDir, "console.log");
    var dump = cli.Option("dump") ?? Path.Combine(Environment.CurrentDirectory, "out", "hud_tree.json");
    var grep = cli.Option("grep") is { } g ? new System.Text.RegularExpressions.Regex(g, System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dump))!);

    // console.log is rewritten at each game launch and full dumps are only sent every few minutes, so while the game runs
    // read the whole file: older messages rebuild the tree quietly, then live ones are printed. If the game isn't running
    // the file is from a previous session; skip it and pick up the new file when the game starts.
    var gameRunning = Minerva.Mod.ModInstaller.IsGameRunning();
    var fromStart = cli.Option("from-start") is { } fs ? fs != "false" : gameRunning;
    Console.WriteLine($"Following {log}");
    Console.WriteLine(gameRunning
        ? "Deadlock is running; reading this session's log."
        : "Deadlock isn't running. Start it now (with -condebug); this picks up the new log when the game starts.");
    var protocol = new Minerva.Hud.HudProtocol();
    var tree = new Minerva.Hud.HudTree();
    var latencies = new List<double>();
    var lastDump = DateTime.MinValue;
    var replayed = 0;
    var warnedForeign = false;
    DateTime? lastWalkUtc = null;
    var outDir = Path.GetDirectoryName(Path.GetFullPath(dump))!;
    var api = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal); // "global" / "panel:<type>" -> members
    var apiDirty = false;
    TimelineWatch? timeline = null;

    static double Num(JsonElement body, string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    await foreach (var line in Minerva.Hud.ConsoleLogFollower.FollowAsync(log, fromStart, ct: cts.Token))
    {
        if (protocol.Accept(line) is not { } msg)
        {
            // Only a live line means the running game has an old mod; old lines are just a previous session's log.
            if (protocol.ForeignVersion is { } fv && !warnedForeign && line.Age is { } age && age < TimeSpan.FromSeconds(30))
            {
                warnedForeign = true;
                Console.WriteLine($"warning: the game is running mod protocol {fv}, this probe expects {Minerva.Hud.HudProtocol.Version}. " +
                                  "Close the game, run 'probe mod install', and start it again.");
            }
            if (line.Channel == "PanoramaScript" && line.Message.Contains("minerva", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  [script] {line.Message}"); // script errors mention the file name
            continue;
        }

        var changed = tree.Apply(msg);
        if (msg.Type is "full" or "delta" && tree.Complete)
            foreach (var e in (timeline ??= new TimelineWatch(Install(), cli.Option("map"))).Update(tree))
                Console.WriteLine($"[event] {e}");
        var latency = msg.Latency.TotalMilliseconds;
        if (latency > 5000 && msg.Type is "full" or "delta")
        {
            if (replayed++ == 0) Console.WriteLine("replaying earlier messages from console.log ...");
            continue;
        }
        if (replayed > 0)
        {
            Console.WriteLine($"replayed {replayed} messages; tree has {tree.Panels.Count} panels{(tree.Complete ? "" : " (no full dump yet)")}");
            replayed = 0;
        }

        switch (msg.Type)
        {
            case "hello":
                Console.WriteLine($"[hello] mod loaded: {msg.Body}");
                break;
            case "error":
                Console.WriteLine($"[error] {msg.Body}");
                break;
            case "api":
                var scope = msg.Body.TryGetProperty("scope", out var sc) ? sc.GetString() : "?";
                var apiKey = scope == "panel" && msg.Body.TryGetProperty("type", out var ty) ? "panel:" + ty.GetString() : scope ?? "?";
                if (msg.Body.TryGetProperty("members", out var mem)) api[apiKey] = mem.Clone();
                apiDirty = true;
                if (scope == "global") Console.WriteLine($"[api  ] {mem.EnumerateObject().Count()} globals (written to {Path.Combine(outDir, "hud_api.json")})");
                break;
            default:
                latencies.Add(latency);
                if (latencies.Count > 300) latencies.RemoveAt(0);
                var sorted = latencies.Order().ToList();
                var interval = lastWalkUtc is { } prev ? (msg.ReceivedUtc - prev).TotalSeconds : 0;
                lastWalkUtc = msg.ReceivedUtc;
                // frameMaxMs is the cost that matters: the longest single frame the script spent (stutter).
                Console.WriteLine(
                    $"[{msg.Type,-5}] seq {msg.Seq,5}  {(msg.Body.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.True ? "all   " : "shown ")}" +
                    $"walked {Num(msg.Body, "n"),5}  tree {tree.Panels.Count,5}  changed {changed.Count,5}  " +
                    $"walk {Num(msg.Body, "busyMs"),3} ms over {Num(msg.Body, "walkMs"),4} ms, worst frame {Num(msg.Body, "frameMaxMs"),2} ms  " +
                    $"every {interval,4:0.0} s  latency {latency,4:0} ms (p50 {sorted[sorted.Count / 2]:0}, p95 {sorted[(int)(sorted.Count * 0.95)]:0})");
                static string Describe(Minerva.Hud.HudPanel p) =>
                    (p.Text != null ? $" = \"{p.Text}\"" : "") +
                    (p.Classes != null ? $" [{string.Join(' ', p.Classes)}] at {p.X},{p.Y}" : "") +
                    (p.Visible ? "" : " (hidden)");
                foreach (var p in changed.Where(p => (p.Text != null || p.Classes != null) &&
                                                     (grep == null || grep.IsMatch(p.Key) || grep.IsMatch(Describe(p)))).Take(msg.Type == "full" ? 0 : 12))
                    Console.WriteLine($"          {p.Key}{Describe(p)}");
                break;
        }

        if (tree.Complete && (msg.Type == "full" || DateTime.UtcNow - lastDump > TimeSpan.FromSeconds(10)))
        {
            lastDump = DateTime.UtcNow;
            var json = new JsonSerializerOptions { WriteIndented = false };
            var panels = tree.Panels.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            await File.WriteAllTextAsync(dump, JsonSerializer.Serialize(panels, json), cts.Token);
            // Panels the mod probed for classes (minimap icons), on their own for quick inspection.
            var probed = panels.Where(p => p.Classes != null)
                .Select(p => new { p.Key, Classes = string.Join(' ', p.ClassList), p.Visible, p.X, p.Y, p.Width, p.Height, p.Layout });
            await File.WriteAllTextAsync(Path.Combine(outDir, "minimap.json"),
                JsonSerializer.Serialize(probed, new JsonSerializerOptions { WriteIndented = true }), cts.Token);
        }
        if (apiDirty && (msg.Type is "full" or "delta" || api.Count % 25 == 0))
        {
            apiDirty = false;
            await File.WriteAllTextAsync(Path.Combine(outDir, "hud_api.json"),
                JsonSerializer.Serialize(api, new JsonSerializerOptions { WriteIndented = true }), cts.Token);
        }
    }
    return 0;
}

int ModCommand()
{
    // probe mod status | build [--out <x_dir.vpk>] | install [--force] | uninstall | preview
    var install = Install();
    var installer = new Minerva.Mod.ModInstaller(install);
    switch (cli.Positional(0))
    {
        case "status":
        {
            var s = installer.Status();
            Console.WriteLine($"state:     {s.State}{(s.Reasons.Count > 0 ? " (" + string.Join("; ", s.Reasons) + ")" : "")}");
            Console.WriteLine($"installed: {s.InstalledPath ?? "-"}{(s.Installed != null ? $" (built {s.Installed.BuiltUtc:u} for build {s.Installed.GameBuild})" : "")}");
            Console.WriteLine($"game:      build {s.Expected.GameBuild}, host {s.Expected.HostLayout} crc {s.Expected.HostLayoutCrc:x8}");
            foreach (var probe in Minerva.Mod.ModBuilder.GenerateScript(install).Probes)
                Console.WriteLine($"probes:    {probe.Layout}: panels with .{probe.Gate} tested for {probe.Classes.Count} classes " +
                                  $"({string.Join(' ', probe.Classes.Take(12))}{(probe.Classes.Count > 12 ? " ..." : "")})");
            foreach (var w in s.Warnings) Console.WriteLine($"warning:   {w}");
            return s.State == Minerva.Mod.ModState.UpToDate ? 0 : 2;
        }
        case "build":
        {
            var outPath = cli.Option("out") ?? Path.Combine(Environment.CurrentDirectory, "out", "minerva_dir.vpk");
            var m = Minerva.Mod.ModBuilder.Build(install, outPath);
            Console.WriteLine($"built {outPath} for game build {m.GameBuild} (host {m.HostLayout})");
            return 0;
        }
        case "install":
        {
            var before = installer.Status();
            var after = installer.Ensure(force: cli.Option("force") == "true");
            Console.WriteLine(before.State == Minerva.Mod.ModState.UpToDate && after.InstalledPath == before.InstalledPath && cli.Option("force") != "true"
                ? $"already up to date: {after.InstalledPath}"
                : $"installed {after.InstalledPath}{(before.Reasons.Count > 0 ? " (" + string.Join("; ", before.Reasons) + ")" : "")}");
            foreach (var w in after.Warnings) Console.WriteLine($"warning: {w}");
            if (Minerva.Mod.ModInstaller.IsGameRunning()) Console.WriteLine("note: restart Deadlock to load the updated addon.");
            return 0;
        }
        case "uninstall":
            Console.WriteLine(installer.Uninstall() ? "removed" : "not installed");
            return 0;
        case "preview":
        {
            var (path, bytes) = Minerva.Mod.ModBuilder.ReadHostLayout(install);
            var tree = Minerva.Source2.Panorama.PanoramaCompiler.ReadLayoutTree(bytes);
            Minerva.Source2.Panorama.LayoutTree.AddScriptInclude(tree, Minerva.Mod.ModBuilder.CompiledScriptReference);
            Console.WriteLine($"// override for {path}");
            Console.WriteLine(Minerva.Source2.Panorama.PanoramaCompiler.DecompileLayout(
                Minerva.Source2.Panorama.PanoramaCompiler.ReplaceLayoutTree(bytes, tree)));
            return 0;
        }
        default:
            return Usage();
    }
}

int VpkCommand()
{
    // probe vpk ls [<vpk>] [regex]   |   probe vpk cat [<vpk>] <entry>
    // <vpk> defaults to the game's pak01_dir.vpk.
    var sub = cli.Positional(0);
    string? a = cli.Positional(1), b = cli.Positional(2);
    var isVpk = a != null && a.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase);
    var vpk = isVpk ? a! : Install().MainVpk;
    var arg = isVpk ? b : a;
    return sub switch
    {
        "ls" => VpkCommands.List(vpk, arg),
        "cat" => VpkCommands.Cat(vpk, arg ?? throw new ArgumentException("vpk cat [<vpk>] <entry>")),
        _ => Usage(),
    };
}

JsonLines? JsonOut() => cli.Option("json") is { } p ? new JsonLines(p) : null;

namespace Minerva.Probe
{
    internal sealed class JsonLines(string path) : IDisposable
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        private readonly StreamWriter _writer = new(path, append: false);

        public void Write(GameSnapshot snap)
        {
            _writer.WriteLine(JsonSerializer.Serialize(snap, Options));
            _writer.Flush();
        }

        public void Dispose() => _writer.Dispose();
    }

    internal sealed class Args
    {
        private readonly List<string> _positional = [];
        private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

        public Args(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--") && i + 1 < args.Length) _options[args[i][2..]] = args[++i];
                else _positional.Add(args[i]);
            }
        }

        public string Command => _positional.FirstOrDefault() ?? "";
        public string? Positional(int i) => i + 1 < _positional.Count ? _positional[i + 1] : null;
        public string? Option(string name) => _options.GetValueOrDefault(name);
        public float? FloatOption(string name) => Option(name) is { } v ? float.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
