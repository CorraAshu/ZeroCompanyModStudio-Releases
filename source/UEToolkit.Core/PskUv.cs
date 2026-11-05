using System.Buffers.Binary;
using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace UEToolkit.Core;

/// <summary>
/// Bakes UDIM atlas addressing into a written .psk/.pskx.
///
/// The exporter writes UV0 exactly as the game authored it, which for an atlas game means coordinates
/// well outside 0..1 — one head spans U 0.003..2.990 and V -0.997..0.990 across a 3x2
/// grid. A DCC reads anything outside 0..1 as tiling and repeats the atlas instead of selecting
/// a region of it, so nearly every part imports with its texture in the wrong place. The
/// viewport has always remapped this in its vertex shader; export never did.
///
/// Rewriting the file afterwards rather than rebuilding the mesh keeps CUE4Parse's tested PSK
/// writer in the path and works the same for skeletal and static exports.
/// </summary>
public static class PskUv
{
    /// <summary>Reports which texture each material's atlas grid was measured from.</summary>
    public static Action<string>? Measured;

    private const int WedgeUvOffset = 4;      // u16 point, u16 pad, then float U, float V
    private const int WedgeMatOffset = 12;

    public sealed record Result(int Sections, int Remapped, int Wedges, string Detail)
    {
        public override string ToString() =>
            $"{Remapped}/{Sections} section(s) remapped over {Wedges:N0} wedges" +
            (Detail.Length > 0 ? "  " + Detail : "");
    }

    /// <summary>One material slot's UV extent, read straight out of a written file.</summary>
    public sealed record SectionUv(int Slot, string Material, float UMin, float UMax, float VMin, float VMax);

    /// <summary>
    /// Per-section UV bounds of a written mesh, with no reference to any texture.
    ///
    /// Separate from the remap because the grid can only be settled once EVERY mesh sharing an
    /// atlas has been measured -- see <see cref="AtlasGrids"/>.
    /// </summary>
    public static List<SectionUv> ReadSections(string pskPath)
    {
        var data = FreshFile.ReadAllBytes(pskPath);
        var chunks = ReadChunks(data);
        var result = new List<SectionUv>();
        if (!chunks.TryGetValue("VTXW0000", out var wedges)) return result;
        var names = MaterialNames(data, chunks);
        var (lo, hi) = Bounds(data, wedges);
        foreach (var (mat, min) in lo.OrderBy(k => k.Key))
            result.Add(new SectionUv(mat, mat < names.Count ? names[mat] : "",
                min.U, hi[mat].U, min.V, hi[mat].V));
        return result;
    }

    private static (Dictionary<int, (float U, float V)> Lo, Dictionary<int, (float U, float V)> Hi)
        Bounds(byte[] data, Chunk wedges)
    {
        var lo = new Dictionary<int, (float U, float V)>();
        var hi = new Dictionary<int, (float U, float V)>();
        for (var i = 0; i < wedges.Count; i++)
        {
            var o = wedges.Offset + i * wedges.Size;
            var mat = data[o + WedgeMatOffset];
            var u = BitConverter.ToSingle(data, o + WedgeUvOffset);
            var v = BitConverter.ToSingle(data, o + WedgeUvOffset + 4);
            if (float.IsNaN(u) || float.IsNaN(v)) continue;
            if (!lo.TryGetValue(mat, out var l)) { lo[mat] = (u, v); hi[mat] = (u, v); continue; }
            lo[mat] = (MathF.Min(l.U, u), MathF.Min(l.V, v));
            var h = hi[mat];
            hi[mat] = (MathF.Max(h.U, u), MathF.Max(h.V, v));
        }
        return (lo, hi);
    }

