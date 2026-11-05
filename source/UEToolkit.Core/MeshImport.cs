namespace UEToolkit.Core;

/// <summary>
/// Turn a mesh read out of an FBX into the vertices a GPU draws.
///
/// The two are not the same shape and the difference is the whole job. An FBX stores a mesh the
/// way a modeller thinks about it: positions at CONTROL POINTS, normals and UVs at CORNERS, so one
/// point can have four normals where four faces meet at a hard edge. A GPU draws one attribute set
/// per vertex, so every corner whose normal or UV differs has to become its own vertex, and the
/// triangle list has to be rebuilt around the new numbering.
///
/// Skipping that split is not a subtle loss. Averaging the normals at a hard edge is what makes an
/// imported helmet look like it was left in the sun, and it never throws.
/// </summary>
public static class MeshImport
{
    /// <summary>
    /// How the file's axes map onto Unreal's, and how big a file unit is.
    ///
    /// Held as data rather than baked in because it is the single decision most likely to be
    /// wrong, and the one whose wrongness looks like something else -- a helmet lying on its side
    /// reads as a broken rig, not as a swapped axis. Kept alongside a <see cref="Fit"/> that
    /// MEASURES it against the game's own mesh instead of arguing from first principles.
    /// </summary>
    public sealed record Basis(int XFrom, int YFrom, int ZFrom,
                               float XSign, float YSign, float ZSign, float Scale,
                               float OffsetX = 0, float OffsetY = 0, float OffsetZ = 0,
                               float[]? Node = null)
    {
        /// <summary>
        /// The mapping a Y-up FBX needs, MEASURED rather than derived.
        ///
        /// Three attempts to work it out from first principles gave three answers. The file's own
        /// GlobalSettings say up is Y and a unit is 2.54 cm, which would make a helmet eight
        /// millimetres tall. Fitting the bounding box cannot separate the two horizontal axes when
        /// the imported helmet is a different shape from the game's. Looking at a render of both
        /// gave opposite answers from two of the three views.
        ///
        /// What settled it was vertex DENSITY. The front of a helmet carries the visor and the
        /// vents and holds far more vertices than the smooth back, so the centroid sits forward of
        /// the bounding box's centre, and the sign of that offset survives the two helmets being
        /// different shapes:
        ///
        ///     lean          X          Y          Z
        ///     game     0.0002     0.0795    -0.1028
        ///     import   0.0003     0.0869    -0.1088     under this mapping
        ///
        /// Both axes agree in sign AND magnitude, and X is nearly zero in both -- which is itself
        /// the finding that this game's helmets face along Y, not X.
        ///
        /// The determinant is negative, so this MIRRORS and the winding is reversed to match. That
        /// is correct rather than a bug: FBX is right-handed and Unreal is left-handed, and a
        /// mapping between them that preserves handedness has to be wrong somewhere.
        /// </summary>
        public static Basis YUp(float scale) => new(0, 2, 1, 1, 1, 1, scale);

        /// <summary>
        /// A mapping written the way it reads: "-X-ZY" means Unreal's X comes from the file's
        /// negated X, Unreal's Y from its negated Z, Unreal's Z from its Y.
        ///
        /// Spelled out rather than offered as a menu of named conventions, because the conventions
        /// are what keep being wrong. Every FBX exporter has its own idea of "Y-up", and the only
        /// reliable way to settle it is to try one, look at the render, and write down what
        /// worked -- which needs a way to say what was tried.
        /// </summary>
        public static Basis? Parse(string text, float scale)
        {
            var from = new int[3];
            var sign = new float[3];
            var at = 0;
            for (var i = 0; i < 3; i++)
            {
                sign[i] = 1;
                while (at < text.Length && (text[at] is '-' or '+'))
                { if (text[at] == '-') sign[i] = -sign[i]; at++; }
                if (at >= text.Length) return null;
                from[i] = "XYZ".IndexOf(char.ToUpperInvariant(text[at]));
                if (from[i] < 0) return null;
                at++;
            }
            if (at != text.Length || from.Distinct().Count() != 3) return null;
            return new Basis(from[0], from[1], from[2], sign[0], sign[1], sign[2], scale);
        }

        /// <summary>
        /// The mapping ONE FILE asks for: the node's own rotation and move as well as its scale,
        /// with the axis mapping laid over the top.
        ///
        /// In one place because four callers each did three of the four, and the move was the one
        /// none of them did. Moving an object in a modelling package sets Lcl Translation and
        /// leaves the vertices exactly where they were, the same way resizing sets Lcl Scaling and
        /// rotating sets Lcl Rotation. A DLT-20A aligned in Max against the rifle it stands beside
        /// came into the game 6.85 cm above the hands holding it -- which reads as a rigging fault,
        /// or as a modelling one, and is neither.
        ///
        /// THE MAPPING NAMES THE FILE'S SCENE, NOT ITS VERTICES. Naming one used to switch the
        /// node's rotation off, which pushed the artist into folding that rotation into the letters
        /// they chose; the move, which the file applies AFTER the rotation, then has no frame left
        /// to be expressed in and lands on the wrong axis. So the rotation is always applied, and
        /// the mapping describes what the artist sees in the viewport -- the only frame they can
        /// reason about, and the frame the move is already written in.
        /// </summary>
        /// <param name="index">
        /// WHICH OBJECT. A file holds one node per object and each states that object's own
        /// placement, so a basis built from the first and applied to all of them puts every other
        /// piece wherever the first one's transform happens to send it.
        ///
        /// Measured, on a clone torso with a pauldron bolted on: the body's node was identity and
        /// the pad's carried a scale of 2.664. The pad came into the game at 1/2.664 of its size
        /// AND -- because scaling pivots about the origin -- at 1/2.664 of its height, which put a
        /// shoulder pad at the character's knees, buried inside the legs and invisible. Every
        /// number the build printed was correct, because every number described object one.
        /// </param>
        /// <param name="unitScale">
        /// The file's own unit, which is the FIRST node's scale, and which is global to the file.
        /// The per-object rescale below must divide by THIS and not by each object's own scale --
        /// dividing by its own would normalise every object to the same size and throw away the
        /// relative sizing that is the whole point of reading them separately. Zero means "this
        /// node is the unit", which is what a single-object file has always done.
        /// </param>
        public static Basis? ForFile(FbxReader reader, float scale, string? mapping,
                                     out string? why, Action<string>? log = null,
                                     int index = 0, float unitScale = 0)
        {
            why = null;
            var basis = YUp(scale);
            if (!string.IsNullOrWhiteSpace(mapping))
            {
                if (Parse(mapping, scale) is { } parsed) basis = parsed;
                else { why = $"'{mapping}' is not an axis mapping; try XZY or -X-ZY"; return null; }
            }

            var props = reader.MeshNodeProperties(index);
            (double X, double Y, double Z) Of(string name, double blank)
            {
                foreach (var (n, v) in props)
                    if (n == name && v.Length >= 3) return (v[0], v[1], v[2]);
                return (blank, blank, blank);
            }
            static bool Given((double X, double Y, double Z) v, double blank) =>
                Math.Abs(v.X - blank) > 1e-9 || Math.Abs(v.Y - blank) > 1e-9 ||
                Math.Abs(v.Z - blank) > 1e-9;

            var move  = Of("Lcl Translation", 0);
            var spin  = Of("Lcl Rotation", 0);
            var size  = Of("Lcl Scaling", 1);
            var pre   = Of("PreRotation", 0);
            var post  = Of("PostRotation", 0);
            var rOff  = Of("RotationOffset", 0);
            var rPiv  = Of("RotationPivot", 0);
            var sOff  = Of("ScalingOffset", 0);
            var sPiv  = Of("ScalingPivot", 0);
            var gMove = Of("GeometricTranslation", 0);
            var gSpin = Of("GeometricRotation", 0);
            var gSize = Of("GeometricScaling", 1);

            // FBX's own order, written left to right, so the rightmost term reaches the vertex
            // first. Every pivot appears twice because that is what a pivot IS: move to it, do the
            // thing, move back.
            var m = Shift(move.X, move.Y, move.Z);
            m = Compose(m, Shift(rOff.X, rOff.Y, rOff.Z));
            m = Compose(m, Shift(rPiv.X, rPiv.Y, rPiv.Z));
            m = Compose(m, Turn(pre.X, pre.Y, pre.Z));
            m = Compose(m, Turn(spin.X, spin.Y, spin.Z));
            m = Compose(m, Undo(Turn(post.X, post.Y, post.Z)));
            m = Compose(m, Shift(-rPiv.X, -rPiv.Y, -rPiv.Z));
            m = Compose(m, Shift(sOff.X, sOff.Y, sOff.Z));
            m = Compose(m, Shift(sPiv.X, sPiv.Y, sPiv.Z));
            m = Compose(m, Sized(size.X, size.Y, size.Z));
            m = Compose(m, Shift(-sPiv.X, -sPiv.Y, -sPiv.Z));
            m = Compose(m, Shift(gMove.X, gMove.Y, gMove.Z));
            m = Compose(m, Turn(gSpin.X, gSpin.Y, gSpin.Z));
            m = Compose(m, Sized(gSize.X, gSize.Y, gSize.Z));

            // A PINNED meshScale says the file's own scale is wrong -- which means its UNIT is
            // wrong, and a wrong unit is wrong for the moves and the pivots too, not only for the
            // vertices. So the whole matrix is rescaled, not the scale term on its own.
            // The FILE's unit, not this object's scale. See unitScale above -- getting this
            // wrong is silent and it flattens every object to one size.
            var unit = unitScale != 0 ? unitScale : (float) size.X;
            var k = unit != 0 ? scale / unit : 1f;
            if (Math.Abs(k - 1f) > 1e-6f)
            {
                for (var i = 0; i < m.Length; i++) m[i] *= k;
                if (Math.Abs(scale - (float) size.X) > 1e-4f)
                    log?.Invoke($"      meshScale pins {scale:F4} over the file's own {unit:F4}, " +
                                $"so its moves and pivots are rescaled by {k:F4} with it");
            }

            basis = basis with { Node = m };

            // WHAT THE NODE ACTUALLY CARRIED, named. Everything listed here moved the mesh without
            // moving a vertex, and the list is the answer to "why is it not where I put it".
            var carried = new List<string>();
            if (Given(spin, 0)) carried.Add($"rotation ({spin.X:F3}, {spin.Y:F3}, {spin.Z:F3})");
            if (Given(pre, 0)) carried.Add($"PreRotation ({pre.X:F3}, {pre.Y:F3}, {pre.Z:F3})");
            if (Given(post, 0)) carried.Add($"PostRotation ({post.X:F3}, {post.Y:F3}, {post.Z:F3})");
            if (Given(move, 0)) carried.Add($"move ({move.X:F4}, {move.Y:F4}, {move.Z:F4})");
            if (Given(rOff, 0)) carried.Add("RotationOffset");
            if (Given(rPiv, 0)) carried.Add("RotationPivot");
            if (Given(sOff, 0)) carried.Add("ScalingOffset");
            if (Given(sPiv, 0)) carried.Add("ScalingPivot");
            if (Given(gMove, 0)) carried.Add("GeometricTranslation");
            if (Given(gSpin, 0)) carried.Add("GeometricRotation");
            if (Given(gSize, 1)) carried.Add("GeometricScaling");

            if (carried.Count > 0)
            {
                log?.Invoke($"      node carries: {string.Join("; ", carried)} -- all applied");
                var (mx, my, mz) = basis.Move;
                if (Math.Abs(mx) + Math.Abs(my) + Math.Abs(mz) > 1e-4f)
                    log?.Invoke($"      it puts the file's origin at ({mx:F2}, {my:F2}, {mz:F2}) cm " +
                                $"under the {basis.Name} mapping");
            }
            return basis;
        }

        public string Name =>
            $"{(XSign < 0 ? "-" : "")}{"XYZ"[XFrom]}" +
            $"{(YSign < 0 ? "-" : "")}{"XYZ"[YFrom]}" +
            $"{(ZSign < 0 ? "-" : "")}{"XYZ"[ZFrom]}";

        /// <summary>
        /// A vertex in Unreal's space: rotated, axes remapped, scaled, then MOVED twice.
        ///
        /// The offset is in centimetres and comes last, which is the only order that makes it
        /// usable. Scaling pivots about the origin, so changing the size of a helmet sitting a
        /// metre and a half up also slides it up or down -- and no single scale can get both the
        /// size and the height right. Separating them means each can be set for its own reason.
        ///
        /// The node's MOVE is the file's own, and it takes the mapping's permutation and signs but
        /// NOT its scale, because it is already stated in the units the scaled vertices land in.
        /// Scaling it as well is the difference between 6.8 centimetres and 7.7 metres.
        /// </summary>
        public (float X, float Y, float Z) Apply(float a, float b, float c)
        {
            var v = NodeApply(a, b, c);
            return (v[XFrom] * XSign + OffsetX,
                    v[YFrom] * YSign + OffsetY,
                    v[ZFrom] * ZSign + OffsetZ);
        }

        /// <summary>
        /// The node's whole transform on one point, in the file's own axes.
        ///
        /// With no matrix this is the plain scale, which is what a hand-built mapping wants -- the
        /// axis sweep in --meshfit varies the permutation and supplies its own scale afterwards.
        /// </summary>
        private float[] NodeApply(float a, float b, float c)
        {
            if (Node is not { Length: 12 } m) return [a * Scale, b * Scale, c * Scale];
            return
            [
                m[0] * a + m[1] * b + m[2]  * c + m[3],
                m[4] * a + m[5] * b + m[6]  * c + m[7],
                m[8] * a + m[9] * b + m[10] * c + m[11],
            ];
        }

        /// <summary>Where the node puts the file's own origin, in centimetres, after the mapping.</summary>
        public (float X, float Y, float Z) Move
        {
            get
            {
                var v = NodeApply(0, 0, 0);
                return (v[XFrom] * XSign, v[YFrom] * YSign, v[ZFrom] * ZSign);
            }
        }

        /// <summary>
        /// Directions take the rotation and the mirroring, never the scale and never the position.
        ///
        /// Taken as the difference between where the node puts a point and where it puts the
        /// origin, which drops the translation without having to take the matrix apart, then
        /// divided by the scale the matrix carries. Uniform scale is assumed, as it was before.
        /// </summary>
        public (float X, float Y, float Z) Rotate(float a, float b, float c)
        {
            var v = NodeApply(a, b, c);
            var o = NodeApply(0, 0, 0);
            var s = Scale != 0 ? Scale : 1f;
            var d = new[] { (v[0] - o[0]) / s, (v[1] - o[1]) / s, (v[2] - o[2]) / s };
            return (d[XFrom] * XSign, d[YFrom] * YSign, d[ZFrom] * ZSign);
        }

        /// <summary>
        /// THE WHOLE NODE TRANSFORM, composed into one matrix.
        ///
        /// FBX builds a node out of ten properties, not three:
        ///
        ///     T * Roff * Rp * Rpre * R * Rpost' * Rp' * Soff * Sp * S * Sp'
        ///
        /// followed by a geometric transform of its own, Tgeo * Rgeo * Sgeo. Every one of them
        /// moves the mesh without moving a single vertex, so every one of them is silent in
        /// exactly the same way -- nothing in the file looks wrong.
        ///
        /// Reading three and WARNING about the rest was the previous answer and it is a worse one:
        /// a warning still leaves the artist to work out what the number should have been. There
        /// is nothing exotic in the other seven -- a pivot is a translation, an offset is a
        /// translation, the geometric transform is the same three things again -- so they are all
        /// composed here and the class of fault is gone rather than announced.
        ///
        /// Loose Euler angles could not express it. A pivot has to SANDWICH the rotation or the
        /// scale, which is a multiplication and not an angle, so this is one 3x4 matrix, row-major,
        /// with the bottom row (0,0,0,1) implied.
        ///
        /// Still assumed: RotationOrder is XYZ. It is FBX's default and what Max writes, and it is
        /// stored as an integer, which this reader does not surface.
        /// </summary>
        internal static float[] Straight(float scale) => [scale, 0, 0, 0, 0, scale, 0, 0, 0, 0, scale, 0];

        internal static float[] Compose(float[] a, float[] b)
        {
            var c = new float[12];
            for (var r = 0; r < 3; r++)
            {
                for (var k = 0; k < 3; k++)
                    c[r * 4 + k] = a[r * 4] * b[k] + a[r * 4 + 1] * b[4 + k] + a[r * 4 + 2] * b[8 + k];
                c[r * 4 + 3] = a[r * 4] * b[3] + a[r * 4 + 1] * b[7] +
                               a[r * 4 + 2] * b[11] + a[r * 4 + 3];
            }
            return c;
        }

        internal static float[] Shift(double x, double y, double z) =>
            [1, 0, 0, (float) x, 0, 1, 0, (float) y, 0, 0, 1, (float) z];

        internal static float[] Sized(double x, double y, double z) =>
            [(float) x, 0, 0, 0, 0, (float) y, 0, 0, 0, 0, (float) z, 0];

        /// <summary>One Euler rotation in XYZ order: a vector takes X first, then Y, then Z.</summary>
        internal static float[] Turn(double rx, double ry, double rz)
        {
            const double rad = Math.PI / 180.0;
            double cx = Math.Cos(rx * rad), sx = Math.Sin(rx * rad);
            double cy = Math.Cos(ry * rad), sy = Math.Sin(ry * rad);
            double cz = Math.Cos(rz * rad), sz = Math.Sin(rz * rad);
            return
            [
                (float) (cz * cy), (float) (cz * sy * sx - sz * cx), (float) (cz * sy * cx + sz * sx), 0,
                (float) (sz * cy), (float) (sz * sy * sx + cz * cx), (float) (sz * sy * cx - cz * sx), 0,
                (float) (-sy),     (float) (cy * sx),                (float) (cy * cx),               0,
            ];
        }

        /// <summary>A rotation's inverse, which for a rotation is its transpose.</summary>
        internal static float[] Undo(float[] r) =>
            [r[0], r[4], r[8], 0, r[1], r[5], r[9], 0, r[2], r[6], r[10], 0];

        /// <summary>
        /// Whether this basis MIRRORS. A permutation with a negative determinant turns the mesh
        /// inside out: it renders, and every face points the wrong way, so the model is visible
        /// only from inside itself. Winding has to be reversed to compensate, which is why this is
        /// asked rather than assumed.
        /// </summary>
        public bool Mirrors
        {
            get
            {
                // The determinant of the permutation-with-signs, worked out as the sign of the
                // permutation times the product of the signs.
                var parity = (XFrom, YFrom, ZFrom) switch
                {
                    (0, 1, 2) or (1, 2, 0) or (2, 0, 1) => 1,
                    (0, 2, 1) or (2, 1, 0) or (1, 0, 2) => -1,
                    _ => 0,   // not a permutation at all
                };
                return parity * XSign * YSign * ZSign < 0;
            }
        }
    }

