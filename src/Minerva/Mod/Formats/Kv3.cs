using ValveKeyValue;

namespace Minerva.Mod.Formats;

/// <summary>
/// An ordered KeyValues3 object. Values are <c>null</c>, <see cref="bool"/>, integers, <see cref="double"/>,
/// <see cref="string"/>, <see cref="Kv3Array"/> or <see cref="Kv3Object"/>.
/// </summary>
public sealed class Kv3Object : List<KeyValuePair<string, object?>>
{
    public Kv3Object() { }

    public Kv3Object(IEnumerable<KeyValuePair<string, object?>> members) : base(members) { }

    public void Add(string key, object? value) => Add(new KeyValuePair<string, object?>(key, value));

    public object? this[string key]
    {
        get => TryGet(key, out var v) ? v : null;
        set
        {
            var i = FindIndex(m => m.Key == key);
            if (i >= 0) this[i] = new(key, value);
            else Add(key, value);
        }
    }

    public bool TryGet(string key, out object? value)
    {
        foreach (var m in this)
        {
            if (m.Key == key)
            {
                value = m.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    public bool Remove(string key) => RemoveAll(m => m.Key == key) > 0;
}

public sealed class Kv3Array : List<object?>
{
    public Kv3Array() { }
    public Kv3Array(IEnumerable<object?> items) : base(items) { }
}

public static class Kv3Convert
{
    /// <summary>Copy a tree read by ValveResourceFormat / ValveKeyValue into the writable model, keeping member order.</summary>
    public static object? FromKv(KVObject kv) => kv.ValueType switch
    {
        KVValueType.Null => null,
        KVValueType.Collection => new Kv3Object(kv.Children.Select(c => new KeyValuePair<string, object?>(c.Key!, FromKv(c.Value)))),
        KVValueType.Array => new Kv3Array(kv.Children.Select(c => FromKv(c.Value))),
        KVValueType.Boolean => (bool)kv,
        KVValueType.String => (string)kv,
        KVValueType.Int16 or KVValueType.Int32 => (int)kv,
        KVValueType.Int64 => (long)kv,
        KVValueType.UInt16 or KVValueType.UInt32 => (uint)kv,
        KVValueType.UInt64 or KVValueType.Pointer => (ulong)kv,
        KVValueType.FloatingPoint => (double)(float)kv,
        KVValueType.FloatingPoint64 => (double)kv,
        KVValueType.BinaryBlob => throw new NotSupportedException("KV3 binary blobs are not supported by Kv3Writer."),
        _ => throw new NotSupportedException($"KV3 value type {kv.ValueType}"),
    };
}
