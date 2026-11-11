using System.Diagnostics.CodeAnalysis;
using Jint;
using Jint.Native;
using Minerva.Reading.Transport;
using Minerva.Mod;

namespace Minerva.Tests.Mod;

/// <summary>
/// Runs the real in-game script (minerva.js) under a JavaScript interpreter against a fake Panorama API, and feeds
/// its console output through the companion's parser. Covers the full mod → companion path except the game itself.
/// </summary>
public class ModScriptTests
{
    [Fact]
    public void Full_dump_then_deltas_reach_the_companion_tree()
    {
        var root = new FakePanel("WindowRoot", "Panel");
        var hud = root.Add(new FakePanel("Hud", "CitadelHud"));
        var bar = hud.Add(new FakePanel("TopBar", "Panel"));
        var souls = bar.Add(new FakePanel("Souls", "Label") { text = "1,234" });
        bar.Add(new FakePanel("", "Label") { text = "12:34" });
        var hidden = bar.Add(new FakePanel("Scoreboard", "Panel") { visible = false });
        hidden.Add(new FakePanel("", "Label") { text = "Café ☕" }); // non-ASCII survives the console
        var unlaid = bar.Add(new FakePanel("Unlaid", "Panel") { actualxoffset = 3.4028234663852886e38 }); // FLT_MAX: not laid out
        var context = hud; // the script's context panel is not the top; it must climb to the root

        var game = new FakeGame(context);
        var companion = new Companion();

        game.Run(ModBuilder.Script);
        game.Frames(5);
        companion.Read(game.TakeLines());
        Assert.Equal(["hello", "full"], companion.Messages.Select(m => m.Type).Where(t => t != "api"));
        Assert.True(companion.Tree.Complete);
        Assert.Equal(8, companion.Tree.Panels.Count);
        Assert.Equal("1,234", companion.Tree.Panels["/Hud/TopBar/Souls"].Text);
        Assert.Equal("Label", companion.Tree.Panels["/Hud/TopBar/Souls"].Type);
        Assert.Equal(10, companion.Tree.Panels["/Hud/TopBar/Souls"].X);
        Assert.Equal("12:34", companion.Tree.Panels["/Hud/TopBar/Label#1"].Text);
        Assert.Equal("Café ☕", companion.Tree.Panels["/Hud/TopBar/Scoreboard/Label#0"].Text);
        Assert.False(companion.Tree.Panels["/Hud/TopBar/Scoreboard"].Visible);
        Assert.Null(companion.Tree.Panels["/Hud/TopBar/Scoreboard/Label#0"].X); // hidden by an ancestor: no geometry
        Assert.Null(companion.Tree.Panels["/Hud/TopBar/Unlaid"].X);
        Assert.Equal(20, companion.Tree.Panels["/Hud/TopBar/Unlaid"].Y);

        souls.text = "1,500"; // change one label, remove one panel
        bar.Children.Remove(hidden);
        unlaid.actualxoffset = 5;
        game.Advance(1100);
        companion.Read(game.TakeLines());

        var delta = companion.Messages.Last();
        Assert.Equal("delta", delta.Type);
        Assert.Equal("1,500", companion.Tree.Panels["/Hud/TopBar/Souls"].Text);
        Assert.Equal(5, companion.Tree.Panels["/Hud/TopBar/Unlaid"].X);
        Assert.False(companion.Tree.Panels.ContainsKey("/Hud/TopBar/Scoreboard"));
        Assert.False(companion.Tree.Panels.ContainsKey("/Hud/TopBar/Scoreboard/Label#0"));
        Assert.Equal(2, delta.Body.GetProperty("set").GetArrayLength());
        Assert.Equal(["/Hud/TopBar/Scoreboard"], delta.Body.GetProperty("del").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(0, companion.Protocol.Rejected);
    }

    [Fact]
    public void Panels_from_probed_layouts_report_their_classes()
    {
        var root = new FakePanel("Hud", "Panel") { layoutfile = "s2r://panorama/layout/base_hud.vxml" };
        var minimap = root.Add(new FakePanel("Minimap", "HudMinimap") { layoutfile = @"panorama\layout\hud_minimap" }); // as the game reports it
        var hero = minimap.Add(new FakePanel("", "Panel") { Classes = { "map_button", "player", "enemy" } });
        var camp = minimap.Add(new FakePanel("", "Panel") { Classes = { "map_button", "neutral" } });
        var image = hero.Add(new FakePanel("MainImage", "Image") { Classes = { "player" } }); // no gate class: not probed
        var elsewhere = root.Add(new FakePanel("Shop", "Panel") { Classes = { "map_button" } }); // other layout: not probed

        var script = ModBuilder.WithProbes(ModBuilder.Script, [new ClassProbe("hud_minimap", "map_button", ["active", "enemy", "neutral", "player"])]);
        var game = new FakeGame(root);
        var companion = new Companion();
        game.Run(script);
        game.Frames(5);
        companion.Read(game.TakeLines());

        var panels = companion.Tree.Panels;
        Assert.Equal("base_hud", panels[""].Layout);
        Assert.Equal("hud_minimap", panels["/Minimap"].Layout);
        Assert.Equal("hud_minimap", panels["/Minimap/Panel#0/MainImage"].Layout); // inherited
        Assert.Equal(["map_button", "enemy", "player"], panels["/Minimap/Panel#0"].ClassList);
        Assert.Equal(["map_button", "neutral"], panels["/Minimap/Panel#1"].ClassList);
        Assert.Null(panels["/Minimap/Panel#0/MainImage"].Classes);
        Assert.Null(panels["/Shop"].Classes);
        Assert.Equal(0, elsewhere.ClassChecks);
        Assert.Equal(1, image.ClassChecks); // only the gate

        camp.Classes.Add("active"); // the camp respawned
        game.Advance(1100);
        companion.Read(game.TakeLines());
        Assert.True(companion.Tree.Panels["/Minimap/Panel#1"].HasClass("active"));
        Assert.Equal("hud_minimap", companion.Tree.Panels["/Minimap/Panel#1"].Layout); // delta rows inherit too
        Assert.Equal(0, companion.Protocol.Rejected);
    }

    [Fact]
    public void Api_of_globals_and_each_panel_type_is_sent_once()
    {
        var root = new FakePanel("Root", "Panel");
        root.Add(new FakePanel("A", "Label"));
        root.Add(new FakePanel("B", "Label"));
        var game = new FakeGame(root);
        var companion = new Companion();
        game.Run(ModBuilder.Script);
        game.Frames(5);
        game.Advance(1100);
        companion.Read(game.TakeLines());

        var api = companion.Messages.Where(m => m.Type == "api").ToList();
        var global = Assert.Single(api, m => m.Body.GetProperty("scope").GetString() == "global");
        Assert.True(global.Body.GetProperty("members").TryGetProperty("$", out _));
        Assert.False(global.Body.GetProperty("members").TryGetProperty("MINERVA_PROBES", out _)); // our own globals left out
        var types = api.Where(m => m.Body.GetProperty("scope").GetString() == "panel").Select(m => m.Body.GetProperty("type").GetString()).ToList();
        Assert.Equal(["Label", "Panel"], types.Order());
    }

    [Fact]
    public void Hidden_subtrees_are_refreshed_on_complete_walks_only()
    {
        var root = new FakePanel("Root", "Panel");
        var shop = root.Add(new FakePanel("Shop", "Panel") { visible = false });
        var price = shop.Add(new FakePanel("Price", "Label") { text = "500" });
        var clock = root.Add(new FakePanel("Clock", "Label") { text = "0:01" });

        var game = new FakeGame(root);
        var companion = new Companion();
        game.Run(ModBuilder.Script);
        game.Frames(5); // walk 0: full (complete)
        companion.Read(game.TakeLines());

        price.text = "600";
        clock.text = "0:02";
        game.Advance(1100); // walk 1: hidden subtrees skipped
        companion.Read(game.TakeLines());
        Assert.Equal("0:02", companion.Tree.Panels["/Clock"].Text);
        Assert.Equal("500", companion.Tree.Panels["/Shop/Price"].Text); // last reported state stands
        Assert.False(companion.Messages.Last().Body.GetProperty("complete").GetBoolean());

        shop.visible = true; // opening the shop: its subtree is walked on the next walk
        game.Advance(1100);
        companion.Read(game.TakeLines());
        Assert.Equal("600", companion.Tree.Panels["/Shop/Price"].Text);
        Assert.Equal(10, companion.Tree.Panels["/Shop/Price"].X);

        shop.visible = false;
        price.text = "700";
        for (var i = 0; i < 12 && companion.Tree.Panels["/Shop/Price"].Text != "700"; i++)
        {
            game.Advance(1100);
            companion.Read(game.TakeLines());
        }
        Assert.Equal("700", companion.Tree.Panels["/Shop/Price"].Text); // picked up by the next complete walk
        Assert.True(companion.Messages.Count(m => m.Type == "delta" && m.Body.GetProperty("complete").GetBoolean()) == 1,
            "expected exactly one complete delta walk (the tenth walk) to have picked it up");
        Assert.Equal(0, companion.Protocol.Rejected);
    }

    [Fact]
    public void Large_trees_are_walked_across_frames_within_the_budget()
    {
        var root = new FakePanel("Root", "Panel");
        for (var i = 0; i < 100; i++)
        {
            var group = root.Add(new FakePanel($"G{i}", "Panel"));
            for (var j = 0; j < 20; j++) group.Add(new FakePanel("", "Label") { text = $"{i}.{j}" });
        }
        var game = new FakeGame(root) { MsPerVisit = 0.01 }; // 2101 panels ≈ 21 ms of walking

        var companion = new Companion();
        game.Run(ModBuilder.Script);
        var frames = 0;
        while (!companion.Tree.Complete && frames < 200)
        {
            game.Frames(1);
            frames++;
            companion.Read(game.TakeLines());
        }

        Assert.True(companion.Tree.Complete);
        Assert.Equal(2101, companion.Tree.Panels.Count);
        Assert.Equal("99.19", companion.Tree.Panels["/G99/Label#19"].Text);
        Assert.True(frames >= 8, $"expected the walk to be split across frames, took {frames}");
        var full = companion.Messages.Single(m => m.Type == "full");
        Assert.True(full.Body.GetProperty("frameMaxMs").GetDouble() <= 3, "no frame may exceed the slice budget");
    }

    [Fact]
    public void An_expensive_complete_walk_does_not_delay_the_next_walk()
    {
        var root = new FakePanel("Root", "Panel");
        root.Add(new FakePanel("Clock", "Label") { text = "0:00" });
        var shop = root.Add(new FakePanel("Shop", "Panel") { visible = false });
        for (var i = 0; i < 2000; i++) shop.Add(new FakePanel("", "Label"));
        var game = new FakeGame(root) { MsPerVisit = 0.05 }; // complete walk ≈ 100 ms of work: 2 s at a 5% cap

        var companion = new Companion();
        game.Run(ModBuilder.Script);
        HudMessage? complete = null;
        HudMessage? next = null;
        for (var i = 0; i < 2000 && next == null; i++)
        {
            game.Frames(1);
            companion.Read(game.TakeLines());
            complete ??= companion.Messages.FirstOrDefault(m => m.Type == "delta" && m.Body.GetProperty("complete").GetBoolean());
            if (complete != null) next = companion.Messages.FirstOrDefault(m => m.Seq > complete.Seq);
        }

        Assert.NotNull(next);
        var gapMs = next.SentUnixMs - complete!.SentUnixMs;
        Assert.True(gapMs < 700, $"the walk after a complete walk came {gapMs} ms later");
    }

    [Fact]
    public void Large_messages_are_chunked_and_reassembled()
    {
        var root = new FakePanel("Root", "Panel");
        var big = new string('x', 5000);
        root.Add(new FakePanel("Big", "Label") { text = big });

        var game = new FakeGame(root);
        var companion = new Companion();
        game.Run(ModBuilder.Script);
        game.Frames(5);
        var lines = game.TakeLines();
        companion.Read(lines);

        Assert.True(lines.Count > 3, "expected the full dump to span several console lines");
        Assert.Equal(big, companion.Tree.Panels["/Big"].Text);
    }

    [Fact]
    public void Reloading_the_script_stops_the_old_copy()
    {
        var game = new FakeGame(new FakePanel("Root", "Panel"));
        game.Run(ModBuilder.Script);
        game.Run(ModBuilder.Script); // Panorama reloads scripts on layout reload
        game.Frames(5);

        var companion = new Companion();
        companion.Read(game.TakeLines());

        // Only the newest copy walks and reschedules.
        Assert.Single(companion.Messages, m => m.Type == "hello");
        Assert.Single(companion.Messages, m => m.Type == "full");
        Assert.Equal(1, game.PendingCallbacks);
    }

    [Fact]
    public void Lines_from_another_protocol_version_are_reported()
    {
        var protocol = new HudProtocol();
        var line = ConsoleLogFollower.Parse("11/10 06:52:29 [PanoramaScript] MNRV2|1|0|1|{\"t\":\"hello\"}", DateTime.UtcNow);
        Assert.Null(protocol.Accept(line));
        Assert.Equal(2, protocol.ForeignVersion);
    }

    [Fact]
    public void Console_line_age_comes_from_the_game_timestamp()
    {
        var read = new DateTime(2026, 11, 10, 12, 0, 30, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal(TimeSpan.FromSeconds(30), ConsoleLogFollower.Parse("11/10 12:00:00 [Panorama] x", read).Age);
        Assert.True(ConsoleLogFollower.Parse("11/09 12:00:00 [Panorama] x", read).Age > TimeSpan.FromHours(23));
        var january = new DateTime(2027, 1, 1, 0, 0, 5, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal(TimeSpan.FromSeconds(10), ConsoleLogFollower.Parse("12/31 23:59:55 [Panorama] x", january).Age);
        Assert.Null(ConsoleLogFollower.Parse("no timestamp", read).Age);
    }

    private sealed class Companion
    {
        public HudProtocol Protocol { get; } = new();
        public HudTree Tree { get; } = new();
        public List<HudMessage> Messages { get; } = [];

        public void Read(IEnumerable<string> consoleMessages)
        {
            foreach (var m in consoleMessages)
            {
                var line = ConsoleLogFollower.Parse($"11/10 06:52:29 [PanoramaScript] {m}", DateTime.UtcNow);
                if (Protocol.Accept(line) is { } msg)
                {
                    Messages.Add(msg);
                    Tree.Apply(msg);
                }
            }
        }
    }

    /// <summary>Minimal stand-in for Panorama's <c>$</c>, with a fake clock advanced one frame at a time.</summary>
    private sealed class FakeGame
    {
        private const double FrameMs = 16;
        private readonly Engine _engine;
        private readonly List<string> _lines = [];
        private readonly List<(double Due, JsValue Callback)> _scheduled = [];
        private double _clock = 1_000_000;

        public FakeGame(FakePanel context)
        {
            _engine = new Engine();
            _engine.SetValue("$", new FakeDollar(this, context));
            _engine.SetValue("__now", new Func<double>(() => _clock));
            _engine.Execute("Date.now = function () { return __now(); };");
            var top = context;
            while (top.Parent != null) top = top.Parent;
            foreach (var p in top.Descendants()) p.Visited = () => _clock += MsPerVisit;
        }

        /// <summary>Simulated cost of visiting one panel.</summary>
        public double MsPerVisit { get; init; }

        public int PendingCallbacks => _scheduled.Count;

        public void Run(string script) => _engine.Execute(script);

        /// <summary>Run <paramref name="count"/> frames: advance the clock and fire what is due.</summary>
        public void Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                _clock += FrameMs;
                var due = _scheduled.Where(s => s.Due <= _clock).ToList();
                _scheduled.RemoveAll(s => s.Due <= _clock);
                foreach (var s in due) _engine.Invoke(s.Callback);
            }
        }

        public void Advance(double ms) => Frames((int)Math.Ceiling(ms / FrameMs));

        public List<string> TakeLines()
        {
            var l = _lines.ToList();
            _lines.Clear();
            return l;
        }

        [SuppressMessage("ReSharper", "UnusedMember.Local", Justification = "Called from JavaScript")]
        private sealed class FakeDollar(FakeGame game, FakePanel context)
        {
            public FakePanel GetContextPanel() => context;
            public void Msg(string text) => game._lines.Add(text);
            public void Schedule(double seconds, JsValue callback) => game._scheduled.Add((game._clock + seconds * 1000, callback));
        }
    }

    /// <summary>Panel with Panorama's JS-facing member names.</summary>
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Matches Panorama's JavaScript API")]
    public sealed class FakePanel(string id, string paneltype)
    {
        public string id { get; } = id;
        public string paneltype { get; } = paneltype;
        public bool visible { get; set; } = true;
        public string? text { get; set; }
        public double actualxoffset { get; set; } = 10;
        public double actualyoffset { get; set; } = 20;
        public double actuallayoutwidth { get; set; } = 100;
        public double actuallayoutheight { get; set; } = 30;
        public string? layoutfile { get; set; }
        public HashSet<string> Classes { get; } = new(StringComparer.Ordinal);
        public int ClassChecks { get; private set; }
        public FakePanel? Parent { get; private set; }
        public List<FakePanel> Children { get; } = [];
        internal Action? Visited { get; set; }

        public FakePanel Add(FakePanel child)
        {
            child.Parent = this;
            Children.Add(child);
            return child;
        }

        public IEnumerable<FakePanel> Descendants() => Children.SelectMany(c => c.Descendants()).Prepend(this);

        public int GetChildCount() => Children.Count;
        public FakePanel? GetChild(int i) => i < Children.Count ? Children[i] : null;
        public FakePanel? GetParent() => Parent;

        public bool BHasClass(string name)
        {
            ClassChecks++;
            return Classes.Contains(name);
        }

        public bool IsValid()
        {
            Visited?.Invoke();
            return true;
        }
    }
}
