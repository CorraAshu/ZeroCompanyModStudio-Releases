using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using Silk.NET.OpenGL;

namespace UEToolkit.App.Rendering;

/// <summary>
/// Uploads CUE4Parse textures as GL textures, cached by asset path: a block-compressed texture goes up as the game's own
/// blocks (<see cref="UploadCompressed"/>), anything else is decoded first. Decode is expensive (some of these are 8K),
/// so nothing is decoded twice.
/// </summary>
public sealed class TextureCache(GL gl) : IDisposable
{
    /// <summary>
    /// Preview textures are capped. Some games ship 16384x4096 diffuse maps — 268 MB each
    /// uncompressed, three per character, which blows past GL_MAX_TEXTURE_SIZE on most
    /// drivers and uploads as garbage. Export still uses full resolution. A decoded texture is resized to fit; a block
    /// upload starts at the game's first mip that fits.
    /// </summary>
    public int MaxPreviewSize = 4096;

    /// <summary>When set, every decoded texture is written here before upload (and every texture is decoded, not sent as blocks).</summary>
    public string? DumpDir;

    private readonly Dictionary<string, uint> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _failed = new();

    public IReadOnlyList<string> Failures => _failed;

    // ---- WHAT THE GPU HOLDS FOR TEXTURES, counted where it changes. The bytes are an ESTIMATE of
    // what the driver was handed -- for a decoded upload width * height * 4 for the RGBA8 level plus a third again for the mip
    // chain GenerateMipmap builds, for a block-compressed upload the real bytes of the blocks and their mips -- not a reading of what the driver really allocated (it may pad, or
    // keep a system copy). The reading of the real thing is the process's own commit, beside it in the report.
    private readonly Dictionary<uint, long> _glBytes = new();
    private static long _liveTextures, _liveTextureBytes, _uploadedTextures, _deletedTextures;

    /// <summary>Process-wide: textures alive now and their estimated bytes, and how many were ever uploaded and deleted.</summary>
    public static (long Live, long Bytes, long Uploaded, long Deleted) LiveTextures =>
        (Interlocked.Read(ref _liveTextures), Interlocked.Read(ref _liveTextureBytes),
         Interlocked.Read(ref _uploadedTextures), Interlocked.Read(ref _deletedTextures));

    /// <summary>Textures this cache holds on the GPU now, and their estimated bytes.</summary>
    public int GlTextureCount => _glBytes.Count;
    public long GlTextureBytes => _glBytes.Values.Sum();

    /// <summary>
    /// What the textures held now WOULD cost if every one were uploaded no larger than <paramref name="cap"/> on its longest
    /// side (the same rule Downscale applies at <see cref="MaxPreviewSize"/>), from the size each was decoded at. For the
    /// memory report's "what would a cap save" table; nothing here changes what is uploaded.
    /// </summary>
    public (int Count, long Bytes) EstimateAtCap(int cap)
    {
        long total = 0; var n = 0;
        foreach (var (handle, size) in _sizes)
        {
            if (!_glBytes.ContainsKey(handle)) continue;
            long w = size.W, h = size.H, longest = Math.Max(w, h);
            if (longest > cap) { var f = (double) cap / longest; w = Math.Max(1, (long) (w * f)); h = Math.Max(1, (long) (h * f)); }
            total += w * h * 4 * 4 / 3;
            n++;
        }
        return (n, total);
    }

    private readonly Dictionary<uint, (string Format, int W, int H)> _formatOf = new();

    /// <summary>
    /// The held textures by the pixel format the GAME stores them in, with what each group occupies as the game ships it
    /// (the block-compressed size: 4 bits a pixel for DXT1/BC4, 8 for DXT5/BC5/BC6H/BC7, 32 for a plain 8-bit image, and a
    /// third again for mips) next to what this cache uploaded for it (a block-compressed texture is uploaded as shipped, so
    /// shipped and uploaded agree; one decoded first is RGBA8, 32 bits a pixel, and shows the gap).
    /// </summary>
    public IEnumerable<string> FormatReport()
    {
        static int BitsPerPixel(string f) => f switch
        {
            "PF_DXT1" or "PF_BC4" or "PF_ETC1" or "PF_ETC2_RGB" or "PF_ASTC_8x8" => 4,
            "PF_DXT3" or "PF_DXT5" or "PF_BC5" or "PF_BC6H" or "PF_BC7" or "PF_ASTC_4x4" or "PF_ETC2_RGBA" or "PF_G8" or "PF_A8" => 8,
            "PF_FloatRGBA" => 64, "PF_G16" or "PF_R16F" => 16,
            _ => 32,
        };
        var groups = new Dictionary<string, (int N, long Shipped, long Uploaded)>();
        foreach (var (handle, (format, w, h)) in _formatOf)
        {
            if (!_glBytes.TryGetValue(handle, out var uploaded)) continue;
            var shipped = (long) w * h * BitsPerPixel(format) / 8 * 4 / 3;
            var g = groups.GetValueOrDefault(format);
            groups[format] = (g.N + 1, g.Shipped + shipped, g.Uploaded + uploaded);
        }
        foreach (var (format, g) in groups.OrderByDescending(kv => kv.Value.Uploaded))
            yield return $"{format,-14} {g.N,4:N0} textures  as the game ships them {g.Shipped / 1048576.0,8:N0} MB  as uploaded here {g.Uploaded / 1048576.0,8:N0} MB";
    }

    /// <summary>How the held textures divide by the longest side they were uploaded at (decoded or as blocks): "4096+:12 2048:30 1024:51 512-:88".</summary>
    public string SizeClasses()
    {
        int c4 = 0, c2 = 0, c1 = 0, c0 = 0;
        long b4 = 0, b2 = 0, b1 = 0, b0 = 0;
        foreach (var (handle, size) in _sizes)
        {
            if (!_glBytes.TryGetValue(handle, out var bytes)) continue;
            var longest = Math.Max(size.W, size.H);
            if (longest >= 4096) { c4++; b4 += bytes; } else if (longest >= 2048) { c2++; b2 += bytes; }
            else if (longest >= 1024) { c1++; b1 += bytes; } else { c0++; b0 += bytes; }
        }
        return $"4096+: {c4:N0} ({b4 / 1048576.0:N0} MB)  2048: {c2:N0} ({b2 / 1048576.0:N0} MB)  1024: {c1:N0} ({b1 / 1048576.0:N0} MB)  under 1024: {c0:N0} ({b0 / 1048576.0:N0} MB)";
    }

    /// <summary>DEV: how many of these names the driver does not know (<c>glIsTexture</c>): deleted, or never made. GL thread only.</summary>
    public int CountUnknownToGl(IEnumerable<uint> handles)
    {
        var dead = 0;
        foreach (var h in handles) if (!gl.IsTexture(h)) dead++;
        return dead;
    }

    /// <summary>The estimated bytes of just these handles (those this cache holds).</summary>
    public long BytesOf(IEnumerable<uint> handles)
    {
        long total = 0;
        foreach (var h in handles) if (_glBytes.TryGetValue(h, out var b)) total += b;
        return total;
    }

    /// <summary>Entries that name a texture which would not decode (kept so a failure is not retried every frame).</summary>
    public int EmptyEntries => _cache.Values.Count(h => h == 0);

    /// <summary>Textures <see cref="ReleaseUnused"/> has deleted, their estimated bytes, and how many were uploaded AGAIN afterwards.</summary>
    public int Released { get; private set; }
    public long ReleasedBytes { get; private set; }
    public int ReUploaded { get; private set; }
    private readonly HashSet<string> _releasedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// DELETE EVERY TEXTURE NO SECTION STILL USES. The cache otherwise keeps each texture it ever uploaded until the
    /// context dies, which is right for a model viewer that shows one thing and wrong for anything that REPLACES what
    /// it shows: the Level Editor opening a second map kept the first map's pictures for the rest of the session.
    ///
    /// <paramref name="sections"/> is every section that may still be drawn; <paramref name="alsoLive"/> names
    /// handles held somewhere that is not a section. GL thread only, like every other call here. A texture deleted
    /// here is simply decoded and uploaded again if something asks for it later (the cache entry goes with it).
    /// Entries for textures that failed to decode (handle 0) and uploaded image FILES are left alone.
    /// </summary>
    public (int Textures, long Bytes) ReleaseUnused(IEnumerable<RenderSection> sections, IEnumerable<uint>? alsoLive = null)
    {
        var live = new HashSet<uint>();
        foreach (var s in sections) s.AddTextureHandles(live);
        if (alsoLive is not null) foreach (var h in alsoLive) live.Add(h);

        var count = 0; long bytes = 0;
        foreach (var (key, handle) in _cache.ToList())
        {
            if (handle == 0 || live.Contains(handle) || key.StartsWith("file:", StringComparison.Ordinal)) continue;
            gl.DeleteTexture(handle);
            _cache.Remove(key);
            _sizes.Remove(handle);
            _noise.Remove(handle);
            _formatOf.Remove(handle);
            _releasedKeys.Add(key);
            if (_glBytes.Remove(handle, out var b))
            {
                bytes += b;
                Interlocked.Decrement(ref _liveTextures);
                Interlocked.Add(ref _liveTextureBytes, -b);
                Interlocked.Increment(ref _deletedTextures);
            }
            count++;
        }
        Released += count;
        ReleasedBytes += bytes;
        return (count, bytes);
    }

    /// <summary>
    /// The GL texture for a game texture, uploaded once. A block-compressed 2D texture goes up AS THE GAME STORES IT (see
    /// <see cref="UploadCompressed"/>); anything else is decoded to RGBA8 first.
    ///
    /// <paramref name="exact"/> is for a caller that SHOWS the texture as a picture (a texture viewer, an icon): it gets the
    /// CPU decode, whose channels are exactly what the exporter writes, and its own cache entry. <paramref name="normalMap"/>
    /// says the slot reads only red and green (a tangent-space normal): the one thing the block upload cannot give is the
    /// blue the CPU decode rebuilds for a BC5 map, so only a slot that never reads it may take a BC5 texture as blocks.
    /// </summary>
    public uint GetOrUpload(UTexture? texture, bool exact = false, bool normalMap = false)
    {
        if (texture is null) return 0;
        var cpuOnly = exact || (UploadCompressed && texture.Format == EPixelFormat.PF_BC5 && !normalMap);
        var key = (cpuOnly ? ExactPrefix : "") + texture.GetPathName();
        if (_cache.TryGetValue(key, out var existing)) return existing;
        if (_releasedKeys.Remove(key)) ReUploaded++;

        uint uploaded = 0;      // the texture made by the CPU path below, so a throw after the upload can delete it
        try
        {
            if (!cpuOnly && TryUploadBlocksSafely(texture, out var blocks))
            {
                _cache[key] = blocks;
                return blocks;
            }
            if (cpuOnly && !exact) CountCpu("BC5 outside a normal-map slot");

            // Decode happens at the SHIPPED size before any downscale, and some games ship
            // 8192x4096 character maps -- 134 MB as RGBA8, in native SkiaSharp memory, on the
            // render thread. A failure in there does not throw a managed exception.
            UEToolkit.Core.CrashLog.Trail(
                $"decode {texture.Name} {texture.PlatformData?.SizeX ?? 0}x{texture.PlatformData?.SizeY ?? 0}");
            var decoded = texture.Decode(ETexturePlatform.DesktopMobile);
            if (decoded is null) { _cache[key] = 0; return 0; }

            // Decode() keeps the source pixel format; ToSkBitmap normalises it. Uploading
            // the raw buffer as if it were BGRA8 produces marbled garbage.
            using var bitmap = decoded.ToSkBitmap();

            // Dump exactly what is about to be uploaded, to separate a decode problem
            // from a sampling problem.
            if (DumpDir is not null)
            {
                Directory.CreateDirectory(DumpDir);
                using var img = SkiaSharp.SKImage.FromBitmap(bitmap);
                using var d = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 85);
                using var fs = File.OpenWrite(Path.Combine(DumpDir, texture.Name + ".png"));
                d.SaveTo(fs);
            }
            var handle = uploaded = Upload(Downscale(bitmap, out var scaled), scaled);
#if DEV
            if (VtCheck && scaled is null) CheckCpuUpload(texture, handle, decoded);
#endif
            _sizes[handle] = (bitmap.Width, bitmap.Height);
            _noise[handle] = TileNoise(bitmap);
            _formatOf[handle] = (texture.Format.ToString(), bitmap.Width, bitmap.Height);
            _cache[key] = handle;
            return handle;
        }
        catch (Exception e)
        {
            // Something threw AFTER the upload made a texture: it is in no cache entry, so neither ReleaseUnused nor
            // Dispose would ever delete it. Delete it here and take it back out of the counters.
            if (uploaded != 0)
            {
                gl.DeleteTexture(uploaded);
                _sizes.Remove(uploaded);
                _noise.Remove(uploaded);
                _formatOf.Remove(uploaded);
                if (_glBytes.Remove(uploaded, out var orphanBytes))
                {
                    Interlocked.Decrement(ref _liveTextures);
                    Interlocked.Add(ref _liveTextureBytes, -orphanBytes);
                    Interlocked.Increment(ref _deletedTextures);
                }
            }
            // A handful of BC7 control maps can fail to decode; don't let one bad
            // texture take down the whole mesh preview.
            _failed.Add($"{texture.Name}: {e.Message}");
            _cache[key] = 0;
            return 0;
        }
    }

    // ---------------------------------------------------------------- THE GAME'S OWN BLOCKS

    /// <summary>
    /// UPLOAD A BLOCK-COMPRESSED TEXTURE AS THE GAME STORES IT -- its own mip chain, block data untouched -- instead of
    /// decoding it to RGBA8 and letting GenerateMipmap rebuild the chain. Decoded is 4 bytes a pixel against 0.5 (DXT1, BC4)
    /// or 1 (DXT5, BC5, BC7) in the file: SK_Anaxes_040's 301 textures were 17.4 GB uploaded decoded and are 3.5 GB as
    /// shipped (measured both ways), the same picture at a fifth of the memory and with no CPU decode. The GPU does the
    /// block decode. UNORM and no sRGB conversion, exactly like the Rgba8 upload it replaces. What a shader reads differs
    /// from the CPU decode's in two ways, both measured: the GPU's block decode against CUE4Parse's (measured on the
    /// virtual BC7 normal maps: isolated texels up to 144 off in 8 of the 94 levels compared, every other texel within one level; which decoder
    /// is right was not decided), and the game's own mips against GenerateMipmap's box filter (about half a level
    /// brighter on average in the close-ups measured; single edge pixels differed by up to 129 to 135 there, the baseline's own noise reaches 56 to 64). Mips above <see cref="MaxPreviewSize"/> are skipped, not resized.
    ///
    /// Most of this game's textures are VIRTUAL (SK_Anaxes_040: 271 of 301), stored as tiles of blocks; those are assembled from
    /// the tiles' blocks without decoding (<see cref="PlanVirtual"/>).
    ///
    /// What stays on the CPU decode (each counted in <see cref="BlockReport"/> with its reason): formats that are not
    /// block-compressed or that GL lacks, a BC5 map in a slot that reads its blue (the CPU decode rebuilds a Z the GPU's
    /// two-channel texture cannot give), sizes that are not a multiple of 4, chains that stop above 4x4, and a virtual texture
    /// that is not the one common layout. Off for the audit, which exists to find textures that FAIL to decode, and for the
    /// texture viewers (<c>exact</c> in <see cref="GetOrUpload"/>).
    /// </summary>
    public bool UploadCompressed = true;

#if DEV
    /// <summary>DEV: <c>--cputex</c> puts the RGBA8 decode back for every texture, so one binary draws both ways.</summary>
    public static bool DecodeOnCpu;
