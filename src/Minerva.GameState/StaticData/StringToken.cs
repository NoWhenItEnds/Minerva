using System.Text;

namespace Minerva.GameState.StaticData;

/// <summary>
/// Source 2 <c>CUtlStringToken</c>: MurmurHash2 (seed 0x31415926) of the lower-cased string.
/// Items and abilities are networked as these tokens (e.g. <c>PlayerDataGlobal.Upgrades</c>, <c>SubclassID</c>).
/// </summary>
public static class StringToken
{
    private const uint Seed = 0x31415926;

    public static uint Hash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToLowerInvariant());
        return MurmurHash2(bytes, Seed);
    }

    private static uint MurmurHash2(ReadOnlySpan<byte> data, uint seed)
    {
        const uint m = 0x5bd1e995;
        const int r = 24;

        var len = data.Length;
        var h = seed ^ (uint)len;
        var i = 0;
        while (len >= 4)
        {
            var k = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
            k *= m;
            k ^= k >> r;
            k *= m;
            h *= m;
            h ^= k;
            i += 4;
            len -= 4;
        }

        switch (len)
        {
            case 3:
                h ^= (uint)data[i + 2] << 16;
                goto case 2;
            case 2:
                h ^= (uint)data[i + 1] << 8;
                goto case 1;
            case 1:
                h ^= data[i];
                h *= m;
                break;
        }

        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;
        return h;
    }
}
