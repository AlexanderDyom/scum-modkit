// CityHash64 (v1.1, as in Engine/Source/Runtime/Core/Private/Hash/CityHash.cpp) and the name hash of the cooked
// AssetRegistry name batch (UnrealNames.cpp: CityHash64 over the lower-cased characters, 1 byte per ANSI char or
// 2 bytes per UTF-16 char).
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ScumModKit.Hashing;

public static class CityHash
{
    private const ulong K0 = 0xc3a5c85c97cb3127UL, K1 = 0xb492b66fbe98f273UL, K2 = 0x9ae16a3b2f90404fUL;

    /// <summary>Hash of a name batch entry: ANSI names hash their Latin-1 bytes, wide names their UTF-16 bytes, both lower-cased.</summary>
    public static ulong NameHash(string name, bool wide)
    {
        if (wide) return Hash64(Encoding.Unicode.GetBytes(name.ToLowerInvariant()));
        var b = Encoding.Latin1.GetBytes(name);
        for (var i = 0; i < b.Length; i++) if (b[i] is >= (byte)'A' and <= (byte)'Z') b[i] += 32;
        return Hash64(b);
    }

    public static ulong Hash64(ReadOnlySpan<byte> s)
    {
        var len = s.Length;
        if (len <= 32) return len <= 16 ? HashLen0To16(s) : HashLen17To32(s);
        if (len <= 64) return HashLen33To64(s);

        var x = F64(s, len - 40);
        var y = F64(s, len - 16) + F64(s, len - 56);
        var z = HashLen16(F64(s, len - 48) + (ulong)len, F64(s, len - 24));
        var v = WeakHashLen32WithSeeds(s, len - 64, (ulong)len, z);
        var w = WeakHashLen32WithSeeds(s, len - 32, y + K1, x);
        x = (x * K1) + F64(s, 0);

        var left = (len - 1) & ~63;
        var p = 0;
        do
        {
            x = Rot(x + y + v.A + F64(s, p + 8), 37) * K1;
            y = Rot(y + v.B + F64(s, p + 48), 42) * K1;
            x ^= w.B;
            y += v.A + F64(s, p + 40);
            z = Rot(z + w.A, 33) * K1;
            v = WeakHashLen32WithSeeds(s, p, v.B * K1, x + w.A);
            w = WeakHashLen32WithSeeds(s, p + 32, z + w.B, y + F64(s, p + 16));
            (z, x) = (x, z);
            p += 64;
            left -= 64;
        } while (left != 0);
        return HashLen16(HashLen16(v.A, w.A) + (ShiftMix(y) * K1) + z, HashLen16(v.B, w.B) + x);
    }

    private static ulong F64(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt64LittleEndian(s[i..]);
    private static uint F32(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt32LittleEndian(s[i..]);
    private static ulong Rot(ulong v, int shift) => BitOperations.RotateRight(v, shift);
    private static ulong ShiftMix(ulong v) => v ^ (v >> 47);

    private static ulong HashLen16(ulong u, ulong v) => HashLen16(u, v, 0x9ddfea08eb382d69UL);

    private static ulong HashLen16(ulong u, ulong v, ulong mul)
    {
        var a = (u ^ v) * mul;
        a ^= a >> 47;
        var b = (v ^ a) * mul;
        b ^= b >> 47;
        return b * mul;
    }

    private static ulong HashLen0To16(ReadOnlySpan<byte> s)
    {
        var len = s.Length;
        if (len >= 8)
        {
            var mul = K2 + ((ulong)len * 2);
            var a = F64(s, 0) + K2;
            var b = F64(s, len - 8);
            var c = (Rot(b, 37) * mul) + a;
            var d = (Rot(a, 25) + b) * mul;
            return HashLen16(c, d, mul);
        }
        if (len >= 4)
        {
            var mul = K2 + ((ulong)len * 2);
            ulong a = F32(s, 0);
            return HashLen16((ulong)len + (a << 3), F32(s, len - 4), mul);
        }
        if (len > 0)
        {
            uint a = s[0], b = s[len >> 1], c = s[len - 1];
            var y = a + (b << 8);
            var z = (uint)len + (c << 2);
            return ShiftMix(((ulong)y * K2) ^ ((ulong)z * K0)) * K2;
        }
        return K2;
    }

    private static ulong HashLen17To32(ReadOnlySpan<byte> s)
    {
        var len = s.Length;
        var mul = K2 + ((ulong)len * 2);
        var a = F64(s, 0) * K1;
        var b = F64(s, 8);
        var c = F64(s, len - 8) * mul;
        var d = F64(s, len - 16) * K2;
        return HashLen16(Rot(a + b, 43) + Rot(c, 30) + d, a + Rot(b + K2, 18) + c, mul);
    }

    private static ulong HashLen33To64(ReadOnlySpan<byte> s)
    {
        var len = s.Length;
        var mul = K2 + ((ulong)len * 2);
        var a = F64(s, 0) * K2;
        var b = F64(s, 8);
        var c = F64(s, len - 24);
        var d = F64(s, len - 32);
        var e = F64(s, 16) * K2;
        var f = F64(s, 24) * 9;
        var g = F64(s, len - 8);
        var h = F64(s, len - 16) * mul;
        var u = Rot(a + g, 43) + ((Rot(b, 30) + c) * 9);
        var v = ((a + g) ^ d) + f + 1;
        var w = BinaryPrimitives.ReverseEndianness((u + v) * mul) + h;
        var x = Rot(e + f, 42) + c;
        var y = (BinaryPrimitives.ReverseEndianness((v + w) * mul) + g) * mul;
        var z = e + f + c;
        a = BinaryPrimitives.ReverseEndianness(((x + z) * mul) + y) + b;
        b = ShiftMix(((z + a) * mul) + d + h) * mul;
        return b + x;
    }

    private static (ulong A, ulong B) WeakHashLen32WithSeeds(ReadOnlySpan<byte> s, int i, ulong a, ulong b)
    {
        ulong w = F64(s, i), x = F64(s, i + 8), y = F64(s, i + 16), z = F64(s, i + 24);
        a += w;
        b = Rot(b + a + z, 21);
        var c = a;
        a += x;
        a += y;
        b += Rot(a, 44);
        return (a + z, b + c);
    }
}
