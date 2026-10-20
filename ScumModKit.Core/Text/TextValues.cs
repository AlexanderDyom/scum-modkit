// FText values of TextProperty (FTextHistory serialization, UE 4.27) and the string hashes localization uses.
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using ScumModKit.Hashing;

namespace ScumModKit.Text;

public static class TextValues
{
    /// <summary>History None with a culture-invariant string: shown as it is in every language.</summary>
    public static byte[] Invariant(string s)
    {
        var w = new MemoryStream();
        w.Write(BitConverter.GetBytes(0)); // flags
        w.WriteByte(0xFF);                 // history: None
        w.Write(BitConverter.GetBytes(1)); // bHasCultureInvariantString
        w.Write(FString(s, latin1: true));
        return w.ToArray();
    }

    /// <summary>History Base: namespace + key + English source; translated through .locres files.</summary>
    public static byte[] Base(string ns, string key, string source)
    {
        var w = new MemoryStream();
        w.Write(BitConverter.GetBytes(0));
        w.WriteByte(0);
        w.Write(FString(ns));
        w.Write(FString(key));
        w.Write(FString(source));
        return w.ToArray();
    }

    /// <summary>Readable value: the source string (Base / None history) or the string table key; null otherwise.</summary>
    public static string? Source(byte[] v)
    {
        var history = (sbyte)v[4];
        if (history == 0) { var o = 5; ReadFString(v, ref o); ReadFString(v, ref o); return ReadFString(v, ref o); }
        if (history == -1 && BinaryPrimitives.ReadInt32LittleEndian(v.AsSpan(5)) != 0) { var o = 9; return ReadFString(v, ref o); }
        if (history == 11) { var o = 13; return ReadFString(v, ref o); }
        return null;
    }

    /// <summary>
    /// FString as UE writes it: ANSI (here: ASCII, or Latin-1 when <paramref name="latin1"/>) with a 0 terminator, else
    /// UTF-16 with a negative length; "" is length 0.
    /// </summary>
    public static byte[] FString(string s, bool latin1 = false)
    {
        if (s.Length == 0) return BitConverter.GetBytes(0);
        if (s.All(c => c <= (latin1 ? 0xFF : 0x7F)))
            return [.. BitConverter.GetBytes(s.Length + 1), .. Encoding.Latin1.GetBytes(s), 0];
        return [.. BitConverter.GetBytes(-(s.Length + 1)), .. Encoding.Unicode.GetBytes(s), 0, 0];
    }

    public static string ReadFString(byte[] d, ref int o)
    {
        var n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
        o += 4;
        if (n == 0) return "";
        if (n > 0) { var s = Encoding.Latin1.GetString(d, o, n - 1); o += n; return s; }
        var w = Encoding.Unicode.GetString(d, o, (-2 * n) - 2);
        o += -2 * n;
        return w;
    }

    /// <summary>FCrc::StrCrc32: CRC-32 over every UTF-16 code unit widened to 4 bytes (locres source hash).</summary>
    public static uint StrCrc32(string s)
    {
        var b = new byte[4 * s.Length];
        for (var i = 0; i < s.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), s[i]);
        return Crc32.HashToUInt32(b);
    }

    /// <summary>TextKeyUtil::HashString: CityHash64 of the UTF-16 string folded to 32 bits (GetTypeHash(uint64)).</summary>
    public static uint TextKeyHash(string s)
    {
        if (s.Length == 0) return 0;
        var h = CityHash.Hash64(Encoding.Unicode.GetBytes(s));
        return (uint)((h & 0xFFFFFFFF) + ((h >> 32) * 23));
    }
}
