// Adds registry records for cloned packages: copies of the template packages' records with the names renamed, the way
// ScumStudio's project export does it (checked against its output for Food_Mod and Chest_Mod).
using System.Text;
using ScumModKit.Clone;
using ScumModKit.Hashing;

namespace ScumModKit.Registry;

public sealed class CloneReport
{
    public int Records { get; set; }
    public List<string> Skipped { get; } = [];
    public List<string> Warnings { get; } = [];
}

public static class RecordCloner
{
    // tag value kinds (low 3 bits of a value id), FixedTagPrivate::EValueType
    private const uint KAnsi = 0, KWide = 1, KNlName = 2, KName = 3, KNlExport = 4, KExport = 5, KText = 6;

    /// <summary>
    /// Appends the records of every group to <paramref name="reg"/>: groups ordered by new primary path (ignoring case),
    /// inside a group the packages in their given order, the records of a package in the template's order.
    /// Clones whose new package already has records are skipped, and so are packages <paramref name="ships"/> rejects
    /// (not in the mod).
    /// </summary>
    public static CloneReport AddClones(AssetRegistry reg, IEnumerable<CloneGroup> groups, Func<string, bool>? ships = null)
    {
        var report = new CloneReport();
        var byPackage = new Dictionary<string, List<AssetRecord>>(StringComparer.Ordinal);
        foreach (var a in reg.Assets)
        {
            var pkg = reg.NameText(a.PackageName);
            if (!byPackage.TryGetValue(pkg, out var list)) byPackage[pkg] = list = [];
            list.Add(a);
        }

        foreach (var group in groups.OrderBy(g => g.NewPrimary, StringComparer.OrdinalIgnoreCase))
        {
            var rename = CloneRenamer.For(group.Packages);
            foreach (var p in group.Packages)
            {
                if (byPackage.ContainsKey(p.New)) { report.Skipped.Add(p.New); continue; }
                if (ships != null && !ships(p.New))
                {
                    report.Warnings.Add($"{p.New}: not in the mod, no registry records added");
                    continue;
                }
                if (!byPackage.TryGetValue(p.Old, out var templates))
                {
                    report.Warnings.Add($"{p.Old}: template has no registry records, {p.New} gets none");
                    continue;
                }
                foreach (var t in templates)
                {
                    reg.Assets.Add(Clone(reg, t, rename, p));
                    report.Records++;
                }
            }
        }
        return report;
    }

    /// <summary>
    /// Sets the tag <paramref name="tag"/> of every record of <paramref name="package"/> that has it to a string value
    /// (the record's tag map is copied to the end of the store first, so maps of other records stay as they are).
    /// Returns the number of records changed.
    /// </summary>
    public static int SetTag(AssetRegistry reg, string package, string tag, string value)
    {
        var n = 0;
        foreach (var a in reg.Assets.Where(a => reg.NameText(a.PackageName) == package))
        {
            var count = reg.TagCount(a.TagHandle);
            var first = (int)(a.TagHandle & 0xFFFFFFFF);
            var numberless = (a.TagHandle >> 63) != 0;
            var keys = Enumerable.Range(first, count)
                .Select(i => numberless ? reg.Names[(int)reg.NlPairs[i].Key] : reg.NameText(reg.Pairs[i].Key)).ToList();
            var at = keys.IndexOf(tag);
            if (at < 0) continue;

            uint id;
            if (value.All(c => c <= 0x7F))
            {
                var off = (uint)reg.AnsiChars.Length;
                reg.AnsiChars.Write(Encoding.Latin1.GetBytes(value));
                reg.AnsiChars.WriteByte(0);
                reg.AnsiOffsets.Add(off);
                id = ((uint)(reg.AnsiOffsets.Count - 1) << 3) | KAnsi;
            }
            else
            {
                var off = (uint)(reg.WideChars.Length / 2);
                reg.WideChars.Write(Encoding.Unicode.GetBytes(value));
                reg.WideChars.WriteByte(0); reg.WideChars.WriteByte(0);
                reg.WideOffsets.Add(off);
                id = ((uint)(reg.WideOffsets.Count - 1) << 3) | KWide;
            }
            if (numberless)
            {
                var start = reg.NlPairs.Count;
                for (var k = 0; k < count; k++) reg.NlPairs.Add(k == at ? (reg.NlPairs[first + k].Key, id) : reg.NlPairs[first + k]);
                a.TagHandle = (1UL << 63) | ((ulong)count << 32) | (uint)start;
            }
            else
            {
                var start = reg.Pairs.Count;
                for (var k = 0; k < count; k++) reg.Pairs.Add(k == at ? (reg.Pairs[first + k].Key, id) : reg.Pairs[first + k]);
                a.TagHandle = ((ulong)count << 32) | (uint)start;
            }
            n++;
        }
        return n;
    }

