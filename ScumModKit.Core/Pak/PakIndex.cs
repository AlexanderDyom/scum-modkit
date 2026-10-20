// Reads the index of an unencrypted UE 4.27 pak (version 11, as PakWriter and repak write it): mount point and the
// entries (path, data offset, size) from the full directory index.
using System.Text;

namespace ScumModKit.Pak;

public sealed record PakEntry(string Path, long Offset, long Size);

public sealed class PakIndex
{
    private const uint Magic = 0x5A6F12E1;
    private const int FooterSize = 16 + 1 + 4 + 4 + 8 + 8 + 20 + (5 * 32);

    public string MountPoint { get; private init; } = "";
    public List<PakEntry> Entries { get; } = [];

    public static PakIndex Read(string pak)
    {
        using var fs = File.OpenRead(pak);
        var r = new BinaryReader(fs);
        if (fs.Length < FooterSize) throw new FormatException($"{pak}: too small for a pak.");
        fs.Position = fs.Length - FooterSize + 16;
        if (r.ReadByte() != 0) throw new NotSupportedException($"{pak}: encrypted index.");
        if (r.ReadUInt32() != Magic) throw new FormatException($"{pak}: no pak footer.");
        var version = r.ReadInt32();
        if (version != 11) throw new NotSupportedException($"{pak}: pak version {version}, only 11 is supported.");
        var indexOffset = r.ReadInt64();
        r.ReadInt64(); // index size

        fs.Position = indexOffset;
        var index = new PakIndex { MountPoint = ReadFString(r) };
        r.ReadInt32();  // entry count
        r.ReadUInt64(); // path hash seed
        if (r.ReadInt32() != 0) { r.ReadInt64(); r.ReadInt64(); r.ReadBytes(20); } // path hash index
        if (r.ReadInt32() == 0) throw new NotSupportedException($"{pak}: no full directory index.");
        var fdiOffset = r.ReadInt64();
        r.ReadInt64();
        r.ReadBytes(20);
        var encoded = r.ReadBytes(r.ReadInt32());

        fs.Position = fdiOffset;
        var dirs = r.ReadInt32();
        for (var d = 0; d < dirs; d++)
        {
            var dir = ReadFString(r);
            var files = r.ReadInt32();
            for (var f = 0; f < files; f++)
            {
                var name = ReadFString(r);
                var loc = r.ReadInt32();
                if (loc < 0) throw new NotSupportedException($"{pak}: unencoded entries are not supported.");
                var (offset, size) = Decode(encoded, loc);
                index.Entries.Add(new PakEntry((dir == "/" ? "" : dir) + name, offset, size));
            }
        }
        return index;
    }

    // FPakFile::EncodePakEntry, uncompressed entries only
    private static (long Offset, long Size) Decode(byte[] e, int at)
    {
        var flags = BitConverter.ToUInt32(e, at);
        at += 4;
        if ((flags & 0x3F) != 0 || ((flags >> 23) & 0x3F) != 0) throw new NotSupportedException("Compressed pak entries are not supported.");
        long offset;
        if ((flags & (1u << 31)) != 0) { offset = BitConverter.ToUInt32(e, at); at += 4; }
        else { offset = BitConverter.ToInt64(e, at); at += 8; }
        var size = (flags & (1u << 30)) != 0 ? BitConverter.ToUInt32(e, at) : BitConverter.ToInt64(e, at);
        return (offset, size);
    }

    private static string ReadFString(BinaryReader r)
    {
        var len = r.ReadInt32();
        if (len == 0) return "";
        if (len > 0) return Encoding.ASCII.GetString(r.ReadBytes(len))[..^1];
        return Encoding.Unicode.GetString(r.ReadBytes(-2 * len))[..^1];
    }
}
