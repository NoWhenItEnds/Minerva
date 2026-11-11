using System.Text;
using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Minerva.App;

/// <summary>Explore game / addon VPKs: list entries and decompile compiled resources to text.</summary>
internal static class VpkCommands
{
    public static int List(string vpkPath, string? pattern)
    {
        using var package = Open(vpkPath);
        var regex = pattern == null ? null : new Regex(pattern, RegexOptions.IgnoreCase);
        var count = 0;
        foreach (var entry in package.Entries!.Values.SelectMany(e => e).OrderBy(e => e.GetFullPath()))
        {
            var path = entry.GetFullPath();
            if (regex != null && !regex.IsMatch(path)) continue;
            Console.WriteLine($"{entry.TotalLength,10}  {path}");
            count++;
        }
        Console.Error.WriteLine($"{count} entries");
        return 0;
    }

    public static int Cat(string vpkPath, string entryPath)
    {
        using var package = Open(vpkPath);
        var entry = package.FindEntry(entryPath) ?? throw new FileNotFoundException($"'{entryPath}' not in {vpkPath}");
        package.ReadEntry(entry, out var bytes);
        Console.Write(Decompile(bytes, entryPath));
        return 0;
    }

    public static string Decompile(byte[] bytes, string name)
    {
        if (!name.EndsWith("_c", StringComparison.Ordinal)) return Encoding.UTF8.GetString(bytes);

        using var resource = new Resource { FileName = name };
        resource.Read(new MemoryStream(bytes));
        var sb = new StringBuilder();
        sb.AppendLine($"// resource type {resource.ResourceType}, version {resource.Version}, blocks: " +
                      string.Join(", ", resource.Blocks.Select(b => $"{b.Type}({b.GetType().Name})")));
        switch (resource.DataBlock)
        {
            case Panorama p when p is not PanoramaLayout and not PanoramaStyle:
                sb.Append(Encoding.UTF8.GetString(p.Data));
                break;
            case { } block:
                using (var writer = new IndentedTextWriter())
                {
                    block.WriteText(writer);
                    sb.Append(writer);
                }
                break;
        }
        return sb.ToString();
    }

    public static Package Open(string vpkPath)
    {
        var package = new Package();
        package.Read(vpkPath);
        return package;
    }
}
