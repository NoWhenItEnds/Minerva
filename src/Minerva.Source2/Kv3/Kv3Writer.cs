using System.Buffers.Binary;
using System.Text;

namespace Minerva.Source2.Kv3;

/// <summary>
/// Writes binary KeyValues3 version 5 (LZ4 "literals only" blocks), the form Valve's resource compiler writes for
/// resource blocks such as RED2 and Panorama's LaCo.
/// </summary>
/// <remarks>
/// Ported from modlock's <c>source2/kv3/encode.go</c> (https://github.com/paralin/modlock, MIT License).
/// Verified by round-tripping through ValveResourceFormat's reader (see tests).
/// </remarks>
public static class Kv3Writer
{
    private const uint Magic = 0x4b563305; // "\x05" "3VK"
    private const uint EndMarker = 0xffeedd00;
    private const uint CompressionLz4 = 1;
    private const ushort FrameSize = 16384;
    private const int MaxDepth = 512;

    // Generic KV3 format GUID 7412167c-06e9-4698-aff2-e63eb59037e7 in on-disk byte order.
    private static readonly byte[] GenericFormat =
    [
        0x7c, 0x16, 0x12, 0x74, 0xe9, 0x06, 0x98, 0x46,
        0xaf, 0xf2, 0xe6, 0x3e, 0xb5, 0x90, 0x37, 0xe7,
    ];

    // KV3 v5 type bytes.
    private const byte TNull = 1, TInt64 = 3, TUInt64 = 4, TDouble = 5, TString = 6, TArray = 8, TObject = 9,
        TInt32 = 11, TUInt32 = 12, TTrue = 13, TFalse = 14;

    public static byte[] Write(object? value)
    {
        var e = new Encoder();
        e.Walk(value, 0);

        // Buffer 1: strings, padded to 4, then their count.
        var first = new List<byte>();
        foreach (var s in e.Order)
        {
            first.AddRange(Encoding.UTF8.GetBytes(s));
            first.Add(0);
        }
        var stringBytes = first.Count;
        Pad(first, 4);
        AddU32(first, (uint)e.Order.Count);

        // Buffer 2: object member counts, 4-byte lane, 8-byte lane (aligned), types, end marker.
        var second = new List<byte>(e.Lengths);
        second.AddRange(e.Bytes4);
        if (e.Bytes8.Count != 0)
        {
            Pad(second, 8);
            second.AddRange(e.Bytes8);
        }
        second.AddRange(e.Types);
        AddU32(second, EndMarker);

        var packedFirst = Lz4Literals(first);
        var packedSecond = Lz4Literals(second);

        var o = new List<byte>();
        AddU32(o, Magic);
        o.AddRange(GenericFormat);
        AddU32(o, CompressionLz4);
        AddU16(o, 0);
        AddU16(o, FrameSize);
        foreach (var f in new[] { stringBytes, 1, 0, e.Types.Count }) AddU32(o, (uint)f);
        AddU16(o, (ushort)e.Objects);
        AddU16(o, (ushort)e.Arrays);
        foreach (var f in new[]
                 {
                     first.Count + second.Count, packedFirst.Count + packedSecond.Count,
                     0, 0, 0, 0,
                     first.Count, packedFirst.Count, second.Count, packedSecond.Count,
                     0, 0, e.Bytes4.Count / 4, e.Bytes8.Count / 8,
                     0, e.Objects, e.Arrays, 0,
                 })
        {
            AddU32(o, (uint)f);
        }

        o.AddRange(packedFirst);
        o.AddRange(packedSecond);
        return o.ToArray();
    }

    private sealed class Encoder
    {
        private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
        public readonly List<string> Order = [];
        public readonly List<byte> Types = [];
        public readonly List<byte> Lengths = [];
        public readonly List<byte> Bytes4 = [];
        public readonly List<byte> Bytes8 = [];
        public int Objects, Arrays;

        public void Walk(object? value, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("KeyValues3 value nests too deeply.");
            switch (value)
            {
                case null:
                    Types.Add(TNull);
                    break;
                case bool b:
                    Types.Add(b ? TTrue : TFalse);
                    break;
                case string s:
                    Types.Add(TString);
                    AddU32(Bytes4, (uint)Text(s));
                    break;
                case sbyte or short or int or long or byte or ushort or uint:
                    Integer(Convert.ToInt64(value));
                    break;
                case ulong ul:
                    if (ul <= long.MaxValue) Integer((long)ul);
                    else
                    {
                        Types.Add(TUInt64);
                        AddU64(Bytes8, ul);
                    }
                    break;
                case float f:
                    Double(f);
                    break;
                case double d:
                    Double(d);
                    break;
                case Kv3Array array:
                    Types.Add(TArray);
                    Arrays++;
                    AddU32(Bytes4, (uint)array.Count);
                    foreach (var item in array) Walk(item, depth + 1);
                    break;
                case Kv3Object obj:
                    Types.Add(TObject);
                    Objects++;
                    AddU32(Lengths, (uint)obj.Count);
                    foreach (var (key, v) in obj)
                    {
                        AddU32(Bytes4, (uint)Text(key));
                        Walk(v, depth + 1);
                    }
                    break;
                default:
                    throw new NotSupportedException($"KeyValues3 cannot hold a {value.GetType().Name}.");
            }
        }

        private void Integer(long v)
        {
            if (v is >= int.MinValue and <= int.MaxValue)
            {
                Types.Add(TInt32);
                AddU32(Bytes4, (uint)(int)v);
            }
            else if (v is >= 0 and <= uint.MaxValue)
            {
                Types.Add(TUInt32);
                AddU32(Bytes4, (uint)v);
            }
            else
            {
                Types.Add(TInt64);
                AddU64(Bytes8, (ulong)v);
            }
        }

        private void Double(double v)
        {
            Types.Add(TDouble);
            AddU64(Bytes8, (ulong)BitConverter.DoubleToInt64Bits(v));
        }

        /// <summary>String id, numbered on first use; the empty string is -1.</summary>
        private int Text(string s)
        {
            if (s.Length == 0) return -1;
            if (!_strings.TryGetValue(s, out var id))
            {
                id = Order.Count;
                _strings[s] = id;
                Order.Add(s);
            }
            return id;
        }
    }

    /// <summary>One LZ4 block containing only literals: valid for every LZ4 decoder.</summary>
    private static List<byte> Lz4Literals(List<byte> data)
    {
        var o = new List<byte>(data.Count + 8) { (byte)(Math.Min(data.Count, 15) << 4) };
        if (data.Count >= 15)
        {
            var rest = data.Count - 15;
            for (; rest >= 255; rest -= 255) o.Add(255);
            o.Add((byte)rest);
        }
        o.AddRange(data);
        return o;
    }

    private static void Pad(List<byte> b, int alignment)
    {
        while (b.Count % alignment != 0) b.Add(0);
    }

    private static void AddU16(List<byte> b, ushort v)
    {
        Span<byte> s = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(s, v);
        b.AddRange(s);
    }

    private static void AddU32(List<byte> b, uint v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        b.AddRange(s);
    }

    private static void AddU64(List<byte> b, ulong v)
    {
        Span<byte> s = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(s, v);
        b.AddRange(s);
    }
}
