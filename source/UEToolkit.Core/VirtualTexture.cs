using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace UEToolkit.Core;

/// <summary>
/// Author a VIRTUAL texture: the kind every character in this game actually wears.
///
/// An ordinary texture is a mip chain -- one array per level, each half the last. A virtual texture
/// is not a smaller or worse version of that, it is a different object: the pixels are cut into
/// fixed 128-pixel PAGES, each page grown by a four-pixel border so the GPU can filter across page
/// edges, each page block-compressed on its own, and the pages laid out in MORTON order inside a
/// few large chunks with a table saying where each mip's run begins. Putting an ordinary texture in
/// a virtual slot is not a downgrade, it is the wrong type, and the material samples nothing.
///
/// That is why this exists at all, and the count that forced it: of the readable textures under
/// Characters/Humanoid, fifty-eight of fifty-eight are virtual. There is no ordinary-texture path
/// to a character in this game.
///
/// WHAT IS AUTHORED AND WHAT IS KEPT. The chunk bytes are rebuilt; everything else is the
/// template's. The header -- tile size, border, mip count, the per-mip chunk and offset tables, the
/// fallback colour -- is left exactly as cooked, and the replacement is required to come out the
/// same length. That is a deliberate limit rather than an unfinished one: those tables are six
/// parallel arrays that have to agree with each other and with the chunk sizes, and rewriting them
/// means resizing the package. Repainting at the template's own resolution needs none of it.
/// </summary>
public static class VirtualTexture
{
    /// <summary>
    /// One mip: how many pages it is cut into, where they start, and how a page ADDRESS becomes a
    /// stored page.
    ///
    /// The last part is not decoration. Pages are addressed in MORTON order, which interleaves the
    /// x and y bits, so the address space of a grid is always square-by-power-of-two even when the
    /// grid is not. A 16x32 grid lives in a 1024-address space and only half of those addresses
    /// exist -- so the cooker stores a run-length table saying which stretches are real and where
    /// each one begins among the stored pages.
    ///
    /// A SQUARE texture's table is the identity ([0] -> [0]) and every address is its own page,
    /// which is why treating the address as the page index worked for as long as only square
    /// textures were tried. The weapon scopes are 2048x4096 and every one of them was refused.
    /// </summary>
    public sealed record Level(int Index, int TilesX, int TilesY, int ChunkIndex, int BaseOffset,
                               IReadOnlyList<uint> Addresses, IReadOnlyList<uint> Offsets)
    {
        public int Tiles => TilesX * TilesY;

        /// <summary>
        /// Whether this level's pages are in the container at all.
        ///
        /// The bottom of a non-square chain is not: the scopes declare thirteen mips and store
        /// twelve, the last one marked ~0. There is nothing to author for such a level and nothing
        /// wrong with it -- refusing it rejected the whole texture over its 1x1 tail.
        /// </summary>
        public bool Stored { get; init; } = true;

        /// <summary>Whether every address maps straight to itself -- a square grid's table.</summary>
        public bool Contiguous => Addresses.Count == 1 && Addresses[0] == 0 && Offsets[0] == 0;

        /// <summary>
        /// Which stored page an address is, or -1 when the address holds none.
        ///
        /// The engine's own lookup (FVirtualTextureTileOffsetData::GetTileOffset): find the last
        /// run beginning at or before the address, and count forward from where that run starts.
        /// An offset of ~0 marks a run that is not stored at all.
        /// </summary>
        public int PageAt(uint address)
        {
            var run = -1;
            for (var i = 0; i < Addresses.Count && Addresses[i] <= address; i++) run = i;
            if (run < 0 || Offsets[run] == uint.MaxValue) return -1;
            return (int) (Offsets[run] + (address - Addresses[run]));
        }
    }

    /// <summary>One stored chunk, with the bytes as cooked.</summary>
    public sealed record Chunk(int Index, int Size, long BulkOffset, byte[] Data);