#endif

    private const string ExactPrefix = "exact:";

    /// <summary>Textures uploaded as blocks, their bytes, and the mips above the cap that were skipped (not resized).</summary>
    public int BlockTextures { get; private set; }
    public long BlockBytes { get; private set; }
    public int BlockMipsSkipped { get; private set; }
    private readonly Dictionary<string, int> _cpuReasons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _blockFormats = new(StringComparer.Ordinal);

    private bool CountCpu(string reason)
    {
        _cpuReasons[reason] = _cpuReasons.GetValueOrDefault(reason) + 1;
        return false;
    }

    /// <summary>DEV report lines: how many textures went up as blocks, and every reason one was decoded instead.</summary>
    public IEnumerable<string> BlockReport()
    {
        yield return $"{BlockTextures:N0} textures uploaded as the game's blocks = {BlockBytes / 1048576.0:N0} MB " +
                     $"({string.Join(", ", _blockFormats.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"))}), " +
                     $"{BlockMipsSkipped:N0} mips above the {MaxPreviewSize} cap skipped; decoded on the CPU instead: {_cpuReasons.Values.Sum():N0}";
        foreach (var (reason, n) in _cpuReasons.OrderByDescending(kv => kv.Value))
            yield return $"    {n,5:N0}  {reason}";
#if DEV
        if (VtChecked > 0) yield return $"    virtual levels checked against the CPU decode: {VtChecked:N0}, differing: {VtMismatched:N0}";
#endif
    }

    // What this context can take. Asked once, on the GL thread, the first time a block texture comes up.
    private bool _probed, _s3tc, _rgtc, _bptc;

    private void ProbeBlockFormats()
    {
        _probed = true;
        var version = gl.GetStringS(StringName.Version) ?? "";
        var es = version.StartsWith("OpenGL ES", StringComparison.Ordinal);
        var parsed = System.Text.RegularExpressions.Regex.Match(version, @"(\d+)\.(\d+)");
        int major = parsed.Success ? int.Parse(parsed.Groups[1].Value) : 0, minor = parsed.Success ? int.Parse(parsed.Groups[2].Value) : 0;
        var extensions = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < gl.GetInteger(GetPName.NumExtensions); i++)
            if (gl.GetStringS(StringName.Extensions, (uint) i) is { } name) extensions.Add(name);

        // S3TC is an extension everywhere. RGTC (BC4/BC5) is core from 3.0 and BPTC (BC7) from 4.2 on desktop GL, and an
        // extension otherwise. The swizzle the BC4 case needs is core from 3.3, which the shaders already require.
        _s3tc = extensions.Contains("GL_EXT_texture_compression_s3tc");
        _rgtc = (!es && major >= 3) || extensions.Contains("GL_ARB_texture_compression_rgtc") || extensions.Contains("GL_EXT_texture_compression_rgtc");
        _bptc = (!es && (major > 4 || (major == 4 && minor >= 2))) || extensions.Contains("GL_ARB_texture_compression_bptc") || extensions.Contains("GL_EXT_texture_compression_bptc");
        Console.WriteLine($"[textures] block upload: DXT {(_s3tc ? "yes" : "NO")}, BC4/BC5 {(_rgtc ? "yes" : "NO")}, BC7 {(_bptc ? "yes" : "NO")} (GL {version})");
    }

    // GL's COMPRESSED_RGBA_S3TC_DXT1/3/5_EXT, COMPRESSED_RED_RGTC1, COMPRESSED_RG_RGTC2 and COMPRESSED_RGBA_BPTC_UNORM, written as
    // numbers because the binding names each by its extension. The RGBA form of DXT1 on purpose: the CPU decode gives alpha 0 (and
    // black) to the format's "transparent" code and 255 to everything else, which is what the RGBA form does and the RGB form does not.
    private bool BlockFormat(EPixelFormat f, out InternalFormat glFormat, out int blockBytes)
    {
        glFormat = default;
        blockBytes = 0;
        switch (f)
        {
            case EPixelFormat.PF_DXT1: glFormat = (InternalFormat) 0x83F1; blockBytes = 8; return _s3tc;
            case EPixelFormat.PF_DXT3: glFormat = (InternalFormat) 0x83F2; blockBytes = 16; return _s3tc;
            case EPixelFormat.PF_DXT5: glFormat = (InternalFormat) 0x83F3; blockBytes = 16; return _s3tc;
            case EPixelFormat.PF_BC4:  glFormat = (InternalFormat) 0x8DBB; blockBytes = 8; return _rgtc;
            case EPixelFormat.PF_BC5:  glFormat = (InternalFormat) 0x8DBD; blockBytes = 16; return _rgtc;
            case EPixelFormat.PF_BC7:  glFormat = (InternalFormat) 0x8E8C; blockBytes = 16; return _bptc;
            default: return false;
        }
    }

    /// <summary>A mip whose header says it carries bytes, judged without reading them.</summary>
    private static bool HasPayload(FTexture2DMipMap m) =>
        m.BulkData is { } b && ((b.Header.SizeOnDisk > 0 && !b.BulkDataFlags.HasFlag(CUE4Parse.UE4.Assets.Objects.EBulkDataFlags.BULKDATA_Unused))
                                || b.Data is { Length: > 0 });

    private uint _building;   // the GL texture a block upload is filling, so a throw can delete it

    /// <summary>A block upload that threw (a package whose tables are not what they claim) is the CPU decode's to do, not a lost texture.</summary>
    private bool TryUploadBlocksSafely(UTexture texture, out uint handle)
    {
        try { return TryUploadBlocks(texture, out handle); }
        catch (Exception e)
        {
            if (_building != 0) { gl.BindTexture(TextureTarget.Texture2D, 0); gl.DeleteTexture(_building); _building = 0; }
            Console.WriteLine($"[textures] block upload of {texture.Name} threw {e.GetType().Name}: {e.Message}; decoding it instead");
            handle = 0;
            return CountCpu("the block upload threw " + e.GetType().Name);
        }
    }

    private unsafe bool TryUploadBlocks(UTexture texture, out uint handle)
    {
        handle = 0;
        if (!UploadCompressed || DumpDir is not null) return false;
#if DEV
        if (DecodeOnCpu) return false;
#endif
        if (texture is not UTexture2D || texture.PlatformData is not { } pd) return CountCpu("not a plain 2D texture");
        var vt = pd.VTData is { } v && pd.FirstMipToSerialize >= 0 && v.IsInitialized() ? v : null;
        if (vt is null && pd.Mips.Length == 0) return CountCpu("no mips");
        if (pd.PixelFormat.EndsWith("_crunched", StringComparison.OrdinalIgnoreCase)) return CountCpu("crunched");
        if (!_probed) ProbeBlockFormats();
        var format = vt is not null && vt.LayerTypes.Length > 0 ? vt.LayerTypes[0] : texture.Format;
        if (!BlockFormat(format, out var glFormat, out var blockBytes))
            return CountCpu($"format {format} (not block-compressed here, or this GL lacks it)");

        // THE TOP LEVEL, how many levels follow it, and the picture's full size (what the CPU decode reports: atlas detection and
        // the trim-mask check read it, so it is kept even when the top levels are above the cap and not uploaded).
        int fullW, fullH, w0, h0, maxLevels, aboveCap, topLevel;
        Func<int, byte[]?> fetch;
        _blockFailure = null;
        if (vt is not null)
        {
            if (!PlanVirtual(texture, vt, format, blockBytes, out fullW, out fullH, out w0, out h0, out maxLevels, out aboveCap, out fetch))
                return CountCpu(_blockFailure ?? "virtual texture");
            topLevel = aboveCap;
        }
        else
        {
            var mips = pd.Mips;
            fullW = fullH = aboveCap = 0;
            var start = -1;
            for (var i = 0; i < mips.Length; i++)
            {
                var m = mips[i];
                if (Math.Max(m.SizeX, m.SizeY) > MaxPreviewSize)
                {
                    if (fullW == 0 && HasPayload(m)) { fullW = m.SizeX; fullH = m.SizeY; }
                    aboveCap++;
                    continue;
                }
                if (texture.GetMip(i) is null) continue;
                if (fullW == 0) { fullW = m.SizeX; fullH = m.SizeY; }
                start = i;
                break;
            }
            if (start < 0) return CountCpu("no mip within the cap carries data");
            w0 = mips[start].SizeX;
            h0 = mips[start].SizeY;
            if (mips[start].SizeZ > 1) return CountCpu("volume or array slices");
            if (w0 % 4 != 0 || h0 % 4 != 0) return CountCpu("size not a multiple of 4");
            maxLevels = mips.Length - start;
            topLevel = start;
            var first = start;
            var wTop = w0;
            var hTop = h0;
            fetch = k =>
            {
                int w = Math.Max(1, wTop >> k), h = Math.Max(1, hTop >> k);
                return texture.GetMip(first + k) is { } mip && mip.SizeX == w && mip.SizeY == h ? mip.BulkData?.Data : null;
            };
        }

        for (var drained = 0; drained < 32 && gl.GetError() != GLEnum.NoError; drained++) { }   // an old error is not this upload's (bounded: a lost context answers an error forever)
        handle = gl.GenTexture();
        _building = handle;
        gl.BindTexture(TextureTarget.Texture2D, handle);
        gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);

        long bytes = 0;
        var levels = 0;
        for (var k = 0; k < maxLevels; k++)
        {
            int w = Math.Max(1, w0 >> k), h = Math.Max(1, h0 >> k);
            var need = (w + 3) / 4 * ((h + 3) / 4) * blockBytes;
            if (fetch(k) is not { } data || data.Length < need) break;
#if DEV
            if (CpuMips) { UploadDecodedLevel(texture, topLevel + k, k, w, h); bytes += (long) w * h * 4; levels++; continue; }
#endif
            fixed (byte* p = data)
                gl.CompressedTexImage2D(TextureTarget.Texture2D, k, glFormat, (uint) w, (uint) h, 0, (uint) need, p);
            bytes += need;
            levels++;
        }

        ShrinkScratch();

        // THE CHAIN MUST REACH THE SMALL END. A chain the package cuts short (a mip that is not carried) would sample its last
        // level at any distance, which is not the picture GenerateMipmap gives the decoded path: those are decoded instead.
        var err = gl.GetError();
        var last = Math.Max(Math.Max(1, w0 >> Math.Max(levels - 1, 0)), Math.Max(1, h0 >> Math.Max(levels - 1, 0)));
        if (levels == 0 || err != GLEnum.NoError || last > 4)
        {
            gl.BindTexture(TextureTarget.Texture2D, 0);
            gl.DeleteTexture(handle);
            handle = 0;
            _building = 0;
            return CountCpu(err != GLEnum.NoError ? $"GL error {err} on the block upload"
                          : _blockFailure is not null ? _blockFailure
                          : levels == 0 ? "the top mip's bytes are missing or short"
                          : "mip chain stops above 4x4");
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, levels - 1);
        SetSampling();
        if (format == EPixelFormat.PF_BC4)
        {
            // One channel, which the CPU decode spreads over red, green and blue with alpha 255.
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleR, (int) GLEnum.Red);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleG, (int) GLEnum.Red);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleB, (int) GLEnum.Red);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleA, (int) GLEnum.One);
        }
#if DEV
        if (VtCheck) CheckGpuDecode(texture, format, vt is not null, topLevel, levels, w0, h0);
#endif
        gl.BindTexture(TextureTarget.Texture2D, 0);
        _building = 0;

        _glBytes[handle] = bytes;
        Interlocked.Increment(ref _liveTextures);
        Interlocked.Increment(ref _uploadedTextures);
        Interlocked.Add(ref _liveTextureBytes, bytes);
        _sizes[handle] = (fullW, fullH);
        _formatOf[handle] = (format.ToString(), fullW, fullH);
        BlockTextures++;
        BlockBytes += bytes;
        BlockMipsSkipped += aboveCap;
        var name = vt is not null ? format + " (virtual)" : format.ToString();
        _blockFormats[name] = _blockFormats.GetValueOrDefault(name) + 1;
        return true;
    }

    // ---------------------------------------------------------------- virtual textures, assembled from blocks

    private byte[] _blockScratch = Array.Empty<byte>();
    private int _scratchPeak;       // the most the texture being uploaded needed of it

    /// <summary>
    /// The assembly buffer of a virtual texture grows to the largest level seen (up to 16 MB) and was kept for the life
    /// of the cache. Called after each block upload: a buffer over 4 MB that the texture just finished needed under half
    /// of is let go, so one 4096 map does not pin 16 MB through a batch of small ones.
    /// </summary>
    private void ShrinkScratch()
    {
        if (_blockScratch.Length > (4 << 20) && _scratchPeak * 2 < _blockScratch.Length) _blockScratch = Array.Empty<byte>();
        _scratchPeak = 0;
    }
    private string? _blockFailure;

    private static uint Part1By1(uint x)
    {
        x &= 0x0000ffff;
        x = (x | (x << 8)) & 0x00FF00FF;
        x = (x | (x << 4)) & 0x0F0F0F0F;
        x = (x | (x << 2)) & 0x33333333;
        x = (x | (x << 1)) & 0x55555555;
        return x;
    }

    /// <summary>
    /// A VIRTUAL TEXTURE AS BLOCKS, NOT PIXELS. Nine in ten of a level's textures are virtual here (SK_Anaxes_040: 271 of 301),
    /// stored as tiles of block-compressed data: 128 pixels square plus a border of 4 on every side, so every tile is a whole
    /// number of 4x4 blocks. The CPU decode (CUE4Parse's DecodeVT) decodes each tile to pixels and pastes its inside into one big
    /// picture; pasting the BLOCKS instead needs no decode at all, because the tile, its border and the picture's tiling all fall on
    /// block boundaries. The result is the same blocks the CPU decode would have decoded, in the same places, which the DEV
    /// self-check (<see cref="VtCheck"/>) proves by decoding them with the CPU decoder and comparing every pixel.
    ///
    /// The levels: the tiled ones (a 4096 picture is 32x32 tiles at level 0 down to 1x1 at level 5), and then the game stores each
    /// smaller level (64x64 ... 1x1) in the top-left corner of ONE tile, so a level's blocks are the corner of that tile's inside.
    /// The picture's size is the tile grid times the tile size (padding included), as the CPU decode reports it.
    ///
    /// Anything unusual goes to the CPU decode with its reason (<see cref="_blockFailure"/>): an older layout, several layers, a
    /// codec other than raw GPU data, a tile that is absent, or a level whose tile grid is not the halved picture's.
    /// </summary>
    private bool PlanVirtual(UTexture texture, FVirtualTextureBuiltData vt, EPixelFormat format, int blockBytes,
        out int fullW, out int fullH, out int w0, out int h0, out int maxLevels, out int aboveCap, out Func<int, byte[]?> fetch)
    {
        fullW = fullH = w0 = h0 = maxLevels = aboveCap = 0;
        fetch = _ => null;
        if (vt.IsLegacyData() || vt.NumLayers != 1 || vt.LayerTypes.Length != 1 || format != texture.Format
            || vt.TileOffsetData is null || vt.ChunkIndexPerMip is null || vt.BaseOffsetPerMip is null || vt.TileDataOffsetPerLayer is null)
        { _blockFailure = "virtual texture: older layout, several layers, or a layer format that is not the texture's"; return false; }
        int ts = (int) vt.TileSize, border = (int) vt.TileBorderSize;
        if (ts % 4 != 0 || border % 4 != 0) { _blockFailure = "virtual texture: tile or border size not a multiple of 4"; return false; }
        var levelsInData = (int) Math.Min(vt.NumMips, (uint) vt.TileOffsetData.Length);
        var top = levelsInData > 0 ? vt.GetTileOffsetData(0) : default;
        fullW = (int) top.Width * ts;
        fullH = (int) top.Height * ts;
        if (fullW <= 0 || fullH <= 0 || (long) fullW * fullH * 4 >= int.MaxValue / 2) { _blockFailure = "virtual texture: no size, or too big for one decode"; return false; }

        // The first level that fits the cap; the levels above it are skipped, as plain mips are.
        var first = 0;
        while (first < levelsInData && Math.Max(fullW >> first, fullH >> first) > MaxPreviewSize) first++;
        if (first >= levelsInData) { _blockFailure = "virtual texture: no level within the cap"; return false; }
        aboveCap = first;
        w0 = Math.Max(1, fullW >> first);
        h0 = Math.Max(1, fullH >> first);
        maxLevels = levelsInData - first;
        var fullWidth = fullW;
        var fullHeight = fullH;
        var packed = (ts / 4 + 2 * (border / 4)) * (ts / 4 + 2 * (border / 4)) * blockBytes;   // one tile's bytes, border included

        fetch = k =>
        {
            var level = first + k;
            int w = Math.Max(1, fullWidth >> level), h = Math.Max(1, fullHeight >> level);
            var grid = vt.GetTileOffsetData(level);
            int wantX = Math.Max(1, (w + ts - 1) / ts), wantY = Math.Max(1, (h + ts - 1) / ts);
            if (grid.Width != wantX || grid.Height != wantY)
            { _blockFailure = $"virtual texture: a level's tile grid is not the halved picture's"; return null; }
            int bw = (w + 3) / 4, bh = (h + 3) / 4, tileBlocks = ts / 4, edge = border / 4, side = tileBlocks + 2 * edge;
            var need = bw * bh * blockBytes;
            if (_blockScratch.Length < need) _blockScratch = new byte[need];
            _scratchPeak = Math.Max(_scratchPeak, need);
            for (var ty = 0; ty < wantY; ty++)
            for (var tx = 0; tx < wantX; tx++)
            {
                int ox = tx * tileBlocks, oy = ty * tileBlocks;
                if (ox >= bw || oy >= bh) continue;
                var address = Part1By1((uint) tx) | (Part1By1((uint) ty) << 1);
                if (!vt.IsValidAddress(level, address)) { _blockFailure = "virtual texture: a tile address is not valid"; return null; }
                var (chunkIndex, tileStart, _) = vt.GetTileData(level, address, 0);
                if (chunkIndex < 0 || chunkIndex >= vt.Chunks.Length || tileStart == ~0u) { _blockFailure = "virtual texture: a tile is absent"; return null; }
                var chunk = vt.Chunks[chunkIndex];
                if (chunk.CodecType.Length < 1 || chunk.CodecType[0] != EVirtualTextureCodec.RawGPU) { _blockFailure = "virtual texture: a codec other than raw GPU data"; return null; }
                if (chunk.BulkData.Data is not { } src || (long) tileStart + packed > src.Length) { _blockFailure = "virtual texture: a tile's bytes are missing or short"; return null; }
                int rows = Math.Min(tileBlocks, bh - oy), cols = Math.Min(tileBlocks, bw - ox);
                for (var j = 0; j < rows; j++)
                    System.Buffer.BlockCopy(src, (int) tileStart + ((edge + j) * side + edge) * blockBytes,
                                     _blockScratch, ((oy + j) * bw + ox) * blockBytes, cols * blockBytes);
            }
#if DEV
            if (VtCheck) CheckVirtualLevel(texture, level, k, w, h, bw, bh, format, _blockScratch);
#endif
            return _blockScratch;
        };
        return true;
    }

