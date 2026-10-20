// Hashes stored next to every name map entry of a 4.27 package (FNameEntrySerialized): Strihash_DEPRECATED (upper-cased,
// FCrc::CRCTable_DEPRECATED) and StrCrc32 (TCHAR as 4 bytes), each truncated to 16 bits.
namespace ScumModKit.Package;

public static class NameHashes
{
    private static readonly uint[] Reflected = Table(true), Deprecated = Table(false);

    private static uint[] Table(bool reflected)
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = reflected ? i : i << 24;
            for (var k = 0; k < 8; k++)
                c = reflected ? ((c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1) : ((c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1);
            t[i] = c;
        }
        return t;
    }

    public static ushort StrihashDeprecated(string s)
    {
        var wide = s.Any(c => c > 0x7F);
        uint h = 0;
        foreach (var ch in s)
        {
            var c = char.ToUpperInvariant(ch);
            h = ((h >> 8) & 0x00FFFFFF) ^ Deprecated[(h ^ (byte)c) & 0xFF];
            if (wide) h = ((h >> 8) & 0x00FFFFFF) ^ Deprecated[(h ^ (byte)(c >> 8)) & 0xFF];
        }
        return (ushort)h;
    }

    public static ushort StrCrc32(string s)
    {
        var crc = 0xFFFFFFFF;
        foreach (var ch in s)
        {
            uint c = ch;
            for (var k = 0; k < 4; k++)
            {
                crc = (crc >> 8) ^ Reflected[(crc ^ c) & 0xFF];
                c >>= 8;
            }
        }
        return (ushort)~crc;
    }
}
