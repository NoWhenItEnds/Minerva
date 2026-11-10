using System.Globalization;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Minerva.GameState.StaticData;

/// <summary>Reads Valve data files (<c>*.vdata_c</c>, entity lumps) out of game packages.</summary>
internal static class VData
{
    public static byte[] ReadEntry(Package package, string path)
    {
        var entry = package.FindEntry(path) ?? throw new FileNotFoundException($"'{path}' not found in {package.FileName}.");
        package.ReadEntry(entry, out var bytes);
        return bytes;
    }

    /// <summary>Top-level definitions of a vdata file (class name → its keys).</summary>
    public static IEnumerable<(string Key, KVObject Value)> Read(Package package, string path)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(ReadEntry(package, path)));
        if (resource.DataBlock is not BinaryKV3 kv3)
            throw new InvalidDataException($"'{path}' is not KV3 data (got {resource.DataBlock?.GetType().Name}).");

        var root = kv3.Data.Root;
        foreach (var key in root.Keys)
        {
            if (root.TryGetValue(key, out var value) && value.ValueType == KVValueType.Collection)
                yield return (key, value);
        }
    }

    public static string? GetString(KVObject obj, string key) =>
        obj.TryGetValue(key, out var v) && v.ValueType == KVValueType.String ? (string)v : null;

    /// <summary>A number, whether stored as a number or as a string (entity keys are often strings).</summary>
    public static double? GetNumber(KVObject obj, string key)
    {
        if (!obj.TryGetValue(key, out var v) || v.IsNull) return null;
        return v.ValueType switch
        {
            KVValueType.String => double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            KVValueType.Collection or KVValueType.Array or KVValueType.BinaryBlob => null,
            _ => v.ToDouble(CultureInfo.InvariantCulture),
        };
    }
}