    /// <param name="CR">
    /// The author's own vertex colour, defaulting to WHITE -- which is the value that changes
    /// nothing, because a material multiplies by it. It was previously written as white
    /// unconditionally, so a file that painted its vertices lost that paint without a word.
    /// </param>
    public sealed record Vertex(float X, float Y, float Z,
                                float NX, float NY, float NZ,
                                float TX, float TY, float TZ, float TSign,
                                float U, float V,
                                ushort[] Bones, byte[] Weights,
                                byte CR = 255, byte CG = 255, byte CB = 255, byte CA = 255);

    /// <param name="Groups">
    /// One run of indices per material the file used, in the order the cooked mesh will hold
    /// them. A single-material import is one group covering everything, so a caller that ignores
    /// this reads exactly as it did before.
    /// </param>
    /// <param name="Spans">
    /// Where each object's vertices begin and how many it has, in object order. Needed because a
    /// per-object decision -- restoring a vertex colour mask, for one -- cannot be made on a list
    /// that has already been merged into one.
    /// </param>
    public sealed record Result(List<Vertex> Vertices, List<int> Indices,
                                List<string> BoneMap, List<string> Notes,
                                List<Group> Groups,
                                List<(int Start, int Count)>? Spans = null)
    {
        public int Triangles => Indices.Count / 3;
    }

