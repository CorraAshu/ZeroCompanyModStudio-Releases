using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Readers;

namespace UEToolkit.Core;

/// <summary>
/// The game's `AssetRegistry.bin`, as a thing we can inspect and (eventually) write.
///
/// Why it matters: this file is how the game DISCOVERS customization parts. The menu is built by
/// asking the asset registry for everything of type CustomizationPartDefinition, so a modded part
/// that is not in the registry does not exist as far as the game is concerned -- however
/// correctly it was cooked and packaged.
///
/// Today the merge is done by a commandlet running inside the Unreal editor. That is one of the
/// two reasons a mod author needs a 100 GB engine install, and it is the reason this class exists:
/// the registry is a self-contained binary format, and reading it already needs no engine at all.
/// </summary>
public static class RegistryFile
{
    /// <summary>What a registry contains, for checking an understanding of the format against a
    /// real file before trying to write one.</summary>
    public sealed record Summary(
        string Path,
        long Bytes,
        int Assets,
        int DependsNodes,
        int PackageDatas,
        int DistinctClasses,
        int DistinctTagKeys,
        int TaggedAssets);

    /// <summary>
    /// Size of FAssetRegistryHeader: a 16-byte version guid, an int32 version, and
    /// bFilterEditorOnlyData -- which UE serialises as an INT32, not a single byte. Getting that
    /// wrong puts every following field three bytes out, and the name batch then reads as a
    /// plausible-looking 1.4 million names.
    /// </summary>
    private const int HeaderBytes = 24;

    /// <summary>
    /// The name batch: every string the registry uses, each with the hash UE computed for it.
    ///
    /// Read raw rather than through CUE4Parse, which discards the hashes. Writing a registry needs
    /// them, and having UE's own alongside ours is what makes <see cref="NameHash"/> checkable
    /// rather than merely plausible.
    /// </summary>
    public sealed record NameBatch(
        ulong HashVersion, IReadOnlyList<string> Names, IReadOnlyList<ulong> Hashes,
        int StringBytes);

    public static NameBatch ReadNameBatch(string path)
    {
        var raw = File.ReadAllBytes(path);
        var p = HeaderBytes;

        var num = BitConverter.ToInt32(raw, p); p += 4;
        var stringBytes = BitConverter.ToInt32(raw, p); p += 4;
        var hashVersion = BitConverter.ToUInt64(raw, p); p += 8;

        var hashes = new ulong[num];
        for (var i = 0; i < num; i++, p += 8) hashes[i] = BitConverter.ToUInt64(raw, p);

        var headers = p;
        p += num * 2;

        var names = new string[num];
        for (var i = 0; i < num; i++)
        {
            // Two bytes, packed big-endian-ish: the top bit of the first says UTF-16, and the
            // length is the remaining 7 bits of it followed by all 8 of the second. Reading the
            // pair as a little-endian uint16 gives lengths that run the names together.
            var b0 = raw[headers + i * 2];
            var b1 = raw[headers + i * 2 + 1];
            var length = ((b0 & 0x7F) << 8) | b1;
            if ((b0 & 0x80) != 0)
            {
                names[i] = System.Text.Encoding.Unicode.GetString(raw, p, length * 2);
                p += length * 2;
            }
            else
            {
                names[i] = System.Text.Encoding.UTF8.GetString(raw, p, length);
                p += length;
            }
        }

        return new NameBatch(hashVersion, names, hashes, stringBytes);
    }

    public static FAssetRegistryState Read(byte[] bytes, string name = "AssetRegistry.bin") =>
        new(new FByteArchive(name, bytes));

    public static FAssetRegistryState Read(string path) =>
        Read(File.ReadAllBytes(path), Path.GetFileName(path));

    public static Summary Describe(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var state = Read(bytes, Path.GetFileName(path));

        var classes = new HashSet<string>(StringComparer.Ordinal);
        var tagKeys = new HashSet<string>(StringComparer.Ordinal);
        var tagged = 0;

        foreach (var a in state.PreallocatedAssetDataBuffers)
        {
            classes.Add(a.AssetClass.Text);
            if (a.TagsAndValues is not { Count: > 0 }) continue;
            tagged++;
            foreach (var kv in a.TagsAndValues) tagKeys.Add(kv.Key.Text);
        }

        return new Summary(path, bytes.LongLength,
            state.PreallocatedAssetDataBuffers.Length,
            state.PreallocatedDependsNodeDataBuffers.Length,
            state.PreallocatedPackageDataBuffers.Length,
            classes.Count, tagKeys.Count, tagged);
    }
}
