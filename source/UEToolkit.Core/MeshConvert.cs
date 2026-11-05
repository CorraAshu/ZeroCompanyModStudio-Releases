using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;

namespace UEToolkit.Core;

/// <summary>
/// Converting a static mesh, with the Nanite decision made in ONE place.
///
/// A Nanite mesh ships two things: the Nanite data, which is the real geometry, and a non-Nanite
/// fallback for hardware that cannot run it. Some games' fallbacks are not reduced meshes, they are
/// proxies — one helmet's is 106 vertices against 4,060 real ones, and some are three-vertex
/// stubs. `TryConvert` with no argument returns the fallback.
///
/// That is not merely "lower quality". A proxy carries **different UVs and a different material
/// slot list**: one body mesh really covers `U 1.005..1.946` of its atlas texture through the armour
/// variant of its material, and its proxy covers `U 0.024..0.944` through the base material. So
/// anything measured off the fallback is not a coarser answer about the mesh, it is a precise
/// answer about a different mesh — and it looks entirely reasonable, which is why it survived.
///
/// The viewport had this right and nothing else did. The atlas library, written later, called plain
/// `TryConvert`, pooled every part's proxy UVs, found nothing outside 0..1, and concluded
/// one body texture was not an atlas — the exact texture the whole table was built to get right. The
/// rule lives here now so a third caller cannot get it wrong a third time.
/// </summary>
public static class MeshConvert
{
    /// <summary>
    /// The mesh as the EXPORTER writes it, and whether that came from Nanite.
    ///
    /// **Prefer Nanite whenever it is genuinely bigger, not merely twice as big.** The exporter
    /// always uses `ENaniteMeshFormat.OnlyNaniteLOD`, so any threshold here is a threshold at which
    /// the viewport starts showing something the export does not ship.
    ///
    /// The old rule demanded `nanite > best * 2` to avoid paying for a second conversion. That is a
    /// performance guard with a correctness cost, and it was not hypothetical:
    /// one helmet part is 7,526 Nanite verts against a 4,027-vert normal LOD —
    /// 1.87x, just under the bar — so it rendered and measured as the normal LOD while the export
    /// wrote Nanite. Its UVs differ between the two (`V -0.131..1.018` against `0.007..0.989`), which
    /// silently pushed the body texture from 9x1 to 18x2 and put 24 parts at half
    /// scale in the whole-game atlas table.
    ///
    /// The `> best` comparison is still what rejects a degenerate proxy, and it is still decided
    /// against real converted geometry rather than a magic vertex count — a cooked proxy can be any
    /// size, so "≤64 means proxy" silently keeps the stub. Only the 2x margin is gone.
    /// </summary>
    public static CStaticMesh? Static(UStaticMesh mesh, out bool usedNanite)
    {
        usedNanite = false;
        CStaticMesh? converted = null;
        if (mesh.TryConvert(out var normal) && normal.LODs.Count > 0) converted = normal;

        var best = converted?.LODs.Max(l => l.Verts?.Length ?? 0) ?? 0;

        // The header is only a cheap way to skip meshes with no Nanite data at all; the decision
        // below is made on what actually converted.
        var naniteVerts = (int) (mesh.RenderData?.NaniteResources?.NumInputVertices ?? 0);

        if (naniteVerts > 0 &&
            mesh.TryConvert(out var nanite, ENaniteMeshFormat.OnlyNaniteLOD) &&
            nanite.LODs.Count > 0 &&
            nanite.LODs.Max(l => l.Verts?.Length ?? 0) > best)
        {
            converted = nanite;
            usedNanite = true;
        }
        return converted;
    }
}
