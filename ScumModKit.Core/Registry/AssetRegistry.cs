// Cooked UE 4.27 AssetRegistry.bin (FAssetRegistryVersion 8 = FixedTags): full reader and writer.
// Layout follows Engine/Source/Runtime/AssetRegistry (FAssetRegistryState::Save, FAssetRegistryWriter, FixedTagPrivate)
// and ScumStudio's AssetRegistryFile (MIT, github.com/ALLAWI32/SCUM-Modding-Studio), which keeps the part after the asset
// list verbatim; here the depends nodes and package data are decoded too, so they can be merged.
// An unmodified file saves byte-identical (checked by the `verify` command).
using System.Buffers.Binary;
using System.Text;

namespace ScumModKit.Registry;

/// <summary>A name reference: index into the name batch plus FName number (0 = none).</summary>
public readonly record struct NameRef(uint Index, int Number);

/// <summary>One FAssetData record. Rest = bundles, chunk ids and package flags as stored (names inside are batch indices).</summary>
public sealed class AssetRecord
{
    public NameRef ObjectPath, PackagePath, AssetClass, PackageName, AssetName;
    public ulong TagHandle;
    public List<Bundle> Bundles = [];
    public int[] ChunkIds = [];
    public uint PackageFlags;
}

public sealed class Bundle
{
    public NameRef Name;
    public List<(NameRef Path, byte[] SubPath)> Assets = []; // SubPath = serialized FString (len + chars)
}

/// <summary>FAssetIdentifier: field bits + up to four names.</summary>
public sealed class AssetId
{
    public byte Bits;
    public NameRef PackageName, PrimaryAssetType, ObjectName, ValueName;
}

/// <summary>FDependsNode as serialized: identifier and four lists of node indices, the flagged ones with a flag value per edge.</summary>
public sealed class DependsNode
{
    public const int PackageFlagWidth = 3, ManageFlagWidth = 1;
    public AssetId Id = new();
    public List<(int Node, uint Flags)> Package = [];
    public List<(int Node, uint Flags)> Name = [];
    public List<(int Node, uint Flags)> Manage = [];
    public List<(int Node, uint Flags)> Referencers = [];
}

/// <summary>FAssetPackageData (4.27): disk size, package guid, cooked MD5 hash.</summary>
public sealed class PackageRecord
{
    public NameRef PackageName;
    public long DiskSize;
    public byte[] Guid = new byte[16];
    public uint HashValid;
    public byte[] Hash = [];
}

public sealed class AssetRegistry
{
    public const uint StoreBeginMagic = 0x12345679, StoreEndMagic = 0x87654321;
    public const ulong HashAlgorithm = 0xC1640000;

    // value kinds (low 3 bits of a value id)
    private const uint KAnsi = 0, KWide = 1, KNlName = 2, KName = 3, KNlExport = 4, KExport = 5, KText = 6;

    public byte[] Head = []; // version guid + int32 version
    public readonly List<string> Names = [];
    public readonly List<bool> NameWide = [];
    public readonly List<ulong> NameHashes = [];
    private readonly Dictionary<(string, bool), int> _nameIndex = new();

    // fixed tag store
    public readonly List<byte[]> Texts = [];
    public readonly List<uint> NlNames = [];
    public readonly List<NameRef> NameValues = [];
    public readonly List<uint[]> NlExports = [];
    public readonly List<NameRef[]> Exports = [];
    public readonly List<uint> AnsiOffsets = [];
    public readonly List<uint> WideOffsets = [];
    public readonly MemoryStream AnsiChars = new();
    public readonly MemoryStream WideChars = new(); // UTF-16 bytes
    public readonly List<(uint Key, uint Value)> NlPairs = [];
    public readonly List<(NameRef Key, uint Value)> Pairs = [];

    public readonly List<AssetRecord> Assets = [];
    public readonly List<DependsNode> Nodes = [];
    public readonly List<PackageRecord> Packages = [];

    public int Version => BinaryPrimitives.ReadInt32LittleEndian(Head.AsSpan(16));

    // ------------------------------------------------------------------ names

    public string NameText(NameRef n) => n.Number == 0 ? Names[(int)n.Index] : $"{Names[(int)n.Index]}_{n.Number - 1}";

