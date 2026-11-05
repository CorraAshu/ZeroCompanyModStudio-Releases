using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace UEToolkit.Core;

/// <summary>
/// The hash UE stores beside every name in an asset registry's name batch.
///
/// This is the one piece of writing a registry that could not be read off the format. CUE4Parse
/// SKIPS the hashes -- it only needs the strings -- so nothing in the reader says how they are
/// produced, and a merge has to produce them: the names a mod introduces have never been hashed
/// by anyone.
///
/// It is CityHash64 over the ASCII-LOWERCASED bytes of the name, which the batch header declares
/// as algorithm id 0xC1640000. That was established by trying the four plausible combinations
/// (lowercased or not, ANSI or UTF-16) against the 223,870 names in the shipped registry, which
/// is its own answer key: every name in it already carries the hash UE computed. The winning
/// combination matches all 223,870. `--namehash` re-runs that check.
///
/// Lowercasing is ASCII-ONLY, matching FChar::ToLower. Do not substitute a culture-aware or
/// Unicode-aware lowercase: those differ on characters UE leaves alone.
/// </summary>
public static class NameHash
{
    /// <summary>The algorithm id an asset registry name batch declares for this hash.</summary>
    public const ulong AlgorithmId = 0xC1640000UL;

    private const ulong K0 = 0xc3a5c85c97cb3127UL;
    private const ulong K1 = 0xb492b66fbe98f273UL;
    private const ulong K2 = 0x9ae16a3b2f90404fUL;
    private const ulong KMul = 0x9ddfea08eb382d69UL;

    /// <summary>Hash a name the way an asset registry does.</summary>
    public static ulong Of(string name)
    {
        // UTF-8 rather than ASCII: a handful of names are not ASCII, and the registry stores their
        // UTF-8 bytes. The LOWERCASING stays ASCII-only regardless, which is what UE does.
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] is >= (byte) 'A' and <= (byte) 'Z')
                bytes[i] += 32;
        return CityHash64(bytes);
    }

    // ------------------------------------------------------------------------ CityHash64
    //
    // Google's CityHash64, transcribed. C# ulong arithmetic wraps, which is what the algorithm
    // assumes -- the Python prototype needed an explicit mask at every product, and forgetting one
    // in the short-string branch made every name of three bytes or fewer hash wrong while the
    // low 16 bits still matched, which looked like a near miss rather than a bug.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Rot(ulong v, int s) => s == 0 ? v : (v >> s) | (v << (64 - s));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ShiftMix(ulong v) => v ^ (v >> 47);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong F64(ReadOnlySpan<byte> s, int i) =>
        BinaryPrimitives.ReadUInt64LittleEndian(s[i..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint F32(ReadOnlySpan<byte> s, int i) =>
        BinaryPrimitives.ReadUInt32LittleEndian(s[i..]);

    private static ulong H16(ulong u, ulong v, ulong mul)
    {
        var a = (u ^ v) * mul;
        a ^= a >> 47;
        var b = (v ^ a) * mul;
        b ^= b >> 47;
        return b * mul;
    }

    private static ulong H16(ulong u, ulong v) => H16(u, v, KMul);

    private static ulong Len0To16(ReadOnlySpan<byte> s)
    {
        var n = s.Length;
        if (n >= 8)
        {
            var mul = K2 + (ulong) n * 2;
            var a = F64(s, 0) + K2;
            var b = F64(s, n - 8);
            var c = Rot(b, 37) * mul + a;
            var d = (Rot(a, 25) + b) * mul;
            return H16(c, d, mul);
        }
        if (n >= 4)
        {
            var mul = K2 + (ulong) n * 2;
            ulong a = F32(s, 0);
            return H16((ulong) n + (a << 3), F32(s, n - 4), mul);
        }
        if (n > 0)
        {
            uint a = s[0], b = s[n >> 1], c = s[n - 1];
            var y = a + (b << 8);
            var z = (uint) n + (c << 2);
            return ShiftMix(y * K2 ^ z * K0) * K2;
        }
        return K2;
    }

    private static ulong Len17To32(ReadOnlySpan<byte> s)
    {
        var n = s.Length;
        var mul = K2 + (ulong) n * 2;
        var a = F64(s, 0) * K1;
        var b = F64(s, 8);
        var c = F64(s, n - 8) * mul;
        var d = F64(s, n - 16) * K2;
        return H16(Rot(a + b, 43) + Rot(c, 30) + d, a + Rot(b + K2, 18) + c, mul);
    }

    private static (ulong, ulong) Weak32(ReadOnlySpan<byte> s, int i, ulong a, ulong b)
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

    private static ulong Len33To64(ReadOnlySpan<byte> s)
    {
        var n = s.Length;
        var mul = K2 + (ulong) n * 2;
        var a = F64(s, 0) * K2;
        var b = F64(s, 8);
        var c = F64(s, n - 24);
        var d = F64(s, n - 32);
        var e = F64(s, 16) * K2;
        var f = F64(s, 24) * 9;
        var g = F64(s, n - 8);
        var h = F64(s, n - 16) * mul;
        var u = Rot(a + g, 43) + (Rot(b, 30) + c) * 9;
        var v = ((a + g) ^ d) + f + 1;
        var w = BinaryPrimitives.ReverseEndianness((u + v) * mul) + h;
        var x = Rot(e + f, 42) + c;
        var y = (BinaryPrimitives.ReverseEndianness((v + w) * mul) + g) * mul;
        var z = e + f + c;
        a = BinaryPrimitives.ReverseEndianness((x + z) * mul + y) + b;
        b = ShiftMix((z + a) * mul + d + h) * mul;
        return b + x;
    }

    public static ulong CityHash64(ReadOnlySpan<byte> s)
    {
        var n = s.Length;
        if (n <= 32) return n <= 16 ? Len0To16(s) : Len17To32(s);
        if (n <= 64) return Len33To64(s);

        var x = F64(s, n - 40);
        var y = F64(s, n - 16) + F64(s, n - 56);
        var z = H16(F64(s, n - 48) + (ulong) n, F64(s, n - 24));
        var v = Weak32(s, n - 64, (ulong) n, z);
        var w = Weak32(s, n - 32, y + K1, x);
        x = x * K1 + F64(s, 0);

        var rem = (n - 1) & ~63;
        var i = 0;
        do
        {
            x = Rot(x + y + v.Item1 + F64(s, i + 8), 37) * K1;
            y = Rot(y + v.Item2 + F64(s, i + 48), 42) * K1;
            x ^= w.Item2;
            y += v.Item1 + F64(s, i + 40);
            z = Rot(z + w.Item1, 33) * K1;
            v = Weak32(s, i, v.Item2 * K1, x + w.Item1);
            w = Weak32(s, i + 32, z + w.Item2, y + F64(s, i + 16));
            (z, x) = (x, z);
            i += 64;
            rem -= 64;
        } while (rem != 0);

        return H16(H16(v.Item1, w.Item1) + ShiftMix(y) * K1 + z, H16(v.Item2, w.Item2) + x);
    }
}
