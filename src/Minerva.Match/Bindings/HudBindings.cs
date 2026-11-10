using Minerva.GameState.StaticData;
using Minerva.Hud;

namespace Minerva.Match.Bindings;

/// <summary>A live panel matched to the node of the layout file it was created from.</summary>
/// <param name="InstanceKey">Key of the panel the layout was loaded into (e.g. one top bar player).</param>
public sealed record BoundPanel(HudPanel Panel, string Layout, string InstanceKey, LayoutNode Node)
{
    /// <summary>The value of one of the node's variables, parsed from the text the panel shows.</summary>
    public string? Value(string variable) => Node.Values(Panel.Text)?.GetValueOrDefault(variable);
}

/// <summary>One loaded instance of a layout, e.g. one hero's row in the top bar.</summary>
public sealed class LayoutInstance(string layout, string key, IReadOnlyList<BoundPanel> panels, HudBindings owner)
{
    public string Layout { get; } = layout;
    public string Key { get; } = key;
    public IReadOnlyList<BoundPanel> Panels { get; } = panels;

    /// <summary>The value shown for a dialog variable, e.g. <c>Field("kills")</c>.</summary>
    public string? Field(string variable) => Bound(variable)?.Value(variable);

    /// <summary>The value shown for a dialog variable inside the element with id <paramref name="under"/>.</summary>
    public string? Field(string variable, string under) => Bound(variable, under)?.Value(variable);

    /// <summary>The panel bound to a dialog variable (optionally inside the element with id <paramref name="under"/>).</summary>
    public BoundPanel? Bound(string variable, string? under = null) =>
        Panels.FirstOrDefault(p => p.Node.Variables.Contains(variable) &&
                                   (under == null || p.Panel.Key.Contains("/" + under + "/", StringComparison.Ordinal)));

    /// <summary>The panel with this id in the layout (ids are the layout's, not paths).</summary>
    public BoundPanel? ById(string id) => Panels.FirstOrDefault(p => p.Node.Id == id);

    /// <summary>Instances of other layouts loaded inside this one.</summary>
    public IEnumerable<LayoutInstance> Nested(string layout) => owner.Instances(layout).Where(i => i.Key.StartsWith(Key + "/", StringComparison.Ordinal));

    public bool IsShown(BoundPanel panel) => owner.IsShown(panel.Panel.Key);
}

/// <summary>
/// Gives live HUD panels the meaning their layout file gives them. Each panel the mod reports carries the layout
/// file it came from; the layout's nodes are matched to live panels by id, then by type and order, and a label's dialog
/// variable (<c>{i:kills}</c>) names what it shows. Field readers then ask for <c>kills</c> in
/// <c>citadel_hud_top_bar_player</c> instead of following panel paths that change between patches.
/// </summary>
public sealed class HudBindings
{
    private readonly IReadOnlyDictionary<string, HudPanel> _panels;
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoundPanel> _bound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<LayoutInstance>> _instances = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LayoutNode> _placements = new(StringComparer.Ordinal);

    /// <summary>
    /// For a panel that loaded its own layout: the node in its parent's layout that placed it (with the attributes the
    /// parent gave it), or null.
    /// </summary>
    public LayoutNode? Placement(string key) => _placements.GetValueOrDefault(key);

    public HudBindings(IReadOnlyDictionary<string, HudPanel> panels, LayoutCatalog catalog)
    {
        _panels = panels;
        foreach (var key in panels.Keys)
        {
            if (HudPanel.ParentKey(key) is not { } parent) continue;
            if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = [];
            list.Add(key);
        }

        // A layout instance starts where a panel's layout differs from its parent's.
        var perInstance = new Dictionary<string, List<BoundPanel>>(StringComparer.Ordinal);
        foreach (var (key, panel) in panels)
        {
            if (panel.Layout is not { } layout) continue;
            var parentLayout = HudPanel.ParentKey(key) is { } pk && panels.TryGetValue(pk, out var parent) ? parent.Layout : null;
            if (parentLayout == layout) continue;
            if (catalog.Get(layout) is not { } file)
            {
                _missingLayouts.Add(layout);
                continue;
            }
            var bound = new List<BoundPanel>();
            Match(key, file.Root, layout, key, bound);
            perInstance[key] = bound;
        }

        foreach (var (key, bound) in perInstance)
        {
            var layout = panels[key].Layout!;
            if (!_instances.TryGetValue(layout, out var list)) _instances[layout] = list = [];
            list.Add(new LayoutInstance(layout, key, bound, this));
        }
        foreach (var list in _instances.Values) list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
    }

