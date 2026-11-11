using System.Text.RegularExpressions;

namespace Minerva.Mod.Formats;

/// <summary>Reads the class names Panorama stylesheets select on.</summary>
public static partial class CssSelectors
{
    /// <summary>
    /// The class sets of every compound selector in a stylesheet: <c>.a.b .c, .d</c> gives {a, b}, {c}, {d}.
    /// Classes in one compound apply to the same panel.
    /// </summary>
    public static IEnumerable<IReadOnlySet<string>> CompoundClassSets(string css)
    {
        css = CommentRegex().Replace(css, " ");
        var boundary = 0;
        for (var i = 0; i < css.Length; i++)
        {
            switch (css[i])
            {
                case '{':
                    // The text since the last brace is a selector list (or an at-rule such as @media, @keyframes).
                    var selectors = css[boundary..i].Trim();
                    boundary = i + 1;
                    if (selectors.StartsWith('@')) continue;
                    foreach (var selector in selectors.Split(','))
                    foreach (var compound in CombinatorRegex().Split(selector.Trim()))
                    {
                        var classes = ClassRegex().Matches(compound).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                        if (classes.Count > 0) yield return classes;
                    }
                    break;
                case '}':
                case ';':
                    boundary = i + 1;
                    break;
            }
        }
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"\s*[>+~]\s*|\s+")]
    private static partial Regex CombinatorRegex();

    [GeneratedRegex(@"\.(-?[_a-zA-Z][\w-]*)")]
    private static partial Regex ClassRegex();
}