    /// <param name="Material">The material id the FBX gave these polygons.</param>
    /// <param name="FirstIndex">Where the run starts in <see cref="Result.Indices"/>.</param>
    /// <param name="Triangles">How many triangles it holds.</param>
    public sealed record Group(int Material, int FirstIndex, int Triangles,
                                List<(int Start, int Count)>? Spans = null);

    /// <summary>
    /// Build every object in a file into ONE result, each object its own material group.
    ///
    /// Merged AFTER import, not before. Concatenating raw FBX arrays would mean reconciling
    /// per-corner normal and UV layers that different objects can map differently -- the exact
    /// thing that produces a mesh which looks melted rather than one that fails. Each object goes
    /// through the same proven path on its own, and what gets joined is uniform: a list of
    /// vertices and a list of indices.
    ///
    /// Objects keep their file order, so the body stays material 0 and what is bolted to it
    /// follows. An object carrying its own material groups keeps them, offset past the ones
    /// already used, so a two-material pad on a one-material body gives three sections and not
    /// two.
    /// </summary>
    /// <param name="bases">
    /// ONE PER OBJECT, because each object's node states its own placement. A list shorter than
    /// the object count reuses its last entry, which is what a caller with nothing per-object to
    /// say passes.
    /// </param>
    public static Result? BuildAll(IReadOnlyList<FbxReader.Mesh> meshes, IReadOnlyList<Basis> bases,
                                   IReadOnlyList<string>? boneOrder, out string? why,
                                   Action<string>? log = null)
    {
        why = null;
        if (meshes.Count == 0) { why = "the file holds no geometry"; return null; }
        if (bases.Count == 0) { why = "no basis to place the geometry with"; return null; }

        Basis For(int i) => bases[Math.Min(i, bases.Count - 1)];

        if (meshes.Count == 1)
        {
            var only = Build(meshes[0], For(0), boneOrder, out why, log);
            return only is null ? null
                 : only with { Spans = new List<(int, int)> { (0, only.Vertices.Count) } };
        }

        var vertices = new List<Vertex>();
        var indices = new List<int>();
        var bones = new List<string>();
        var notes = new List<string>();
        var groups = new List<Group>();
        var spans = new List<(int Start, int Count)>();
        var nextMaterial = 0;

        for (var i = 0; i < meshes.Count; i++)
        {
            var one = Build(meshes[i], For(i), boneOrder, out var oneWhy, log);
            if (one is null)
            {
                notes.Add($"object {i + 1} could not be read: {oneWhy}");
                continue;
            }

            // The bone map has to be SHARED, or a vertex's bone slot means one thing in the body
            // and another in the pad. Passing boneOrder makes every object index the same
            // skeleton; without one, the first object's map wins and later objects are remapped
            // by NAME onto it.
            var remap = new int[Math.Max(1, one.BoneMap.Count)];
            for (var b = 0; b < one.BoneMap.Count; b++)
            {
                var at = bones.IndexOf(one.BoneMap[b]);
                if (at < 0) { at = bones.Count; bones.Add(one.BoneMap[b]); }
                remap[b] = at;
            }

            var baseVertex = vertices.Count;
            foreach (var v in one.Vertices) vertices.Add(Rebone(v, remap));
            spans.Add((baseVertex, one.Vertices.Count));

            // COMPACTED, not offset by the file's own numbering.
            //
            // A modelling package numbers material ids however the artist's multi-sub material
            // happens to be laid out, and those numbers need not start at zero or be contiguous:
            // a holster object exported from Max carried id 1 and nothing carried id 0, so
            // offsetting by "max id + 1" produced sections naming materials 1, 2 and 3 and a mesh
            // needing FOUR slots with the first one dead. A section names its material by index,
            // so the indices have to be the ones the material array will actually have.
            //
            // Each object's own DISTINCTIONS are preserved -- two ids in one object stay two --
            // and only the numbering changes.
            var seenMaterials = new Dictionary<int, int>();
            foreach (var g in one.Groups.OrderBy(g => g.Material))
                if (!seenMaterials.ContainsKey(g.Material))
                    seenMaterials[g.Material] = nextMaterial + seenMaterials.Count;

            foreach (var g in one.Groups)
                groups.Add(new Group(seenMaterials.GetValueOrDefault(g.Material, nextMaterial),
                                     indices.Count + g.FirstIndex, g.Triangles));
            nextMaterial += Math.Max(1, seenMaterials.Count);

            foreach (var ix in one.Indices) indices.Add(ix + baseVertex);

            foreach (var n in one.Notes) notes.Add($"object {i + 1}: {n}");
        }

        if (vertices.Count == 0) { why = "no object in the file could be read"; return null; }

        log?.Invoke($"  {meshes.Count} objects merged: {vertices.Count:N0} verts, " +
                    $"{indices.Count / 3:N0} tris, {groups.Count} section(s)");
        return new Result(vertices, indices, bones, notes, groups, spans);
    }

