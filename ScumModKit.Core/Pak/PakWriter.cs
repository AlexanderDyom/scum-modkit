// Minimal UE 4.27 pak writer (version 11 = Fnv64BugFix): uncompressed, unencrypted, with the path hash index and the full
// directory index. The byte layout matches what repak (`repak pack --version V11 -m ../../../`) writes for the same files
// in the same data order.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ScumModKit.Pak;

public static class PakWriter
{
    private const uint Magic = 0x5A6F12E1;
    private const int Version = 11;

    /// <summary>Writes <paramref name="files"/> (path relative to the mount point, e.g. SCUM/AssetRegistry.bin) to a pak.</summary>
    public static void Write(string pakPath, IReadOnlyList<(string Path, byte[] Data)> files, string mountPoint = "../../../")
    {
        using var fs = new FileStream(pakPath, FileMode.Create, FileAccess.Write);
        var w = new BinaryWriter(fs);
        var entries = new List<(string Path, long Offset, long Size)>();
        foreach (var (path, data) in files)
        {
            var offset = fs.Position;
            WriteEntryHeader(w, 0, data.Length, SHA1.HashData(data));
            w.Write(data);
            entries.Add((path.Replace('\\', '/'), offset, data.Length));
        }
        // data in the given order, index entries by path (repak's order; its data order varies, it packs in parallel)
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        // encoded entries (FPakFile::EncodePakEntry): flags, then offset and size as u32 or u64
        var encoded = new MemoryStream();
        var ew = new BinaryWriter(encoded);
        var location = new Dictionary<string, int>();
        foreach (var (path, offset, size) in entries)
        {
            location[path] = (int)encoded.Length;
            var offset32 = offset <= uint.MaxValue;
            var size32 = size <= uint.MaxValue;
            var flags = (offset32 ? 1u << 31 : 0) | (size32 ? 1u << 30 : 0) | (size32 ? 1u << 29 : 0);
            ew.Write(flags);
            if (offset32) ew.Write((uint)offset); else ew.Write(offset);
            if (size32) ew.Write((uint)size); else ew.Write(size);
        }

        // path hash index: TMap<u64 hash, i32 location> + empty pruned directory index
        const ulong seed = 0;
        var phi = new MemoryStream();
        var pw = new BinaryWriter(phi);
        pw.Write(entries.Count);
        foreach (var (path, _, _) in entries)
        {
            pw.Write(Fnv64(path.ToLowerInvariant(), seed));
            pw.Write(location[path]);
        }
        pw.Write(0);

        // full directory index: directories ("/" and every parent) -> file name -> location
        var dirs = new SortedDictionary<string, List<(string Name, int Loc)>>(StringComparer.Ordinal) { ["/"] = [] };
        foreach (var (path, _, _) in entries)
        {
            var slash = path.LastIndexOf('/');
            var dir = slash < 0 ? "/" : path[..(slash + 1)];
            for (var p = dir.IndexOf('/'); p >= 0 && p < dir.Length - 1; p = dir.IndexOf('/', p + 1))
                dirs.TryAdd(dir[..(p + 1)], []);
            if (!dirs.TryGetValue(dir, out var list)) dirs[dir] = list = [];
            list.Add((path[(slash + 1)..], location[path]));
        }
        var fdi = new MemoryStream();
        var fw = new BinaryWriter(fdi);
        fw.Write(dirs.Count);
        foreach (var (dir, list) in dirs)
        {
            WriteFString(fw, dir);
            fw.Write(list.Count);
            foreach (var (name, loc) in list) { WriteFString(fw, name); fw.Write(loc); }
        }

        // primary index
        var indexOffset = fs.Position;
        var phiBytes = phi.ToArray();
        var fdiBytes = fdi.ToArray();
        var index = new MemoryStream();
        var iw = new BinaryWriter(index);
        WriteFString(iw, mountPoint);
        iw.Write(entries.Count);
        iw.Write(seed);
        var indexSize = 0L; // computed below: secondary indices follow the primary one
        // sizes are fixed, so the primary index length is known before the offsets are written
        var primaryLength = 4 + Encoding.ASCII.GetByteCount(mountPoint) + 1 + 4 + 8 + (4 + 8 + 8 + 20) * 2 + 4 + encoded.Length + 4;
        var phiOffset = indexOffset + primaryLength;
        var fdiOffset = phiOffset + phiBytes.Length;
        iw.Write(1); iw.Write(phiOffset); iw.Write((long)phiBytes.Length); iw.Write(SHA1.HashData(phiBytes));
        iw.Write(1); iw.Write(fdiOffset); iw.Write((long)fdiBytes.Length); iw.Write(SHA1.HashData(fdiBytes));
        iw.Write((int)encoded.Length);
        iw.Write(encoded.ToArray());
        iw.Write(0); // no unencodable entries
        var indexBytes = index.ToArray();
        if (indexBytes.Length != primaryLength) throw new InvalidOperationException("Pak index size mismatch.");
        indexSize = indexBytes.Length;
        w.Write(indexBytes);
        w.Write(phiBytes);
        w.Write(fdiBytes);

        // footer (FPakInfo v11)
        w.Write(new byte[16]); // encryption key guid
        w.Write((byte)0);      // index not encrypted
        w.Write(Magic);
        w.Write(Version);
        w.Write(indexOffset);
        w.Write(indexSize);
        w.Write(SHA1.HashData(indexBytes));
        w.Write(new byte[5 * 32]); // compression method names (none)
    }

    private static void WriteEntryHeader(BinaryWriter w, long offset, long size, byte[] sha1)
    {
        w.Write(offset);
        w.Write(size);
        w.Write(size); // uncompressed size
        w.Write(0);    // compression method index (none)
        w.Write(sha1);
        w.Write((byte)0); // flags
        w.Write(0);    // compression block size
    }

    private static void WriteFString(BinaryWriter w, string s)
    {
        w.Write(s.Length + 1);
        w.Write(Encoding.ASCII.GetBytes(s));
        w.Write((byte)0);
    }

    // FPakFile::HashPath (version >= Fnv64BugFix): FNV-1a 64 over the lower-cased UTF-16 path, offset basis + seed.
    private static ulong Fnv64(string lower, ulong seed)
    {
        var h = 0xcbf29ce484222325UL + seed;
        foreach (var b in Encoding.Unicode.GetBytes(lower))
        {
            h ^= b;
            h *= 0x100000001b3UL;
        }
        return h;
    }
}