    private static AssetRecord Clone(AssetRegistry reg, AssetRecord t, Func<string, string> rename, PackageRename p)
    {
        // tags first, then the record's names: the order ScumStudio interns new names in (same bytes as its registry)
        var tags = CloneTags(reg, t.TagHandle, rename);
        var c = new AssetRecord
        {
            ObjectPath = Name(reg, t.ObjectPath, rename),
            PackagePath = new NameRef(Intern(reg, p.New[..p.New.LastIndexOf('/')]), 0), // the clone may live in another folder
            AssetClass = Name(reg, t.AssetClass, rename),
            PackageName = Name(reg, t.PackageName, rename),
            AssetName = Name(reg, t.AssetName, rename),
            TagHandle = tags,
            ChunkIds = (int[])t.ChunkIds.Clone(),
            PackageFlags = t.PackageFlags,
        };
        foreach (var b in t.Bundles)
        {
            var nb = new Bundle { Name = Name(reg, b.Name, rename) };
            foreach (var (path, sub) in b.Assets) nb.Assets.Add((Name(reg, path, rename), sub));
            c.Bundles.Add(nb);
        }
        return c;
    }

    /// <summary>Renamed name (the FName number is kept), interned with its CityHash64 when new.</summary>
    private static NameRef Name(AssetRegistry reg, NameRef n, Func<string, string> rename) =>
        new(Intern(reg, rename(reg.Names[(int)n.Index])), n.Number);

    private static uint Intern(AssetRegistry reg, string text)
    {
        var wide = text.Any(ch => ch > 0x7F);
        return reg.InternName(text, wide, CityHash.NameHash(text, wide));
    }

    private static ulong CloneTags(AssetRegistry reg, ulong handle, Func<string, string> rename)
    {
        var count = reg.TagCount(handle);
        var first = (int)(handle & 0xFFFFFFFF);
        var numberless = (handle >> 63) != 0;
        if (count == 0) return handle & (1UL << 63);
        if (numberless)
        {
            var start = reg.NlPairs.Count;
            for (var k = 0; k < count; k++)
            {
                var (key, value) = reg.NlPairs[first + k];
                reg.NlPairs.Add((key, CloneValue(reg, value, rename)));
            }
            return (1UL << 63) | ((ulong)count << 32) | (uint)start;
        }
        else
        {
            var start = reg.Pairs.Count;
            for (var k = 0; k < count; k++)
            {
                var (key, value) = reg.Pairs[first + k];
                reg.Pairs.Add((key, CloneValue(reg, value, rename)));
            }
            return ((ulong)count << 32) | (uint)start;
        }
    }

    private static uint CloneValue(AssetRegistry reg, uint id, Func<string, string> rename)
    {
        var i = (int)(id >> 3);
        switch (id & 7)
        {
            case KAnsi:
            {
                var buf = reg.AnsiChars.GetBuffer();
                var o = (int)reg.AnsiOffsets[i];
                var e = Array.IndexOf(buf, (byte)0, o, (int)reg.AnsiChars.Length - o);
                var text = rename(Encoding.Latin1.GetString(buf, o, (e < 0 ? (int)reg.AnsiChars.Length : e) - o));
                if (text.Any(ch => ch > 0xFF)) throw new NotSupportedException($"Renamed ANSI tag value is not ANSI: {text}");
                var off = (uint)reg.AnsiChars.Length;
                reg.AnsiChars.Write(Encoding.Latin1.GetBytes(text));
                reg.AnsiChars.WriteByte(0);
                reg.AnsiOffsets.Add(off);
                return ((uint)(reg.AnsiOffsets.Count - 1) << 3) | KAnsi;
            }
            case KWide:
            {
                var buf = reg.WideChars.GetBuffer();
                var o = 2 * (int)reg.WideOffsets[i];
                var e = o;
                while (e + 1 < reg.WideChars.Length && (buf[e] | buf[e + 1]) != 0) e += 2;
                var text = rename(Encoding.Unicode.GetString(buf, o, e - o));
                var off = (uint)(reg.WideChars.Length / 2);
                reg.WideChars.Write(Encoding.Unicode.GetBytes(text));
                reg.WideChars.WriteByte(0); reg.WideChars.WriteByte(0);
                reg.WideOffsets.Add(off);
                return ((uint)(reg.WideOffsets.Count - 1) << 3) | KWide;
            }
            case KNlName:
                reg.NlNames.Add(Intern(reg, rename(reg.Names[(int)reg.NlNames[i]])));
                return ((uint)(reg.NlNames.Count - 1) << 3) | KNlName;
            case KName:
                reg.NameValues.Add(Name(reg, reg.NameValues[i], rename));
                return ((uint)(reg.NameValues.Count - 1) << 3) | KName;
            case KNlExport:
            {
                var x = reg.NlExports[i];
                reg.NlExports.Add([Intern(reg, rename(reg.Names[(int)x[0]])), Intern(reg, rename(reg.Names[(int)x[1]])), Intern(reg, rename(reg.Names[(int)x[2]]))]);
                return ((uint)(reg.NlExports.Count - 1) << 3) | KNlExport;
            }
            case KExport:
            {
                var x = reg.Exports[i];
                reg.Exports.Add([Name(reg, x[0], rename), Name(reg, x[1], rename), Name(reg, x[2], rename)]);
                return ((uint)(reg.Exports.Count - 1) << 3) | KExport;
            }
            case KText:
                reg.Texts.Add(reg.Texts[i]);
                return ((uint)(reg.Texts.Count - 1) << 3) | KText;
            default:
                throw new NotSupportedException($"Tag value kind {id & 7} cannot be cloned.");
        }
    }
}
