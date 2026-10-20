// Cooked UE 4.27 package split into .uasset (header) + .uexp (export data), as SCUM ships them (legacy version -7,
// PKG_FilterEditorOnly, tagged properties). The header is parsed into the summary, name map, import and export tables; the
// sections after the export table (depends, soft package references, preload dependencies...) are kept as bytes, only
// the offsets pointing into them are moved. Export data is kept per export, so exports can grow or shrink.
// An unmodified package saves byte-identical (Save checks nothing, `modkit pkg verify` does).
// Layout: Engine/Source/Runtime/CoreUObject (FPackageFileSummary, FObjectImport, FObjectExport), as in
// Chest_Mod/tools/rename_names.py.
using System.Buffers.Binary;
using System.Text;

namespace ScumModKit.Package;

public readonly record struct FName(int Index, int Number);

public sealed class NameEntry
{
    public string Text = "";
    /// <summary>Stored hashes (Strihash_DEPRECATED, StrCrc32 low 16 bits); recomputed when the text changes.</summary>
    public ushort NonCaseHash, CaseHash;
}

public sealed class ObjectImport
{
    public FName ClassPackage, ClassName, ObjectName;
    public int OuterIndex;
}

public sealed class ObjectExport
{
    public int ClassIndex, SuperIndex, TemplateIndex, OuterIndex;
    public FName ObjectName;
    public uint ObjectFlags;
    public long SerialSize, SerialOffset;
    /// <summary>bForcedExport .. CreateBeforeCreate as stored (60 bytes after SerialOffset).</summary>
    public byte[] Rest = new byte[60];
    /// <summary>The export's serialized data (in .uexp).</summary>
    public byte[] Data = [];
}

public sealed class CookedPackage
{
    public const uint PackageTag = 0x9E2A83C1;
    private const uint FilterEditorOnly = 0x80000000;
    private const int ImportSize = 28, ExportSize = 104;

    private byte[] _summary = [];
    // positions inside the summary
    private int _posTotal, _posNameCount, _posNameOffset, _posGatherCount, _posGatherOffset, _posExportCount, _posExportOffset,
        _posImportCount, _posImportOffset, _posDepends, _posSoftCount, _posSoftOffset, _posSearchable, _posThumbnail,
        _posAssetRegistry, _posBulk, _posWorldTile, _posPreloadCount, _posPreloadOffset, _posGenerations;
    private byte[] _betweenNamesAndImports = [], _betweenImportsAndExports = [], _tail = [];
    /// <summary>.uexp bytes not covered by any export (normally just the trailing package tag), by position.</summary>
    private readonly List<(long At, byte[] Bytes)> _uexpGaps = [];
    private long _uexpLength;

    public readonly List<NameEntry> Names = [];
    public readonly List<ObjectImport> Imports = [];
    public readonly List<ObjectExport> Exports = [];
    public uint PackageFlags { get; private set; }

    // ------------------------------------------------------------------ names

    public string NameText(FName n) => n.Number == 0 ? Names[n.Index].Text : $"{Names[n.Index].Text}_{n.Number - 1}";

    public int FindName(string text) => Names.FindIndex(n => n.Text == text);

    /// <summary>Index of a name map entry, appended when missing (existing indices stay valid).</summary>
    public int AddName(string text)
    {
        var i = FindName(text);
        if (i >= 0) return i;
        Names.Add(NewEntry(text));
        return Names.Count - 1;
    }

    public static NameEntry NewEntry(string text) =>
        new() { Text = text, NonCaseHash = NameHashes.StrihashDeprecated(text), CaseHash = NameHashes.StrCrc32(text) };

    public void SetName(int index, string text) => Names[index] = NewEntry(text);

    /// <summary>The 8-byte "None" name that ends a list of tagged properties.</summary>
    public byte[] NoneTag()
    {
        var i = FindName("None");
        if (i < 0) throw new InvalidOperationException("The package has no None name.");
        return [.. BitConverter.GetBytes(i), 0, 0, 0, 0];
    }

    /// <summary>The export with this object name.</summary>
    public ObjectExport Export(string objectName)
    {
        var hits = Exports.Where(e => NameText(e.ObjectName) == objectName).ToList();
        return hits.Count == 1 ? hits[0] : throw new KeyNotFoundException($"{hits.Count} exports named {objectName}.");
    }

