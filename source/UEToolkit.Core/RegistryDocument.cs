using System.Text;
using CUE4Parse.UE4.Versions;

namespace UEToolkit.Core;

/// <summary>
/// A whole AssetRegistry.bin, split into the pieces a merge needs to touch and the pieces it does
/// not.
///
/// The shape of the file:
///     header      24 bytes, copied verbatim
///     name batch  every string, each with its hash. DECODED, because a merge adds names.
///     store       the shared tag value table. Mostly raw spans; see <see cref="RegistryStore"/>.
///     assets      one record per asset. RAW -- see below.
///     tail        16 bytes: an empty dependency section and an empty package-data section.
///
/// The asset records are kept as opaque bytes on purpose, and it is the reason this approach is
/// safe. They reference names and store entries BY INDEX, and a merge only ever appends, so every
/// existing index stays correct and the bytes stay correct with them. Nothing here has to
/// understand a field it never writes -- which matters, because the record format varies with
/// registry version and a misread field would corrupt 120,757 assets to add five.
///
/// Finding where those records end needs no parsing either: the two trailing sections are empty
/// in a cooked registry, so the array ends exactly 16 bytes before EOF. That is asserted rather
/// than assumed.
/// </summary>
public sealed class RegistryDocument
{
    public const int HeaderBytes = 24;

    public byte[] Header = [];
    public FAssetRegistryVersionType Version;
    public ulong HashVersion;
    public List<string> Names = new();
    public List<bool> NameIsUtf16 = new();
    public RegistryStore Store = new();
    public int AssetCount;
    public byte[] AssetBytes = [];
    public byte[] Tail = [];

    /// <summary>
    /// An EMPTY registry, shaped like the game's own.
    ///
    /// This is what a mod should ship: its own assets and nothing else, so that the engine can
    /// append it beside every other mod's instead of one copy of the game's 26 MB registry
    /// overwriting another. Built from a model rather than from constants, so the version, the
    /// alignment rule and the tail all come from the build in front of us instead of from an
    /// assumption about which build it is.
    ///
    /// Verifiable: this game ships a cooked empty registry of its own inside a Game Feature
    /// plugin, and what this produces should be that file byte for byte.
    /// </summary>
    public static RegistryDocument EmptyLike(RegistryDocument model) => new()
    {
        Header = (byte[]) model.Header.Clone(),
        Version = model.Version,
        HashVersion = model.HashVersion,
        Names = new List<string>(),
        NameIsUtf16 = new List<bool>(),
        Store = new RegistryStore { Magic = RegistryStore.BeginMagic, Aligned = model.Store.Aligned },
        AssetCount = 0,
        AssetBytes = [],
        Tail = (byte[]) model.Tail.Clone(),
    };