#if DEV
    /// <summary>
    /// DEV: <c>--cpumips</c> uploads the game's OWN mips (the ones the block upload sends) but decoded on the CPU, as RGBA8, level by
    /// level, with no GenerateMipmap. A picture taken with it separates the two things the block upload changes: against the block
    /// upload it shows only the GPU's block decode against the CPU's, and against <c>--cputex</c> it shows only the game's mip chain
    /// against the box filter.
    /// </summary>
    public static bool CpuMips;

    private unsafe void UploadDecodedLevel(UTexture texture, int level, int k, int w, int h)
    {
        var cpu = texture.DecodeMip(level) ?? throw new InvalidOperationException($"{texture.Name}: level {level} did not decode");
        var rgba = new byte[w * h * 4];
        var bgra = cpu.PixelFormat != EPixelFormat.PF_R8G8B8A8;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var from = (y * cpu.Width + x) * 4;
            var to = (y * w + x) * 4;
            rgba[to] = cpu.Data[from + (bgra ? 2 : 0)];
            rgba[to + 1] = cpu.Data[from + 1];
            rgba[to + 2] = cpu.Data[from + (bgra ? 0 : 2)];
            rgba[to + 3] = cpu.Data[from + 3];
        }
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            gl.TexImage2D(TextureTarget.Texture2D, k, InternalFormat.Rgba8, (uint) w, (uint) h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
    }

    /// <summary>DEV: <c>--vtcheck</c> decodes every virtual level this cache assembles with the CPU decoder and compares it, pixel for pixel, with the CPU decode of that level.</summary>
    public static bool VtCheck;

    /// <summary>What the decoded path really uploaded (level 0, read back) against the decoded pixels it was given: the Skia conversions in between must not change a byte.</summary>
    private unsafe void CheckCpuUpload(UTexture texture, uint handle, CUE4Parse_Conversion.Textures.CTexture decoded)
    {
        if (decoded.PixelFormat is not (EPixelFormat.PF_B8G8R8A8 or EPixelFormat.PF_R8G8B8A8 or EPixelFormat.PF_A8R8G8B8)) return;
        int w = decoded.Width, h = decoded.Height;
        var gpu = new byte[w * h * 4];
        gl.BindTexture(TextureTarget.Texture2D, handle);
        fixed (byte* p = gpu) gl.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        var bgra = decoded.PixelFormat != EPixelFormat.PF_R8G8B8A8;
        var sum = new double[4]; var max = new int[4];
        for (var i = 0; i < w * h; i++)
            for (var c = 0; c < 4; c++)
            {
                var cc = bgra ? (c == 3 ? 3 : 2 - c) : c;
                var d = Math.Abs(decoded.Data[i * 4 + cc] - gpu[i * 4 + c]);
                sum[c] += d; if (d > max[c]) max[c] = d;
            }
        Console.WriteLine($"[vtcheck] DECODED upload of {texture.Name} {texture.Format} {w}x{h}: against the CPU decode: mean R {sum[0] / (w * h):0.000} G {sum[1] / (w * h):0.000} B {sum[2] / (w * h):0.000} A {sum[3] / (w * h):0.000}; largest R {max[0]} G {max[1]} B {max[2]} A {max[3]}");
    }

    private byte[]? _checkPrev;
    private int _checkPrevW, _checkPrevH;
    public int VtChecked, VtMismatched;

    /// <summary>
    /// The GPU's block decode against the CPU's, on the texture just uploaded (bound): two levels of it (the first at 1024 or less, and
    /// the last of at least 4x4) are read back with glGetTexImage, which hands over the driver's decode as RGBA, and compared with
    /// CUE4Parse's decode of the same level, channel by channel. One line per level: the mean and largest difference of each channel.
    /// </summary>
    private unsafe void CheckGpuDecode(UTexture texture, EPixelFormat format, bool virtualTexture, int topLevel, int levels, int w0, int h0)
    {
        var picks = new List<int>();
        for (var k = 0; k < levels; k++)
            if (Math.Max(w0 >> k, h0 >> k) <= 1024) { picks.Add(k); break; }
        for (var k = levels - 1; k >= 0; k--)
            if (Math.Min(Math.Max(1, w0 >> k), Math.Max(1, h0 >> k)) >= 4) { if (!picks.Contains(k)) picks.Add(k); break; }
        foreach (var k in picks)
        {
            int w = Math.Max(1, w0 >> k), h = Math.Max(1, h0 >> k);
            var cpu = texture.DecodeMip(topLevel + k);
            if (cpu is null || cpu.Width < w || cpu.Height < h) continue;
            var gpu = new byte[w * h * 4];
            fixed (byte* p = gpu) gl.GetTexImage(TextureTarget.Texture2D, k, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            var bgra = cpu.PixelFormat == EPixelFormat.PF_B8G8R8A8;
            var sum = new double[4]; var max = new int[4];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 4; c++)
                {
                    var cc = bgra ? (c == 3 ? 3 : 2 - c) : c;
                    var d = Math.Abs(cpu.Data[(y * cpu.Width + x) * 4 + cc] - gpu[(y * w + x) * 4 + c]);
                    sum[c] += d; if (d > max[c]) max[c] = d;
                }
            var n = (double) (w * h);
            Console.WriteLine($"[vtcheck] GPU decode of {texture.Name} {format}{(virtualTexture ? " (virtual)" : "")} level {topLevel + k} {w}x{h} against the CPU decode: " +
                              $"mean R {sum[0] / n:0.000} G {sum[1] / n:0.000} B {sum[2] / n:0.000} A {sum[3] / n:0.000}; largest R {max[0]} G {max[1]} B {max[2]} A {max[3]}");
        }
    }

    /// <summary>
    /// The proof that the pasted blocks are the CPU decode's. Level <paramref name="level"/> is decoded twice: by CUE4Parse's own DecodeMip
    /// (what the CPU path draws) and from the blocks assembled here with the same block decoder. Every pixel of the level's picture
    /// must be equal, byte for byte. Beside it, for the levels below the first: the mean difference between this level and the previous
    /// level shrunk by two, which is small for a real mip chain and large if the picture is not where the corner of the tile was assumed.
    /// </summary>
    private void CheckVirtualLevel(UTexture texture, int level, int k, int w, int h, int bw, int bh, EPixelFormat format, byte[] blocks)
    {
        int aw = bw * 4, ah = bh * 4;
        var mine = blocks.AsSpan(0, bw * bh * (format is EPixelFormat.PF_DXT1 or EPixelFormat.PF_BC4 ? 8 : 16)).ToArray();
        byte[]? pixels = null;
        if (aw >= 4 && ah >= 4)
        {
            try
            {
                pixels = format switch
                {
                    EPixelFormat.PF_DXT1 => CUE4Parse_Conversion.Textures.DXT.DXTDecoder.DXT1(mine, aw, ah, 1),
                    EPixelFormat.PF_DXT5 => CUE4Parse_Conversion.Textures.DXT.DXTDecoder.DXT5(mine, aw, ah, 1),
                    EPixelFormat.PF_BC5 => CUE4Parse_Conversion.Textures.BC.BCDecoder.BC5(mine, aw, ah, 1),
                    EPixelFormat.PF_BC4 => CUE4Parse_Conversion.Textures.BC.BCDecoder.BC4(mine, aw, ah, 1),
                    EPixelFormat.PF_BC7 => CUE4Parse_Conversion.Textures.BC.DetexHelper.DecodeDetexLinear(mine, aw, ah, false,
                        CUE4Parse_Conversion.Textures.BC.DetexTextureFormat.DETEX_TEXTURE_FORMAT_BPTC,
                        CUE4Parse_Conversion.Textures.BC.DetexPixelFormat.DETEX_PIXEL_FORMAT_BGRA8),
                    _ => null,
                };
            }
            catch (Exception e) { Console.WriteLine($"[vtcheck] {texture.Name} level {level}: block decode failed: {e.Message}"); }
        }
        var cpu = texture.DecodeMip(level);
        if (pixels is not null && cpu is not null && cpu.Width >= w && cpu.Height >= h)
        {
            // BC5: the CPU decode rebuilds blue from red and green, which the block decoder does not; compare red and green only.
            var channels = format == EPixelFormat.PF_BC5 ? new[] { 1, 2 } : new[] { 0, 1, 2, 3 };
            long bad = 0;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                foreach (var c in channels)
                    if (cpu.Data[(y * cpu.Width + x) * 4 + c] != pixels[(y * aw + x) * 4 + c]) bad++;
            VtChecked++;
            if (bad > 0) { VtMismatched++; Console.WriteLine($"[vtcheck] {texture.Name} level {level} {w}x{h}: {bad:N0} bytes DIFFER from the CPU decode"); }

            // The corner assumption: this level against the previous level shrunk by two (RGB only).
            if (k > 0 && _checkPrev is { } prev && format != EPixelFormat.PF_BC5 && format != EPixelFormat.PF_BC4)
            {
                double sum = 0; long n = 0;
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    for (var c = 0; c < 3; c++)
                    {
                        int px = 2 * x, py = 2 * y;
                        if (px + 1 >= _checkPrevW || py + 1 >= _checkPrevH) continue;
                        var avg = (prev[(py * _checkPrevW + px) * 4 + c] + prev[(py * _checkPrevW + px + 1) * 4 + c]
                                 + prev[((py + 1) * _checkPrevW + px) * 4 + c] + prev[((py + 1) * _checkPrevW + px + 1) * 4 + c]) / 4.0;
                        sum += Math.Abs(avg - pixels[(y * aw + x) * 4 + c]);
                        n++;
                    }
                Console.WriteLine($"[vtcheck] {texture.Name} level {level} {w}x{h}: {(bad == 0 ? "identical to the CPU decode" : "DIFFERS")}; against the previous level shrunk by 2: mean difference {(n > 0 ? sum / n : 0):0.00}");
            }
            else Console.WriteLine($"[vtcheck] {texture.Name} level {level} {w}x{h}: {(bad == 0 ? "identical to the CPU decode" : "DIFFERS")}");
        }
        _checkPrev = pixels;
        _checkPrevW = aw;
        _checkPrevH = ah;
    }