    /// <summary>Number of SoftPackageReferences in the summary (cooked SCUM packages mostly have none).</summary>
    public int SoftPackageReferenceCount => BinaryPrimitives.ReadInt32LittleEndian(_summary.AsSpan(_posSoftCount));

    // ------------------------------------------------------------------ load

    public static CookedPackage Load(string uassetPath)
    {
        var uexp = Path.ChangeExtension(uassetPath, ".uexp");
        return Load(File.ReadAllBytes(uassetPath), File.Exists(uexp) ? File.ReadAllBytes(uexp) : []);
    }

    public static CookedPackage Load(byte[] uasset, byte[] uexp)
    {
        var p = new CookedPackage();
        var r = new Reader(uasset);
        if (r.U32() != PackageTag) throw new FormatException("Not a UE package (tag).");
        var legacy = r.I32();
        if (legacy != -7) throw new NotSupportedException($"Package file version {legacy}, only -7 (UE 4.27) is supported.");
        r.I32(); // UE3 version
        r.I32(); // UE4 version: 0 in SCUM's cooked packages (unversioned header, 4.27 layout)
        r.I32(); // licensee
        var customVersions = r.I32();
        r.Pos += customVersions * 20;
        p._posTotal = r.Pos; var total = r.I32();
        r.FString(); // FolderName
        p.PackageFlags = r.U32();
        p._posNameCount = r.Pos; var nameCount = r.I32();
        p._posNameOffset = r.Pos; var nameOffset = r.I32();
        if ((p.PackageFlags & FilterEditorOnly) == 0) r.FString(); // LocalizationId
        p._posGatherCount = r.Pos; var gatherCount = r.I32();
        p._posGatherOffset = r.Pos; r.I32();
        p._posExportCount = r.Pos; var exportCount = r.I32();
        p._posExportOffset = r.Pos; var exportOffset = r.I32();
        p._posImportCount = r.Pos; var importCount = r.I32();
        p._posImportOffset = r.Pos; var importOffset = r.I32();
        p._posDepends = r.Pos; r.I32();
        p._posSoftCount = r.Pos; r.I32();
        p._posSoftOffset = r.Pos; r.I32();
        p._posSearchable = r.Pos; r.I32();
        p._posThumbnail = r.Pos; r.I32();
        r.Pos += 16; // Guid
        if ((p.PackageFlags & FilterEditorOnly) == 0) throw new NotSupportedException("Editor (uncooked) packages are not supported.");
        p._posGenerations = r.Pos;
        var generations = r.I32();
        r.Pos += generations * 8;
        for (var i = 0; i < 2; i++) { r.Pos += 10; r.FString(); } // saved-by / compatible-with engine version
        r.U32(); // compression flags
        if (r.I32() != 0) throw new NotSupportedException("Compressed chunks in the summary.");
        r.U32(); // package source
        if (r.I32() != 0) throw new NotSupportedException("AdditionalPackagesToCook in the summary.");
        p._posAssetRegistry = r.Pos; r.I32();
        p._posBulk = r.Pos; r.I64();
        p._posWorldTile = r.Pos; r.I32();
        var chunkIds = r.I32();
        r.Pos += chunkIds * 4;
        p._posPreloadCount = r.Pos; r.I32();
        p._posPreloadOffset = r.Pos; r.I32();
        if (r.Pos != nameOffset) throw new FormatException($"Summary ends at {r.Pos}, name map starts at {nameOffset}.");
        if (gatherCount != 0) throw new NotSupportedException("Gatherable text data in a cooked package.");
        p._summary = uasset[..nameOffset];

        for (var i = 0; i < nameCount; i++)
        {
            var text = r.FString();
            p.Names.Add(new NameEntry { Text = text, NonCaseHash = r.U16(), CaseHash = r.U16() });
        }
        if (r.Pos > importOffset || importOffset > exportOffset || exportOffset + (exportCount * ExportSize) > total)
            throw new FormatException("Unexpected section order (expected names, imports, exports).");
        p._betweenNamesAndImports = uasset[r.Pos..importOffset];
        r.Pos = importOffset;
        for (var i = 0; i < importCount; i++)
            p.Imports.Add(new ObjectImport { ClassPackage = r.Name(), ClassName = r.Name(), OuterIndex = r.I32(), ObjectName = r.Name() });
        p._betweenImportsAndExports = uasset[r.Pos..exportOffset];
        r.Pos = exportOffset;
        for (var i = 0; i < exportCount; i++)
        {
            var e = new ObjectExport
            {
                ClassIndex = r.I32(), SuperIndex = r.I32(), TemplateIndex = r.I32(), OuterIndex = r.I32(),
                ObjectName = r.Name(), ObjectFlags = r.U32(), SerialSize = r.I64(), SerialOffset = r.I64(), Rest = r.Bytes(60),
            };
            p.Exports.Add(e);
        }
        if (total > uasset.Length) throw new FormatException("TotalHeaderSize beyond the .uasset.");
        p._tail = uasset[r.Pos..total];
        if (total != uasset.Length) throw new NotSupportedException("Export data inside the .uasset (expected a separate .uexp).");

        // export data from the .uexp; whatever lies between exports is kept as gaps
        long at = 0;
        foreach (var e in p.Exports.OrderBy(e => e.SerialOffset))
        {
            var start = e.SerialOffset - total;
            if (start < at || start + e.SerialSize > uexp.Length) throw new FormatException("Overlapping or truncated export data.");
            if (start > at) p._uexpGaps.Add((at, uexp[(int)at..(int)start]));
            e.Data = uexp[(int)start..(int)(start + e.SerialSize)];
            at = start + e.SerialSize;
        }
        if (at < uexp.Length) p._uexpGaps.Add((at, uexp[(int)at..]));
        p._uexpLength = uexp.Length;
        return p;
    }

