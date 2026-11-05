using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace UEToolkit.Core;

/// <summary>
/// BLAKE3, enough of it to produce UE's FIoHash.
///
/// Every chunk in a .utoc carries a 20-byte hash in its entry meta, so writing a container means
/// computing it. It is not SHA-1, SHA-256, MD5, BLAKE2b or BLAKE2s -- all were checked against a
/// real entry and none matched. It is BLAKE3 truncated to 160 bits, over the UNCOMPRESSED chunk
/// data, which was confirmed by a reference implementation before a line of this was written.
///
/// Only what is needed: unkeyed, no key derivation, and no extended output past the first 32
/// bytes. `--iohash` checks it against the hashes the game already ships.
/// </summary>
public static class Blake3
{
    private const int ChunkLen = 1024;
    private const int BlockLen = 64;

    private const uint ChunkStart = 1 << 0;
    private const uint ChunkEnd = 1 << 1;
    private const uint Parent = 1 << 2;
    private const uint Root = 1 << 3;

    private static readonly uint[] Iv =
    {
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
        0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    };

    private static readonly int[] Permutation =
        { 2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8 };

    /// <summary>UE's FIoHash: the first 20 bytes of a BLAKE3 digest.</summary>
    public static byte[] Hash160(ReadOnlySpan<byte> data)
    {
        var full = Hash256(data);
        return full[..20];
    }

    public static byte[] Hash256(ReadOnlySpan<byte> data)
    {
        var chunkCount = Math.Max(1, (data.Length + ChunkLen - 1) / ChunkLen);

        if (chunkCount == 1)
        {
            // A single chunk is its own root: the last compression of the only chunk carries ROOT
            // rather than being merged into a parent.
            var only = ChunkChainingValue(data, 0, root: true);
            return WordsToBytes(only);
        }

        // The tree is left-complete, which a plain pairwise merge does not reproduce -- that only
        // agrees when the chunk count is a power of two. Every chunk EXCEPT THE LAST is pushed
        // onto a stack, merging once for each trailing zero bit of (i + 1).
        //
        // The last chunk is deliberately held back. It is the starting output, and the stack is
        // folded into it so the FINAL merge is the one that carries ROOT. Pushing it like the
        // others lets the stack collapse to a single node with no merge left to mark as root, and
        // then every multi-chunk hash is wrong while the single-chunk spec vectors still pass.
        var stack = new List<uint[]>();
        for (var i = 0; i < chunkCount - 1; i++)
        {
            var cv = ChunkChainingValue(data.Slice(i * ChunkLen, ChunkLen), (ulong) i, root: false);
            var total = (ulong) (i + 1);
            while ((total & 1) == 0)
            {
                cv = ParentChainingValue(stack[^1], cv, root: false);
                stack.RemoveAt(stack.Count - 1);
                total >>= 1;
            }
            stack.Add(cv);
        }

        var lastStart = (chunkCount - 1) * ChunkLen;
        var output = ChunkChainingValue(data[lastStart..], (ulong) (chunkCount - 1), root: false);
        while (stack.Count > 0)
        {
            var left = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            output = ParentChainingValue(left, output, root: stack.Count == 0);
        }

        return WordsToBytes(output);
    }

    private static uint[] ChunkChainingValue(ReadOnlySpan<byte> chunk, ulong counter, bool root)
    {
        var cv = (uint[]) Iv.Clone();
        var blockCount = Math.Max(1, (chunk.Length + BlockLen - 1) / BlockLen);

        for (var i = 0; i < blockCount; i++)
        {
            var start = i * BlockLen;
            var len = Math.Min(BlockLen, Math.Max(0, chunk.Length - start));
            Span<byte> block = stackalloc byte[BlockLen];
            block.Clear();
            if (len > 0) chunk.Slice(start, len).CopyTo(block);

            var flags = 0u;
            if (i == 0) flags |= ChunkStart;
            if (i == blockCount - 1)
            {
                flags |= ChunkEnd;
                if (root) flags |= Root;
            }

            cv = Compress(cv, block, counter, (uint) len, flags)[..8];
        }
        return cv;
    }

    private static uint[] ParentChainingValue(uint[] left, uint[] right, bool root)
    {
        Span<byte> block = stackalloc byte[BlockLen];
        for (var i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block[(i * 4)..], left[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(block[(32 + i * 4)..], right[i]);
        }
        return Compress((uint[]) Iv.Clone(), block, 0, BlockLen, Parent | (root ? Root : 0))[..8];
    }

    private static uint[] Compress(uint[] cv, ReadOnlySpan<byte> block, ulong counter,
                                   uint blockLen, uint flags)
    {
        Span<uint> m = stackalloc uint[16];
        for (var i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);

        Span<uint> s = stackalloc uint[16];
        for (var i = 0; i < 8; i++) s[i] = cv[i];
        for (var i = 0; i < 4; i++) s[8 + i] = Iv[i];
        s[12] = (uint) counter;
        s[13] = (uint) (counter >> 32);
        s[14] = blockLen;
        s[15] = flags;

        Span<uint> permuted = stackalloc uint[16];
        for (var round = 0; round < 7; round++)
        {
            Round(s, m);
            if (round == 6) break;
            for (var i = 0; i < 16; i++) permuted[i] = m[Permutation[i]];
            permuted.CopyTo(m);
        }

        var result = new uint[16];
        for (var i = 0; i < 8; i++)
        {
            result[i] = s[i] ^ s[i + 8];
            result[i + 8] = s[i + 8] ^ cv[i];
        }
        return result;
    }

    private static void Round(Span<uint> s, ReadOnlySpan<uint> m)
    {
        G(s, 0, 4, 8, 12, m[0], m[1]);
        G(s, 1, 5, 9, 13, m[2], m[3]);
        G(s, 2, 6, 10, 14, m[4], m[5]);
        G(s, 3, 7, 11, 15, m[6], m[7]);
        G(s, 0, 5, 10, 15, m[8], m[9]);
        G(s, 1, 6, 11, 12, m[10], m[11]);
        G(s, 2, 7, 8, 13, m[12], m[13]);
        G(s, 3, 4, 9, 14, m[14], m[15]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G(Span<uint> s, int a, int b, int c, int d, uint mx, uint my)
    {
        s[a] = s[a] + s[b] + mx;
        s[d] = uint.RotateRight(s[d] ^ s[a], 16);
        s[c] = s[c] + s[d];
        s[b] = uint.RotateRight(s[b] ^ s[c], 12);
        s[a] = s[a] + s[b] + my;
        s[d] = uint.RotateRight(s[d] ^ s[a], 8);
        s[c] = s[c] + s[d];
        s[b] = uint.RotateRight(s[b] ^ s[c], 7);
    }

    private static byte[] WordsToBytes(uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }
}
