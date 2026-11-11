using Minerva.Mod.Formats;

namespace Minerva.Mod.Formats;

/// <summary>
/// Edits a compiled Panorama layout tree (<c>{ m_AST: { m_pRoot: node } }</c>). Nodes have an <c>eType</c>
/// (ROOT, STYLES, SCRIPTS, INCLUDE, PANEL, ...), children in <c>child</c> (one) or <c>vecChildren</c> (several),
/// and a <c>sourceLineColumn</c>.
/// </summary>
public static class LayoutTree
{
    public static Kv3Object Root(Kv3Object tree) =>
        (tree["m_AST"] as Kv3Object)?["m_pRoot"] as Kv3Object
        ?? throw new InvalidDataException("Layout tree has no m_AST.m_pRoot.");

    public static IReadOnlyList<Kv3Object> Children(Kv3Object node) => node["vecChildren"] switch
    {
        Kv3Array many => many.OfType<Kv3Object>().ToList(),
        _ => node["child"] is Kv3Object one ? [one] : [],
    };

    public static string? Type(Kv3Object node) => node["eType"] as string;

    /// <summary>All compiled references a layout includes (styles and scripts).</summary>
    public static IEnumerable<string> Includes(Kv3Object tree) =>
        Descendants(Root(tree))
            .Where(n => Type(n) == "INCLUDE")
            .Select(n => (n["child"] as Kv3Object)?["name"] as string)
            .OfType<string>();

    public static IEnumerable<Kv3Object> Descendants(Kv3Object node)
    {
        yield return node;
        foreach (var c in Children(node))
        foreach (var d in Descendants(c))
            yield return d;
    }

    /// <summary>
    /// Add <c>&lt;scripts&gt;&lt;include src="s2r://{compiledScript}"/&gt;&lt;/scripts&gt;</c> to the layout root,
    /// reusing an existing scripts block when there is one. Idempotent.
    /// </summary>
    public static void AddScriptInclude(Kv3Object tree, string compiledScript)
    {
        if (Includes(tree).Contains(compiledScript)) return;

        var include = new Kv3Object
        {
            { "eType", "INCLUDE" },
            { "child", new Kv3Object { { "eType", "REFERENCE_COMPILED" }, { "name", compiledScript } } },
            { "sourceLineColumn", new Kv3Array { 1, 1 } },
        };

        var root = Root(tree);
        var existingScripts = Children(root).FirstOrDefault(n => Type(n) == "SCRIPTS");
        if (existingScripts != null)
        {
            AppendChild(existingScripts, include);
            return;
        }

        var scripts = new Kv3Object
        {
            { "eType", "SCRIPTS" },
            { "child", include },
            { "sourceLineColumn", new Kv3Array { 1, 1 } },
        };

        // Panorama expects <styles>, then <scripts>, then the panel.
        var children = Children(root).ToList();
        var stylesIndex = children.FindIndex(n => Type(n) == "STYLES");
        children.Insert(stylesIndex + 1, scripts);
        SetChildren(root, children);
    }

    private static void AppendChild(Kv3Object node, Kv3Object child) => SetChildren(node, [.. Children(node), child]);

    private static void SetChildren(Kv3Object node, List<Kv3Object> children)
    {
        // Replace child/vecChildren in place so member order (before sourceLineColumn) is preserved.
        var index = node.FindIndex(m => m.Key is "child" or "vecChildren");
        node.Remove("child");
        node.Remove("vecChildren");
        KeyValuePair<string, object?> member = children.Count == 1
            ? new("child", children[0])
            : new("vecChildren", new Kv3Array(children));
        if (index < 0) index = Math.Max(0, node.FindIndex(m => m.Key == "sourceLineColumn"));
        node.Insert(Math.Min(index, node.Count), member);
    }
}