    // ------------------------------------------------------------------ save

    public (byte[] Uasset, byte[] Uexp) Save()
    {
        var names = new Writer();
        foreach (var n in Names)
        {
            names.FString(n.Text);
            names.U16(n.NonCaseHash);
            names.U16(n.CaseHash);
        }
        var nameOffset = _summary.Length;
        var importOffset = nameOffset + names.Length + _betweenNamesAndImports.Length;
        var exportOffset = importOffset + (Imports.Count * ImportSize) + _betweenImportsAndExports.Length;
        var tailOffset = exportOffset + (Exports.Count * ExportSize);
        var total = tailOffset + _tail.Length;

        var old = new Reader(_summary);
        int Get(int pos) { old.Pos = pos; return old.I32(); }
        var oldNameOffset = Get(_posNameOffset);
        var oldImportOffset = Get(_posImportOffset);
        var oldExportOffset = Get(_posExportOffset);
        var oldTailOffset = oldExportOffset + (Get(_posExportCount) * ExportSize);
        var oldTotal = Get(_posTotal);

        // maps an offset of the old header to the new one by the section it points into
        int Move(int o)
        {
            if (o <= 0) return o;
            if (o >= oldTailOffset) return o - oldTailOffset + tailOffset;
            if (o >= oldExportOffset) return o - oldExportOffset + exportOffset;
            if (o >= oldImportOffset) return o - oldImportOffset + importOffset;
            if (o >= oldNameOffset) return o - oldNameOffset + nameOffset;
            return o;
        }

        // .uexp: export data in the original order of their offsets, the gaps where they were
        var uexp = new MemoryStream();
        var newGaps = new List<(long At, byte[] Bytes)>();
        var gaps = new Queue<(long At, byte[] Bytes)>(_uexpGaps);
        void Gap() { var g = gaps.Dequeue(); newGaps.Add((uexp.Length, g.Bytes)); uexp.Write(g.Bytes); }
        foreach (var e in Exports.OrderBy(e => e.SerialOffset).ToList())
        {
            var oldStart = e.SerialOffset - oldTotal;
            while (gaps.Count > 0 && gaps.Peek().At < oldStart) Gap();
            e.SerialOffset = total + uexp.Length;
            e.SerialSize = e.Data.Length;
            uexp.Write(e.Data);
        }
        while (gaps.Count > 0) Gap();

        var summary = (byte[])_summary.Clone();
        void Put(int pos, int v) => BinaryPrimitives.WriteInt32LittleEndian(summary.AsSpan(pos), v);
        Put(_posTotal, total);
        Put(_posNameCount, Names.Count);
        Put(_posNameOffset, nameOffset);
        Put(_posExportCount, Exports.Count);
        Put(_posExportOffset, exportOffset);
        Put(_posImportCount, Imports.Count);
        Put(_posImportOffset, importOffset);
        foreach (var pos in new[] { _posGatherOffset, _posDepends, _posSoftOffset, _posSearchable, _posThumbnail, _posAssetRegistry, _posWorldTile, _posPreloadOffset })
            Put(pos, Move(Get(pos)));
        old.Pos = _posBulk;
        var bulk = old.I64();
        if (bulk > 0) BinaryPrimitives.WriteInt64LittleEndian(summary.AsSpan(_posBulk), bulk + (total - oldTotal) + (uexp.Length - _uexpLength));

        var w = new Writer();
        w.Bytes(summary);
        w.Bytes(names.ToArray());
        w.Bytes(_betweenNamesAndImports);
        foreach (var i in Imports) { w.Name(i.ClassPackage); w.Name(i.ClassName); w.I32(i.OuterIndex); w.Name(i.ObjectName); }
        w.Bytes(_betweenImportsAndExports);
        foreach (var e in Exports)
        {
            w.I32(e.ClassIndex); w.I32(e.SuperIndex); w.I32(e.TemplateIndex); w.I32(e.OuterIndex);
            w.Name(e.ObjectName); w.U32(e.ObjectFlags); w.I64(e.SerialSize); w.I64(e.SerialOffset); w.Bytes(e.Rest);
        }
        w.Bytes(_tail);
        if (w.Length != total) throw new InvalidOperationException("Header size mismatch.");

        _summary = summary;
        _uexpGaps.Clear();
        _uexpGaps.AddRange(newGaps);
        _uexpLength = uexp.Length;
        return (w.ToArray(), uexp.ToArray());
    }