#endif

    /// <summary>
    /// A PNG or TGA on disk -- the author's own artwork, for a thumbnail beside the field that
    /// names it. Decoded by ImageSharp, which is what the build encodes it with, so what the
    /// Studio shows is what the game will get. Keyed apart from game textures so a file and an
    /// asset with the same name never share a handle. GL work: render thread.
    /// </summary>
    public (uint Handle, int Width, int Height) UploadFile(string path)
    {
        var key = "file:" + path;
        if (_cache.TryGetValue(key, out var existing))
            return (existing, _fileSizes.GetValueOrDefault(existing).Width, _fileSizes.GetValueOrDefault(existing).Height);
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(path);
            var rgba = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(rgba);

            var info = new SkiaSharp.SKImageInfo(image.Width, image.Height,
                                                 SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
            using var bitmap = new SkiaSharp.SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);

            var handle = Upload(Downscale(bitmap, out var scaled), scaled);
            _cache[key] = handle;
            _fileSizes[handle] = (image.Width, image.Height);
            return (handle, image.Width, image.Height);
        }
        catch (Exception e)
        {
            _failed.Add($"{Path.GetFileName(path)}: {e.Message}");
            _cache[key] = 0;
            return (0, 0, 0);
        }
    }

    private readonly Dictionary<uint, (int Width, int Height)> _fileSizes = new();

    private SkiaSharp.SKBitmap Downscale(SkiaSharp.SKBitmap src, out SkiaSharp.SKBitmap? owned)
    {
        owned = null;
        var longest = Math.Max(src.Width, src.Height);
        if (longest <= MaxPreviewSize) return src;

        var f = (float) MaxPreviewSize / longest;
        var w = Math.Max(1, (int) (src.Width * f));
        var h = Math.Max(1, (int) (src.Height * f));
        // Mitchell cubic, named outright: it is what SKFilterQuality.High meant under SkiaSharp 2.x,
        // and 3.x marks that enum obsolete, so leaving it implicit would leave the choice to a shim.
        // Measured: resizing a BGRA bitmap with unpremultiplied alpha keeps every colour byte exactly,
        // including under alpha 0, 10 and 100 on an 8192x4096 source -- Skia does not premultiply here, so the resize needs
        // no byte swap first the way Upload's conversion does.
        owned = src.Resize(new SkiaSharp.SKImageInfo(w, h, src.ColorType, src.AlphaType),
            new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
        return owned ?? src;
    }

    /// <summary>Repeat, trilinear: the sampler state of the bound texture, the same for a decoded upload and a block upload.</summary>
    private void SetSampling()
    {
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int) GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int) GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int) GLEnum.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int) GLEnum.Linear);
    }

    /// <summary>A BGRA bitmap as RGBA with every byte kept: only red and blue change places.</summary>
    private static unsafe SkiaSharp.SKBitmap SwapRedBlue(SkiaSharp.SKBitmap source)
    {
        var info = new SkiaSharp.SKImageInfo(source.Width, source.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        var swapped = new SkiaSharp.SKBitmap(info);
        for (var y = 0; y < source.Height; y++)
        {
            var from = (uint*) ((byte*) source.GetPixels() + (long) y * source.RowBytes);
            var to = (uint*) ((byte*) swapped.GetPixels() + (long) y * swapped.RowBytes);
            for (var x = 0; x < source.Width; x++)
            {
                var p = from[x];
                to[x] = ((p & 0xFFu) << 16) | (p & 0xFF00u) | ((p >> 16) & 0xFFu) | (p & 0xFF000000u);
            }
        }
        return swapped;
    }

    private unsafe uint Upload(SkiaSharp.SKBitmap source, SkiaSharp.SKBitmap? owned)
    {
        using var _ = owned;
        // Force a known layout rather than trusting the platform default. BGRA is swapped byte for byte: Skia's own Copy to RGBA
        // went through a premultiplied form, which turns the colour of every texel whose alpha is 0 into black and darkens the
        // partly transparent ones. A BC7 normal map the game cooked with alpha 0 reached the GPU as an all-zero texture that way.
        using var rgba = source.ColorType == SkiaSharp.SKColorType.Rgba8888
            ? null
            : source.ColorType == SkiaSharp.SKColorType.Bgra8888 ? SwapRedBlue(source) : source.Copy(SkiaSharp.SKColorType.Rgba8888);
        var bitmap = rgba ?? source;

        var handle = gl.GenTexture();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, handle);

            // SKBitmap rows can be padded. Uploading as if tightly packed shears the image
            // progressively down the texture, which looks like smeared streaks on the model.
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            gl.PixelStore(PixelStoreParameter.UnpackRowLength, bitmap.RowBytes / 4);

            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint) bitmap.Width, (uint) bitmap.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, (void*) bitmap.GetPixels());

            gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);

            gl.GenerateMipmap(TextureTarget.Texture2D);
            SetSampling();
            gl.BindTexture(TextureTarget.Texture2D, 0);
        }
        catch
        {
            // A throw between the name and the count would orphan the texture, uncounted: it goes here.
            try { gl.BindTexture(TextureTarget.Texture2D, 0); gl.DeleteTexture(handle); } catch { /* the original failure is the one to report */ }
            throw;
        }

        // The estimate (see _glBytes): the level-0 RGBA8 image and a third again for its mip chain.
        var estimate = (long) bitmap.Width * bitmap.Height * 4 * 4 / 3;
        _glBytes[handle] = estimate;
        Interlocked.Increment(ref _liveTextures);
        Interlocked.Increment(ref _uploadedTextures);
        Interlocked.Add(ref _liveTextureBytes, estimate);
        return handle;
    }

    /// <summary>
    /// Resolve a material's diffuse/normal/ORME using the same lookup CUE4Parse export uses,
    /// so what you see here matches what gets exported.
    /// </summary>
    /// <summary>Names of the textures actually bound, for diagnostics.</summary>
    public readonly List<string> BoundLog = new();

    /// <summary>
    /// Does this material draw its back faces? Instances do not restate the flag, so the parent
    /// chain has to be walked. Reading only the instance reports every material as one-sided, and
    /// in some games every bound material is an instance.
    /// </summary>
    private static bool ResolveTwoSided(UUnrealMaterial? mat)
    {
        // Follow the real Parent property, not a name lookup: the flag lives on the concrete
        // UMaterial at the end of the chain, and in some games every bound material is an instance.
        for (var depth = 0; mat is not null && depth < 8; depth++)
        {
            if (mat is UMaterial concrete) return concrete.TwoSided;
            if (mat is UMaterialInstance instance) { mat = instance.Parent; continue; }
            return false;
        }
        return false;
    }

    /// <summary>How many sections came back two-sided, so "none" is distinguishable from "unread".</summary>
    public int TwoSidedCount { get; private set; }

    /// <summary>
    /// WHAT THE VIEWPORT'S TEXTURE CHOICE CAME TO FOR ONE MATERIAL, as objects rather than GL handles.
    ///
    /// <see cref="BaseColor"/>, <see cref="Normal"/> and <see cref="Masks"/> are the three textures
    /// <see cref="BindMaterial"/> uploads -- FirstLayer, the AllLayers widening, the named merge, and
    /// <c>Resolve</c> with the diffuse / normal / mask name lists and their fallbacks -- and
    /// <see cref="Params"/> is the final <see cref="CMaterialParams2"/> it resolved them from, which is
    /// also what every later bind step (trim, emissive, decal, tint...) reads. Anything that has to
    /// agree with the picture -- the level's FBX export -- takes the choice from here and does not
    /// re-derive it: the exporter and the viewport once measured different maps that way.
    /// </summary>
    /// <remarks>
    /// The three <c>...Via</c> members carry the phrase the binder's log prints for each pick
    /// (<c>NAME via 'Param' type=... imported=WxH</c>, <c>NAME[fallback]</c> or <c>none</c>). They are
    /// additive: the positional shape of the record is the contract.
    /// </remarks>
    public sealed record MaterialChoice(UTexture? BaseColor, UTexture? Normal, UTexture? Masks,
                                        CMaterialParams2 Params)
    {
        /// <summary>How the base colour was picked: the log phrase, or <c>none</c>.</summary>
        public string BaseColorVia { get; init; } = "none";

        /// <summary>How the normal map was picked: the log phrase, or <c>none</c>.</summary>
        public string NormalVia { get; init; } = "none";

        /// <summary>How the mask was picked: the log phrase, or <c>none</c>.</summary>
        public string MasksVia { get; init; } = "none";
    }

    /// <summary>
    /// The viewport's texture choice for a material, with NO GL and NO state of this cache: a static
    /// method, so it can run on any thread that may read the material, with no context current.
    /// Null when the material's own parameters cannot be read at all (what <see cref="BindMaterial"/>
    /// has always treated as "leave the section as it is").
    ///
    /// <see cref="BindMaterial"/> calls this and then uploads what it returns; it chooses nothing
    /// itself, so the picture and anything built from this cannot disagree.
    /// </summary>
    public static MaterialChoice? Choose(UMaterialInterface material)
    {
        // FirstLayer only exposes the instance's own parameters. Some materials keep their
        // maps in the parent's referenced textures, which only AllLayers pulls in — so fall
        // back to it rather than showing an untextured mesh.
        var p = new CMaterialParams2();
        try { material.GetParams(p, EMaterialFormat.FirstLayer); }
        catch { return null; }

        if (p.Textures.Count == 0 || !HasNamedDiffuse(p))
        {
            var wider = new CMaterialParams2();
            try
            {
                material.GetParams(wider, EMaterialFormat.AllLayers);
                // Only take the wider result WHOLE if it produces a NAMED diffuse. AllLayers also
                // enables a regex fallback that happily binds any referenced texture --
                // it put a rusty metal sheet on a character's arm. A wrong texture is worse
                // than none, because it looks plausible.
                if (HasNamedDiffuse(wider)) p = wider;

                // OTHERWISE TAKE THE ALBEDO AND NOTHING ELSE.
                //
                // PM_Diffuse is this code's own declared FallbackDiffuse -- Resolve binds it
                // without any condition -- so a material carrying its base colour there was being
                // thrown away by a gate that only asked about the named list. Measured on Umbara:
                // the missing NAME below fixes 998 grey sections and this fixes a further 94.
                //
                // Copying the single entry rather than adopting the whole set is the point. None
                // of the regex fallback's other guesses come across, so this cannot put the wrong
                // texture on anything: the only thing gained is a parameter the binder already
                // trusted by name.
                else if (wider.Textures.TryGetValue(CMaterialParams2.FallbackDiffuse, out var packed) &&
                         packed is UTexture &&
                         !p.Textures.ContainsKey(CMaterialParams2.FallbackDiffuse))
                    p.Textures[CMaterialParams2.FallbackDiffuse] = packed;
            }
            catch { /* keep the narrower result */ }
        }

        // THE FAMILY EXTRAS LIVE IN AllLayers, AND `p` IS FirstLayer.
        //
        // This is why MM_Rocks' projection bound nothing. Six of Dantooine's nine MM_Rocks
        // instances set `Projected Texture` true and name a real `Projected Color`, and the
        // binder resolved zero of them: `p` comes from `GetParams(FirstLayer)` and is only
        // widened when the narrow set has no named diffuse -- which a tree trunk plainly does.
        // The trim sheet works only because `RGB Mask VT` happens to sit in the first layer.
        //
        // MERGED BY EXACT NAME, never wholesale. The reason `p` is not simply AllLayers is
        // recorded above: that format enables a regex fallback which binds any referenced
        // texture to the diffuse, and it once put a rusty metal sheet on a character's arm. Naming
        // the parameters means nothing can arrive here by guess -- these are keys a material
        // either has or does not.
        MergeNamedFromAllLayers(material, p);

        var diffuse = PickTexture(p, DiffuseNames, CMaterialParams2.FallbackDiffuse, IsPackedMap);
        var normal = PickTexture(p, NormalNames, CMaterialParams2.FallbackNormals);
        var masks = PickTexture(p, MaskNames, CMaterialParams2.FallbackSpecularMasks);
        return new MaterialChoice(diffuse.Texture, normal.Texture, masks.Texture, p)
        {
            BaseColorVia = diffuse.Via,
            NormalVia = normal.Via,
            MasksVia = masks.Via,
        };
    }

    public void BindMaterial(RenderSection section)
    {
        if (section.Material is null) return;
        section.TwoSided = ResolveTwoSided(section.Material);
        section.Family = FamilyOf(section.Material);
        if (section.TwoSided) TwoSidedCount++;

        // THE CHOICE IS MADE BY Choose, WITHOUT GL; THIS ONLY UPLOADS IT. Diffuse, then normal, then
        // mask, in the order the binder has always uploaded them.
        if (Choose(section.Material) is not { } choice) return;
        var p = choice.Params;

        section.Diffuse = GetOrUpload(choice.BaseColor);
        section.DiffusePath = choice.BaseColor?.GetPathName() ?? "";
        var dn = choice.BaseColorVia;
        section.NormalMap = GetOrUpload(choice.Normal, normalMap: true);
        var nn = choice.NormalVia;
        section.Orme = GetOrUpload(choice.Masks);
        var on = choice.MasksVia;

        // THIS GAME'S MASK HAS NO OCCLUSION, so say so rather than letting red be read as one.
        // Decided on the NAME THAT MATCHED, which Resolve reports, not on a filename suffix --
        // guessing from a suffix is how a rusty metal sheet ended up on a character's arm.
        if (on.Contains($"via '{ZeroCompanyMask}'", StringComparison.Ordinal))
            section.MaskHasAo = false;
        BoundLog.Add($"{section.MaterialName}: D={dn} N={nn} ORME={on}");

        // WHEN THE MASK DID NOT RESOLVE, SAY WHAT THE MATERIAL ACTUALLY OFFERS.
        //
        // A material with no mask is indistinguishable IN THE PICTURE from one whose mask failed
        // to bind: both leave the shader on ao=1, rough=0.6, metal=0, and a surface-channel view
        // of those constants looks exactly like a mode that works. Naming the parameters the
        // material really has is the difference between reading the answer and guessing it — and
        // guessing one from a filename suffix is how a rusty metal sheet ended up on a character's
        // arm.
        if (on == "none" && p.Textures.Count > 0)
            BoundLog.Add($"{section.MaterialName}: NO MASK. offers = " +
                         string.Join(", ", p.Textures.Keys.OrderBy(k => k, StringComparer.Ordinal)));

        // THE DECAL'S SHAPE IS ITS ALPHA, and we were drawing the whole quad.
        //
        // MM_Decals cooks as Translucent and `Use BaseColor Texture Alpha` is set on 78 of 200
        // sampled instances -- the material saying outright that the texture's alpha is the
        // opacity. With no opacity path in the mesh shader, 440 decal quads in the hangar alone
        // rendered as solid rectangles of their own texture.
        //
        // AN ALPHA TEST, NOT BLENDING. A cutout needs no sort order; true translucency needs the
        // whole scene depth-sorted per frame, which this renderer does not do and which would be
        // a worse lie than the rectangles -- unsorted blending picks a different winner every
        // time the camera moves. The clip value is UE's own default of 0.333; an instance can
        // override it through BasePropertyOverrides, which is not read here and is noted rather
        // than guessed at.
        //
        // THE RULE IS LevelFbxExport.IsCutOut, ONE copy shared with the level FBX export: Translucent and the
        // switch cut, Masked cuts except on the character / weapon / eye masters, whose base colour alpha is a
        // packed paint mask (Rex's helmet was pierced by it; a head vanished). Decided by a survey of every
        // material the game ships; LevelFbxExport.IsCutOut says what it measured.
        section.AlphaCut = UEToolkit.Core.LevelFbxExport.IsCutOut(section.Material, p);

        BindDecalMask(section, p);

        MeasureEmissive(section, p);
        BindEmissive(section, p);

        // THE MATERIAL'S OWN TINT MASK. Rendered through now, and by default -- see
        // RenderSection.TintOwn for the phase 2 visor this was hiding.
        section.TintMaskName = p.Textures.TryGetValue("Tint Mask VT", out var tint)
            ? tint?.Name ?? "" : "";

        // AND THE MASK ITSELF, uploaded, so something can actually paint through it.
        //
        // The NAME alone was enough while nothing rendered tints; a picker that shows the game's
        // colours on the model needs the texture. Uploading it costs nothing when no tint is
        // active — Draw binds unit 4 only when a tint is passed in — and GetOrUpload dedupes, so
        // the one mask a whole outfit shares is decoded once.
        section.TintMask = tint is UTexture tintTex ? GetOrUpload(tintTex) : 0;

        // AND THE COLOURS IT PAINTS THROUGH THAT MASK WITH NOBODY CHOOSING ANYTHING.
        //
        // These are what the material LOOKS LIKE, not a customisation of it: on the phase 2 clone
        // helmet Color 03 is 000000 and the mask's blue channel is the visor, which is why that
        // visor is black in the game and was pale grey here. Gated on the material's own
        // `Use Tint` switch so a material that does not tint itself is untouched.
        section.TintOwn = section.TintMask != 0
                       && p.TryGetSwitch(out var useTint, "Use Tint") && useTint;
        if (section.TintOwn)
        {
            section.TintC1 = Raw(p, "Color 01", System.Numerics.Vector3.One);
            section.TintC2 = Raw(p, "Color 02", System.Numerics.Vector3.One);
            section.TintC3 = Raw(p, "Color 03", System.Numerics.Vector3.One);
        }

        BindTrim(section, p);
        BindDetailNormal(section, p);
        BindUvTint(section, p);
        BindEmissiveSheet(section, p);
        BindProjection(section, p);
        BindFuzz(section, p);

        // When nothing binds, the parameter names are the evidence needed to fix it.
        if (section.Diffuse == 0)
        {
            var names = p.Textures.Keys.OrderBy(x => x).ToArray();
            BoundLog.Add(names.Length == 0
                ? $"    {section.MaterialName}: material exposes NO textures at all"
                : $"    {section.MaterialName}: unmatched params = {string.Join(", ", names.Take(14))}");
        }
        ResolveUvTransform(section);
    }

    private readonly Dictionary<uint, (int W, int H)> _sizes = new();
    private readonly Dictionary<uint, float[]> _noise = new();

    /// <summary>
    /// Per-tile high-frequency energy, used to spot regions that decoded as garbage.
    ///
    /// These are UE virtual textures, which CUE4Parse assembles from tiles. On a handful of
    /// them a contiguous block of the image comes back as dense repeating noise instead of
    /// art, and nothing throws -- so a mesh silently samples rubbish. Every tile address
    /// reports valid, so it is an assembly fault rather than missing data; the mechanism is
    /// not pinned down yet. Authored art is far smoother than that noise, which makes mean
    /// adjacent-pixel difference a good enough separator to at least flag it.
    /// </summary>
    private static float[] TileNoise(SkiaSharp.SKBitmap bmp)
    {
        var cols = Math.Max(1, bmp.Width / TileSize);
        var rows = Math.Max(1, bmp.Height / TileSize);
        var outp = new float[cols * rows];
        var tw = bmp.Width / cols;
        var th = bmp.Height / rows;
        const int step = 16;   // sampling, not a full scan -- these are up to 8K images

        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            long sum = 0; var n = 0;
            var x0 = c * tw; var y0 = r * th;
            for (var y = y0; y < y0 + th - step; y += step)
            for (var x = x0; x < x0 + tw - step; x += step)
            {
                var p = bmp.GetPixel(x, y);
                var px = bmp.GetPixel(x + step, y);
                var py = bmp.GetPixel(x, y + step);
                sum += Math.Abs(p.Red - px.Red) + Math.Abs(p.Green - px.Green) + Math.Abs(p.Blue - px.Blue)
                       + Math.Abs(p.Red - py.Red) + Math.Abs(p.Green - py.Green) + Math.Abs(p.Blue - py.Blue);
                n++;
            }
            outp[r * cols + c] = n > 0 ? sum / (6f * n) : 0f;
        }
        return outp;
    }

    /// <summary>Highest per-tile noise among the tiles a section's UVs actually touch.</summary>
    public float SectionNoise(RenderSection section)
    {
        if (section.Diffuse == 0 || !_noise.TryGetValue(section.Diffuse, out var tiles)) return 0f;
        var cols = (int) section.Udim.X;
        var rows = (int) section.Udim.Y;
        if (cols <= 0 || rows <= 0 || tiles.Length != cols * rows) return tiles.Length > 0 ? tiles.Max() : 0f;

        var worst = 0f;
        for (var tv = (int) MathF.Floor(section.VMin); tv <= (int) MathF.Floor(section.VMax); tv++)
        for (var tu = (int) MathF.Floor(section.UMin); tu <= (int) MathF.Floor(section.UMax); tu++)
        {
            var imgRow = (rows - 1) - (tv - (int) section.Udim.Z);
            if (tu < 0 || tu >= cols || imgRow < 0 || imgRow >= rows) continue;
            worst = MathF.Max(worst, tiles[imgRow * cols + tu]);
        }
        return worst;
    }

    /// <summary>
    /// CUE4Parse's diffuse names, PLUS the ones this game uses that its list does not carry.
    ///
    /// Kept here rather than patched into CMaterialParams2 on purpose. That clone is a detached
    /// HEAD with no branch, so an edit there is silently discarded by any re-clone or checkout --
    /// exactly that happened once, and the symptom looked like an art bug. A list of our own
    /// survives.
    ///
    /// "Base Color/Alpha VT" is Zero Company's name for a plain 4K albedo:
    /// MI_REP_MetalPrimerB_DkGry_RGBTrim carries UNI_MetalPrimerC_DIF under it. CUE4Parse already
    /// knows "Base Color VT" and "Base Color Map VT" and simply never met this spelling.
    /// </summary>
    /// <summary>
    /// THE DIFFUSE, BY EVERY NAME THIS GAME USES FOR IT.
    ///
    /// `BaseColor + Opacity Texture` is MM_Decals' own name for its albedo, and leaving it out
    /// was not merely incomplete -- it bound the WRONG TEXTURE. `MI_UNI_DecalGlassBrokenC` sets
    /// `BaseColor + Opacity Texture` to Decal_GlassBroken_03_C and also carries a `PM_Diffuse` of
    /// Decal_GlassBroken_**01**_C, and PM_Diffuse is this binder's declared fallback. So every
    /// decal in the game wore a different decal's artwork, plausibly enough that nobody could
    /// see it was the wrong one. MM_Decals is 10-11% of the sections drawn in a level.
    ///
    /// Same shape as the repaint that shipped wearing its donor's texture: a name absent from a
    /// list, and a fallback close enough to look deliberate.
    /// </summary>
    private static readonly string[] DiffuseNames =
        CMaterialParams2.Diffuse[0]
            .Append("Base Color/Alpha VT")
            .Append("BaseColor + Opacity Texture")
            .ToArray();

    /// <summary>
    /// Normals, plus MM_Decals' `Normal Texture`. Without it a decal resolved `PM_Normals`, which
    /// on that parent is a 16x16 `DefaultMSKNormal` placeholder -- so every decal was flat while
    /// its own 2048 normal map sat unbound beside it.
    /// </summary>
    private static readonly string[] NormalNames =
        CMaterialParams2.Normals[0].Append("Normal Texture").ToArray();

    /// <summary>
    /// Zero Company's mask parameter. `... VT` is this game's whole naming family — the tint
    /// system already reaches its companion, `Tint Mask VT`, by the same convention.
    /// </summary>
    public const string ZeroCompanyMask = "Base Mask 1 VT";

    /// <summary>
    /// The specular/ORME names, plus the one above.
    ///
    /// WITHOUT IT THE MASK WAS NEVER BOUND AT ALL. `MI_TORS` reported
    /// `D=..[fallback] N=..[fallback] ORME=none` while `T_Clo001_TORS_MSK` sat in the same
    /// material at 4096x4096, and the shader ran on ao=1, rough=0.6, metal=0 — a hardcoded
    /// roughness on every character in the game. CUE4Parse's own list does not carry the name
    /// and its regex fallback is not reached from here on purpose.
    ///
    /// Appending a NAME is the established move in this file: `Base Color/Alpha VT`,
    /// `BaseColor + Opacity Texture` and `Normal Texture` are all here for the same reason. A
    /// name is a key a material either has or does not, so nothing can arrive by guess.
    ///
    /// NOT ADDED: `Base BSC VT` and `Base N VT`, this material's named colour and normal. They
    /// already resolve to the right textures through the `PM_` parent fallback, so adding them
    /// would change a route that is currently producing the correct answer. Noted because the
    /// comment on DiffuseNames warns about exactly this shape — "a name absent from a list, and
    /// a fallback close enough to look deliberate" — and it is worth revisiting deliberately
    /// rather than as a side effect of this change.
    /// </summary>
    private static readonly string[] MaskNames =
        CMaterialParams2.SpecularMasks[0].Append(ZeroCompanyMask).ToArray();

    /// <summary>
    /// IS THERE A DIFFUSE IN HERE, BY NAME?
    ///
    /// Deliberately still a NAMED test. Widening it to accept the PM_Diffuse fallback would mean
    /// keeping the whole AllLayers set far more often, and AllLayers is where CUE4Parse's regex
    /// fallback lives -- the one that put a rusty metal sheet on a character's arm.
    ///
    /// Measured rather than argued, because the first version of this comment guessed and was
    /// wrong: on Umbara the missing NAME below fixes 998 of 1,115 grey sections, and the
    /// PM_Diffuse case is a further 94. So it IS worth having -- just not by swallowing the set.
    /// BindMaterial copies that one entry across instead.
    /// </summary>
    private static bool HasNamedDiffuse(CMaterialParams2 p) =>
        p.TryGetTexture2d(out _, DiffuseNames);

    /// <summary>
    /// Work out how this section's UVs address its texture.
    ///
    /// The key point: the tile grid belongs to the TEXTURE, not to the mesh. A glove mesh
    /// may use only tile 1 of an 8x2 atlas (U 1.004..1.994) — measuring the mesh's own span
    /// would call that "one tile" and sample raw UVs, which wrap to 0..1 and stretch the
    /// whole atlas over the glove.
    ///
    /// So rows come from the mesh's V extent, and columns from the texture's aspect ratio
    /// assuming square tiles (a 2-row, 4096x1024 map at 4:1, therefore 8
    /// columns). If U then runs past the column count the UVs really are tiling, and are
    /// left alone.
    /// </summary>
    /// <summary>
    /// V coordinate of the top edge of an atlas. Assumed to be 1 — check the per-section
    /// audit (vMax column) before trusting it for another game.
    /// </summary>
    public static float VTop = 1f;

    /// <summary>
    /// Edge length of one atlas tile. These are usually authored at 1024; smaller textures are whole
    /// atlases scaled down, so their tile is smaller and falls out of the gcd instead.
    /// </summary>
    public static int TileSize = 1024;

    /// <summary>
    /// Tile grid of a NON-SQUARE atlas texture, assuming square tiles. A non-square texture is
    /// itself the evidence that it is an atlas, which is what lets a part sitting in tile (0,0)
    /// -- UVs in 0..1, indistinguishable from an ordinary mesh -- still get the right grid.
    ///
    /// Square textures are genuinely ambiguous and are resolved from the UVs instead.
    /// </summary>
    public static (float Cols, float Rows) TileGrid(int w, int h)
    {
        if (w <= 0 || h <= 0 || w == h) return (1f, 1f);
        var tile = w % TileSize == 0 && h % TileSize == 0 ? TileSize : Gcd(w, h);
        if (tile <= 0) return (1f, 1f);
        return (w / (float) tile, h / (float) tile);
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    /// <summary>
    /// The tile grid that actually contains this section's UVs, keeping tiles SQUARE.
    ///
    /// <see cref="TileSize"/> = 1024 is the usual authoring size but it is NOT universal:
    /// one body atlas is 4096x1024 holding 8x2 tiles of 512. Assuming 1024 there gives
    /// a 4x1 grid, the section's U extent of 8 does not fit inside it, and the atlas is
    /// abandoned as "tiling" -- which samples the whole 4:1 map across every part and smears
    /// it into vertical streaks.
    ///
    /// So start at exactly the tile <see cref="TileGrid"/> would pick and halve from there.
    /// Starting there matters: the first candidate is always today's answer, so every mesh
    /// that already resolves correctly is untouched and this can only ever SHRINK the tile.
    /// Searching upward as well would hand a 4096x2048 atlas a 2x1 grid where 4x2 works today.
    ///
    /// Squareness is the check that keeps it honest: a genuinely tiling mesh has no square
    /// grid that fits, and falls through to the caller's tiling path.
    /// </summary>
    private static (float Cols, float Rows) FitGrid(int w, int h,
        float uMin, float uMax, float vMin, float vMax)
    {
        const float slack = 0.05f;
        var first = w % TileSize == 0 && h % TileSize == 0 ? TileSize : Gcd(w, h);
        for (var tile = first; tile >= 64; tile /= 2)
        {
            if (w % tile != 0 || h % tile != 0) continue;
            float cols = w / (float) tile, rows = h / (float) tile;
            if (uMax <= cols + slack && uMin >= -slack &&
                vMax <= VTop + slack && vMin >= VTop - rows - slack)
                return (cols, rows);
        }
        return TileGrid(w, h);
    }

    /// <summary>
    /// How many sections got their grid from the pooled table rather than from their own UVs.
    /// Zero when no library is loaded, which is the difference between "the fix is on" and
    /// "the fix is compiled in and doing nothing".
    /// </summary>
    public int PooledGridCount { get; private set; }

    private void ResolveUvTransform(RenderSection section)
    {
        const float slack = 0.05f;
        float uMin = section.UMin, uMax = section.UMax;
        float vMin = section.VMin, vMax = section.VMax;

        if (section.Diffuse != 0 && _sizes.TryGetValue(section.Diffuse, out var sz))
            (section.TexW, section.TexH) = sz;

        // The pooled table first, when the game has one.
        //
        // A tile grid cannot be derived from ONE mesh, and this is the only place that ever has
        // one. A body mesh that spans U 1.005..1.946 is one tile wide, and EVERY square-tile
        // grid of a 6144x2048 texture contains that, so the measurement below concludes 1x1, skips
        // the remap and samples the wrong column. Nothing in the arithmetic is wrong; the evidence
        // simply is not present in one part. Pooled across every mesh sharing the texture, the
        // extent reaches U 3.995 and only one grid fits.
        //
        // A "Flat" verdict is honoured too, and is not the same as an absent one. Flat means the
        // whole population was examined and this is not an atlas — a conclusion that should beat a
        // single mesh's guess, which is exactly the case where one backpack on a
        // square texture read as a 6x6 atlas that does not exist. Absent means the scan never
        // reached it, and there the per-mesh derivation below is still the best answer there is.
        if (UEToolkit.Core.AtlasLibrary.Active is { } library)
        {
            switch (library.Lookup(section.DiffusePath, out var pooled))
            {
                case UEToolkit.Core.AtlasLibrary.Verdict.Atlas:
                    section.Udim = new System.Numerics.Vector4(pooled.Cols, pooled.Rows, pooled.VMinTile, 1f);
                    section.UvMode = $"udim {pooled.Cols:0}x{pooled.Rows:0} pooled";
                    PooledGridCount++;
                    return;
                case UEToolkit.Core.AtlasLibrary.Verdict.Flat:
                    section.Udim = new System.Numerics.Vector4(1f, 1f, 0f, 0f);
                    section.UvMode = "direct (pooled: not an atlas)";
                    PooledGridCount++;
                    return;
            }
        }

        // Grid, origin and whether this is an atlas at all: UEToolkit.Core.UvAtlas, shared
        // with the exporter so the shader and the written UVs cannot disagree. The reasoning
        // (texture-not-mesh, the square-atlas fallback, the rejected extent measure) lives
        // there with it.
        var (cols, rows, vMinTile, udim) =
            UEToolkit.Core.UvAtlas.Grid(section.TexW, section.TexH, uMin, uMax, vMin, vMax);

        section.Udim = udim
            ? new System.Numerics.Vector4(cols, rows, vMinTile, 1f)
            : new System.Numerics.Vector4(1f, 1f, 0f, 0f);
        section.UvMode = udim ? $"udim {cols:0}x{rows:0}"
            : uMax > 1f + slack ? $"tiling (u<={uMax:0.#})" : "direct";
    }

    /// <summary>
    /// Is this parameter a packed mask rather than a colour?
    ///
    /// Some games' vehicles have NO base-colour texture. Their materials are UE material LAYER
    /// stacks (a track instance pulling a dirt layer plus tiling detail maps and a shared
    /// default-textures library) whose colour comes from layer tints blended by
    /// masks. The only mesh-specific map is a cavity/occlusion one bound to a parameter named
    /// "Mesh - CO", which CUE4Parse's colour name table matches -- so every vehicle rendered
    /// in the magenta and green of raw packed channels.
    ///
    /// This rejects by the PARAMETER name, not the texture filename. The distinction matters:
    /// a filename rule guesses at authoring intent, while "Mesh - CO" is the material stating
    /// what the input is. 17 materials were affected, all vehicles. They now render
    /// untextured, which is honest; reproducing
    /// them properly means evaluating the layer stack, which a texture-slot viewport cannot do.
    /// </summary>
    // Shared with the exporter via UEToolkit.Core.UvAtlas, so the viewport and the written
    // file cannot disagree about which texture is the colour map.
    private static bool IsPackedMap(string param) => UEToolkit.Core.UvAtlas.IsPackedMap(param);

    // ---------------------------------------------------------------- emissive, COUNTED not drawn

    /// <summary>
    /// HOW MANY MATERIALS ACTUALLY CARRY A GLOW, and in what form.
    ///
    /// MEASURED BEFORE BUILT, on purpose. MI_LightFixture_C_Wall_R8_D_LIght_Yellow resolves both
    /// PM_Emissive and "Emissive Texture" -- to DefaultColor PLACEHOLDERS. The yellow is a
    /// material PARAMETER, and which parameter feeds emissive is decided by a graph a cooked
    /// instance does not expose. Binding what resolves would put a Default* texture on the
    /// emissive slot of everything that names one: a constant glow over the whole level, which is
    /// worse than no glow at all and looks like a bug in the lighting rather than in the binder.
    ///
    /// This is the same instrument --bare was for the base colour, asked before anything renders:
    /// count the three outcomes, then decide whether there is a feature here.
    /// </summary>
    public int EmissiveTextured, EmissivePlaceholder, EmissiveColourOnly, EmissiveNone;
    public readonly List<string> EmissiveLog = new();

    /// <summary>
    /// WHETHER TO BIND THE GLOW AT ALL. Off by default.
    ///
    /// The level editor wants it; the toolkit and the Mod Studio are for judging artwork somebody
    /// is making, and quietly adding light to their preview would change what they are looking at
    /// without being asked. Same reasoning as releasing materials only in the level editor.
    /// </summary>
    public static bool WantEmissive;

    /// <summary>
    /// The material's own light, bound ONLY when it resolves to real artwork.
    ///
    /// Every one of the 40 that resolved on LGT_Dropship came through `PM_Emissive` -- the packed
    /// fallback slot, not a named parameter -- which is the same convention that turned out to be
    /// right for the base colour. The named list is still tried first.
    ///
    /// A PLACEHOLDER IS NOT A GLOW. `MI_LightFixture_C_Wall_R8_D_LIght_Yellow` resolves both
    /// PM_Emissive and "Emissive Texture" to DefaultColor stand-ins: the yellow of that fixture is
    /// a material parameter, and which parameter feeds emissive is decided by a graph a cooked
    /// instance does not expose. Binding what resolves would light 988 of Umbara's 3,046 sections
    /// with a stand-in, which reads as a bug in the lighting rather than in the binder.
    /// </summary>
    private void BindEmissive(RenderSection section, CMaterialParams2 p)
    {
        section.Emissive = 0;
        section.EmissiveTint = System.Numerics.Vector3.One;
        section.EmissiveScale = 1f;
        section.EmissiveFlat = false;
        if (!WantEmissive) return;

        // ---------------------------------------------------------------- THE MAP, FOR ANY FAMILY
        // FIRST NON-PLACEHOLDER WINS, rather than first name present. `Emissive Texture VT` is
        // MM_Emissive's own name for its glow map and is absent from CUE4Parse's list, so the 26
        // instances that set a real one were arriving only through the `.*Emiss.*` regex landing
        // them in `PM_Emissive` -- which works by luck, and silently picks a winner when a
        // material sets more than one. But simply putting the name first is the opposite mistake:
        // 179 of 205 instances set `Emissive Texture VT` to the `DefaultVirtualColor` stand-in,
        // so a first-name-wins list would pick the placeholder, reject it, and throw away the real
        // map sitting in the next slot. Walking the candidates and skipping stand-ins has no
        // ordering hazard in either direction.
        UTexture? tex = null;
        foreach (var name in EmissiveNames)
        {
            if (!p.Textures.TryGetValue(name, out var cand) || cand is not UTexture ct) continue;
            if (IsPlaceholder(ct.Name)) { EmissiveRejected++; continue; }
            tex = ct; break;
        }

        // A MATERIAL WITH ITS OWN SHEET OWNS BOTH `PM_Emissive` AND `Emissive Color`.
        //
        // `PM_Emissive` is not a parameter -- it is where CUE4Parse's `.*Emiss.*` regex DEPOSITS
        // any texture whose name matches. On MM_Tiled_RGB the names that match are `Emissive Sheet
        // VT` and `Emissive Mask VT`, so the fallback hands the plain emissive path the trim
        // family's SHEET, which `BindEmissiveSheet` is already drawing correctly -- masked, tiled
        // and coloured. Drawn a second time here it is unmasked and untiled: the whole surface
        // glows. `Emissive Color` goes the same way; `BindEmissiveSheet` reads that exact name as
        // the sheet's own colour, and the plain path was taking it as a full-surface tint.
        //
        // The result was materials called `Gray`, `gray_paint` and
        // `MI_HQ_HulkMetalPaintedAGrayClean_B_RGBTrimA` glowing pure red and pink. It is not a new
        // fault -- it has been live on every `--openmap` render -- but it was invisible on
        // `--opensub` until `WantEmissive` started reaching that path at all, which is a fair
        // description of why it lasted.
        var ownsSheet = p.Switches.ContainsKey("Use Emissive Sheet?") ||
                        p.Switches.ContainsKey("Use Emissive Sheet") ||
                        p.Textures.ContainsKey("Emissive Sheet VT") ||
                        p.Textures.ContainsKey("Emissive Mask VT");

        if (tex is null && !ownsSheet &&
            p.Textures.TryGetValue(CMaterialParams2.FallbackEmissive, out var fb) &&
            fb is UTexture ft && !IsPlaceholder(ft.Name))
            tex = ft;

        // The emissive COLOUR multiplies the map where the material carries one. "Color" is in
        // CUE4Parse's emissive-colour list and is far too generic to trust on its own, so only a
        // name that actually says emissive is taken. Unchanged, and applies to every family.
        var tint = System.Numerics.Vector3.One;
        if (!ownsSheet &&
            p.TryGetLinearColor(out var c, "Emissive", "Emissive Color", "EmissiveColor", "EmColor") &&
            c.R + c.G + c.B > 0.01f)
            tint = new System.Numerics.Vector3(c.R, c.G, c.B);

        // ------------------------------------------------- WHAT ONLY MM_EMISSIVE'S DATA SUPPORTS
        // SCOPED BY FAMILY, and the counters are why. Applied to everything, the strength below
        // read `Emissive Strength` on 549 of 645 sections in the hangar -- because that name is
        // ALSO MM_Tiled_RGB's emissive-sheet strength, where its modal value is 1 rather than 6.
        // Normalising one family's parameter by another family's mode divided 541 wall sections'
        // glow by six. That is the same fault as reading one trim sheet's channel strengths over
        // another's mask: a name shared between two families means two different things, and the
        // family is the only thing that says which.
        //
        // `Temperature` and `UseColorTemperature` are generic enough to belong to anything, so
        // they stay in here too. Other families may well carry these names and mean them; they
        // have not been measured, so they are not assumed.
        var family = section.Family.Equals("MM_Emissive", StringComparison.OrdinalIgnoreCase);
        if (family)
        {
            // THE COLOUR. `Emissive Color` is set by 9 of MM_Emissive's 205 instances.
            // `M_Flat_EMS_Color_Scale` is carried by all 205 and overridden by about 118, and the
            // INSTANCE NAMES say outright that it is the glow colour:
            //
            //   MI_CRT_Emissive_Blue  0.109,0.246,0.815    MI_CRT_Emissive_Green  0,1,0
            //   MI_CRT_Emissive_Red   1,0,0                MI_CRT_Emissive_Off    0,0,0
            //   MI_LUN_EmissiveWarm   1,0.714,0.36         MI_UNI_Emissive_Red_Hub 1,0,0
            //
            // An instance named `_Off` carrying 0,0,0 is not a coincidence, and on `M_Flat_EMS_Red`
            // -- the one instance that sets BOTH -- the two hold the IDENTICAL value 0.568,0.027,0.
            // They name one concept, so they multiply: where the scale is white it passes
            // `Emissive Color` through, and where the colour is absent the scale carries it.
            //
            // Taken LINEAR, not sRGB-encoded: this is added into `lit` before the ACES curve, and
            // a UE material colour parameter is already linear. The alpha varies independently of
            // everything else in the family and is left alone rather than guessed at.
            tint *= Linear(p, "M_Flat_EMS_Color_Scale", System.Numerics.Vector3.One);

            // Colour temperature, for the 17 instances that set it. Those leave the colour
            // parameter white and mean the temperature: MI_CRT_LensSquare_Red_A_Blink is white at
            // 1100 K, so reading the colour and ignoring the temperature renders a red bulb WHITE.
            // The 188 instances at 0 carry a Temperature of 6500 the material ignores, which is
            // why this is gated on the switch and not on a value being present.
            if (Scalar(p, "UseColorTemperature", 0f) >= 0.5f && Scalar(p, "Temperature", 0f) > 0f)
                tint *= Kelvin.ToRgb(Scalar(p, "Temperature", 6500f));

            // THE STRENGTH, per instance, spanning 0 to 10 over the 205: 84 at 6, 23 at 7, 20 at
            // 8, 15 at 5, 11 at 0, 10 at 4. Eleven instances asking for nothing is the parameter
            // being used as an on/off -- `MI_UNI_Emissive_GridOff` is one -- so it cannot be
            // ignored.
            //
            // RELATIVE TO THE MODAL 6, NOT ABSOLUTE. The absolute values are UE's own emissive
            // units and applying them raw makes every glowing thing about six times brighter than
            // the picture the author has been calibrating his exposure slider against. Dividing by
            // the value 84 of 205 instances chose leaves that picture where it is and lets only
            // the DIFFERENCES show: the panel at 10 brightens, the screen at 1 dims, the eleven
            // at 0 go out.
            const float modalStrength = 6f;
            section.EmissiveScale = Scalar(p, "Emissive Strength", modalStrength) / modalStrength;
        }

        if (tex is not null)
        {
            section.EmissiveTint = tint;
            section.Emissive = GetOrUpload(tex);
            if (section.Emissive != 0) EmissiveBound++;
            return;
        }

        // ------------------------------------------------------- A GLOW WITH NO MAP AT ALL
        // THE MAJORITY OF THE FAMILY, and it used to render nothing. 194 of 205 instances resolve
        // `Emissive Texture` to `DefaultColor` and 179 resolve `Emissive Texture VT` to
        // `DefaultVirtualColor` -- there is no glow artwork, because the glow is a flat authored
        // colour on a small light panel or a crate indicator. The old binder returned early on the
        // placeholder and those sections got no emissive whatsoever.
        //
        // IT REPLACES THE SURFACE RATHER THAN ADDING TO IT, and that is the one claim here worth
        // being careful about. `PM_Diffuse` is the `DefaultDiffuse` stand-in on 205 of 205, so the
        // family carries no base-colour artwork either -- meaning `albedo * light` is currently
        // shading an INVENTED 0.55 grey. Replacing a fallback grey with a measured colour needs no
        // assumption about the shading model, which is just as well: MM_Emissive's package carries
        // no `ShadingModel` property at all, so the "Unlit" CUE4Parse reports for it is the enum's
        // zero showing through an absent field and says nothing at all.
        //
        // Scoped to this ONE family. Letting any placeholder-emissive material glow flat would
        // light 988 of Umbara's 3,046 sections off a stand-in, which is the fault the old early
        // return existed to prevent; the difference is that here the parameter feeding the glow
        // has been identified from the data rather than assumed.
        if (!family) return;

        // An "off" indicator is meant to be dark, but a section whose glow works out to zero must
        // not become a black hole in the mesh -- so zero falls through to ordinary shading instead
        // of replacing it with black.
        var lit = tint * section.EmissiveScale;
        if (lit.X + lit.Y + lit.Z <= 0.001f) { EmissiveOff++; return; }

        section.EmissiveTint = tint;
        section.EmissiveFlat = true;
        EmissiveFlatBound++;
    }

    /// <summary>
    /// A DECAL'S SHAPE, WHEN IT HAS NO ARTWORK TO GET IT FROM.
    ///
    /// `BaseColor + Opacity Texture` resolves to the `DefaultTexture` stand-in on **84 of
    /// MM_Decals' 258 instances**, and `IsPlaceholder` rightly refuses it -- so those sections
    /// reach the shader with no diffuse at all. With no diffuse the fallback alpha is 1.0, the
    /// alpha test can never fire, and the decal draws as **a solid grey rectangle**. It is the
    /// same fault as the opaque decal quads, one layer further in: fixed for the decals that have
    /// their own artwork, still live for the ones that never did.
    ///
    /// THEIR SHAPE IS IN A CHANNEL MASK. 80 of those 84 carry a real `RGB Channel Mask Texture`
    /// -- `DecalGrunge_01`, `DecalLeak_01_MASK`, `UNI_MossDecalA_DIF` -- and `Used Mask Channel(s)`
    /// says which channel of it to read: 166 instances use R, 51 use G, 39 use B, 1 uses A. One
    /// grunge atlas packs three different decals and each instance picks one, which is why the
    /// texture alone was never enough and why a guess at "probably the red channel" would have
    /// been wrong for 91 of them.
    ///
    /// The switch and the artwork agree exactly, which is the check worth having: `Use RGB Channel
    /// Mask Texture` is TRUE on 159 instances, and 258 minus the 99 placeholder masks is 159.
    ///
    /// SHAPE ONLY, NOT COLOUR, AND THE COMMENT IS THE HONEST PART. A decal in UE blends over the
    /// surface beneath it; a grunge splatter DARKENS what it lies on. This renderer draws decals
    /// as geometry with an alpha test because it does not depth-sort the scene, so there is
    /// nothing beneath to multiply against. Making the quad the right SHAPE is the part that is
    /// both correct and reachable -- a grunge-shaped smear instead of a grey rectangle. `Color
    /// Tint` is applied only where its own alpha says to, which is a convention read off the data
    /// (182 of 258 sit at alpha 0) and NOT something the stripped graph confirms.
    /// </summary>
    private void BindDecalMask(RenderSection section, CMaterialParams2 p)
    {
        section.DecalMask = 0;
        section.DecalChannel = System.Numerics.Vector4.Zero;
        section.DecalMaskPower = 1f;
        section.DecalMaskStrength = 1f;
        section.DecalAlpha = 1f;
        section.DecalTint = System.Numerics.Vector3.One;
        section.DecalTintStrength = 0f;

        // Scoped to the family whose data was read. Other families carry `Mask Power` and
        // `Color Tint` too and mean their own things by them -- the trim sheet's `Emissive Color`
        // taught that lesson expensively enough already.
        if (!section.Family.Equals("MM_Decals", StringComparison.OrdinalIgnoreCase)) return;
        if (!(p.TryGetSwitch(out var use, "Use RGB Channel Mask Texture") && use)) return;

        if (!p.Textures.TryGetValue("RGB Channel Mask Texture", out var raw) ||
            raw is not UTexture tex || IsPlaceholder(tex.Name)) { DecalMaskRejected++; return; }

        // WHICH CHANNEL. A zero vector would select nothing and blank the decal, so a mask that
        // names no channel is refused rather than drawn empty.
        if (!p.TryGetLinearColor(out var ch, "Used Mask Channel(s)")) return;
        var channel = new System.Numerics.Vector4(ch.R, ch.G, ch.B, ch.A);
        if (channel.X + channel.Y + channel.Z + channel.W <= 0.001f) { DecalMaskRejected++; return; }

        section.DecalMask = GetOrUpload(tex);
        if (section.DecalMask == 0) return;

        section.DecalChannel = channel;
        section.DecalMaskPower = MathF.Max(Scalar(p, "Mask Power", 1f), 0.01f);
        section.DecalMaskStrength = Scalar(p, "Mask Strength", 1f);
        section.DecalAlpha = Scalar(p, "Alpha Amount", 1f);

        // The tint, only where its alpha asks for it. See the class comment: a convention, and
        // the conservative reading -- 182 of 258 sit at alpha 0 and keep the surface they had.
        if (p.TryGetLinearColor(out var tc, "Color Tint") && tc.A > 0.5f)
        {
            section.DecalTint = new System.Numerics.Vector3(tc.R, tc.G, tc.B);
            section.DecalTintStrength = Scalar(p, "Decal Color Strength", 0.5f);
        }
        DecalMaskBound++;
    }

    /// <summary>Decals given their shape from a channel mask, and decals refused one.</summary>
    public int DecalMaskBound, DecalMaskRejected;

    /// <summary>
    /// The emissive map by every name this game uses. `Emissive Texture VT` is MM_Emissive's own
    /// and is absent from CUE4Parse's list, so the 26 instances that set a real one were reaching
    /// the renderer only through the `.*Emiss.*` regex landing them in `PM_Emissive` -- which
    /// works, but by luck rather than by naming, and silently picks a winner when a material sets
    /// more than one.
    /// </summary>
    private static readonly string[] EmissiveNames =
        CMaterialParams2.Emissive[0].Append("Emissive Texture VT").ToArray();

    /// <summary>
    /// A material colour parameter, LEFT LINEAR. Distinct from <see cref="Srgb"/>, which encodes:
    /// a value added into `lit` ahead of the tonemapper must stay linear, while one multiplying a
    /// texture the shader has already decoded must not.
    /// </summary>
    private static System.Numerics.Vector3 Linear(CMaterialParams2 p, string name,
                                                  System.Numerics.Vector3 fallback)
        => p.TryGetLinearColor(out var c, name)
            ? new System.Numerics.Vector3(c.R, c.G, c.B)
            : fallback;

    /// <summary>
    /// Sections given a glow from a map, sections refused one because the map was a placeholder,
    /// sections glowing a flat authored colour with no map, and sections whose authored glow works
    /// out to nothing. FOUR POSITIVE COUNTS, because "no output" has hidden working code from
    /// broken code four times on this renderer alone -- a zero here is only meaningful beside the
    /// others.
    /// </summary>
    public int EmissiveBound, EmissiveRejected, EmissiveFlatBound, EmissiveOff;

    /// <summary>
    /// The texture's own name out of whatever the resolver described around it -- Resolve
    /// reports a phrase ("NAME via 'Param' type=... imported=WxH"), not a bare name.
    /// </summary>
    private static string Leaf(string described)
    {
        var sp = described.IndexOf(' ');
        var head = sp > 0 ? described[..sp] : described;
        return head[(head.LastIndexOf('/') + 1)..].Trim(QuoteChars);
    }

    private static readonly char[] QuoteChars = ['\'', '"'];

    /// <summary>The root parent material's name; see <see cref="UEToolkit.Core.LevelFbxExport.FamilyOf"/>.</summary>
    internal static string FamilyOf(CUE4Parse.UE4.Assets.Exports.Material.UMaterialInterface? mi) =>
        UEToolkit.Core.LevelFbxExport.FamilyOf(mi);

    /// <summary>A stand-in the cook leaves behind, not artwork. Binding one is the trap.</summary>
    internal static bool IsPlaceholder(string name) =>
        name.StartsWith("Default", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WhiteTexture", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("BlackTexture", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("T_Default", StringComparison.OrdinalIgnoreCase);

    private void MeasureEmissive(RenderSection section, CMaterialParams2 p)
    {
        var hasTex = p.TryGetTexture2d(out var tex, CMaterialParams2.Emissive[0]);

        // WHICH PARAMETER MATCHED, not just which texture. The first run of this reported
        // "UNI_Decal_A -> UNI_Decal_A_MSK", and a mask bound as emissive would make every decal
        // in the level glow. Whether that is this binder guessing or the material's own naming
        // cannot be told apart from the texture name, and guessing from the suffix is exactly how
        // a rusty metal sheet once ended up on a character's arm.
        var via = hasTex
            ? CMaterialParams2.Emissive[0].FirstOrDefault(p.Textures.ContainsKey) ?? "?"
            : "";
        if (!hasTex && p.Textures.TryGetValue(CMaterialParams2.FallbackEmissive, out var fb))
        { tex = fb as UTexture; hasTex = tex is not null; via = CMaterialParams2.FallbackEmissive; }

        // A COLOUR IS STILL A GLOW. If the instance carries an emissive VECTOR parameter, the
        // fixture's yellow is readable even though no texture is -- and a flat colour on the
        // sections that name one is exactly what a wall light needs.
        var hasColour = p.TryGetLinearColor(out var colour, CMaterialParams2.EmissiveColors[0]);
        var bright = hasColour && (colour.R + colour.G + colour.B) > 0.01f;

        if (hasTex && tex is not null && !IsPlaceholder(tex.Name))
        {
            EmissiveTextured++;
            if (EmissiveLog.Count < 400)
                EmissiveLog.Add($"TEX   {section.MaterialName} -> {tex.Name} via '{via}'" +
                                (bright ? $"  x colour {colour.R:0.##},{colour.G:0.##},{colour.B:0.##}" : ""));
        }
        else if (hasTex)
        {
            EmissivePlaceholder++;
            if (EmissiveLog.Count < 400)
                EmissiveLog.Add($"PLACE {section.MaterialName} -> {tex?.Name} via '{via}'" +
                                (bright ? $"  but colour {colour.R:0.##},{colour.G:0.##},{colour.B:0.##}" : ""));
        }
        else if (bright)
        {
            EmissiveColourOnly++;
            if (EmissiveLog.Count < 400)
                EmissiveLog.Add($"COL   {section.MaterialName} -> {colour.R:0.##},{colour.G:0.##},{colour.B:0.##}");
        }
        else EmissiveNone++;
    }

    /// <summary>
    /// THE RGB TRIM SHEET: one mask, four colours, and the switches that say how to use them.
    ///
    /// This is the environment's painted metal -- `MI_*_RGBTrim*` -- and it is the one masked
    /// material in this game that can be reproduced honestly, because it NAMES its colours:
    /// `R Color`, `G Color`, `B Color`, `A Color`. The character tint mask cannot (see
    /// RenderSection.TintMaskName) because there the channel-to-colour wiring is graph-only.
    ///
    /// Gated on `Use RGB?`. Without that a material merely carrying a mask texture would get
    /// painted, and plenty carry one they never sample.
    ///
    /// SRGB, NOT LINEAR. The shader blends in the same space the diffuse arrives in and applies
    /// pow(2.2) once afterwards, so a linear colour dropped in there would come out too dark.
    /// UE material colour parameters are linear, so they are encoded here rather than in the
    /// shader -- one conversion, at the boundary, where it can be read.
    /// </summary>
    /// <summary>
    /// THE DETAIL NORMAL, from the biggest material family in the game.
    ///
    /// `MM_Tiled_RGB` carries 2,111 of the game's 6,520 material instances -- 32.4%, more than
    /// families two through nine put together -- and `Use Detail Normal?` is set on 39 of 200
    /// sampled. It names `Detail Normal VT` with `Detail Normal Tile` and
    /// `Detail Normal Intensity`, which is the whole specification: the graph is stripped from
    /// every one of the 634 parent materials, so a parameter set is all there is to work from.
    ///
    /// GATED ON THE SWITCH, and on the switch alone. The parent ships the parameter with a
    /// 128x128 `DefaultVirtualMSKNormal` placeholder bound to it, so a section that has not asked
    /// for a detail normal still has one to resolve -- binding whatever resolves would put a
    /// placeholder's slopes on a third of the game. That is the same trap as the 128x128
    /// `DefaultTexture` beside the hull's real base colour.
    /// </summary>
    /// <summary>
    /// THE UV-QUADRANT TINT, and it is a reading rather than a convention.
    ///
    /// `MM_Tiled_RGB` names three colours by the UV quadrant they apply to -- `( 1,-1) Color`,
    /// `(-1, 1) Color`, `(-1,-1) Color` -- each with its own `Color Strength`, behind
    /// `Use UVTint?`. There is no `( 1, 1) Color`, so the positive quadrant is the surface as
    /// authored and one material serves four colour variants.
    ///
    /// **The interpretation was measured before it was written.** A quadrant test means nothing
    /// if every UV sits in 0..1; `--uvrange` found 10 of 29 hangar meshes with negative UVs, UV0
    /// spanning -2.691..4.109 and -1.894..2.699, crossing both axes inside a single mesh. Two
    /// readings tonight went in on the strength of a parameter NAME and both were wrong -- the
    /// alpha channel that turned out constant, and a colour table that belonged to another
    /// instance -- so this one waited for a number.
    ///
    /// Strengths default to ZERO here, not to one. The other binders default a missing strength
    /// to 1 so that an absent parameter cannot hide a mask; the opposite is right for a tint,
    /// where a missing strength must not invent a colour shift on a surface the material never
    /// asked to tint.
    /// </summary>
    /// <summary>
    /// MM_Rocks' WORLD-AXIS PROJECTION -- moss and dust settling on level faces.
    ///
    /// The second-biggest environment family, 396 instances, with `Projected Texture` set on 99
    /// of 200 sampled. Every term is named, which is why this is reproducible rather than
    /// approximated: `Projected Color` and `Projected NRM` for the layer, `World Projection Axis`
    /// for the direction, `Projected Slope Min`/`Max` for the band of surface angles it settles
    /// on, `Projection Size` for the world tiling, and separate intensity, tint and roughness
    /// range.
    ///
    /// THE AXIS IS A VECTOR PACKED IN A COLOUR -- `0000FF`, meaning (0,0,1), UE's up. It is read
    /// RAW rather than through `Srgb`, which encodes linear to sRGB for actual colours: a
    /// component of 0.5 would come back as 0.74 and tilt the projection. It happens not to
    /// matter for an axis of exactly 0 and 1, and relying on that would be waiting for the first
    /// slanted axis to produce a bug nobody could place.
    ///
    /// `Projected Displacement` is read by nothing here: it drives parallax occlusion, which
    /// needs a ray march this renderer does not have. Named so the omission is deliberate.
    /// </summary>
    private void BindProjection(RenderSection section, CMaterialParams2 p)
    {
        section.ProjColor = 0;
        section.ProjNrm = 0;
        if (!(p.TryGetSwitch(out var use, "Projected Texture", "Use Projected Texture") && use)) return;

        var colour = Resolve(p, ["Projected Color", "Projected Colour"], "", out var colourName);
        if (colour == 0 || IsPlaceholder(Leaf(colourName)))
        {
            // BOTH BRANCHES SPEAK. This printed only when the texture resolved AND was a
            // placeholder, so the case that actually happened -- switch on, texture absent from
            // the params -- returned in silence and read exactly like a level with no rocks in
            // it. Four levels were rendered on the strength of that silence.
            Console.WriteLine(colour == 0
                ? "   projected texture: switch is ON and no 'Projected Color' in the material's " +
                  "parameters - nothing applied"
                : $"   projected texture '{Leaf(colourName)}' is a placeholder, not applied");
            return;
        }

        section.ProjColor = colour;
        var nrm = Resolve(p, ["Projected NRM", "Projected Normal"], "", out var nrmName, normalMap: true);
        section.ProjNrm = nrm != 0 && !IsPlaceholder(Leaf(nrmName)) ? nrm : 0u;

        section.ProjAxis = Raw(p, "World Projection Axis", System.Numerics.Vector3.UnitZ);
        if (section.ProjAxis.LengthSquared() < 1e-8f) section.ProjAxis = System.Numerics.Vector3.UnitZ;

        section.ProjTint = Srgb(p, "Projected Tint Color", System.Numerics.Vector3.One)
                           * Scalar(p, "Projected Tint Brightness", 1f);
        section.ProjSize = Scalar(p, "Projection Size", 512f);
        section.ProjIntensity = Scalar(p, "Projected Intensity", 1f);
        section.ProjNrmIntensity = Scalar(p, "Projected Normal Intensity", 1f);

        // MIN BELOW MAX, or smoothstep returns a step and the moss arrives as a hard edge. The
        // shipped values are 0.2 and 0.4; an instance that inverts them is the kind of thing the
        // trim sheet's disagreeing instances already taught this file to expect.
        var lo = Scalar(p, "Projected Slope Min", 0.2f);
        var hi = Scalar(p, "Projected Slope Max", 0.4f);
        section.ProjSlopeMin = MathF.Min(lo, hi);
        section.ProjSlopeMax = MathF.Max(lo, hi) is var h && h - section.ProjSlopeMin < 0.001f
            ? section.ProjSlopeMin + 0.001f : h;

        section.ProjRough = new System.Numerics.Vector2(
            Scalar(p, "Projected Roughness Min", 0f), Scalar(p, "Projected Roughness Max", 1f));

        // THE BREAK-UP MASK, its own switch and its own projection size. 700 against the main
        // projection's 512 on purpose -- two coprime-ish tilings so the layer never repeats
        // visibly. Its own switch, because a material can project a colour without breaking it up.
        section.ProjAlpha = 0;
        if (p.TryGetSwitch(out var addA, "Use Projected Additional Alpha") && addA)
        {
            var a = Resolve(p, ["Projected Additional Alpha"], "", out var aName);
            if (a != 0 && !IsPlaceholder(Leaf(aName)))
            {
                section.ProjAlpha = a;
                section.ProjAlphaSize = Scalar(p, "Projection Size Additional Alpha", 700f);
                section.ProjAlphaContrast = Scalar(p, "Projected Additional Alpha Contrast", 0f);
            }
        }
    }

    /// <summary>
    /// FUZZ, and the whole of what the material says about it: `Fuzz Color`.
    ///
    /// UE's FuzzyShading also takes a core darkness and a power; both are graph constants and a
    /// cooked instance carries neither, so the colour is a reading and the falloff is a
    /// convention. Set on 20 of 200 sampled MM_Rocks instances.
    ///
    /// A BLACK FUZZ COLOUR IS NO FUZZ. The parameter's absence and a deliberate black are the
    /// same thing here, and adding a black rim term costs a branch to achieve nothing.
    /// </summary>
    private void BindFuzz(RenderSection section, CMaterialParams2 p)
    {
        section.Fuzz = false;
        section.FuzzColor = System.Numerics.Vector3.Zero;
        if (!(p.TryGetSwitch(out var use, "Use Fuzz Shading", "Use Fuzz") && use)) return;

        var c = Srgb(p, "Fuzz Color", System.Numerics.Vector3.Zero);
        if (c.X <= 0f && c.Y <= 0f && c.Z <= 0f) return;
        section.FuzzColor = c;
        section.Fuzz = true;
    }

    private void BindUvTint(RenderSection section, CMaterialParams2 p)
    {
        section.UvTint = false;
        section.UvTintStrength = System.Numerics.Vector3.Zero;
        if (!(p.TryGetSwitch(out var use, "Use UVTint?", "Use UVTint") && use)) return;

        section.UvTintPN = Srgb(p, "( 1,-1) Color", System.Numerics.Vector3.One);
        section.UvTintNP = Srgb(p, "(-1, 1) Color", System.Numerics.Vector3.One);
        section.UvTintNN = Srgb(p, "(-1,-1) Color", System.Numerics.Vector3.One);
        section.UvTintStrength = new System.Numerics.Vector3(
            Scalar(p, "( 1,-1) Color Strength", 0f),
            Scalar(p, "(-1, 1) Color Strength", 0f),
            Scalar(p, "(-1,-1) Color Strength", 0f));
        section.UvTint = true;
    }

    /// <summary>
    /// THE EMISSIVE SHEET: a pattern, a mask saying where it shows, a colour and a strength.
    ///
    /// A DIFFERENT MECHANISM from the single `PM_Emissive` map the renderer already had, and a
    /// section can carry both, so this adds rather than replaces. `Use Emissive Sheet?` is set on
    /// 40 of 200 sampled `MM_Tiled_RGB` instances.
    ///
    /// `Emissive Color` defaults to 000000 -- BLACK -- which is the parent stating that a section
    /// which sets no colour emits nothing. So an unset section is a no-op by the material's own
    /// default rather than by a rule invented here, and a missing colour cannot light a room.
    ///
    /// BOTH TEXTURES REQUIRED. A sheet with no mask has nothing saying where to show, and
    /// applying it everywhere would make whole surfaces glow -- the same shape of destructive
    /// literal reading as taking `Use tint only? (No DIF)` at its word.
    /// </summary>
    private void BindEmissiveSheet(RenderSection section, CMaterialParams2 p)
    {
        section.EmSheet = 0;
        section.EmMask = 0;
        section.EmSheetColor = System.Numerics.Vector3.Zero;
        section.EmSheetTile = 1f;
        section.EmSheetStrength = 1f;
        if (!(p.TryGetSwitch(out var use, "Use Emissive Sheet?", "Use Emissive Sheet") && use)) return;

        var sheet = Resolve(p, ["Emissive Sheet VT", "Emissive Sheet"], "", out var sheetName);
        var mask = Resolve(p, ["Emissive Mask VT", "Emissive Mask"], "", out var maskName);
        if (sheet == 0 || mask == 0) return;

        // PLACEHOLDERS, BY NAME. The parent binds `DefaultVirtualColor` and
        // `DefaultVirtualLinear` to these two parameters, so every instance in the family
        // resolves something here whether it asked or not.
        //
        // AND THIS IS THE CASE THAT KILLED THE SIZE GATE: the real sheet `UNI_CrateRebelsA_E` is
        // 128x128 while the `DefaultVirtualLinear` mask standing in beside it is 4096x4096. Big
        // is not real and small is not fake.
        if (IsPlaceholder(Leaf(sheetName)) || IsPlaceholder(Leaf(maskName)))
        {
            Console.WriteLine($"   emissive sheet '{Leaf(sheetName)}' / mask '{Leaf(maskName)}': " +
                              "one is a placeholder, not applied");
            return;
        }

        var colour = Srgb(p, "Emissive Color", System.Numerics.Vector3.Zero);
        if (colour.X <= 0f && colour.Y <= 0f && colour.Z <= 0f) return;   // black emits nothing

        section.EmSheet = sheet;
        section.EmMask = mask;
        section.EmSheetColor = colour;
        section.EmSheetTile = Scalar(p, "Emissive Tile", 1f);
        section.EmSheetStrength = Scalar(p, "Emissive Strength", 1f);
    }

    private void BindDetailNormal(RenderSection section, CMaterialParams2 p)
    {
        section.DetailNormal = 0;
        section.DetailTile = 1f;
        section.DetailIntensity = 1f;
        if (!(p.TryGetSwitch(out var use, "Use Detail Normal?", "Use Detail Normal") && use)) return;

        var handle = Resolve(p, ["Detail Normal VT", "Detail Normal", "DetailNormal"], "", out var which, normalMap: true);
        if (handle == 0) return;

        // A PLACEHOLDER IS NOT A DETAIL NORMAL -- the parent binds `DefaultVirtualMSKNormal` to
        // this parameter, so every section in the family resolves SOMETHING here whether it asked
        // or not.
        //
        // BY NAME, VIA THE TEST THIS FILE ALREADY HAD. I first gated on size, ">= 256 square",
        // reasoning that the defaults are 128 and real artwork is bigger. That is false in both
        // directions and the emissive sheet proved it within the hour: the genuine
        // `UNI_CrateRebelsA_E` is 128x128 and the `DefaultVirtualLinear` placeholder beside it is
        // 4096x4096. A size gate rejects the artwork and admits the stand-in. `IsPlaceholder`
        // was twenty lines up the file.
        if (IsPlaceholder(Leaf(which)))
        {
            Console.WriteLine($"   detail normal '{which}' is a placeholder, not applied");
            return;
        }

        section.DetailNormal = handle;
        // TWO FAMILIES, TWO SPELLINGS. MM_Tiled_RGB says `Detail Normal Tile`; MM_Rocks says
        // `Detail Normal Tiling` and names the texture `Detail Normal` rather than
        // `Detail Normal VT`. Asking for one spelling silently gave MM_Rocks' 44 instances a
        // tiling of 1 whatever they asked for -- the parameter was there and the lookup missed
        // it by a suffix.
        section.DetailTile = Scalar(p, "Detail Normal Tile", 0f) is var t && t > 0f
            ? t : Scalar(p, "Detail Normal Tiling", 1f);
        section.DetailIntensity = Scalar(p, "Detail Normal Intensity", 1f);
    }

    private void BindTrim(RenderSection section, CMaterialParams2 p)
    {
        section.TrimMask = 0;
        if (!p.TryGetSwitch(out var useRgb, "Use RGB?") || !useRgb) return;
        if (!p.TryGetTexture2d(out var mask, "RGB Mask VT", "RGB Mask")) return;
        if (IsPlaceholder(mask.Name)) return;

        section.TrimMask = GetOrUpload(mask);
        if (section.TrimMask == 0) return;

        // A VIRTUAL TEXTURE DOES NOT COME BACK THROUGH THE MIP PATH, AND THIS IS WHERE THAT SHOWS.
        //
        // `RGB Mask VT` is exactly that -- a VT, whose pixels are bordered pages in VTData rather
        // than a mip chain. GetOrUpload calls UTexture.Decode, which for one of these hands back
        // the degenerate top level: --vtinfo shows levels 7 to 12 are all 1x1 against 18.9 MB of
        // real page data. Uploaded and sampled, that reads BLACK -- measured at 77.5% of the
        // frame, mean luma 0.022 -- so every channel weight came out zero and the material's base
        // showed through as flat white.
        //
        // SO THE SIZE IS THE GATE, not a switch somebody has to remember. A mask this small is
        // not a trim sheet, and refusing it leaves the surface exactly as it rendered before --
        // incomplete rather than confidently wrong, which is the same call RenderSection.TintMask
        // makes. It also means this feature turns itself on the day a VT reader lands, with
        // nothing to go back and re-enable.
        if (!_sizes.TryGetValue(section.TrimMask, out var size) || size.W < 64 || size.H < 64)
        {
            BoundLog.Add($"    {section.MaterialName}: RGB trim mask '{mask.Name}' decoded " +
                         $"{(_sizes.TryGetValue(section.TrimMask, out var s2) ? $"{s2.W}x{s2.H}" : "not at all")}" +
                         " -- a virtual texture needs page assembly, which is not built. Left untinted.");
            section.TrimMask = 0;
            return;
        }

        section.TrimUv1 = p.TryGetSwitch(out var uv1, "RGBMaskUV1") && uv1;
        section.TrimUseA = p.TryGetSwitch(out var useA, "Use A Channel") && useA;
        // READ AND DELIBERATELY NOT ACTED ON -- see RenderSection.TrimNoDif. Every instance
        // measured sets this true AND ships `Tint Color` = FEFEFE with `Tint Brightness` 1.25,
        // so taking it literally makes the base pure white. A shipping container's two trim
        // sections came out white-on-white that way, and its painted panels are plainly not.
        // White times a brightness above one is what a MULTIPLIER looks like, not a ground.
        section.TrimNoDif = false;
        section.TrimNoDifSaid = p.TryGetSwitch(out var noDif, "Use tint only? (No DIF)") && noDif;

        section.TrimBase = Srgb(p, "Tint Color", System.Numerics.Vector3.One);
        section.TrimR = Srgb(p, "R Color", System.Numerics.Vector3.One);
        section.TrimG = Srgb(p, "G Color", System.Numerics.Vector3.One);
        section.TrimB = Srgb(p, "B Color", System.Numerics.Vector3.One);
        section.TrimA = Srgb(p, "A Color", System.Numerics.Vector3.One);

        section.TrimStrength = new System.Numerics.Vector4(
            Scalar(p, "R Channel Color Strength", 1f), Scalar(p, "G Channel Color Strength", 1f),
            Scalar(p, "B Channel Color Strength", 1f), Scalar(p, "A Channel Color Strength", 1f));
        // Zero would hide the mask entirely and is what a missing parameter must NOT look like,
        // so the fallback is 1 in both and only a stated 0 turns a channel off.
        section.TrimRough = new System.Numerics.Vector4(
            Scalar(p, "R Channel Roughness", 0.6f), Scalar(p, "G Channel Roughness", 0.6f),
            Scalar(p, "B Channel Roughness", 0.6f), Scalar(p, "A Channel Roughness", 0.6f));
        section.TrimRoughStrength = new System.Numerics.Vector4(
            Scalar(p, "R Channel Roughness Strength", 1f), Scalar(p, "G Channel Roughness Strength", 1f),
            Scalar(p, "B Channel Roughness Strength", 1f), Scalar(p, "A Channel Roughness Strength", 1f));
        // `Roughness Min/Max`, NOT `Base Roughness Min/Max`. Both ship: the hull states
        // 0.091/0.789 for the first and a flat 0/1 for the second, and a 0..1 remap is a no-op,
        // so the one carrying a real range is the one doing the work. If that reading is wrong it
        // is wrong in the harmless direction -- a narrower gloss range, not an inverted one.
        section.TrimRoughRemap = new System.Numerics.Vector2(
            Scalar(p, "Roughness Min", 0f), Scalar(p, "Roughness Max", 1f));

        section.TrimMetal = new System.Numerics.Vector4(
            Scalar(p, "R Channel Metallic", 0f), Scalar(p, "G Channel Metallic", 0f),
            Scalar(p, "B Channel Metallic", 0f), Scalar(p, "A Channel Metallic", 0f));
        section.TrimMetalStrength = Scalar(p, "RGB Metallic Strength", 1f);
        section.TrimMetalRemap = new System.Numerics.Vector2(
            Scalar(p, "Metallic Min", 0f), Scalar(p, "Metallic Max", 1f));
        section.TrimMetalBaseSaid = Scalar(p, "Metallic Strength", 1f);

        section.TrimTile = Scalar(p, "RGB Tile", 1f);
        section.TrimBright = Scalar(p, "Tint Brightness", 1f);
        if (section.TrimTile <= 0f) section.TrimTile = 1f;

        BoundLog.Add($"    TRIM {section.MaterialName}: mask '{mask.Name}' " +
                     $"decoded {size.W}x{size.H} (shipped " +
                     $"{mask.PlatformData?.SizeX ?? 0}x{mask.PlatformData?.SizeY ?? 0})");
        BoundLog.Add($"    TRIM {section.MaterialName}: base={Hex(section.TrimBase)} " +
                     $"R={Hex(section.TrimR)} G={Hex(section.TrimG)} B={Hex(section.TrimB)} " +
                     $"A={Hex(section.TrimA)} strength=" +
                     $"{section.TrimStrength.X:0.##}/{section.TrimStrength.Y:0.##}/" +
                     $"{section.TrimStrength.Z:0.##}/{section.TrimStrength.W:0.##} " +
                     $"tile={section.TrimTile:0.##} bright={section.TrimBright:0.##} " +
                     $"rough={section.TrimRough.X:0.##}/{section.TrimRough.Y:0.##}/" +
                     $"{section.TrimRough.Z:0.##}/{section.TrimRough.W:0.##} " +
                     $"roughStr={section.TrimRoughStrength.X:0.##}/{section.TrimRoughStrength.Y:0.##}/" +
                     $"{section.TrimRoughStrength.Z:0.##}/{section.TrimRoughStrength.W:0.##} " +
                     $"remap={section.TrimRoughRemap.X:0.###}..{section.TrimRoughRemap.Y:0.###} " +
                     $"metal={section.TrimMetal.X:0.##}/{section.TrimMetal.Y:0.##}/" +
                     $"{section.TrimMetal.Z:0.##}/{section.TrimMetal.W:0.##} " +
                     $"metalStr={section.TrimMetalStrength:0.##} " +
                     $"metalRemap={section.TrimMetalRemap.X:0.###}..{section.TrimMetalRemap.Y:0.###} " +
                     $"(Metallic Strength said {section.TrimMetalBaseSaid:0.##}, not applied)");
        BoundLog.Add($"    {section.MaterialName}: RGB trim '{mask.Name}'" +
                     (section.TrimUv1 ? " on UV1" : " on UV0") +
                     (section.TrimNoDif ? ", REPLACING the diffuse" : ", tinting it") +
                     (section.TrimUseA ? ", A channel live" : ""));
    }

    private static float Scalar(CMaterialParams2 p, string name, float fallback) =>
        p.TryGetScalar(out var v, name) ? v : fallback;

    private static string Hex(System.Numerics.Vector3 c) =>
        $"{(int) (Math.Clamp(c.X, 0, 1) * 255):X2}{(int) (Math.Clamp(c.Y, 0, 1) * 255):X2}" +
        $"{(int) (Math.Clamp(c.Z, 0, 1) * 255):X2}";

    /// <summary>A linear colour parameter, encoded to sRGB for the shader's working space.</summary>
    /// <summary>
    /// A colour read with NO gamma applied, for parameters that pack a vector into a colour.
    /// `Srgb` encodes linear to sRGB, which is right for something the eye will see and wrong
    /// for a direction: 0.5 would come back 0.74.
    /// </summary>
    private static System.Numerics.Vector3 Raw(CMaterialParams2 p, string name,
                                               System.Numerics.Vector3 fallback)
        => p.TryGetLinearColor(out var c, name)
            ? new System.Numerics.Vector3(c.R, c.G, c.B) : fallback;

    private static System.Numerics.Vector3 Srgb(CMaterialParams2 p, string name,
                                                System.Numerics.Vector3 fallback)
    {
        if (!p.TryGetLinearColor(out var c, name)) return fallback;
        static float E(float v) => v <= 0.0031308f ? v * 12.92f
                                                   : 1.055f * MathF.Pow(MathF.Max(v, 0f), 1f / 2.4f) - 0.055f;
        return new System.Numerics.Vector3(E(c.R), E(c.G), E(c.B));
    }

    /// <summary>
    /// Parameters the per-family binders need, copied in from `AllLayers` when the first layer
    /// does not carry them. BY EXACT NAME: see the call site for why the whole set is not adopted.
    /// </summary>
    private static readonly string[] ExtraTextureParams =
    [
        "Detail Normal VT", "Detail Normal", "DetailNormal",
        "Emissive Sheet VT", "Emissive Mask VT",
        "Projected Color", "Projected Colour", "Projected NRM", "Projected Normal",
        "Projected Additional Alpha",
        "BaseColor + Opacity Texture", "Normal Texture",
        "Emissive Texture VT", "Emissive Texture",
        "RGB Channel Mask Texture",
    ];

    private static readonly string[] ExtraScalarParams =
    [
        "Detail Normal Tile", "Detail Normal Tiling", "Detail Normal Intensity",
        "Emissive Tile", "Emissive Strength",
        "( 1,-1) Color Strength", "(-1, 1) Color Strength", "(-1,-1) Color Strength",
        "Projection Size", "Projected Intensity", "Projected Normal Intensity",
        "Projected Slope Min", "Projected Slope Max",
        "Projected Roughness Min", "Projected Roughness Max",
        "Projection Size Additional Alpha", "Projected Additional Alpha Contrast",
        "Projected Tint Brightness",
        // MM_Emissive. `Emissive Strength` was already here for the trim sheet; the other two are
        // SCALARS and not switches, which is how the material stores them -- 188 instances at 0
        // and 17 at 1 for `UseColorTemperature`.
        "UseColorTemperature", "Temperature", "Emissive EV",
        "Mask Power", "Mask Strength", "Alpha Amount", "Decal Color Strength",
    ];

    private static readonly string[] ExtraColourParams =
    [
        "( 1,-1) Color", "(-1, 1) Color", "(-1,-1) Color",
        "Emissive Color", "World Projection Axis", "Projected Tint Color", "Fuzz Color",
        // THE NAME MM_EMISSIVE ACTUALLY USES FOR ITS GLOW COLOUR. Layer-qualified -- layer
        // `M_Flat`, parameter `EMS_Color_Scale` -- which is why nothing looking for "Emissive"
        // ever found it. See BindEmissive for the evidence that it IS the colour.
        "M_Flat_EMS_Color_Scale",
        "Used Mask Channel(s)", "Mask Multiplier", "Color Tint",
    ];

    private static readonly string[] ExtraSwitchParams =
    [
        "Use Detail Normal?", "Use Detail Normal", "Use UVTint?", "Use UVTint",
        "Use Emissive Sheet?", "Use Emissive Sheet",
        "Projected Texture", "Use Projected Texture",
        "Use Projected Additional Alpha", "Use Fuzz Shading", "Use Fuzz",
        "Use BaseColor Texture", "Use BaseColor Texture Alpha",
        "Use RGB Channel Mask Texture",
    ];

    private static void MergeNamedFromAllLayers(UMaterialInterface material, CMaterialParams2 p)
    {
        var needs = ExtraTextureParams.Any(n => !p.Textures.ContainsKey(n))
                 || ExtraSwitchParams.Any(n => !p.Switches.ContainsKey(n));
        if (!needs) return;

        var wide = new CMaterialParams2();
        try { material.GetParams(wide, EMaterialFormat.AllLayers); }
        catch { return; }

        foreach (var n in ExtraTextureParams)
            if (!p.Textures.ContainsKey(n) && wide.Textures.TryGetValue(n, out var t))
                p.Textures[n] = t;
        foreach (var n in ExtraScalarParams)
            if (!p.Scalars.ContainsKey(n) && wide.Scalars.TryGetValue(n, out var v))
                p.Scalars[n] = v;
        foreach (var n in ExtraColourParams)
            if (!p.Colors.ContainsKey(n) && wide.Colors.TryGetValue(n, out var c))
                p.Colors[n] = c;
        foreach (var n in ExtraSwitchParams)
            if (!p.Switches.ContainsKey(n) && wide.Switches.TryGetValue(n, out var b))
                p.Switches[n] = b;
    }

    /// <summary>One texture the name lists settled on, and the phrase the binder logs for it.</summary>
    private readonly record struct Picked(UTexture? Texture, string Via);

    /// <summary>
    /// THE PICK: which texture a material's parameters name for one slot, by the names in order and
    /// then by the slot's fallback. Static and GL-free -- <see cref="Choose"/> runs it with no context
    /// and no cache. <see cref="Resolve"/> is this plus the upload.
    /// </summary>
    private static Picked PickTexture(CMaterialParams2 p, string[] names, string fallback,
        Func<string, bool>? reject = null)
    {
        if (reject is not null) names = names.Where(n => !reject(n)).ToArray();
        if (p.TryGetTexture2d(out var tex, names))
        {
            // Record WHICH parameter matched, not just the texture. A vehicle can bind a
            // packed control map into the colour slot and render magenta
            // and green; whether that is our mistake or the material's own naming cannot be
            // told apart without this, and guessing from the filename suffix is how a rusty
            // metal sheet ended up on a character's arm.
            var matched = names.FirstOrDefault(n => p.Textures.ContainsKey(n)) ?? "?";
            return new Picked(tex,
                $"{tex.Name} via '{matched}' type={tex.ExportType} " +
                $"imported={(tex as UTexture2D)?.ImportedSize.X}x{(tex as UTexture2D)?.ImportedSize.Y}");
        }
        if (p.Textures.TryGetValue(fallback, out var fb) && fb is UTexture t)
            return new Picked(t, $"{t.Name}[fallback]");
        return new Picked(null, "none");
    }

    private uint Resolve(CMaterialParams2 p, string[] names, string fallback, out string which,
        Func<string, bool>? reject = null, bool normalMap = false)
    {
        var pick = PickTexture(p, names, fallback, reject);
        which = pick.Via;
        return GetOrUpload(pick.Texture, normalMap: normalMap);
    }

    public void Dispose()
    {
        foreach (var h in _cache.Values) if (h != 0) gl.DeleteTexture(h);
        _cache.Clear();
        foreach (var b in _glBytes.Values)
        {
            Interlocked.Decrement(ref _liveTextures);
            Interlocked.Add(ref _liveTextureBytes, -b);
            Interlocked.Increment(ref _deletedTextures);
        }
        _glBytes.Clear();
        _formatOf.Clear();
        _blockScratch = Array.Empty<byte>();
    }
}
