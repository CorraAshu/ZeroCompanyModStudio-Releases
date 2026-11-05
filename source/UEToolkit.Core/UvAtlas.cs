namespace UEToolkit.Core;

/// <summary>
/// UDIM atlas addressing, in one place because two copies would drift.
///
/// UV0 spans several tiles (U 0..8, V -1..1) rather than 0..1, and the cooked texture is those
/// tiles flattened into a single image. The viewport remaps in its vertex shader; export has to
/// bake the same remap into the written UVs, or the coordinates leave the 0..1 range and a DCC
/// tiles the atlas instead of selecting a region of it.
/// </summary>
public static class UvAtlas
{
    /// <summary>
    /// The active game's atlas settings. **Off until a profile enables it.**
    ///
    /// These were `const`s — one studio's authoring facts asserted as universal truths, applied to every
    /// game unconditionally. The remap is not inert on other titles: a trim sheet spanning
    /// U 0..4 on a square texture reads as a 4x4 grid, passes every guard, and gets its U divided
    /// by four. The scheme only escapes that because its atlases keep V within roughly -1..1, so
    /// vertically-tiling maps fail the V check — its convention protecting us, not this code
    /// being careful.
    /// </summary>
    public static AtlasSettings Settings { get; set; } = new();

    /// <summary>The usual authoring tile, but NOT universal — see <see cref="Fit"/>.</summary>
    public static int TileSize => Settings.TileSize;

    /// <summary>These atlases end at this V; the origin follows from the row count.</summary>
    public static float VTop => Settings.VTop;

    /// <summary>Islands bleed a fraction past a tile edge; without this V=1.008 buys a row.</summary>
    public static float Slack => Settings.Slack;

    /// <summary>Whether to remap at all. A disabled atlas leaves every UV exactly as authored.</summary>
    public static bool Enabled => Settings.Enabled;