    public void Save(string uassetPath)
    {
        var (uasset, uexp) = Save();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(uassetPath))!);
        File.WriteAllBytes(uassetPath, uasset);
        File.WriteAllBytes(Path.ChangeExtension(uassetPath, ".uexp"), uexp);
    }

    /// <summary>Package index -> object path text (imports by outer chain, exports by outer chain).</summary>
    public string ObjectPath(int index)
    {
        if (index == 0) return "";
        if (index < 0)
        {
            var i = Imports[-index - 1];
            var outer = ObjectPath(i.OuterIndex);
            return outer.Length == 0 ? NameText(i.ObjectName) : $"{outer}.{NameText(i.ObjectName)}";
        }
        var e = Exports[index - 1];
        var o = ObjectPath(e.OuterIndex);
        return o.Length == 0 ? NameText(e.ObjectName) : $"{o}.{NameText(e.ObjectName)}";
    }

    // ------------------------------------------------------------------ binary helpers

    private sealed class Reader(byte[] d)
    {
        public int Pos;
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(Pos)); Pos += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(Pos)); Pos += 4; return v; }
        public int I32() => (int)U32();
        public long I64() { var v = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(Pos)); Pos += 8; return v; }
        public byte[] Bytes(int n) { var b = d.AsSpan(Pos, n).ToArray(); Pos += n; return b; }
        public FName Name() => new(I32(), I32());

        public string FString()
        {
            var n = I32();
            if (n == 0) return "";
            if (n > 0) { var s = Encoding.Latin1.GetString(d, Pos, n - 1); Pos += n; return s; }
            var w = Encoding.Unicode.GetString(d, Pos, (-2 * n) - 2);
            Pos += -2 * n;
            return w;
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _s = new();
        private readonly byte[] _b = new byte[8];
        public void U16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(_b, v); _s.Write(_b, 0, 2); }
        public void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(_b, v); _s.Write(_b, 0, 4); }
        public void I32(int v) => U32((uint)v);
        public void I64(long v) { BinaryPrimitives.WriteInt64LittleEndian(_b, v); _s.Write(_b, 0, 8); }
        public void Bytes(byte[] b) => _s.Write(b);
        public void Name(FName n) { I32(n.Index); I32(n.Number); }
        public int Length => (int)_s.Length;
        public byte[] ToArray() => _s.ToArray();

        public void FString(string s)
        {
            if (s.Length == 0) { I32(0); return; }
            if (s.All(c => c <= 0x7F))
            {
                I32(s.Length + 1);
                Bytes(Encoding.Latin1.GetBytes(s));
                _s.WriteByte(0);
            }
            else
            {
                I32(-(s.Length + 1));
                Bytes(Encoding.Unicode.GetBytes(s));
                U16(0);
            }
        }
    }
}