    public sealed record Template(string PackagePath, EPixelFormat Format,
                                  int Width, int Height, int TileSize, int BorderSize,
                                  int NumMips, int TileBytes,
                                  IReadOnlyList<Level> Levels, IReadOnlyList<Chunk> Chunks)
    {
        public int PhysicalTileSize => TileSize + BorderSize * 2;
        public int BulkBytes => Chunks.Sum(c => c.Size);

        public string Describe =>
            $"{Width} x {Height} {Format}, page {TileSize}+{BorderSize} = {PhysicalTileSize} " +
            $"({TileBytes:N0} bytes), {NumMips} level(s) in {Chunks.Count} chunk(s), " +
            $"{BulkBytes:N0} bytes of pages";
    }

    // ------------------------------------------------------------------ reading a template

    /// <summary>
    /// Measure a virtual texture, and refuse anything this writer cannot reproduce exactly.
    ///
    /// The refusals are the point. Every one of them is a shape that would otherwise be written
    /// half-correctly: a second layer whose pages interleave with the first, a legacy table with a
    /// different addressing scheme, a page-offset table that remaps addresses instead of leaving
    /// them alone. Each would produce a texture that loads and decodes to nonsense.
    /// </summary>
    public static Template? Describe(IFileProvider provider, string templatePath, out string? why)
    {
        why = null;
        try
        {
            // Through AssetLoader rather than LoadPackageObject, so a mounted container path
            // ("SWZeroCompany/Content/.../T_X.uasset") and a package name ("/Game/.../T_X") both
            // work. The first form asks for an export literally named "uasset", and the error it
            // produces reads like a missing asset rather than a mistyped path.
            if (AssetLoader.LoadPrimary(provider, templatePath) is not UTexture2D tex)
            { why = $"{templatePath} is not a texture"; return null; }
            if (tex.PlatformData.VTData is not { } vt || !vt.IsInitialized())
            { why = $"{templatePath} is not a virtual texture"; return null; }

            if (vt.NumLayers != 1)
            { why = $"{templatePath} has {vt.NumLayers} layers; this writer handles one"; return null; }
            if (vt.IsLegacyData())
            { why = $"{templatePath} uses the legacy page table"; return null; }
            if (vt.ChunkIndexPerMip is null || vt.BaseOffsetPerMip is null || vt.TileOffsetData is null)
            { why = $"{templatePath} has no per-mip tables"; return null; }
            if (vt.WidthInBlocks != 1 || vt.HeightInBlocks != 1)
            { why = $"{templatePath} is a UDIM block texture ({vt.WidthInBlocks}x{vt.HeightInBlocks})"; return null; }

            var format = vt.LayerTypes[0];
            var blockBytes = BlockBytes(format);
            if (blockBytes == 0) { why = $"no encoder for {format}"; return null; }

            var physical = (int) vt.GetPhysicalTileSize();
            if (physical % 4 != 0)
            { why = $"a {physical}-pixel page does not divide into 4x4 blocks"; return null; }

            var perSide = physical / 4;
            var tileBytes = perSide * perSide * blockBytes;

            // Cross-checked against the cooker's own number rather than trusted. If these disagree
            // the format table is wrong, and every page after the first would land at the wrong
            // offset -- which decodes as a texture that is merely SHIFTED, and so reads as a
            // plausible bad import rather than as a bug here.
            var stated = vt.TileDataOffsetPerLayer is { Length: > 0 } tdo ? (int) tdo[^1] : 0;
            if (stated != 0 && stated != tileBytes)
            { why = $"{templatePath} stores {stated} bytes per page, {format} needs {tileBytes}"; return null; }

            var levels = new List<Level>();
            for (var i = 0; i < vt.NumMips; i++)
            {
                var tod = vt.TileOffsetData[i];

                // Addresses/Offsets are a run-length remapping table from a page's MORTON address
                // to its slot among the stored pages. A square grid ships the identity and every
                // address is its own page; a non-square one has holes, because morton addressing
                // always spans a square power-of-two space. This used to refuse anything but the
                // identity, which refused every weapon scope in the game.
                if (tod.Addresses is not { Length: > 0 } addresses ||
                    tod.Offsets is not { Length: > 0 } offsets ||
                    addresses.Length != offsets.Length || addresses[0] != 0)
                { why = $"{templatePath} level {i} has an unreadable page-offset table"; return null; }

                var level = new Level(i, (int) tod.Width, (int) tod.Height,
                                      (int) vt.ChunkIndexPerMip[i], (int) vt.BaseOffsetPerMip[i],
                                      addresses, offsets);

                // PROVED PER LEVEL, not argued. Walk every page of the grid through the table and
                // require the answers to be exactly the pages 0..n-1, each once. That is the whole
                // property the writer depends on -- pages stored densely, one slot each, none
                // landing on top of another -- and checking it directly costs a few hundred
                // iterations and replaces a paragraph of reasoning about bit interleaving.
                var slots = new HashSet<int>();
                var missing = 0;
                for (var ty = 0; ty < level.TilesY; ty++)
                for (var tx = 0; tx < level.TilesX; tx++)
                {
                    var slot = level.PageAt(Morton((uint) tx, (uint) ty));
                    if (slot < 0) { missing++; continue; }
                    if (slot >= level.Tiles || !slots.Add(slot))
                    { why = $"{templatePath} level {i} maps page ({tx},{ty}) to slot {slot}, " +
                            "which is not its own"; return null; }
                }

                // ALL of them or NONE. A level the cooker left out is ordinary and is simply not
                // authored; a level that is HALF there is a shape this writer has never seen, and
                // filling in the half that exists would leave a texture that is neither.
                if (missing > 0 && missing < level.Tiles)
                { why = $"{templatePath} level {i} stores {level.Tiles - missing} of its " +
                        $"{level.Tiles} pages, which this writer does not handle"; return null; }

                levels.Add(missing == 0 ? level : level with { Stored = false });
            }

            var chunks = new List<Chunk>();
            for (var i = 0; i < vt.Chunks.Length; i++)
            {
                var data = vt.Chunks[i].BulkData?.Data;
                if (data is null || data.Length == 0)
                { why = $"{templatePath} chunk {i} has no data"; return null; }
                chunks.Add(new Chunk(i, data.Length, vt.Chunks[i].BulkData!.Header.OffsetInFile, data));
            }

            // Every page must land inside its chunk. Checked here so a bad table is a message
            // rather than an IndexOutOfRange in the middle of encoding four thousand pages.
            foreach (var l in levels)
            {
                if (l.ChunkIndex < 0 || l.ChunkIndex >= chunks.Count)
                { why = $"level {l.Index} names chunk {l.ChunkIndex} of {chunks.Count}"; return null; }
                var end = l.BaseOffset + l.Tiles * (long) tileBytes;
                if (end > chunks[l.ChunkIndex].Size)
                { why = $"level {l.Index} needs {end:N0} bytes of chunk {l.ChunkIndex}, " +
                        $"which is {chunks[l.ChunkIndex].Size:N0}"; return null; }
            }

            var resolved = tex.Owner?.Name ?? templatePath;
            return new Template(resolved, format, (int) vt.Width, (int) vt.Height,
                                (int) vt.TileSize, (int) vt.TileBorderSize,
                                (int) vt.NumMips, tileBytes, levels, chunks);
        }
        catch (Exception e) { why = $"{templatePath}: {e.Message}"; return null; }
    }