    /// <summary>
    /// TAKE THE TEMPLATE'S VERTEX COLOURS BACK, for an object whose own carry no information.
    ///
    /// This game keeps a MASK in the vertex colour ALPHA -- the Clo010 torso has two states across
    /// 11,376 vertices, and the tint system reads them to decide which panels take the player's
    /// colour. A modelling package round trip flattens that: the same mesh exported from Max comes
    /// back with one alpha for every vertex, so the mask arrives uniformly "on" and the tint
    /// paints panels it should not. Nothing in the file is wrong; the information simply is not in
    /// it any more.
    ///
    /// It is not guessed at either. TWO GATES, and both must pass:
    ///
    ///   THE AUTHOR DID NOT AUTHOR THEM. Only an object whose colours are a single value
    ///   throughout is a candidate. Somebody who painted vertex colours meant it, and their data
    ///   is never second-guessed.
    ///
    ///   THE GEOMETRY REALLY IS THE TEMPLATE'S. Vertices are paired by POSITION within a tight
    ///   tolerance and a large majority must pair, or the object is left entirely alone.
    ///
    /// All or nothing per object, never per vertex. Partial transfer is the case that would
    /// actually hurt: a custom mesh that happens to graze the template's surface would come back
    /// half masked and half not, which is worse than either answer on its own. A fully custom
    /// piece pairs with nothing, fails the second gate, and keeps its own colours -- which is what
    /// it did before this existed.
    /// </summary>
    public static int RestoreColours(List<Vertex> vertices, int from, int count,
                                     IReadOnlyList<(float X, float Y, float Z, byte R, byte G, byte B, byte A)> template,
                                     out string? note)
    {
        note = null;
        if (count <= 0 || template.Count == 0) return 0;

        // GATE ONE: does this object's own colour say anything?
        var first = vertices[from];
        for (var i = from; i < from + count; i++)
            if (vertices[i].CR != first.CR || vertices[i].CG != first.CG ||
                vertices[i].CB != first.CB || vertices[i].CA != first.CA)
                return 0;                                   // authored -- leave it alone

        // A hash on whole centimetres, then the nearest inside it. The same shape as
        // --skincompare's pairing, and for the same reason: two meshes of the same body do not
        // sit at identical coordinates once one has been through a modelling package.
        const float near = 0.25f;                           // centimetres
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (var i = 0; i < template.Count; i++)
        {
            var key = ((int) MathF.Floor(template[i].X), (int) MathF.Floor(template[i].Y),
                       (int) MathF.Floor(template[i].Z));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
            list.Add(i);
        }

        // A SEPARATE "did it match" FLAG, not a sentinel value.
        //
        // The first version used default -- (0,0,0,0) -- to mean "no pair found", and that is a
        // REAL COLOUR here: it is the mask's OFF state, which is precisely the information being
        // recovered. So every masked-off vertex was read as unmatched, the transfer saw one
        // distinct value, decided the template had no mask either, and did nothing at all --
        // silently, because "nothing to copy" is not worth a note.
        var taken = new (byte R, byte G, byte B, byte A)[count];
        var found = new bool[count];
        var matched = 0;
        for (var v = 0; v < count; v++)
        {
            var p = vertices[from + v];
            var cell = ((int) MathF.Floor(p.X), (int) MathF.Floor(p.Y), (int) MathF.Floor(p.Z));
            var best = -1; var bestD = near * near;

            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!grid.TryGetValue((cell.Item1 + dx, cell.Item2 + dy, cell.Item3 + dz), out var list)) continue;
                foreach (var t in list)
                {
                    var d = (p.X - template[t].X) * (p.X - template[t].X) +
                            (p.Y - template[t].Y) * (p.Y - template[t].Y) +
                            (p.Z - template[t].Z) * (p.Z - template[t].Z);
                    if (d < bestD) { bestD = d; best = t; }
                }
            }

