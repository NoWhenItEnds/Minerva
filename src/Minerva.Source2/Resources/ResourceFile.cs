using System.Buffers.Binary;
using System.Text;

namespace Minerva.Source2.Resources;

public sealed record ResourceBlock(string Tag, byte[] Data);

/// <summary>
/// The Source 2 compiled-resource container (<c>*_c</c> files): a 16-byte header followed by a table of tagged blocks.
/// Reading keeps the header fields so a resource taken from the game can be written back with only some blocks
/// replaced, staying byte-compatible with the build it came from.
/// </summary>
/// <remarks>Container layout follows modlock's <c>source2/resource</c> (https://github.com/paralin/modlock, MIT).</remarks>
public sealed class ResourceFile
{
    public const ushort DefaultHeaderVersion = 12;

    public ResourceFile(ushort resourceVersion, IEnumerable<ResourceBlock> blocks, ushort headerVersion = DefaultHeaderVersion)
    {
        ResourceVersion = resourceVersion;
        HeaderVersion = headerVersion;
        Blocks = blocks.ToList();
    }

    public ushort HeaderVersion { get; }
    public ushort ResourceVersion { get; }
    public List<ResourceBlock> Blocks { get; }

    public ResourceBlock? Find(string tag) => Blocks.FirstOrDefault(b => b.Tag == tag);

    public ResourceFile WithBlock(string tag, byte[] data)
    {
        var blocks = Blocks.ToList();
        var i = blocks.FindIndex(b => b.Tag == tag);
        if (i >= 0) blocks[i] = new ResourceBlock(tag, data);
        else blocks.Add(new ResourceBlock(tag, data));
        return new ResourceFile(ResourceVersion, blocks, HeaderVersion);
    }

    public static ResourceFile Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) throw new InvalidDataException("Resource is truncated.");
        var headerVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var table = 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (count > bytes.Length / 12) throw new InvalidDataException("Resource block table is truncated.");

        var blocks = new List<ResourceBlock>(count);
        for (var i = 0; i < count; i++)
        {
            var at = table + i * 12;
            if (at + 12 > bytes.Length) throw new InvalidDataException("Resource block table is truncated.");
            var tag = Encoding.ASCII.GetString(bytes.Slice(at, 4));
            var start = at + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 8)..]);
            if (start < 0 || size < 0 || start + size > bytes.Length) throw new InvalidDataException($"Resource block {tag} is truncated.");
            blocks.Add(new ResourceBlock(tag, bytes.Slice(start, size).ToArray()));
        }
        return new ResourceFile(version, blocks, headerVersion);
    }

    public byte[] Write()
    {
        static int Align(int offset) => (offset + 15) / 16 * 16;

        var offsets = new int[Blocks.Count];
        var end = Align(16 + Blocks.Count * 12);
        var size = 16;
        for (var i = 0; i < Blocks.Count; i++)
        {
            if (Blocks[i].Tag.Length != 4) throw new InvalidDataException($"Block tag '{Blocks[i].Tag}' is not four characters.");
            offsets[i] = end;
            size = end + Blocks[i].Data.Length;
            end = Align(size);
        }

        var o = new byte[size];
        var s = o.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], HeaderVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], ResourceVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 8); // block table follows at offset 16
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)Blocks.Count);

        for (var i = 0; i < Blocks.Count; i++)
        {
            var at = 16 + i * 12;
            Encoding.ASCII.GetBytes(Blocks[i].Tag, s.Slice(at, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 4)..], (uint)(offsets[i] - (at + 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 8)..], (uint)Blocks[i].Data.Length);
        }
        for (var i = 0; i < Blocks.Count; i++) Blocks[i].Data.CopyTo(s[offsets[i]..]);
        return o;
    }
}