    // ------------------------------------------------------------------ authoring the pages

    /// <summary>
    /// Cut an image into pages and encode every one of them.
    ///
    /// Two things here are not obvious and were measured from the game's own data rather than
    /// assumed.
    ///
    /// The sampling WRAPS. A page's border reaches four pixels outside itself, and at the texture's
    /// edge that wraps to the far side instead of clamping. One rule then covers the whole chain:
    /// once a mip is smaller than a page -- level 5 of a 2048 texture is 64 pixels in a 128 page --
    /// the same modulo fills the page by REPEATING the image, which is exactly what the cooked
    /// data contains. Decoding the game's own levels 5, 6 and 7 shows the mip tiled 2x2, 4x4 and
    /// 8x8. Clamping instead would smear the last row across the tail of every texture.
    ///
    /// The order is MORTON, not row-major, because that is how the page table addresses them.
    /// Row-major would produce a texture that loads, decodes, and shows the pages shuffled.
    /// </summary>
    public static List<byte[]>? Author(Template t, byte[] rgba, MipChain.Kind kind,
                                       out string? why, Action<string>? log = null)
    {
        why = null;
        try
        {
            if (rgba.Length < t.Width * t.Height * 4)
            { why = $"need {t.Width}x{t.Height} pixels, got {rgba.Length / 4:N0}"; return null; }

            var levels = MipChain.Build(rgba, t.Width, t.Height, kind);
            if (levels.Count < t.NumMips)
            { why = $"the image makes {levels.Count} levels and the template wants {t.NumMips}"; return null; }

            // Started from the cooked bytes, so whatever sits in front of the pages -- this game
            // writes a four-byte codec header -- is carried across untouched instead of guessed at.
            var chunks = t.Chunks.Select(c => (byte[]) c.Data.Clone()).ToList();

            var pages = 0;
            for (var i = 0; i < t.NumMips; i++)
            {
                var level = t.Levels[i];
                if (!level.Stored) continue;
                var img = levels[i];
                var into = chunks[level.ChunkIndex];

                for (var ty = 0; ty < level.TilesY; ty++)
                for (var tx = 0; tx < level.TilesX; tx++)
                {
                    var bytes = EncodePage(t, img, tx, ty);
                    if (bytes.Length != t.TileBytes)
                    { why = $"level {i} page encoded to {bytes.Length} bytes, layout wants {t.TileBytes}"; return null; }

                    Array.Copy(bytes, 0, into, PageOffset(t, level, tx, ty), t.TileBytes);
                    pages++;
                }
            }

            log?.Invoke($"      {pages:N0} page(s) across {t.NumMips} level(s), " +
                        $"{t.BulkBytes:N0} bytes");
            return chunks;
        }
        catch (Exception e) { why = e.Message; return null; }
    }