    /// <summary>
    /// Bake an already-decided grid into a file's UVs. <paramref name="gridFor"/> answers per
    /// material name; a section it declines is left exactly as written.
    ///
    /// The grid is NOT decided here. It used to be, from the one mesh in hand, and that cannot
    /// work: one part's UVs span U 0..1, which sits inside a 4x1 grid and inside the 8x2
    /// grid the rest of the character uses, so the two parts disagreed about their shared atlas and
    /// one exported at double scale. No single mesh holds enough of the atlas to settle it.
    /// </summary>
    public static Result Apply(string pskPath, Func<string, (float Cols, float Rows, float VMinTile)?> gridFor)
    {
        var data = FreshFile.ReadAllBytes(pskPath);
        var chunks = ReadChunks(data);
        if (!chunks.TryGetValue("VTXW0000", out var wedges))
            return new Result(0, 0, 0, "no wedge chunk");

        var names = MaterialNames(data, chunks);
        var (lo, hi) = Bounds(data, wedges);

        var grids = new Dictionary<int, (float Cols, float Rows, float VMinTile)>();
        var detail = new List<string>();
        foreach (var (mat, _) in lo.OrderBy(k => k.Key))
        {
            var name = mat < names.Count ? names[mat] : "";
            if (name.Length == 0 || gridFor(name) is not { } g)
            {
                detail.Add($"[{mat}]{name}=direct");
                continue;
            }
            grids[mat] = g;
            detail.Add($"[{mat}]{name}={g.Cols:0}x{g.Rows:0}");
        }

        if (grids.Count > 0)
        {
            for (var i = 0; i < wedges.Count; i++)
            {
                var o = wedges.Offset + i * wedges.Size;
                if (!grids.TryGetValue(data[o + WedgeMatOffset], out var g)) continue;
                var u = BitConverter.ToSingle(data, o + WedgeUvOffset);
                var v = BitConverter.ToSingle(data, o + WedgeUvOffset + 4);
                if (float.IsNaN(u) || float.IsNaN(v)) continue;

                // No special case for a V sitting exactly on a tile edge any more: UvAtlas.Remap
                // clamps the tile index, so the top of the top tile resolves to the top tile by
                // construction. The nudge this replaces -- decrement the section's own maximum --
                // fixed the upper edge and broke the lower one, sending a section whose maximum
                // V is 0 to V = 2.0.
                var (nu, nv) = UvAtlas.Remap(u, v, g.Cols, g.Rows, g.VMinTile);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(o + WedgeUvOffset), nu);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(o + WedgeUvOffset + 4), nv);
            }
            FreshFile.WriteAllBytes(pskPath, data);
        }

        return new Result(lo.Count, grids.Count, wedges.Count, string.Join(" ", detail));
    }

    /// <summary>
    /// The atlas texture behind each of a mesh's material names, read without decoding anything.
    ///
    /// The texture PATH comes back as well as its size, because the path is the atlas's real
    /// identity. Material names collide -- six unrelated assets ship a "lambert1" and a
    /// "standardSurface1" straight out of Maya -- so pooling anything by name merges atlases
    /// that have nothing to do with each other.
    /// </summary>
    /// <summary>
    /// Why a material slot produced no atlas. Counted rather than swallowed: dropping one of
    /// these silently means that mesh never gets remapped, and four identical runs recorded
    /// 668, 676, 677 and 681 slots because the failures were invisible.
    /// </summary>
    public sealed record Unresolved(int NullMaterial, int ParamsThrew, int NoDiffuse, int NoSize,
                                    int Recovered = 0)
    {
        public int Total => NullMaterial + ParamsThrew + NoDiffuse + NoSize;
        public override string ToString() =>
            $"null-material={NullMaterial} params-threw={ParamsThrew} no-diffuse={NoDiffuse} " +
            $"no-size={NoSize} recovered-on-retry={Recovered}";
    }

    // Retrying was tried first and never once helped -- "recovered-on-retry" stayed at 0 across
    // every run. The empty result gets cached, so asking again returns the same nothing; the bad
    // load has to be prevented, which is what ResolveLock does.

    public static Dictionary<string, (string Path, int W, int H)> AtlasTextures(UObject mesh)
        => AtlasTextures(mesh, out _);

    /// <summary>
    /// Resolving a material lazily loads its texture packages, and CUE4Parse's package cache is
    /// not safe to race on: under the export's 22-way parallelism a texture that plainly exists
    /// intermittently resolved to nothing, the mesh was recorded with no atlas, and it was never
    /// remapped -- a different set of meshes each run (668, 676, 677, 681 slots over four
    /// identical exports). Retrying does not help, because the empty result is then cached; the
    /// bad load has to be prevented rather than repeated. Only this walk is serialised, so mesh
    /// conversion, PSK writing and texture decode -- the actual cost of an export -- stay
    /// parallel.
    ///
    /// It takes <see cref="AssetLoader.Gate"/> and not a lock of its own, which it did until
    /// 2026-08-03. A private lock here excluded other material walks and nothing else, so this walk
    /// still raced every `AssetLoader` package load -- two mutexes over one cache, which is no
    /// mutex. The symptom survived in a quieter form: `--regress` reported a changed game roughly
    /// once per four-game run, always materials vanishing, and the diff showed the tell --
    /// identical geometry with UVs left at 1.007..1.99 instead of the remapped 0.336..0.663,
    /// because a material that failed to resolve has no diffuse to derive an atlas grid from.
    /// </summary>
    public static Dictionary<string, (string Path, int W, int H)> AtlasTextures(
        UObject mesh, out Unresolved unresolved)
    {
        lock (AssetLoader.Gate) return AtlasTexturesCore(mesh, out unresolved);
    }

    private static Dictionary<string, (string Path, int W, int H)> AtlasTexturesCore(
        UObject mesh, out Unresolved unresolved)
    {
        int nullMat = 0, threw = 0, noDiffuse = 0, noSize = 0, recovered = 0;
        var byName = new Dictionary<string, (string, int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var mat in AssetLoader.MaterialsOf(mesh))
        {
            if (mat is null) { nullMat++; continue; }
            // Same lookup the viewport binds with, so the grid is derived from the same
            // texture the shader samples. Dimensions come from the properties -- decoding an
            // 8K atlas to learn its width would make a full export unbearable.
            // Packed masks are excluded exactly as the viewport excludes them. Some games author
            // them at a different resolution from the diffuse -- one ORME is
            // 12288x2048 against a 6144x1024 diffuse -- so measuring one picks a 12x2 grid
            // where the atlas is 6x1, and every UV lands at half scale in both axes.
            var diffuse = CMaterialParams2.Diffuse[0].Where(n => !UvAtlas.IsPackedMap(n)).ToArray();
            var p = new CMaterialParams2();
            try { mat.GetParams(p, EMaterialFormat.FirstLayer); } catch { threw++; continue; }
            if (!p.TryGetTexture2d(out var tex, diffuse) || tex is null)
            {
                try { mat.GetParams(p = new CMaterialParams2(), EMaterialFormat.AllLayers); }
                catch { threw++; continue; }
                if (!p.TryGetTexture2d(out tex, diffuse) || tex is null)
                {
                    // Last resort: CUE4Parse's own answer. When a material's diffuse is not bound
                    // to a parameter this list knows, CUE4Parse walks the expression graph and
                    // records what it found under PM_Diffuse. That is a better source than a name
                    // table, and it is what `--matdump` prints -- so without this the manifest
                    // claims "no diffuse" for a slot whose diffuse the tool can plainly name.
                    //
                    // A cloaked character is the case: its cloak, hood and cloth materials
                    // bind their colour maps through a material function, so every garment on the
                    // character came out with a blank diffuse column while the textures themselves
                    // exported fine. The user gets the file and no record of which slot it fills.
                    //
                    // Still excluded: a packed map. The fallback can land on one, and measuring an
                    // ORME against a diffuse-sized atlas is what puts every UV at half scale.
                    if (!p.TryGetTexture2d(out tex, CMaterialParams2.FallbackDiffuse) || tex is null ||
                        UvAtlas.IsPackedMap(tex.Name))
                    { noDiffuse++; continue; }
                }
            }
            // The COOKED size, not ImportedSize. ImportedSize is the pre-cook authoring size and
            // Some games cook down: one armour texture imports at 6144x4096 and ships 3072x2048.
            // Using the imported size picks a 6x4 grid where the shipped atlas is 3x2, which
            // still lands every UV inside 0..1 -- at half scale, in the wrong tile. "Inside
            // 0..1" is necessary and nowhere near sufficient. Read it off PlatformData rather
            // than GetFirstMip(), which needs mip bulk data this path has not loaded and
            // returns null -- leaving every section unmapped and looking untouched.
            var w = tex.PlatformData.SizeX;
            var h = tex.PlatformData.SizeY;
            // Name the texture the grid was measured from. Which map got picked is the whole
            // question when a grid comes out wrong, and it cannot be inferred from the result.
            if (w > 0 && h > 0)
            {
                byName[mat.Name] = (tex.GetPathName(), w, h);
                Measured?.Invoke($"{mat.Name} <- {tex.Name} {w}x{h}");
            }
            else noSize++;
        }
        unresolved = new Unresolved(nullMat, threw, noDiffuse, noSize, recovered);
        return byName;
    }

    // MaterialMap was here: slot index, material name and diffuse path per slot, used only to
    // build materials.tsv in a second pass over every exported asset. It is deleted rather than
    // left unused, because it is a loaded gun -- it resolved materials OUTSIDE ResolveLock and
    // killed a 74-minute extraction with an AccessViolationException from inside CUE4Parse,
    // which .NET Core does not permit catching. AtlasGrids now records the same facts during the
    // pass that already resolves every material, and materials.tsv is generated from that.

    // The walk itself moved to AssetLoader.MaterialsOf, because MeshTextures had a byte-identical
    // private copy of it running with no lock at all. One operation, one place, and it asserts the
    // gate rather than trusting a comment.

    /// <summary>
    /// Points in a written mesh, from the chunk header alone.
    ///
    /// Public because a level's cost is decided per MESH and paid per PLACEMENT: some games ship
    /// Nanite-only geometry with stub fallbacks — a 63 cm ceiling light is 105,759 verts against a
    /// 126-vert non-Nanite proxy — so one prop placed 1,598 times dominates a whole level. A15
    /// assembles to 513 M polygons in 3ds Max, and knowing which meshes that is turns an
    /// unusable scene into a choice about what to import.
    /// </summary>
    public static long PointCount(string pskPath)
    {
        var chunks = ReadChunks(FreshFile.ReadAllBytes(pskPath));
        return chunks.TryGetValue("PNTS0000", out var c) ? c.Count : 0;
    }

    internal readonly record struct Chunk(int Offset, int Size, int Count);

    internal static Dictionary<string, Chunk> ReadChunks(byte[] d)
    {
        var map = new Dictionary<string, Chunk>(StringComparer.Ordinal);
        for (var off = 0; off + 32 <= d.Length;)
        {
            var id = Encoding.ASCII.GetString(d, off, 20).Split('\0')[0];
            var size = BitConverter.ToInt32(d, off + 24);
            var count = BitConverter.ToInt32(d, off + 28);
            if (size < 0 || count < 0 || off + 32L + (long) size * count > d.Length) break;
            map[id] = new Chunk(off + 32, size, count);
            off += 32 + size * count;
        }
        return map;
    }

    internal static List<string> MaterialNames(byte[] d, Dictionary<string, Chunk> chunks)
    {
        var list = new List<string>();
        if (!chunks.TryGetValue("MATT0000", out var c)) return list;
        for (var i = 0; i < c.Count; i++)
        {
            var o = c.Offset + i * c.Size;
            var end = Array.IndexOf(d, (byte) 0, o, Math.Min(64, d.Length - o));
            list.Add(Encoding.ASCII.GetString(d, o, (end < 0 ? o + 64 : end) - o));
        }
        return list;
    }
}