    /// <summary>Index of a name batch entry, appended (with the given stored hash) when missing.</summary>
    public uint InternName(string text, bool wide, ulong hash)
    {
        if (_nameIndex.TryGetValue((text, wide), out var i)) return (uint)i;
        i = Names.Count;
        Names.Add(text);
        NameWide.Add(wide);
        NameHashes.Add(hash);
        _nameIndex[(text, wide)] = i;
        return (uint)i;
    }

    /// <summary>The name <paramref name="n"/> of <paramref name="src"/> as a name of this registry.</summary>
    public NameRef Import(AssetRegistry src, NameRef n)
    {
        var i = (int)n.Index;
        return new NameRef(InternName(src.Names[i], src.NameWide[i], src.NameHashes[i]), n.Number);
    }

    // ------------------------------------------------------------------ load

    public static AssetRegistry Load(byte[] d)
    {
        var reg = new AssetRegistry();
        var r = new Reader(d);
        reg.Head = r.Bytes(20);
        var version = reg.Version;
        if (version != 8) throw new FormatException($"AssetRegistry version {version}, only 8 (UE 4.27 FixedTags) is supported.");

        // name batch
        var num = checked((int)r.U32());
        var nbytes = r.U32();
        if (r.U64() != HashAlgorithm) throw new FormatException("Unexpected name batch hash algorithm.");
        for (var i = 0; i < num; i++) reg.NameHashes.Add(r.U64());
        var hdr = r.Bytes(2 * num);
        var s0 = r.Pos;
        for (var i = 0; i < num; i++)
        {
            var wide = (hdr[2 * i] & 0x80) != 0;
            var len = ((hdr[2 * i] & 0x7F) << 8) | hdr[(2 * i) + 1];
            var name = wide ? Encoding.Unicode.GetString(r.Bytes(2 * len)) : Encoding.Latin1.GetString(r.Bytes(len));
            reg.Names.Add(name);
            reg.NameWide.Add(wide);
            reg._nameIndex.TryAdd((name, wide), i);
        }
        if (r.Pos - s0 != nbytes) throw new FormatException("Name batch size mismatch.");

        // fixed tag store
        if (r.U32() != StoreBeginMagic) throw new FormatException("Tag store begin magic not found.");
        var n = new int[11];
        for (var i = 0; i < 11; i++) n[i] = r.I32();
        var textBytes = r.U32();
        var t0 = r.Pos;
        for (var i = 0; i < n[4]; i++)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(r.Pos));
            reg.Texts.Add(r.Bytes(4 + (len >= 0 ? len : -2 * len)));
        }
        if (r.Pos - t0 != textBytes) throw new FormatException("Tag store text size mismatch.");
        for (var i = 0; i < n[0]; i++) reg.NlNames.Add(r.U32());
        for (var i = 0; i < n[1]; i++) reg.NameValues.Add(r.Name());
        for (var i = 0; i < n[2]; i++) reg.NlExports.Add([r.U32(), r.U32(), r.U32()]);
        for (var i = 0; i < n[3]; i++) reg.Exports.Add([r.Name(), r.Name(), r.Name()]);
        for (var i = 0; i < n[5]; i++) reg.AnsiOffsets.Add(r.U32());
        for (var i = 0; i < n[6]; i++) reg.WideOffsets.Add(r.U32());
        reg.AnsiChars.Write(r.Bytes(n[7]));
        reg.WideChars.Write(r.Bytes(2 * n[8]));
        for (var i = 0; i < n[9]; i++) reg.NlPairs.Add((r.U32(), r.U32()));
        for (var i = 0; i < n[10]; i++) reg.Pairs.Add((r.Name(), r.U32()));
        if (r.U32() != StoreEndMagic) throw new FormatException("Tag store end magic not found.");

        // assets
        var na = r.I32();
        for (var i = 0; i < na; i++)
        {
            var a = new AssetRecord
            {
                ObjectPath = r.Name(), PackagePath = r.Name(), AssetClass = r.Name(), PackageName = r.Name(), AssetName = r.Name(),
                TagHandle = r.U64(),
            };
            var nb = r.I32();
            for (var b = 0; b < nb; b++)
            {
                var bundle = new Bundle { Name = r.Name() };
                var nc = r.I32();
                for (var c = 0; c < nc; c++) bundle.Assets.Add((r.Name(), r.FString()));
                a.Bundles.Add(bundle);
            }
            a.ChunkIds = new int[r.I32()];
            for (var c = 0; c < a.ChunkIds.Length; c++) a.ChunkIds[c] = r.I32();
            a.PackageFlags = r.U32();
            reg.Assets.Add(a);
        }

        // depends nodes
        // depends nodes, preceded by the byte size of that section (int64)
        var depSize = r.I64();
        var depStart = r.Pos;
        var nn = r.I32();
        for (var i = 0; i < nn; i++)
        {
            var node = new DependsNode();
            node.Id.Bits = r.U8();
            if ((node.Id.Bits & 1) != 0) node.Id.PackageName = r.Name();
            if ((node.Id.Bits & 2) != 0) node.Id.PrimaryAssetType = r.Name();
            if ((node.Id.Bits & 4) != 0) node.Id.ObjectName = r.Name();
            if ((node.Id.Bits & 8) != 0) node.Id.ValueName = r.Name();
            if ((node.Id.Bits & ~15) != 0) throw new FormatException($"Depends node {i}: unknown identifier bits {node.Id.Bits}.");
            ReadDeps(r, node.Package, DependsNode.PackageFlagWidth, nn);
            ReadDeps(r, node.Name, 0, nn);
            ReadDeps(r, node.Manage, DependsNode.ManageFlagWidth, nn);
            ReadDeps(r, node.Referencers, 0, nn);
            reg.Nodes.Add(node);
        }

        if (r.Pos - depStart != depSize) throw new FormatException("Dependency section size mismatch.");

        // package data
        var np = r.I32();
        for (var i = 0; i < np; i++)
        {
            var p = new PackageRecord { PackageName = r.Name(), DiskSize = r.I64(), Guid = r.Bytes(16), HashValid = r.U32() };
            if (p.HashValid > 1) throw new FormatException($"Package data {i}: bad hash flag {p.HashValid}.");
            if (p.HashValid != 0) p.Hash = r.Bytes(16);
            reg.Packages.Add(p);
        }

        if (r.Pos != d.Length) throw new FormatException($"{d.Length - r.Pos} unread bytes at the end of the registry.");
        return reg;
    }

    private static void ReadDeps(Reader r, List<(int, uint)> list, int width, int nodeCount)
    {
        var count = r.I32();
        var idx = new int[count];
        for (var i = 0; i < count; i++)
        {
            idx[i] = r.I32();
            if (idx[i] < 0 || idx[i] >= nodeCount) throw new FormatException($"Dependency index {idx[i]} out of range.");
        }
        var words = width == 0 ? [] : new uint[((width * count) + 31) / 32];
        for (var i = 0; i < words.Length; i++) words[i] = r.U32();
        for (var i = 0; i < count; i++)
        {
            uint flags = 0;
            for (var b = 0; b < width; b++)
            {
                var bit = (i * width) + b;
                if ((words[bit >> 5] & (1u << (bit & 31))) != 0) flags |= 1u << b;
            }
            list.Add((idx[i], flags));
        }
    }

    // ------------------------------------------------------------------ save

    public byte[] Save()
    {
        var w = new Writer();
        w.Bytes(Head);
        var nbytes = 0;
        for (var i = 0; i < Names.Count; i++) nbytes += NameWide[i] ? 2 * Names[i].Length : Names[i].Length;
        w.U32((uint)Names.Count);
        w.U32((uint)nbytes);
        w.U64(HashAlgorithm);
        foreach (var h in NameHashes) w.U64(h);
        for (var i = 0; i < Names.Count; i++)
        {
            var len = Names[i].Length;
            if (len > 0x7FFF) throw new InvalidOperationException("Name too long for the name batch.");
            w.U8((byte)((len >> 8) | (NameWide[i] ? 0x80 : 0)));
            w.U8((byte)len);
        }
        for (var i = 0; i < Names.Count; i++)
            w.Bytes(NameWide[i] ? Encoding.Unicode.GetBytes(Names[i]) : Encoding.Latin1.GetBytes(Names[i]));

        w.U32(StoreBeginMagic);
        foreach (var c in new[] { NlNames.Count, NameValues.Count, NlExports.Count, Exports.Count, Texts.Count,
                     AnsiOffsets.Count, WideOffsets.Count, (int)AnsiChars.Length, (int)(WideChars.Length / 2), NlPairs.Count, Pairs.Count })
            w.I32(c);
        w.U32((uint)Texts.Sum(t => t.Length));
        foreach (var t in Texts) w.Bytes(t);
        foreach (var x in NlNames) w.U32(x);
        foreach (var x in NameValues) w.Name(x);
        foreach (var x in NlExports) { w.U32(x[0]); w.U32(x[1]); w.U32(x[2]); }
        foreach (var x in Exports) { w.Name(x[0]); w.Name(x[1]); w.Name(x[2]); }
        foreach (var x in AnsiOffsets) w.U32(x);
        foreach (var x in WideOffsets) w.U32(x);
        w.Bytes(AnsiChars.ToArray());
        w.Bytes(WideChars.ToArray());
        foreach (var (k, v) in NlPairs) { w.U32(k); w.U32(v); }
        foreach (var (k, v) in Pairs) { w.Name(k); w.U32(v); }
        w.U32(StoreEndMagic);

        w.I32(Assets.Count);
        foreach (var a in Assets)
        {
            w.Name(a.ObjectPath); w.Name(a.PackagePath); w.Name(a.AssetClass); w.Name(a.PackageName); w.Name(a.AssetName);
            w.U64(a.TagHandle);
            w.I32(a.Bundles.Count);
            foreach (var b in a.Bundles)
            {
                w.Name(b.Name);
                w.I32(b.Assets.Count);
                foreach (var (path, sub) in b.Assets) { w.Name(path); w.Bytes(sub); }
            }
            w.I32(a.ChunkIds.Length);
            foreach (var c in a.ChunkIds) w.I32(c);
            w.U32(a.PackageFlags);
        }

        var depSizeAt = w.Length;
        w.I64(0);
        w.I32(Nodes.Count);
        foreach (var node in Nodes)
        {
            w.U8(node.Id.Bits);
            if ((node.Id.Bits & 1) != 0) w.Name(node.Id.PackageName);
            if ((node.Id.Bits & 2) != 0) w.Name(node.Id.PrimaryAssetType);
            if ((node.Id.Bits & 4) != 0) w.Name(node.Id.ObjectName);
            if ((node.Id.Bits & 8) != 0) w.Name(node.Id.ValueName);
            WriteDeps(w, node.Package, DependsNode.PackageFlagWidth);
            WriteDeps(w, node.Name, 0);
            WriteDeps(w, node.Manage, DependsNode.ManageFlagWidth);
            WriteDeps(w, node.Referencers, 0);
        }

        w.PatchI64(depSizeAt, w.Length - depSizeAt - 8);
        w.I32(Packages.Count);
        foreach (var p in Packages)
        {
            w.Name(p.PackageName); w.I64(p.DiskSize); w.Bytes(p.Guid); w.U32(p.HashValid);
            if (p.HashValid != 0) w.Bytes(p.Hash);
        }
        return w.ToArray();
    }

    private static void WriteDeps(Writer w, List<(int Node, uint Flags)> list, int width)
    {
        w.I32(list.Count);
        foreach (var (node, _) in list) w.I32(node);
        if (width == 0) return;
        var words = new uint[((width * list.Count) + 31) / 32];
        for (var i = 0; i < list.Count; i++)
            for (var b = 0; b < width; b++)
                if ((list[i].Flags & (1u << b)) != 0)
                {
                    var bit = (i * width) + b;
                    words[bit >> 5] |= 1u << (bit & 31);
                }
        foreach (var x in words) w.U32(x);
    }

    // ------------------------------------------------------------------ tags

    public int TagCount(ulong handle) => (int)((handle >> 32) & 0xFFFF);

    /// <summary>
    /// Copies the tag map <paramref name="handle"/> of <paramref name="src"/> into this store (values appended, names
    /// re-interned) and returns the new handle.
    /// </summary>
    public ulong ImportTags(AssetRegistry src, ulong handle)
    {
        var count = TagCount(handle);
        var first = (int)(handle & 0xFFFFFFFF);
        var numberless = (handle >> 63) != 0;
        if (count == 0) return handle & (1UL << 63);
        if (numberless)
        {
            var start = NlPairs.Count;
            for (var k = 0; k < count; k++)
            {
                var (key, value) = src.NlPairs[first + k];
                var newKey = Import(src, new NameRef(key, 0)).Index;
                NlPairs.Add((newKey, ImportValue(src, value)));
            }
            return (1UL << 63) | ((ulong)count << 32) | (uint)start;
        }
        else
        {
            var start = Pairs.Count;
            for (var k = 0; k < count; k++)
            {
                var (key, value) = src.Pairs[first + k];
                Pairs.Add((Import(src, key), ImportValue(src, value)));
            }
            return ((ulong)count << 32) | (uint)start;
        }
    }

    private uint ImportValue(AssetRegistry src, uint id)
    {
        var kind = id & 7;
        var i = (int)(id >> 3);
        switch (kind)
        {
            case KAnsi:
            {
                var chars = src.AnsiChars.GetBuffer();
                var o = (int)src.AnsiOffsets[i];
                var e = Array.IndexOf(chars, (byte)0, o, (int)src.AnsiChars.Length - o);
                var off = (uint)AnsiChars.Length;
                AnsiChars.Write(chars, o, (e < 0 ? (int)src.AnsiChars.Length : e + 1) - o);
                if (e < 0) AnsiChars.WriteByte(0);
                AnsiOffsets.Add(off);
                return ((uint)(AnsiOffsets.Count - 1) << 3) | KAnsi;
            }
            case KWide:
            {
                var chars = src.WideChars.GetBuffer();
                var o = 2 * (int)src.WideOffsets[i];
                var e = o;
                while (e + 1 < src.WideChars.Length && (chars[e] | chars[e + 1]) != 0) e += 2;
                var off = (uint)(WideChars.Length / 2);
                WideChars.Write(chars, o, e - o);
                WideChars.WriteByte(0); WideChars.WriteByte(0);
                WideOffsets.Add(off);
                return ((uint)(WideOffsets.Count - 1) << 3) | KWide;
            }
            case KNlName:
                NlNames.Add(Import(src, new NameRef(src.NlNames[i], 0)).Index);
                return ((uint)(NlNames.Count - 1) << 3) | KNlName;
            case KName:
                NameValues.Add(Import(src, src.NameValues[i]));
                return ((uint)(NameValues.Count - 1) << 3) | KName;
            case KNlExport:
            {
                var e = src.NlExports[i];
                NlExports.Add([Import(src, new NameRef(e[0], 0)).Index, Import(src, new NameRef(e[1], 0)).Index, Import(src, new NameRef(e[2], 0)).Index]);
                return ((uint)(NlExports.Count - 1) << 3) | KNlExport;
            }
            case KExport:
            {
                var e = src.Exports[i];
                Exports.Add([Import(src, e[0]), Import(src, e[1]), Import(src, e[2])]);
                return ((uint)(Exports.Count - 1) << 3) | KExport;
            }
            case KText:
                Texts.Add(src.Texts[i]);
                return ((uint)(Texts.Count - 1) << 3) | KText;
            default:
                throw new NotSupportedException($"Tag value kind {kind} cannot be copied.");
        }
    }

    /// <summary>Tags of a handle as (key, value text).</summary>
    public IEnumerable<(string Key, string Value)> Tags(ulong handle)
    {
        var count = TagCount(handle);
        var first = (int)(handle & 0xFFFFFFFF);
        for (var k = 0; k < count; k++)
        {
            if ((handle >> 63) != 0) { var (key, v) = NlPairs[first + k]; yield return (Names[(int)key], ValueText(v)); }
            else { var (key, v) = Pairs[first + k]; yield return (NameText(key), ValueText(v)); }
        }
    }

    private string ValueText(uint id)
    {
        var i = (int)(id >> 3);
        switch (id & 7)
        {
            case KAnsi:
            {
                var b = AnsiChars.GetBuffer();
                var o = (int)AnsiOffsets[i];
                var e = Array.IndexOf(b, (byte)0, o, (int)AnsiChars.Length - o);
                return Encoding.Latin1.GetString(b, o, (e < 0 ? (int)AnsiChars.Length : e) - o);
            }
            case KWide:
            {
                var b = WideChars.GetBuffer();
                var o = 2 * (int)WideOffsets[i];
                var e = o;
                while (e + 1 < WideChars.Length && (b[e] | b[e + 1]) != 0) e += 2;
                return Encoding.Unicode.GetString(b, o, e - o);
            }
            case KNlName: return Names[(int)NlNames[i]];
            case KName: return NameText(NameValues[i]);
            case KNlExport: { var e = NlExports[i]; return $"{Names[(int)e[0]]}'{Names[(int)e[2]]}.{Names[(int)e[1]]}'"; }
            case KExport: { var e = Exports[i]; return $"{NameText(e[0])}'{NameText(e[2])}.{NameText(e[1])}'"; }
            case KText: return "text:" + Convert.ToHexString(Texts[i]);
            default: return $"?{id}";
        }
    }

    /// <summary>Copies a record of <paramref name="src"/> with its tags, bundles, chunk ids and flags.</summary>
    public AssetRecord ImportAsset(AssetRegistry src, AssetRecord a)
    {
        var b = new AssetRecord
        {
            ObjectPath = Import(src, a.ObjectPath), PackagePath = Import(src, a.PackagePath), AssetClass = Import(src, a.AssetClass),
            PackageName = Import(src, a.PackageName), AssetName = Import(src, a.AssetName),
            TagHandle = ImportTags(src, a.TagHandle), ChunkIds = (int[])a.ChunkIds.Clone(), PackageFlags = a.PackageFlags,
        };
        foreach (var bundle in a.Bundles)
        {
            var nb = new Bundle { Name = Import(src, bundle.Name) };
            foreach (var (path, sub) in bundle.Assets) nb.Assets.Add((Import(src, path), sub));
            b.Bundles.Add(nb);
        }
        Assets.Add(b);
        return b;
    }

    public AssetId ImportId(AssetRegistry src, AssetId id) => new()
    {
        Bits = id.Bits,
        PackageName = (id.Bits & 1) != 0 ? Import(src, id.PackageName) : default,
        PrimaryAssetType = (id.Bits & 2) != 0 ? Import(src, id.PrimaryAssetType) : default,
        ObjectName = (id.Bits & 4) != 0 ? Import(src, id.ObjectName) : default,
        ValueName = (id.Bits & 8) != 0 ? Import(src, id.ValueName) : default,
    };

    /// <summary>Identifier as text (stable key across registries): "pkg|type|obj|value" by bits.</summary>
    public string IdKey(AssetId id) =>
        $"{id.Bits}|{((id.Bits & 1) != 0 ? NameText(id.PackageName) : "")}|{((id.Bits & 2) != 0 ? NameText(id.PrimaryAssetType) : "")}|" +
        $"{((id.Bits & 4) != 0 ? NameText(id.ObjectName) : "")}|{((id.Bits & 8) != 0 ? NameText(id.ValueName) : "")}";

    // ------------------------------------------------------------------ binary helpers

    private sealed class Reader(byte[] d)
    {
        public int Pos;
        public byte U8() => d[Pos++];
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(Pos)); Pos += 4; return v; }
        public int I32() => (int)U32();
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(Pos)); Pos += 8; return v; }
        public long I64() => (long)U64();
        public byte[] Bytes(int n) { if (n < 0 || Pos + n > d.Length) throw new FormatException("Unexpected end of registry."); var b = d.AsSpan(Pos, n).ToArray(); Pos += n; return b; }
        // registry FName: bit 31 of the index = a number follows
        public NameRef Name() { var i = U32(); return (i & 0x80000000) != 0 ? new NameRef(i & 0x7FFFFFFF, I32()) : new NameRef(i, 0); }
        public byte[] FString() { var len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(Pos)); return Bytes(4 + (len >= 0 ? len : -2 * len)); }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _s = new();
        private readonly byte[] _b = new byte[8];
        public void U8(byte v) => _s.WriteByte(v);
        public void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(_b, v); _s.Write(_b, 0, 4); }
        public void I32(int v) => U32((uint)v);
        public void U64(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(_b, v); _s.Write(_b, 0, 8); }
        public void I64(long v) => U64((ulong)v);
        public void Bytes(byte[] b) => _s.Write(b);
        public void Name(NameRef n) { if (n.Number == 0) U32(n.Index); else { U32(n.Index | 0x80000000); I32(n.Number); } }
        public long Length => _s.Length;
        public void PatchI64(long at, long v) { var p = _s.Position; _s.Position = at; I64(v); _s.Position = p; }
        public byte[] ToArray() => _s.ToArray();
    }
}