    /// <summary>Load an image, fit it to the template, and author it.</summary>
    public static List<byte[]>? Author(Template t, string imagePath, MipChain.Kind? kind,
                                       out string? why, Action<string>? log = null)
    {
        why = null;
        try
        {
            using var image = Image.Load<Rgba32>(imagePath);
            if (image.Width != t.Width || image.Height != t.Height)
            {
                // WITHDRAWN: this warned that a resized virtual texture does not display.
                //
                // It was written on a correlation -- an author's diffuse showed and their other
                // three maps did not, and the diffuse was the only one not resized. The next
                // observation killed it: their MSK is resized and displays correctly. The real
                // cause was two of their source files being byte-identical, so a part was showing
                // the other part's art because it had literally been given it.
                //
                // A resize is still worth saying out loud -- an upscaled 2048 in a 4096 slot is
                // soft, and an author who did not intend it should know -- but it is a note about
                // QUALITY, not a fault.
                log?.Invoke($"      resized {image.Width}x{image.Height} to {t.Width}x{t.Height} " +
                            "to match the template -- author at that size to avoid the upscale");
                TextureImport.ScaleTo(image, t.Width, t.Height);
            }

            var rgba = new byte[t.Width * t.Height * 4];
            image.CopyPixelDataTo(rgba);

            var use = kind ?? TextureImport.KindFor(imagePath);
            log?.Invoke($"      {Path.GetFileName(imagePath)}: {t.Width}x{t.Height} -> " +
                        $"{t.Format} virtual, {use} filtering");
            return Author(t, rgba, use, out why, log);
        }
        catch (Exception e) { why = $"{Path.GetFileName(imagePath)}: {e.Message}"; return null; }
    }

    /// <summary>
    /// One page, cut from a level's image and block-compressed.
    ///
    /// Public because the round-trip test authors levels INDIVIDUALLY from the game's own decode of
    /// each one. Authoring the whole chain from level 0 measures two things at once -- the page
    /// layout, and how far a mip chain built from already-compressed pixels drifts from the
    /// cooker's -- and the second swamps the first by the bottom of the chain.
    /// </summary>
    public static byte[] EncodePage(Template t, MipChain.Level img, int tx, int ty) =>
        TextureImport.Encode(t.Format, Page(img, tx, ty, t.TileSize, t.BorderSize),
                             t.PhysicalTileSize, t.PhysicalTileSize);

