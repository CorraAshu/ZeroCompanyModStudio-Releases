using System.Numerics;

namespace UEToolkit.Core;

/// <summary>
/// An exported `.psk`/`.pskx` read back as renderable geometry.
///
/// Everything else that reads a PSK here answers a question about it — are the UVs in range, do the
/// bones agree, which material does each section use. This one hands back the mesh itself, so the
/// written file can be **rendered** and compared against the game's own mesh.
///
/// That comparison is the only way to make "the UVs are correct" measured rather than argued.
/// `--regress` proves nothing MOVED; `PskValidate` proves the UVs are *plausible* — inside 0..1,
/// filling their atlas. Neither can catch a grid that is wrong but plausible, and the atlas grid is
/// DERIVED from texture dimensions because no material states one (`--matdump`: 427 scalars,
/// not one names a layout). The viewport remaps in its vertex shader from the same `UvAtlas` code
/// the export bakes into the file, so rendering both and diffing the pixels is an independent check
/// of the derivation against itself.
/// </summary>
public sealed class PskGeometry
{
    /// <summary>Per WEDGE, in UE space. UVs are per-wedge, so positions are expanded to match.</summary>
    public Vector3[] Positions = [];
    public Vector3[] Normals = [];
    public Vector2[] Uvs = [];

    /// <summary>Triangle indices into the wedge arrays, grouped so each section is contiguous.</summary>
    public uint[] Indices = [];

    /// <summary>One per material actually used, in the order the indices are grouped.</summary>
    public List<(string Material, int FirstIndex, int IndexCount)> Sections = [];

    public string? Error;

    /// <summary>
    /// **PSK stores positions with Y negated** — the ActorX exporter mirrors on write. Undoing it
    /// puts the mesh back in UE space, which is where the viewport's `uModel` expects it and the
    /// only way the two renders can be compared at all.
    ///
    /// Taken from `tools/psk2fbx.py`, which states the same convention and was verified in 3ds Max
    /// by the user ("the body is not inside out and looks proper to how it is with the psk").
    /// Guessing handedness has cost this project several days; this is copied, not derived.
    ///
    /// Winding is left as stored. Negating Y flips handedness, so faces wind the other way — which
    /// would matter if back-face culling were on, and it is off by default here precisely because
    /// some games' parts are hollow single-sided shells.
    /// </summary>
    /// <summary>
    /// Normals get the same mirror as positions, on the reasoning that a writer which mirrors the
    /// geometry must mirror its normals to stay consistent.
    ///
    /// **This is NOT verified.** `--pskcompare` renders unlit, so it never exercises a normal, and
    /// testing both conventions against a lit render moved the mean by 0.7/255 — far too little to
    /// call. Recorded as an assumption rather than left to look like a measured fact; anything that
    /// renders an imported PSK *lit* should settle it first.
    /// </summary>
    public static bool NegateNormalY = true;

    public static PskGeometry Read(string path)
    {
        var g = new PskGeometry();
        byte[] d;
        try { d = File.ReadAllBytes(path); }
        catch (Exception e) { g.Error = e.Message; return g; }

        var chunks = PskUv.ReadChunks(d);
        var points = chunks.GetValueOrDefault("PNTS0000");
        var wedges = chunks.GetValueOrDefault("VTXW0000");
        var normals = chunks.GetValueOrDefault("VTXNORMS");
        var names = PskUv.MaterialNames(d, chunks);
        var faces = chunks.TryGetValue("FACE3200", out var f32) ? f32 : chunks.GetValueOrDefault("FACE0000");

        if (points.Count == 0 || wedges.Count == 0 || faces.Count == 0)
        {
            g.Error = $"not renderable geometry: {points.Count} point(s), {wedges.Count} wedge(s), {faces.Count} face(s)";
            return g;
        }

        g.Positions = new Vector3[wedges.Count];
        g.Normals = new Vector3[wedges.Count];
        g.Uvs = new Vector2[wedges.Count];
        var wedgeMaterial = new byte[wedges.Count];

        for (var i = 0; i < wedges.Count; i++)
        {
            var o = wedges.Offset + i * wedges.Size;
            var pt = BitConverter.ToInt32(d, o);
            if (pt < 0 || pt >= points.Count) pt = 0;

            var po = points.Offset + pt * points.Size;
            g.Positions[i] = new Vector3(
                BitConverter.ToSingle(d, po),
                -BitConverter.ToSingle(d, po + 4),        // undo the exporter's mirror
                BitConverter.ToSingle(d, po + 8));

            g.Uvs[i] = new Vector2(BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));
            wedgeMaterial[i] = d[o + 12];

            if (normals.Count == wedges.Count)
            {
                var no = normals.Offset + i * normals.Size;
                var ny = BitConverter.ToSingle(d, no + 4);
                g.Normals[i] = new Vector3(
                    BitConverter.ToSingle(d, no),
                    NegateNormalY ? -ny : ny,
                    BitConverter.ToSingle(d, no + 8));
            }
        }

        // Group triangles by material so each section is one contiguous index range, which is what
        // the renderer draws. A PSK's faces are in authoring order and need not be grouped at all.
        var wide = faces.Size >= 18;
        var byMaterial = new Dictionary<byte, List<uint>>();
        var tri = new uint[3];
        for (var i = 0; i < faces.Count; i++)
        {
            var o = faces.Offset + i * faces.Size;
            // NOT stackalloc. A stack allocation inside a loop is not freed until the method
            // returns, so this leaked 12 bytes per face and overflowed the stack on any mesh past
            // roughly 80k triangles -- the single-mesh test passed at 20,230 and the sweep died.
            var ok = true;
            for (var k = 0; k < 3; k++)
            {
                var w = wide ? BitConverter.ToInt32(d, o + k * 4) : BitConverter.ToUInt16(d, o + k * 2);
                if (w < 0 || w >= wedges.Count) { ok = false; break; }
                tri[k] = (uint) w;
            }
            if (!ok) continue;

            // The face carries its own material index; fall back to the first corner's wedge,
            // which is what the material actually belongs to when a writer leaves the face blank.
            var matOffset = wide ? 12 : 6;
            var mat = matOffset < faces.Size ? d[o + matOffset] : wedgeMaterial[tri[0]];
            if (names.Count > 0 && mat >= names.Count) mat = wedgeMaterial[tri[0]];

            if (!byMaterial.TryGetValue(mat, out var list)) byMaterial[mat] = list = new List<uint>();
            list.Add(tri[0]); list.Add(tri[1]); list.Add(tri[2]);
        }

        var indices = new List<uint>();
        foreach (var (mat, list) in byMaterial.OrderBy(k => k.Key))
        {
            g.Sections.Add((mat < names.Count ? names[mat] : $"material_{mat}", indices.Count, list.Count));
            indices.AddRange(list);
        }
        g.Indices = indices.ToArray();

        if (g.Indices.Length == 0) g.Error = "no usable triangles";
        return g;
    }
}