    public static RegistryDocument Read(byte[] raw)
    {
        var d = new RegistryDocument { Header = raw[..HeaderBytes] };
        var p = HeaderBytes;

        var num = BitConverter.ToInt32(raw, p); p += 4;

        // AN EMPTY NAME BATCH IS JUST THE COUNT.
        //
        // UE's LoadNameBatch returns the moment the count is zero -- no string-byte total, no
        // hash version, no hashes, no headers. Reading them anyway consumes twelve bytes of the
        // tag store and reports something confident and wrong: "name batch declares 305419897
        // string bytes", which is 0x12345679, the store's own opening magic.
        //
        // This only shows up on an EMPTY registry, which is exactly the one worth having: a mod
        // that ships its own entries and nothing else can be appended beside every other mod's,
        // where one that ships a copy of the game's 26 MB registry clobbers them.
        if (num == 0)
        {
            d.Version = (FAssetRegistryVersionType) BitConverter.ToInt32(raw, 16);
            d.Store = RegistryStore.Read(raw, p, d.Version);
            p = d.Store.End;
            d.AssetCount = BitConverter.ToInt32(raw, p); p += 4;

            var emptyTail = raw.Length - 16;
            d.AssetBytes = raw[p..emptyTail];
            d.Tail = raw[emptyTail..];
            return d;
        }

        var declaredStringBytes = BitConverter.ToInt32(raw, p); p += 4;
        d.HashVersion = BitConverter.ToUInt64(raw, p); p += 8;

        // The stored hashes are not kept. They are recomputed on write, which makes a
        // byte-identical round-trip a proof of NameHash as well as of the layout -- if the hash
        // were wrong, the output would differ and say so.
        p += num * 8;

        var headers = p;
        p += num * 2;
        var stringStart = p;

        d.Names = new List<string>(num);
        d.NameIsUtf16 = new List<bool>(num);
        for (var i = 0; i < num; i++)
        {
            var b0 = raw[headers + i * 2];
            var b1 = raw[headers + i * 2 + 1];
            var length = ((b0 & 0x7F) << 8) | b1;
            var utf16 = (b0 & 0x80) != 0;
            d.NameIsUtf16.Add(utf16);
            if (utf16)
            {
                d.Names.Add(Encoding.Unicode.GetString(raw, p, length * 2));
                p += length * 2;
            }
            else
            {
                d.Names.Add(Encoding.UTF8.GetString(raw, p, length));
                p += length;
            }
        }

        if (p - stringStart != declaredStringBytes)
            throw new InvalidDataException(
                $"name batch declares {declaredStringBytes} string bytes, read {p - stringStart}");

        // The version drives whether the store pads its sections; see RegistryStore.Aligned.
        var version = (FAssetRegistryVersionType) BitConverter.ToInt32(raw, 16);
        d.Version = version;
        d.Store = RegistryStore.Read(raw, p, version);
        p = d.Store.End;

        d.AssetCount = BitConverter.ToInt32(raw, p); p += 4;

        var tailStart = raw.Length - 16;
        var deps = BitConverter.ToInt64(raw, tailStart);
        var numDepends = BitConverter.ToInt32(raw, tailStart + 8);
        var numPackages = BitConverter.ToInt32(raw, tailStart + 12);
        if (numDepends != 0 || numPackages != 0)
            throw new NotSupportedException(
                $"this registry has {numDepends} depends node(s) and {numPackages} package " +
                "data record(s); splicing assumes a cooked registry where both are empty");
        if (deps != 4)
            throw new NotSupportedException($"unexpected dependency section size {deps}");

        d.AssetBytes = raw[p..tailStart];
        d.Tail = raw[tailStart..];
        return d;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write(Header);

        w.Write(Names.Count);

        // Symmetrical with the read: an empty batch is the count and nothing else, so writing the
        // totals anyway would produce a file this reader could not read back.
        if (Names.Count > 0)
        {
            var stringBytes = 0;
            for (var i = 0; i < Names.Count; i++)
                stringBytes += NameIsUtf16[i]
                    ? Encoding.Unicode.GetByteCount(Names[i])
                    : Encoding.UTF8.GetByteCount(Names[i]);
            w.Write(stringBytes);
            w.Write(HashVersion);
        }

        foreach (var n in Names) w.Write(NameHash.Of(n));

        for (var i = 0; i < Names.Count; i++)
        {
            var len = NameIsUtf16[i]
                ? Encoding.Unicode.GetByteCount(Names[i]) / 2
                : Encoding.UTF8.GetByteCount(Names[i]);
            if (len > 0x7FFF)
                throw new InvalidDataException($"name too long to encode: {Names[i]}");
            // Two bytes, big-endian-ish: the flag and the top 7 bits of the length, then the
            // low 8. Writing them as a little-endian uint16 is the mistake that makes every
            // name after the first run into its neighbour.
            w.Write((byte) ((NameIsUtf16[i] ? 0x80 : 0) | (len >> 8)));
            w.Write((byte) (len & 0xFF));
        }

        for (var i = 0; i < Names.Count; i++)
            w.Write(NameIsUtf16[i]
                ? Encoding.Unicode.GetBytes(Names[i])
                : Encoding.UTF8.GetBytes(Names[i]));

        Store.Write(w, 0);

        w.Write(AssetCount);
        w.Write(AssetBytes);
        w.Write(Tail);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Index of a name, adding it if the batch does not already have it.</summary>
    public uint InternName(string name)
    {
        // Linear on first use would be 223,870 comparisons per name; the map is built once.
        _index ??= BuildIndex();
        if (_index.TryGetValue(name, out var at)) return (uint) at;

        _index[name] = Names.Count;
        Names.Add(name);
        // ASCII names go in as UTF-8. A name needing UTF-16 would have to say so, and none of
        // the ones a mod introduces do.
        NameIsUtf16.Add(false);
        return (uint) (Names.Count - 1);
    }

    private Dictionary<string, int>? _index;

    private Dictionary<string, int> BuildIndex()
    {
        // Ordinal, not case-insensitive: the batch holds names differing only in case, and
        // collapsing them would repoint existing assets at the wrong entry.
        var map = new Dictionary<string, int>(Names.Count, StringComparer.Ordinal);
        for (var i = 0; i < Names.Count; i++) map.TryAdd(Names[i], i);
        return map;
    }
}