    /// <summary>
    /// Where a page's bytes sit in its chunk.
    ///
    /// Through the level's own remap table, which for a square texture is the identity and so
    /// gives exactly what the old arithmetic did. For a non-square one the morton address is NOT
    /// the page index -- half the addresses in the space do not exist -- and using it as one
    /// writes every page past the first hole into the wrong slot.
    /// </summary>
    public static int PageOffset(Template t, Level level, int tx, int ty) =>
        level.BaseOffset + level.PageAt(Morton((uint) tx, (uint) ty)) * t.TileBytes;

    /// <summary>One page's pixels, border included, sampled with wrap-around.</summary>
    private static byte[] Page(MipChain.Level img, int tx, int ty, int tileSize, int border)
    {
        var p = tileSize + border * 2;
        var dst = new byte[p * p * 4];
        var ox = tx * tileSize - border;
        var oy = ty * tileSize - border;

        for (var y = 0; y < p; y++)
        {
            var sy = Wrap(oy + y, img.Height);
            for (var x = 0; x < p; x++)
            {
                var sx = Wrap(ox + x, img.Width);
                Array.Copy(img.Rgba, (sy * img.Width + sx) * 4, dst, (y * p + x) * 4, 4);
            }
        }
        return dst;
    }

    private static int Wrap(int v, int n) => ((v % n) + n) % n;

    /// <summary>Interleave the bits of x and y -- the address a page is stored under.</summary>
    public static uint Morton(uint x, uint y) => Part1By1(x) | (Part1By1(y) << 1);

    private static uint Part1By1(uint v)
    {
        v &= 0x0000ffff;
        v = (v ^ (v << 8)) & 0x00ff00ff;
        v = (v ^ (v << 4)) & 0x0f0f0f0f;
        v = (v ^ (v << 2)) & 0x33333333;
        v = (v ^ (v << 1)) & 0x55555555;
        return v;
    }

    private static int BlockBytes(EPixelFormat format) => format switch
    {
        EPixelFormat.PF_DXT1 => 8,
        EPixelFormat.PF_BC4 => 8,
        EPixelFormat.PF_DXT5 => 16,
        EPixelFormat.PF_BC5 => 16,
        _ => 0,
    };

    // ------------------------------------------------------------------ putting it in the copy

    /// <summary>
    /// Replace the pages in a copied template and rename it into the mod.
    ///
    /// A virtual texture keeps ALL its pixels in the payload chunk -- the package itself is barely
    /// a kilobyte of tables -- so unlike an ordinary texture there is nothing to find and overwrite
    /// inside the package. The payload is rebuilt at the chunk offsets the header already states,
    /// which is why the header can be left alone.
    /// </summary>
    public static List<IoStoreWriter.Chunk>? Swap(AssetCopy.Source source, Template t,
                                                  List<byte[]> authored, string newPackageName,
                                                  string containerPath, out string? why,
                                                  Action<string>? log = null)
    {
        why = null;

        if (authored.Count != t.Chunks.Count)
        { why = $"authored {authored.Count} chunk(s), template has {t.Chunks.Count}"; return null; }

        var pieces = new List<AssetCopy.Piece>();
        var replaced = false;

        foreach (var piece in source.Pieces)
        {
            if (piece.IsPackage || piece.Data.Length != t.BulkBytes) { pieces.Add(piece); continue; }

            var data = (byte[]) piece.Data.Clone();
            for (var i = 0; i < authored.Count; i++)
            {
                var at = t.Chunks[i].BulkOffset;
                if (authored[i].Length != t.Chunks[i].Size)
                { why = $"chunk {i} came to {authored[i].Length:N0} bytes, template has " +
                        $"{t.Chunks[i].Size:N0}"; return null; }
                if (at < 0 || at + authored[i].Length > data.Length)
                { why = $"chunk {i} sits at {at:N0}, past the {data.Length:N0}-byte payload"; return null; }
                Array.Copy(authored[i], 0, data, at, authored[i].Length);
            }

            pieces.Add(piece with { Data = data });
            replaced = true;
        }

        if (!replaced)
        { why = $"no payload chunk of {t.BulkBytes:N0} bytes to replace"; return null; }

        var chunks = AssetCopy.Rename(source with { Pieces = pieces }, newPackageName,
                                      containerPath, log);

        log?.Invoke($"      pages replaced: {t.BulkBytes:N0} bytes across {t.Chunks.Count} chunk(s)");
        return chunks;
    }
}
