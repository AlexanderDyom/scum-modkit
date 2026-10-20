// .locres version 3 (Optimized_CityHash64_UTF16, the format SCUM ships): writer, as Chest_Mod/tools/locres.py write_v3.
using System.Text;

namespace ScumModKit.Text;

public sealed record LocresEntry(string Key, string Source, string Translation);

public static class Locres
{
    private static readonly byte[] Magic = Convert.FromHexString("0e147475674a03fc4a15909dc3377f1b");

    /// <summary>
    /// One namespace; each entry is keyed by the hash of its source string and points at its own string
    /// (ref count 1), in the given order.
    /// </summary>
    public static byte[] WriteV3(string ns, IReadOnlyList<LocresEntry> entries)
    {
        var body = new MemoryStream();
        void U32(uint v) => body.Write(BitConverter.GetBytes(v));
        U32((uint)entries.Count);
        U32(1);
        U32(TextValues.TextKeyHash(ns));
        body.Write(KeyString(ns));
        U32((uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            U32(TextValues.TextKeyHash(entries[i].Key));
            body.Write(KeyString(entries[i].Key));
            U32(TextValues.StrCrc32(entries[i].Source));
            body.Write(BitConverter.GetBytes(i));
        }
        var strings = new MemoryStream();
        strings.Write(BitConverter.GetBytes(entries.Count));
        foreach (var e in entries)
        {
            strings.Write(TextValues.FString(e.Translation));
            strings.Write(BitConverter.GetBytes(1));
        }
        var b = body.ToArray();
        return [.. Magic, 3, .. BitConverter.GetBytes((long)(25 + b.Length)), .. b, .. strings.ToArray()];
    }

    // FTextKey writes "" with length 1
    private static byte[] KeyString(string s) => s.Length == 0 ? [1, 0, 0, 0, 0] : TextValues.FString(s);
}
