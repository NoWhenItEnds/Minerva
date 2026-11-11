namespace Minerva.App;

/// <summary>Command line: a command, positional arguments and <c>--name value</c> options.</summary>
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
