using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Minerva.GameState.Live;
using SteamDatabase.ValvePak;
using ValveResourceFormat;

namespace Minerva.GameState.StaticData;

/// <summary>One element of a Panorama layout file.</summary>
/// <param name="Type">Panel type (the element name), e.g. <c>Label</c>.</param>
/// <param name="Text">The <c>text</c> attribute, e.g. <c>{i:kills}</c> or <c>#Citadel_Hud_Critical</c>.</param>
/// <param name="Variables">Dialog variables the text is bound to, e.g. <c>kills</c>.</param>
/// <param name="Attributes">All of the element's attributes (e.g. <c>team="2"</c>).</param>
public sealed record LayoutNode(string Type, string? Id, IReadOnlyList<string> Classes, string? Text,
    IReadOnlyList<string> Variables, IReadOnlyList<LayoutNode> Children, IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>Extract the value(s) of <see cref="Variables"/> from the text a label shows, using the template.</summary>
    /// <returns>Variable → shown value, or null if the text doesn't fit the template.</returns>
    public IReadOnlyDictionary<string, string>? Values(string? shown)
    {
        if (shown == null || Text == null || Variables.Count == 0) return null;
        var m = LayoutCatalog.TemplateRegex(Text).Match(shown);
        if (!m.Success) return null;
        return Variables.Distinct().ToDictionary(v => v, v => m.Groups[v].Value);
    }
}

/// <summary>A layout file: its root panel and the snippets it defines.</summary>
public sealed record LayoutFile(string Name, string Path, LayoutNode Root, IReadOnlyDictionary<string, LayoutNode> Snippets);

/// <summary>
/// The game's Panorama layouts, read on demand from the installed game's package. Lets the companion attach meaning
/// to live HUD panels from the game's own files: a label's dialog variable (<c>{i:kills}</c>) says what it shows.
/// </summary>
public sealed partial class LayoutCatalog
{
    private readonly Func<string, byte[]?> _read;
    private readonly IReadOnlyDictionary<string, string> _paths; // base name -> package path
    private readonly ConcurrentDictionary<string, LayoutFile?> _cache = new(StringComparer.Ordinal);

    private LayoutCatalog(IReadOnlyDictionary<string, string> paths, Func<string, byte[]?> read)
    {
        _paths = paths;
        _read = read;
    }

    /// <summary>Layout base names (e.g. <c>citadel_hud_top_bar_player</c>) available in the game files.</summary>
    public IReadOnlyCollection<string> Names => _paths.Keys.ToList();

    public static LayoutCatalog Load(DeadlockInstall install)
    {
        var package = new Package();
        package.Read(install.MainVpk);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (package.Entries != null && package.Entries.TryGetValue("vxml_c", out var layouts))
        {
            foreach (var entry in layouts)
            {
                var path = entry.GetFullPath();
                if (path.StartsWith("panorama/layout/", StringComparison.Ordinal)) paths.TryAdd(entry.FileName, path);
            }
        }
        var gate = new object();
        return new LayoutCatalog(paths, path =>
        {
            lock (gate)
            {
                if (package.FindEntry(path) is not { } entry) return null;
                package.ReadEntry(entry, out var bytes);
                return bytes;
            }
        });
    }

    /// <summary>A catalog over layouts given as XML text (tests, or layouts cached elsewhere).</summary>
    public static LayoutCatalog FromXml(IReadOnlyDictionary<string, string> xmlByName) =>
        new(xmlByName.Keys.ToDictionary(k => k, k => k), name => System.Text.Encoding.UTF8.GetBytes(xmlByName[name]));

    /// <summary>The layout with this base name, or null if the game has none (or it can't be read).</summary>
    public LayoutFile? Get(string name) => _cache.GetOrAdd(name, n =>
    {
        if (!_paths.TryGetValue(n, out var path) || _read(path) is not { } bytes) return null;
        try
        {
            return Parse(n, path, path.EndsWith("_c", StringComparison.Ordinal) ? Decompile(path, bytes) : System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
        {
            _failures[n] = $"{path}: {e.Message}";
            return null;
        }
    });

    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);

    /// <summary>Layouts that exist but could not be read, with the reason.</summary>
    public IReadOnlyDictionary<string, string> Failures => _failures;

    /// <summary>Whether the game files have a layout with this base name (readable or not).</summary>
    public bool Contains(string name) => _paths.ContainsKey(name);

    public static LayoutFile Parse(string name, string path, string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidDataException($"{path}: no root element.");
        var panel = root.Elements().FirstOrDefault(e => e.Name.LocalName is not ("styles" or "scripts" or "snippets"))
                    ?? throw new InvalidDataException($"{path}: no panel under <root>.");
        var snippets = root.Elements("snippets").Elements("snippet")
            .Where(s => s.Attribute("name") != null && s.Elements().Any())
            .ToDictionary(s => s.Attribute("name")!.Value, s => Node(s.Elements().First()), StringComparer.Ordinal);
        return new LayoutFile(name, path, Node(panel), snippets);
    }

    private static LayoutNode Node(XElement e)
    {
        var text = e.Attribute("text")?.Value;
        var variables = text == null ? [] : VariableRegex().Matches(text).Select(m => m.Groups[2].Value).ToList();
        var classes = (e.Attribute("class")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new LayoutNode(e.Name.LocalName, e.Attribute("id")?.Value, classes, text, variables,
            e.Elements().Select(Node).ToList(),
            e.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal));
    }

    private static string Decompile(string path, byte[] bytes)
    {
        // ValveResourceFormat needs the file name to reconstruct some layouts (it comes back empty otherwise).
        using var resource = new Resource { FileName = path };
        resource.Read(new MemoryStream(bytes));
        using var writer = new IndentedTextWriter();
        resource.DataBlock!.WriteText(writer);
        return writer.ToString();
    }

    private static readonly ConcurrentDictionary<string, Regex> Templates = new(StringComparer.Ordinal);

    /// <summary><c>"{i:kills} / {i:deaths}"</c> → a regex with a named group per variable.</summary>
    internal static Regex TemplateRegex(string template) => Templates.GetOrAdd(template, t =>
    {
        var pattern = new System.Text.StringBuilder("^");
        var at = 0;
        foreach (Match m in VariableRegex().Matches(t))
        {
            pattern.Append(Regex.Escape(t[at..m.Index]));
            var name = m.Groups[2].Value;
            pattern.Append(pattern.ToString().Contains($"(?<{name}>") ? $"\\k<{name}>" : $"(?<{name}>.*?)");
            at = m.Index + m.Length;
        }
        pattern.Append(Regex.Escape(t[at..])).Append('$');
        return new Regex(pattern.ToString(), RegexOptions.Singleline);
    });

    // {s:gold}, {i:kills}, {d:respawn_time}, {t:...}, {g:...}; a format suffix may follow a colon.
    [GeneratedRegex(@"\{([sidtgl]):([A-Za-z0-9_]+)(?::[^}]*)?\}")]
    private static partial Regex VariableRegex();
}