    /// <summary>Panels matched to a layout node.</summary>
    public IReadOnlyDictionary<string, BoundPanel> Bound => _bound;

    /// <summary>Layout names panels reported that the game files don't have.</summary>
    public IReadOnlyCollection<string> MissingLayouts => _missingLayouts;

    public IReadOnlyList<LayoutInstance> Instances(string layout) => _instances.GetValueOrDefault(layout) ?? [];

    /// <summary>The panel and all its ancestors are visible.</summary>
    public bool IsShown(string key)
    {
        for (var k = key; k != null; k = HudPanel.ParentKey(k))
        {
            if (_panels.TryGetValue(k, out var p) && !p.Visible) return false;
            if (k.Length == 0) break;
        }
        return true;
    }

    private void Match(string key, LayoutNode node, string layout, string instance, List<BoundPanel> bound)
    {
        var b = new BoundPanel(_panels[key], layout, instance, node);
        _bound[key] = b;
        bound.Add(b);
        if (node.Children.Count == 0 || !_children.TryGetValue(key, out var live)) return;

        // Live names are the panel id, or "<type>#<child index>" for panels without one (see minerva.js).
        var unnamed = live
            .Select(k => (Key: k, Name: k[(k.LastIndexOf('/') + 1)..]))
            .Select(c => (c.Key, Hash: c.Name.LastIndexOf('#'), c.Name))
            .Where(c => c.Hash > 0 && int.TryParse(c.Name.AsSpan(c.Hash + 1), out _))
            .Select(c => (c.Key, Type: c.Name[..c.Hash], Index: int.Parse(c.Name.AsSpan(c.Hash + 1))))
            .GroupBy(c => c.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Index).Select(c => (c.Key, c.Index)).ToList(), StringComparer.Ordinal);
        // Pass 1: ids, and unnamed panels of the same type at the same position (the usual case: panels the code adds
        // come after the layout's). Pass 2: remaining unnamed layout children take the remaining panels of their type
        // in order.
        var matches = new string?[node.Children.Count];
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        for (var position = 0; position < node.Children.Count; position++)
        {
            var child = node.Children[position];
            if (child.Id != null)
            {
                var k = key + "/" + child.Id;
                if (_panels.ContainsKey(k)) matches[position] = k;
            }
            else if (unnamed.TryGetValue(child.Type, out var candidates) &&
                     candidates.FirstOrDefault(c => c.Index == position).Key is { } k && claimed.Add(k))
            {
                matches[position] = k;
            }
        }
        for (var position = 0; position < node.Children.Count; position++)
        {
            var child = node.Children[position];
            if (matches[position] != null || child.Id != null || !unnamed.TryGetValue(child.Type, out var candidates)) continue;
            if (candidates.FirstOrDefault(c => !claimed.Contains(c.Key)).Key is { } k && claimed.Add(k)) matches[position] = k;
        }

        for (var position = 0; position < node.Children.Count; position++)
        {
            var child = node.Children[position];
            var match = matches[position];
            if (match == null) continue;
            // A child that loaded its own layout is a separate instance; remember where this layout placed it (its
            // attributes here, e.g. team="2", describe the instance).
            if (_panels[match].Layout == layout) Match(match, child, layout, instance, bound);
            else _placements[match] = child;
        }
    }
}