    /// <summary>
    /// Is this material parameter a packed mask rather than a colour map?
    ///
    /// Rejects by the PARAMETER name, not the texture filename: "Mesh - CO" is the material
    /// stating what the input is, while a filename rule guesses at authoring intent.
    ///
    /// It matters for the atlas grid as well as for what gets drawn. Some games author packed
    /// maps at a different resolution from the diffuse -- one character's ORME/normal/masks are
    /// 12288x2048 against a 6144x1024 diffuse -- so measuring the wrong one picks a 12x2 grid
    /// where the atlas is 6x1 and every UV comes out at half scale in both axes.
    /// </summary>
    public static bool IsPackedMap(string param)
    {
        var tail = param.Split(' ', '-', '_').LastOrDefault(s => s.Length > 0) ?? param;
        return Settings.PackedMapSuffixes.Contains(tail, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is this texture declared to be a single image regardless of what its UVs imply?
    ///
    /// One place, called by BOTH the export pooling and the whole-game scan. Two callers deciding
    /// this separately is the failure this project has hit four times in one week — the viewport
    /// and the export disagreeing about a LOD, two decoders disagreeing about an untracked node,
    /// a component class and its actor disagreeing about an HLOD proxy.
    /// </summary>
    public static bool ForcedFlat(string? texturePath) =>
        texturePath is { Length: > 0 } p &&
        Settings.NotAnAtlas.Any(s => s.Length > 0 &&
                                     p.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    /// <summary>The grid a texture's dimensions imply, with no reference to any mesh.</summary>
    public static (float Cols, float Rows) FromTexture(int w, int h)
    {
        if (w <= 0 || h <= 0 || w == h) return (1f, 1f);
        var tile = Gcd(w, h);
        if (tile <= 0) return (1f, 1f);
        return (w / (float) tile, h / (float) tile);
    }

    /// <summary>
    /// The tile grid that actually contains these UVs, keeping tiles SQUARE.
    ///
    /// 1024 is the usual tile and is not universal: one body atlas is 4096x1024
    /// holding 8x2 tiles of 512. Assuming 1024 gives 4x1, the section's U extent of 8 does not
    /// fit, and the atlas gets abandoned as "tiling" — which samples the whole 4:1 map across
    /// every part and smears it into vertical streaks. So start at exactly the tile
    /// <see cref="FromTexture"/> would pick and halve from there: the first candidate is always
    /// today's answer, so this can only ever SHRINK the tile and nothing that works changes.
    /// </summary>
    public static (float Cols, float Rows) Fit(int w, int h,
        float uMin, float uMax, float vMin, float vMax)
    {
        // Start at the LARGEST square tile the dimensions allow, which is their gcd, and halve
        // from there. Starting at TileSize instead was wrong whenever the gcd is bigger: a
        // 12288x2048 atlas is 6x1 tiles of 2048, and anchoring on 1024 called it 12x2
        // and put every UV at half scale in both axes. 1024 is the USUAL tile, not a floor.
        for (var tile = Gcd(w, h); tile >= 64; tile /= 2)
        {
            if (w % tile != 0 || h % tile != 0) continue;
            float cols = w / (float) tile, rows = h / (float) tile;
            if (uMax <= cols + Slack && uMin >= -Slack &&
                vMax <= VTop + Slack && vMin >= VTop - rows - Slack)
                return (cols, rows);
        }
        return FromTexture(w, h);
    }

    /// <summary>
    /// The atlas grid for one section, and whether it is an atlas at all.
    ///
    /// **The grid is a property of the TEXTURE, never of the mesh.** A character part covers
    /// only a slice of the atlas — one character's legs sit in columns 2-3 of the top row — so measuring
    /// the grid from the part's own UV extent reads that slice as if it were the whole atlas and
    /// stretches the texture over it. Only a SQUARE texture, which carries no shape evidence,
    /// falls back to letting the UVs decide.
    /// </summary>
    /// <param name="maxUSpan">
    /// The widest span any SINGLE slot covers, as opposed to the pooled extent. Only used for a
    /// square texture, where it is the difference between the two conventions — see below.
    /// </param>
    public static (float Cols, float Rows, float VMinTile, bool Enabled) Grid(
        int texW, int texH, float uMin, float uMax, float vMin, float vMax,
        float maxUSpan = 0f, float maxVSpan = 0f)
    {
        // The one gate. A game that has not opted in gets its UVs back untouched, rather than
        // whatever grid these numbers happen to imply.
        if (!Settings.Enabled) return (1f, 1f, 0f, false);

        var (cols, rows) = texW > 0 && texW != texH
            ? Fit(texW, texH, uMin, uMax, vMin, vMax)
            : FromTexture(texW, texH);

        // A square texture's dimensions cannot reveal its grid, so it has to come from the UVs --
        // and WHICH UVs is the whole question, because two conventions look identical pooled.
        //
        //   Convention A packs several tiles INTO one square image, and a single section spans
        //   them all: one weapon part covers U 0..2 by itself, so its own span is the evidence.
        //
        //   Convention B binds ONE image and offsets the UVs to a UDIM tile index. Every
        //   individual slot spans one unit; only the POOL across meshes reaches further, because
        //   different meshes sit in different tiles. One texture pools to U 0.005..5.994 across 291
        //   slots while no slot spans more than 1 -- read as a 6x6 atlas it divided one backpack's
        //   UVs by six, and the user saw it in Max.
        //
        // So the square case keys on the widest SINGLE slot. Convention A is unaffected: its
        // evidence was always one section's own span. A one-unit slot at U 1..2 then gets no grid,
        // keeps its offset, and the DCC's repeat wrap samples the right texels -- which is exactly
        // why one material came out correct while its neighbours did not.
        if (texW > 0 && texW == texH)
        {
            var uSpan = maxUSpan > 0f ? maxUSpan : uMax - MathF.Floor(uMin);
            var vSpan = maxVSpan > 0f ? maxVSpan : vMax - MathF.Floor(vMin);
            cols = rows = MathF.Max(1f, MathF.Max(
                MathF.Ceiling(uSpan - Slack), MathF.Ceiling(vSpan - Slack)));
        }

        var vMinTile = VTop - rows;
        var enabled = (cols > 1f || rows > 1f)
                      && uMax <= cols + Slack && uMin >= -Slack
                      && vMax <= VTop + Slack && vMin >= vMinTile - Slack;
        return (cols, rows, vMinTile, enabled);
    }

    /// <summary>
    /// One UV into the flattened atlas's 0..1 space. Mirrors the vertex shader exactly.
    ///
    /// The flattened atlas stacks rows OPPOSITE to UE's V direction, so the tile row index is
    /// inverted; within a tile V still runs top to bottom. Getting that inversion wrong is
    /// invisible on a 1-row atlas and flips every part on a 2-row one.
    ///
    /// The tile row is CLAMPED to the grid, and the fraction is then measured from the tile that
    /// was actually chosen. Islands bleed a hair past an edge -- V = -0.001, V = 1.0 exactly --
    /// and an unclamped row index sends those to the far side of the atlas, inverted: on a
    /// one-row grid V = -0.001 came out at 1.999 and V = 0 came out at 2.0. Both were hidden
    /// while one grid was misread as two rows, where the same slip lands on a plausible
    /// 1.0 and 0.5. Clamping also makes the old "nudge the section maximum" special case
    /// unnecessary: the top edge of the top tile now resolves to the top tile by construction.
    /// </summary>
    public static (float U, float V) Remap(float u, float v, float cols, float rows, float vMinTile)
    {
        var tileRow = Math.Clamp(MathF.Floor(v) - vMinTile, 0f, rows - 1f);
        var fv = v - (tileRow + vMinTile);
        var imgRow = rows - 1f - tileRow;
        return (u / cols, (imgRow + fv) / rows);
    }
}