            if (best < 0) continue;
            taken[v] = (template[best].R, template[best].G, template[best].B, template[best].A);
            found[v] = true;
            matched++;
        }

        // GATE TWO: enough of it has to BE the template. Below this the object is something else
        // that happens to overlap, and it keeps what the file gave it.
        if (matched < count * 0.9)
        {
            note = $"{matched:N0} of {count:N0} vertex(es) line up with the mesh it replaces, " +
                   "which is too few to take its vertex colours -- yours are kept";
            return 0;
        }

        // A template that carries no mask either is nothing to copy; say so rather than reporting
        // a transfer that changed nothing.
        var distinct = new HashSet<(byte, byte, byte, byte)>();
        for (var v = 0; v < count; v++) if (found[v]) distinct.Add(taken[v]);
        if (distinct.Count <= 1) return 0;

        for (var v = 0; v < count; v++)
        {
            if (!found[v]) continue;
            vertices[from + v] = vertices[from + v] with
            {
                CR = taken[v].R, CG = taken[v].G, CB = taken[v].B, CA = taken[v].A,
            };
        }

        note = $"its vertex colours carried no mask, so the one on the mesh it replaces was " +
               $"copied across ({matched:N0} of {count:N0} vertex(es), {distinct.Count} value(s))";
        return matched;
    }

    /// <summary>
    /// THE MOST BONES ONE SECTION MAY NAME, and it is not 256.
    ///
    /// It was 256, because a bone index in a cooked skin weight buffer is one byte -- but only
    /// when `bUse16BitBoneIndex` is clear, and this game's HEADS set it. Measured with
    /// `--meshlods`: SK_HAAM_Hum_Hawks_Head reports "16-bit bones" at every LOD and its first
    /// section names 639 of them, while a helmet reports "8-bit bones" and names one. So the old
    /// limit was the WRITER's, taken for a format limit, and it refused every head in the game.
    ///
    /// The writer now picks the width from the data (see CookedMesh.Geometry), so this is the
    /// format's own ceiling: sixteen bits of bone index.
    /// </summary>
    public const int MaxBoneMap = 65536;

    /// <summary>A vertex with its bone slots pointed at the merged bone map.</summary>
    private static Vertex Rebone(Vertex v, int[] remap)
    {
        var bi = v.Bones.ToArray();
        for (var i = 0; i < bi.Length; i++)
            if (bi[i] < remap.Length) bi[i] = (ushort) Math.Clamp(remap[bi[i]], 0, MaxBoneMap - 1);
        return v with { Bones = bi };
    }

    /// <summary>
    /// Build the vertex and index arrays, splitting corners that disagree and welding those that
    /// do not.
    /// </summary>
    public static Result? Build(FbxReader.Mesh mesh, Basis basis, IReadOnlyList<string>? boneOrder,
                                out string? why, Action<string>? log = null)
    {
        why = null;
        var notes = new List<string>();

        // MATERIAL ORDER, not file order. A cooked section is a CONTIGUOUS run -- a first index
        // and a count -- so the triangles of one material have to sit together in the index
        // buffer. Sorting the triangles here is what makes that true, and it is stable, so within
        // a material the file's own order survives.
        //
        // Grouped by the POLYGON's material rather than the triangle's, because a quad fanned
        // into two triangles belongs entirely to whichever material the quad had.
        var withPoly = mesh.TriangleCornersWithPolygon();
        var triCount = withPoly.Count / 3;
        var order = Enumerable.Range(0, triCount)
            .OrderBy(t => mesh.MaterialOf(withPoly[t * 3].Polygon))
            .ToList();

        var corners = new List<(int Vertex, int Corner)>(withPoly.Count);
        foreach (var t in order)
            for (var c = 0; c < 3; c++)
                corners.Add((withPoly[t * 3 + c].Vertex, withPoly[t * 3 + c].Corner));
        if (corners.Count == 0) { why = "the mesh has no triangles"; return null; }
        if (corners.Count % 3 != 0) { why = $"{corners.Count} corners is not a whole number of triangles"; return null; }

        // Which bone drives which control point. Built once from the clusters rather than searched
        // per vertex: a 3,000-vertex mesh against 240 bones is 720,000 comparisons otherwise, and
        // this runs while somebody waits.
        var influence = new Dictionary<int, List<(int Bone, float Weight)>>();
        var bones = boneOrder?.ToList() ?? new List<string>();
        var unmatched = new List<string>();

        // WHERE EVERY CLUSTER'S WEIGHTS GO, decided for all of them before any are read.
        //
        // Only when there is a template to map onto. Without one the bone list is built FROM the
        // clusters, so every name matches by construction and there is nothing to translate --
        // that is the viewport's path and it stays exactly as it was.
        //
        // See RigAliases: a name that already matches wins, so a rig that imports correctly today
        // imports identically, and only what would otherwise have been DROPPED is touched.
        var plan = boneOrder is not null ? RigAliases.Plan(mesh.Influences, bones) : null;
        var aliased = new List<string>();
        var folded = new List<string>();
        var merged = new List<string>();
        var offPart = new List<string>();
        var worstMerge = 0f;
        var worstMergeName = "";

        for (var ci = 0; ci < mesh.Influences.Count; ci++)
        {
            var cluster = mesh.Influences[ci];
            int slot;

            if (plan is not null)
            {
                var how = plan[ci];
                if (how.Slot < 0) { unmatched.Add(cluster.Bone); continue; }
                slot = how.Slot;
                if (how.How == RigAliases.How.Alias)
                    aliased.Add($"{cluster.Bone} -> {bones[slot]}");
                else if (how.How == RigAliases.How.Folded)
                    folded.Add($"{cluster.Bone} onto {bones[slot]}");
                else if (how.How == RigAliases.How.Merged)
                {
                    // KNOWN NAME, WRONG PART is not the same fault as an unknown name, and saying
                    // both with one sentence sent the author looking for a rig problem that was
                    // really a slot problem. A torso's bone map holds no `hand_l`, so every finger
                    // of an upper-body mesh lands on the forearm -- correctly, and it still needs
                    // saying as "these belong in an ARMS part" rather than "nobody knows what
                    // these are". 50 of one file's 66 groups read the second way and were the
                    // first.
                    var known = RigAliases.For(cluster.Bone, out _);
                    if (known is { Length: > 0 })
                        offPart.Add($"{cluster.Bone} wants {known}");
                    else
                        merged.Add($"{cluster.Bone} into {bones[slot]}");

                    if (how.Distance > worstMerge)
                    { worstMerge = how.Distance; worstMergeName = $"{cluster.Bone} into {bones[slot]}"; }
                }
            }
            else
            {
                slot = bones.FindIndex(b => b.Equals(cluster.Bone, StringComparison.OrdinalIgnoreCase));
                if (slot < 0) { bones.Add(cluster.Bone); slot = bones.Count - 1; }
            }

            for (var i = 0; i < cluster.Vertices.Length; i++)
            {
                var w = i < cluster.Weights.Length ? (float) cluster.Weights[i] : 0f;
                if (w <= 0) continue;
                if (!influence.TryGetValue(cluster.Vertices[i], out var list))
                    influence[cluster.Vertices[i]] = list = new List<(int, float)>();

                // TWO CLUSTERS CAN NOW LAND ON ONE BONE, which is the whole point of a merge, and
                // a vertex driven by both must end up with ONE entry carrying the sum. Appending
                // twice would spend two of the vertex's four slots on the same bone and then let
                // the top-N sort discard the smaller half of a weight that should have been added
                // to the larger. Nothing has duplicate clusters without a merge, so this is inert
                // on every file that worked before.
                var at = list.FindIndex(e => e.Bone == slot);
                if (at >= 0) list[at] = (slot, list[at].Weight + w);
                else list.Add((slot, w));
            }
        }

        // WHAT THE TRANSLATION DID, as one line and not one per bone.
        //
        // The dropped-bone report used to be one grey note PER bone -- sixty-four lines for a
        // renamed rig, with the sentence that mattered nowhere among them. A count, a handful of
        // examples and the worst merge is what a person can actually act on, and the worst merge
        // is the one that earns its place: a knee pad folded into the calf is a centimetre or two,
        // and anything folded halfway across the body is a name this table should have known.
        if (aliased.Count > 0 || folded.Count > 0 || merged.Count > 0 || offPart.Count > 0)
        {
            var said = $"{aliased.Count + folded.Count + merged.Count + offPart.Count:N0} of this model's " +
                       $"{mesh.Influences.Count:N0} skin groups name bones the template does not " +
                       $"have, and were not dropped.";
            if (aliased.Count > 0)
                said += $" {aliased.Count:N0} were recognised as a standard humanoid rig and " +
                        $"renamed onto this one ({FewPairs(aliased)}), which keeps their original " +
                        $"weighting exactly -- nothing was interpolated.";
            if (folded.Count > 0)
                said += $" {folded.Count:N0} are corrective or physics bones this rig has no " +
                        $"equivalent for, so their weights went onto the joint each one supports " +
                        $"({FewPairs(folded)}) -- the weight is kept, the correction is not.";
            if (offPart.Count > 0)
                said += $" {offPart.Count:N0} name bones this rig HAS but this PART does not -- " +
                        $"{FewPairs(offPart)} -- so they landed on the nearest bone the part does " +
                        $"carry. That is a slot question, not a rig one: geometry weighted to a " +
                        $"hand belongs in an Arms part, not a Torso one.";
            if (merged.Count > 0)
                said += $" {merged.Count:N0} had no known meaning at all, so their weights were " +
                        $"added to the nearest bone that did map ({FewPairs(merged)}); the " +
                        $"furthest was {worstMergeName} at {worstMerge:F1} away.";
            notes.Add(said);
        }

        // WHAT THE NAMES SAY, said once, and with both lists side by side.
        //
        // Weights are matched to the template BY BONE NAME, so a rig whose groups are named
        // anything else loses every one of them -- and an unweighted vertex is pinned to the first
        // bone of the map, which is a whole mesh hanging off one bone and moving as a single rigid
        // piece. That is what a reader described as "the tool kit is stripping all bones except
        // one", and it was being reported as one grey note PER dropped bone -- sixty-four lines for
        // a renamed rig, with the sentence that matters nowhere among them. A helmet hides it
        // completely, because a helmet template's bone map IS one bone.
        //
        // The comparison is the diagnosis, so both sides get printed, and the commonest cause gets
        // named outright: Blender's ".001" suffix, which appears on every bone the moment a second
        // armature is in the scene and renames the vertex groups with it.
        if (unmatched.Count > 0)
        {
            var theirs = Few(unmatched);
            var ours = Few(bones);
            if (influence.Count == 0 && bones.Count > 0)
            {
                var suffixed = unmatched.Count(
                    n => Unsuffixed(n) is var s && !s.Equals(n, StringComparison.Ordinal) &&
                         bones.Any(b => b.Equals(s, StringComparison.OrdinalIgnoreCase)));
                notes.Add($"NOTHING IN THIS MODEL IS SKINNED TO THE TEMPLATE -- all " +
                          $"{unmatched.Count:N0} of its skin groups name bones the template does " +
                          $"not have ({theirs}), so every weight was dropped and the whole mesh now " +
                          $"hangs off '{bones[0]}' and will move as ONE RIGID PIECE in game. The " +
                          $"template's bones are named {ours}. " +
                          (suffixed == unmatched.Count
                              ? "Every one of yours is one of those names with a '.001'-style " +
                                "suffix added, which Blender does when a second armature is in the " +
                                "scene -- delete the spare armature, or rename the vertex groups back."
                              : "Rename the vertex groups to the template's bone names."));
            }
            else
                notes.Add($"{unmatched.Count:N0} of the model's skin groups name bones the template " +
                          $"does not have ({theirs}) -- those weights are dropped, so anything they " +
                          $"drove is left to the bones that did match. The template's bones are " +
                          $"named {ours}");
        }
        // NO SKIN AT ALL is the same ending by a different road, and it has to be said separately
        // because there are no names to compare: nothing was dropped, nothing was ever there. Only
        // worth saying when the template has more than one bone -- against a one-bone helmet an
        // unskinned mesh is simply correct.
        else if (boneOrder is not null && mesh.Influences.Count == 0 && bones.Count > 1)
            notes.Add("THIS MODEL CARRIES NO SKIN AT ALL -- the file states no weights for any " +
                      $"bone, so the whole mesh hangs off '{bones[0]}' and will move as ONE RIGID " +
                      $"PIECE in game, while the template it replaces is skinned to " +
                      $"{bones.Count:N0} bones ({Few(bones)}). In Blender that is a mesh with no " +
                      "armature modifier, or vertex groups whose names no longer match any bone in " +
                      "the armature -- the exporter writes no skin for either.");

        if (bones.Count == 0) { why = "the mesh has no skin, so nothing would move it"; return null; }
        if (bones.Count > MaxBoneMap)
        { why = $"{bones.Count} bones in one section; a bone map holds {MaxBoneMap:N0}"; return null; }

        var unskinned = 0;
        var vertices = new List<Vertex>();
        var indices = new List<int>(corners.Count);
        var seen = new Dictionary<(int, int, int, int, int, int), int>();

        var hasNormals = mesh.Normals.Length > 0;
        // HOW MANY BONES A VERTEX MAY KEEP, taken from what the file actually uses.
        //
        // Rounded up to a multiple of four because that is how the buffer is laid out, and capped
        // at twelve, which is Unreal's own ceiling. A file that only ever uses four still writes
        // four, so nothing that worked before changes shape.
        var deepest = influence.Count == 0 ? 0 : influence.Values.Max(l => l.Count);
        var slots = Math.Clamp((deepest + 3) / 4 * 4, 4, 12);

        var hasUvs = mesh.Uvs.Length > 0;
        var hasTangents = mesh.Tangent.Present;
        var hasColours = mesh.Colour.Present;
        if (!hasNormals) notes.Add("the file has no normals; flat face normals are computed instead");
        if (!hasUvs) notes.Add("the file has no UVs; the mesh will sample one texel");

        // WHAT THE FILE CARRIES THAT THIS CANNOT USE, said out loud.
        //
        // Not everything an FBX holds has anywhere to go, and the honest answer is to name it
        // rather than to build machinery for it or to drop it in silence. A second UV layer is
        // the case that prompted this: EVERY mesh in this game -- clothing, droids, weapons --
        // carries exactly one, so there is no second channel for a material to sample and
        // carrying one would be work with nothing at the end of it. The author still needs to
        // know their layer two went nowhere, because in their modelling package it is right
        // there in front of them.
        if (mesh.UvLayers > 1)
            notes.Add($"the file has {mesh.UvLayers} UV layers and only the FIRST is used -- " +
                      "every mesh in this game has one, so nothing here samples a second; " +
                      "put what matters on layer 1");

        foreach (var (control, corner) in corners)
        {
            var (nx, ny, nz) = hasNormals ? Normal(mesh, control, corner, basis) : (0f, 0f, 1f);
            var (u, v) = hasUvs ? Uv(mesh, control, corner) : (0f, 0f);
            var (tx, ty, tz, tw) = hasTangents ? Tangent(mesh, control, corner, basis, nx, ny, nz)
                                               : (0f, 0f, 0f, 1f);
            var (cr, cg, cb, ca) = hasColours ? Colour(mesh, control, corner)
                                              : ((byte) 255, (byte) 255, (byte) 255, (byte) 255);

            // Welded on a QUANTISED key. Exact float equality would keep two vertices that differ
            // in the last bit of a normal -- which happens constantly, because a normal is the
            // result of an average -- and the mesh would come out with nearly no sharing at all.
            // COLOUR IS PART OF THE KEY, because two corners of one control point that were
            // painted differently are two vertices -- welding them would average nothing and
            // silently pick whichever corner happened to arrive first.
            var key = (control, Q(nx), Q(ny), Q(nz), (Q(u) << 16) ^ Q(v),
                       (cr << 24) | (cg << 16) | (cb << 8) | ca);
            if (seen.TryGetValue(key, out var at)) { indices.Add(at); continue; }

            var (px, py, pz) = basis.Apply((float) mesh.Positions[control * 3],
                                           (float) mesh.Positions[control * 3 + 1],
                                           (float) mesh.Positions[control * 3 + 2]);

            var (bi, bw) = Skin(influence, control, ref unskinned, slots);

            seen[key] = vertices.Count;
            indices.Add(vertices.Count);
            vertices.Add(new Vertex(px, py, pz, nx, ny, nz, tx, ty, tz, tw, u, v, bi, bw,
                                    cr, cg, cb, ca));
        }

        // WINDING, against THE GAME'S OWN CONVENTION rather than against common sense.
        //
        // A triangle's corner order decides which side faces out, and the obvious check is whether
        // the normal implied by that order agrees with the normals the file supplies. It does not
        // decide anything on its own, because the answer depends on which way the ENGINE reads a
        // winding, and Unreal's front faces are clockwise -- so on a correctly wound Unreal mesh
        // the cross product points INTO the surface.
        //
        // Measured on this game's own helmet, which renders correctly by definition:
        //
        //     game   40 agree / 6,797 disagree with their own normals
        //
        // So "disagree" is right here and "agree" is inside out. The first version of this made
        // the mesh agree with its own normals, called that measured, and shipped a helmet you
        // could see the inside of. The vote was sound; the conclusion drawn from it was not. A
        // measurement still needs a reference, and the reference was sitting in the same game.
        var (agree, disagree) = WindingVote(vertices, indices);
        if (agree > disagree)
        {
            for (var i = 0; i + 2 < indices.Count; i += 3)
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            notes.Add($"triangle winding was reversed: {agree:N0} of {agree + disagree:N0} " +
                      "triangles were wound the way Unreal treats as inward-facing");
        }
        else if (agree > 0)
            notes.Add($"{agree:N0} of {agree + disagree:N0} triangles are wound against the rest " +
                      "and were left alone -- the mesh may have inside-out patches");

        if (!hasNormals) FaceNormals(vertices, indices);

        // COMPUTED ONLY WHEN THE FILE HAS NONE. A tangent basis derived from the UVs is a fair
        // approximation and it is not what the artist saw: the file states its own, per corner,
        // and the handedness in particular cannot be derived at all -- a mirrored UV island needs
        // the opposite bitangent, and getting that wrong lights a normal map inside out on exactly
        // the half of a symmetric model that was mirrored.
        if (!hasTangents) Tangents(vertices, indices);
        else notes.Add($"the file's own tangent basis was used ({mesh.Tangent.Values.Length / 3:N0} entries)");

        if (unskinned > 0)
            notes.Add($"{unskinned:N0} of {vertices.Count:N0} vertex(es) had no weights and were " +
                      $"pinned to the first bone of the map ('{bones[0]}')" +
                      (bones.Count == 1
                          ? " -- which is the only bone this template has, so nothing is lost"
                          : ", so they move with it and with nothing else"));

        // HOW MANY BONES ACTUALLY DRIVE A VERTEX, against the four a skin weight buffer holds.
        //
        // The buffer keeps the four heaviest and drops the rest, which is invisible in a bind pose
        // and shows up the moment the mesh is animated: a vertex that needed six bones is now
        // pulled by four, so seams part and pieces clip through what they sit on. Reported because
        // the difference between "fine in Max" and "torn in game" is exactly this number, and
        // nothing said it out loud.
        var over = influence.Values.Count(l => l.Count > slots);
        if (over > 0)
            notes.Add($"{over:N0} of {influence.Count:N0} vertex(es) are driven by more than " +
                      $"{slots} bones (up to {deepest}) -- only the {slots} heaviest are kept, so " +
                      "those deform differently here than in the modelling package");

        log?.Invoke($"      {mesh.VertexCount:N0} control point(s) -> {vertices.Count:N0} vertex(es), " +
                    $"{indices.Count / 3:N0} triangle(s), {bones.Count} bone(s), " +
                    $"up to {deepest} influence(s) per vertex");
        foreach (var n in notes) log?.Invoke($"      note: {n}");

        // The runs, read back off the sorted order so they cannot disagree with the indices.
        var groups = new List<Group>();
        for (var t = 0; t < order.Count;)
        {
            var mat = mesh.MaterialOf(withPoly[order[t] * 3].Polygon);
            var first = t;
            while (t < order.Count && mesh.MaterialOf(withPoly[order[t] * 3].Polygon) == mat) t++;
            groups.Add(new Group(mat, first * 3, t - first));
        }
        if (groups.Count > 1)
            log?.Invoke($"  {groups.Count} materials: " +
                        string.Join(", ", groups.Select(g => $"#{g.Material} {g.Triangles:N0} tris")));

        return new Result(vertices, indices, bones, notes, groups);
    }

    /// <summary>The first few of a list of names, quoted, with a count of whatever is left.</summary>
    static string Few(IReadOnlyList<string> names, int take = 5)
    {
        var head = string.Join(", ", names.Take(take).Select(n => $"'{n}'"));
        return names.Count > take ? $"{head} and {names.Count - take:N0} more" : head;
    }

    /// <summary>
    /// The same, for entries that are already a phrase rather than a name, so they do not get
    /// quoted into "'LeftForeArm -> lowerarm_l'".
    /// </summary>
    static string FewPairs(IReadOnlyList<string> pairs, int take = 4)
    {
        var head = string.Join(", ", pairs.Take(take));
        return pairs.Count > take ? $"{head}, and {pairs.Count - take:N0} more" : head;
    }

    /// <summary>
    /// A name with any trailing ".001"-style duplicate suffixes taken off, or the name itself.
    ///
    /// Repeated, because the suffix can be applied twice: renaming a bone in Blender renames the
    /// vertex group with it, so a group can end up "hand_l.001.001". Used ONLY to explain a
    /// mismatch, never to match -- a file that genuinely holds both "hand_l" and "hand_l.001"
    /// would then have two groups claiming one bone, and guessing which is meant is not ours.
    /// </summary>
    static string Unsuffixed(string name)
    {
        while (true)
        {
            var dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1) return name;
            for (var i = dot + 1; i < name.Length; i++)
                if (!char.IsAsciiDigit(name[i])) return name;
            name = name[..dot];
        }
    }

    /// <summary>
    /// How many triangles face the way their own vertex normals say they should.
    ///
    /// The corner order gives a triangle a front and a back; the vertex normals say which side the
    /// surface faces. On a correctly wound mesh they agree everywhere. Counting rather than
    /// checking one triangle, because a few disagreeing is a modelling artefact and most of them
    /// disagreeing is a convention mismatch.
    /// </summary>
    private static (int Agree, int Disagree) WindingVote(List<Vertex> v, List<int> idx)
    {
        int agree = 0, disagree = 0;
        for (var i = 0; i + 2 < idx.Count; i += 3)
        {
            var a = v[idx[i]]; var b = v[idx[i + 1]]; var c = v[idx[i + 2]];
            var (gx, gy, gz) = Cross(b.X - a.X, b.Y - a.Y, b.Z - a.Z,
                                     c.X - a.X, c.Y - a.Y, c.Z - a.Z);

            var nx = a.NX + b.NX + c.NX;
            var ny = a.NY + b.NY + c.NY;
            var nz = a.NZ + b.NZ + c.NZ;

            var dot = gx * nx + gy * ny + gz * nz;
            // A degenerate or knife-edge triangle votes for nothing rather than for whichever way
            // its floating-point noise happens to fall.
            if (MathF.Abs(dot) < 1e-9f) continue;
            if (dot > 0) agree++; else disagree++;
        }
        return (agree, disagree);
    }

    private static int Q(float f) => (int) MathF.Round(f * 4096f);

    /// <summary>
    /// A corner's vertex colour, as the four bytes the buffer stores.
    ///
    /// FBX writes RGBA as doubles from zero to one; a cooked FColor is bytes, and the buffer's own
    /// order is B, G, R, A -- which is the writer's business, not this one's. This returns them in
    /// the order a person would name them.
    /// </summary>
    private static (byte R, byte G, byte B, byte A) Colour(FbxReader.Mesh mesh, int control, int corner)
    {
        var at = mesh.Colour.At(control, corner);
        if (at < 0) return (255, 255, 255, 255);
        static byte To(double v) => (byte) Math.Clamp((int) MathF.Round((float) v * 255f), 0, 255);
        return (To(mesh.Colour.Values[at]), To(mesh.Colour.Values[at + 1]),
                To(mesh.Colour.Values[at + 2]), To(mesh.Colour.Values[at + 3]));
    }

    /// <summary>
    /// A corner's tangent, and the HANDEDNESS that a tangent alone cannot state.    /// <summary>
    /// A corner's tangent, and the HANDEDNESS that a tangent alone cannot state.
    ///
    /// Unreal stores a tangent plus a sign, and reconstructs the bitangent as
    /// `sign * cross(normal, tangent)`. The file states the bitangent outright, so the sign is
    /// whichever one reproduces it -- which is the only way to get a mirrored UV island right,
    /// and mirrored islands are the norm on anything symmetric.
    ///
    /// Both vectors are rotated by the basis BEFORE the comparison, because a basis whose
    /// determinant is negative -- which this game's is, UE being left-handed and FBX right --
    /// flips handedness itself. Comparing them in file space would get every sign backwards.
    /// </summary>
    private static (float X, float Y, float Z, float W) Tangent(
        FbxReader.Mesh mesh, int control, int corner, Basis basis, float nx, float ny, float nz)
    {
        var t = mesh.Tangent.Vector(control, corner, (0f, 0f, 0f));
        var (tx, ty, tz) = basis.Rotate(t.X, t.Y, t.Z);
        var len = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
        if (len <= 1e-6f) return (0, 0, 0, 1);
        tx /= len; ty /= len; tz /= len;

        var w = 1f;
        if (mesh.Binormal.Present)
        {
            var b = mesh.Binormal.Vector(control, corner, (0f, 0f, 0f));
            var (bx, by, bz) = basis.Rotate(b.X, b.Y, b.Z);
            var (cx, cy, cz) = Cross(nx, ny, nz, tx, ty, tz);
            if (cx * bx + cy * by + cz * bz < 0) w = -1f;
        }
        return (tx, ty, tz, w);
    }

    private static (float, float, float) Normal(FbxReader.Mesh mesh, int control, int corner, Basis basis)
    {
        var at = mesh.NormalMapping.Contains("ControlPoint", StringComparison.Ordinal) ? control : corner;
        if (mesh.NormalReference.Contains("IndexToDirect", StringComparison.Ordinal))
        {
            if (at >= mesh.NormalIndex.Length) return (0, 0, 1);
            at = mesh.NormalIndex[at];
        }
        if (at < 0 || (at + 1) * 3 > mesh.Normals.Length) return (0, 0, 1);

        var (x, y, z) = basis.Rotate((float) mesh.Normals[at * 3], (float) mesh.Normals[at * 3 + 1],
                                     (float) mesh.Normals[at * 3 + 2]);
        var len = MathF.Sqrt(x * x + y * y + z * z);
        return len > 1e-6f ? (x / len, y / len, z / len) : (0, 0, 1);
    }

    /// <summary>The UV a corner wears. Public so a probe can count seams the way the welder does.</summary>
    public static (float, float) UvAt(FbxReader.Mesh mesh, int control, int corner) => Uv(mesh, control, corner);

    /// <summary>The normal a corner wears, unrotated -- the basis only turns it, it cannot merge two.</summary>
    public static (float, float, float) NormalAt(FbxReader.Mesh mesh, int control, int corner) =>
        Normal(mesh, control, corner, Basis.YUp(1f));

    private static (float, float) Uv(FbxReader.Mesh mesh, int control, int corner)
    {
        var at = mesh.UvMapping.Contains("ControlPoint", StringComparison.Ordinal) ? control : corner;
        if (mesh.UvReference.Contains("IndexToDirect", StringComparison.Ordinal))
        {
            if (at >= mesh.UvIndex.Length) return (0, 0);
            at = mesh.UvIndex[at];
        }
        if ((at + 1) * 2 > mesh.Uvs.Length) return (0, 0);

        // FBX counts V from the bottom and Unreal from the top. Getting this wrong flips every
        // texture vertically, which looks like the wrong texture rather than the wrong V.
        return ((float) mesh.Uvs[at * 2], 1f - (float) mesh.Uvs[at * 2 + 1]);
    }

    /// <summary>
    /// A vertex's bones and weights, as the bytes a skin weight buffer stores.
    ///
    /// Biggest first, renormalised to sum to 255. The renormalisation is not cosmetic: weights
    /// that sum to less than one shrink the vertex toward the origin as the skeleton moves, which
    /// shows up as a mesh that deflates during animation.
    ///
    /// THE COUNT IS THE FILE'S, NOT FOUR. It was four, hardcoded, and that is invisible in a bind
    /// pose and ruinous under animation: 31% of a clone torso's vertices are driven by up to
    /// TWELVE bones, and keeping the heaviest four left the rest pulled by the wrong ones. Seams
    /// parted into holes and a shoulder pad swung through the shoulder it sits on, while the same
    /// rig animated correctly in the modelling package -- because the modelling package kept all
    /// twelve. The game's own copy of that mesh declares twelve, so nothing but this was ever the
    /// limit.
    /// </summary>
    private static (ushort[] Bones, byte[] Weights) Skin(
        Dictionary<int, List<(int Bone, float Weight)>> influence, int control, ref int unskinned,
        int slots)
    {
        var bones = new ushort[slots];
        var weights = new byte[slots];

        if (!influence.TryGetValue(control, out var list) || list.Count == 0)
        {
            unskinned++;
            weights[0] = 255;
            return (bones, weights);
        }

        var top = list.OrderByDescending(x => x.Weight).Take(slots).ToList();
        var total = top.Sum(x => x.Weight);
        if (total <= 0) { weights[0] = 255; return (bones, weights); }

        var given = 0;
        for (var i = 0; i < top.Count; i++)
        {
            bones[i] = (ushort) Math.Clamp(top[i].Bone, 0, MaxBoneMap - 1);
            var w = i == top.Count - 1
                ? 255 - given                              // the last one takes the rounding
                : (int) MathF.Round(top[i].Weight / total * 255f);
            w = Math.Clamp(w, 0, 255 - given);
            weights[i] = (byte) w;
            given += w;
        }
        if (given < 255) weights[0] = (byte) (weights[0] + (255 - given));
        return (bones, weights);
    }

    /// <summary>Flat normals, for a file that shipped without any.</summary>
    private static void FaceNormals(List<Vertex> vertices, List<int> indices)
    {
        var acc = new (float X, float Y, float Z)[vertices.Count];
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var a = vertices[indices[i]]; var b = vertices[indices[i + 1]]; var c = vertices[indices[i + 2]];
            var (nx, ny, nz) = Cross(b.X - a.X, b.Y - a.Y, b.Z - a.Z, c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            for (var k = 0; k < 3; k++)
            {
                var at = indices[i + k];
                acc[at] = (acc[at].X + nx, acc[at].Y + ny, acc[at].Z + nz);
            }
        }
        for (var i = 0; i < vertices.Count; i++)
        {
            var (x, y, z) = Normalise(acc[i].X, acc[i].Y, acc[i].Z);
            vertices[i] = vertices[i] with { NX = x, NY = y, NZ = z };
        }
    }

    /// <summary>
    /// Tangents, from how the UVs change across each triangle.
    ///
    /// A normal map is written in tangent space, so a mesh whose tangents point the wrong way
    /// lights as though the surface detail were carved rather than raised -- and it is at its most
    /// wrong where the lighting is most interesting, which reads as a bad normal map.
    ///
    /// The handedness sign is the missing fourth number: the bitangent is the cross product of
    /// normal and tangent, times this, and it is what mirrored UV islands need to shade the same
    /// as their originals.
    /// </summary>
    private static void Tangents(List<Vertex> vertices, List<int> indices)
    {
        var tan = new (float X, float Y, float Z)[vertices.Count];
        var bit = new (float X, float Y, float Z)[vertices.Count];

        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var a = vertices[indices[i]]; var b = vertices[indices[i + 1]]; var c = vertices[indices[i + 2]];

            float e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
            float e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;
            float du1 = b.U - a.U, dv1 = b.V - a.V;
            float du2 = c.U - a.U, dv2 = c.V - a.V;

            var det = du1 * dv2 - du2 * dv1;
            // A degenerate UV triangle -- two corners at the same texel -- has no tangent to
            // give. Skipped rather than divided by, because the alternative is an infinity that
            // poisons every vertex the triangle touches.
            if (MathF.Abs(det) < 1e-12f) continue;
            var r = 1f / det;

            var tx = (dv2 * e1x - dv1 * e2x) * r;
            var ty = (dv2 * e1y - dv1 * e2y) * r;
            var tz = (dv2 * e1z - dv1 * e2z) * r;
            var bx = (du1 * e2x - du2 * e1x) * r;
            var by = (du1 * e2y - du2 * e1y) * r;
            var bz = (du1 * e2z - du2 * e1z) * r;

            for (var k = 0; k < 3; k++)
            {
                var at = indices[i + k];
                tan[at] = (tan[at].X + tx, tan[at].Y + ty, tan[at].Z + tz);
                bit[at] = (bit[at].X + bx, bit[at].Y + by, bit[at].Z + bz);
            }
        }

        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            var (tx, ty, tz) = tan[i];

            // Gram-Schmidt: the tangent has to be perpendicular to the normal, and an averaged one
            // never is.
            var d = tx * v.NX + ty * v.NY + tz * v.NZ;
            tx -= v.NX * d; ty -= v.NY * d; tz -= v.NZ * d;
            var (nx, ny, nz) = Normalise(tx, ty, tz);

            // A vertex no triangle gave a tangent to still needs one perpendicular to its normal.
            if (nx == 0 && ny == 0 && nz == 0)
            {
                var (ax, ay, az) = MathF.Abs(v.NX) < 0.9f ? (1f, 0f, 0f) : (0f, 1f, 0f);
                (nx, ny, nz) = Normalise(Cross(v.NX, v.NY, v.NZ, ax, ay, az));
            }

            var (cx, cy, cz) = Cross(v.NX, v.NY, v.NZ, nx, ny, nz);
            var sign = cx * bit[i].X + cy * bit[i].Y + cz * bit[i].Z < 0 ? -1f : 1f;
            vertices[i] = v with { TX = nx, TY = ny, TZ = nz, TSign = sign };
        }
    }

    private static (float, float, float) Cross(float ax, float ay, float az, float bx, float by, float bz)
        => (ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx);

    private static (float, float, float) Normalise((float X, float Y, float Z) v)
        => Normalise(v.X, v.Y, v.Z);

    private static (float, float, float) Normalise(float x, float y, float z)
    {
        var len = MathF.Sqrt(x * x + y * y + z * z);
        return len > 1e-8f ? (x / len, y / len, z / len) : (0, 0, 0);
    }
}
