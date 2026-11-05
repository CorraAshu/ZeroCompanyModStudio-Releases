using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;

namespace UEToolkit.Core;

/// <summary>
/// Measure an imported mesh against the one it replaces, and say what would put it in place.
///
/// This exists because the alternative is a person guessing, exporting, launching the game,
/// squinting, and guessing again -- a loop that costs minutes per attempt and gives one bit of
/// information each time ("still wrong"). Every number here is knowable before the game runs.
///
/// The offsets are the useful part. Scaling pivots about the origin, so resizing a helmet that
/// sits a metre and a half up also slides it, and no single scale gets both the size and the
/// height right. Separating them turns two entangled unknowns into two independent settings.
/// </summary>
public static class MeshFit
{
    public sealed record Box(float X0, float X1, float Y0, float Y1, float Z0, float Z1)
    {
        public float Depth => X1 - X0;     // Unreal X is forward
        public float Width => Y1 - Y0;     // Y is right
        public float Height => Z1 - Z0;    // Z is up

        public (float X, float Y, float Z) Centre => ((X0 + X1) / 2, (Y0 + Y1) / 2, (Z0 + Z1) / 2);

        public string Describe => $"{Depth:F1} x {Width:F1} x {Height:F1} cm";

        public static Box Of(IEnumerable<(float X, float Y, float Z)> points)
        {
            float x0 = float.MaxValue, x1 = float.MinValue;
            float y0 = float.MaxValue, y1 = float.MinValue;
            float z0 = float.MaxValue, z1 = float.MinValue;
            foreach (var (x, y, z) in points)
            {
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            }
            return new Box(x0, x1, y0, y1, z0, z1);
        }
    }

    public sealed record Report(Box Template, Box Imported, int Vertices, int Triangles,
                                float Scale, string Basis,
                                (float X, float Y, float Z) ToCentre,
                                (float X, float Y, float Z) ToRest,
                                List<string> Notes)
    {
        /// <summary>How much bigger the import is on each axis. 1.0 is the same size.</summary>
        public (float Depth, float Width, float Height) Ratio =>
            (Template.Depth > 0 ? Imported.Depth / Template.Depth : 0,
             Template.Width > 0 ? Imported.Width / Template.Width : 0,
             Template.Height > 0 ? Imported.Height / Template.Height : 0);
    }

    /// <summary>
    /// Read the FBX, place it the way the builder would, and compare.
    ///
    /// The scale comes from the file's own node unless overridden, because that is where resizing
    /// in a modelling package actually goes -- read only the vertices and an artist's resize has
    /// no effect at all, with nothing in the file looking wrong.
    /// </summary>
    public static Report? Measure(IFileProvider provider, string templateMesh, string fbxPath,
                                  string? basisText, float scaleOverride,
                                  (float X, float Y, float Z) offset, out string? why)
    {
        why = null;
        try
        {
            if (AssetLoader.LoadPrimary(provider, templateMesh) is not USkeletalMesh template ||
                template.LODModels is not { Length: > 0 } lods ||
                lods[0].VertexBufferGPUSkin?.VertsFloat is not { Length: > 0 } verts)
            { why = $"{templateMesh} has no vertices to compare against"; return null; }

            if (!File.Exists(fbxPath)) { why = $"{Path.GetFileName(fbxPath)} is not on disk"; return null; }

            var reader = FbxReader.Read(fbxPath);
            // EVERY OBJECT. Measuring the first one alone reported a shoulder-holster's dimensions
            // against a whole pair of legs and called it "0.29x" -- a number that is not wrong so
            // much as about nothing, sitting in red where an author is looking for reassurance
            // that their scale is right. What the panel is asking is "does MY FILE fit the part it
            // replaces", and the file is all of it.
            var objects = reader.ReadMeshes(out why);
            if (objects.Count == 0) return null;
            var mesh = objects[0];

            var scale = scaleOverride > 0 ? scaleOverride
                      : reader.MeshNodeTransform() is { } node && node.Scale.X > 0 ? (float) node.Scale.X
                      : ModBuild.DefaultMeshScale;

            // The same transform the builder assembles -- rotation, move and all -- so that a
            // measurement here describes what a build would actually produce rather than an
            // unrotated, unmoved stand-in for it.
            if (MeshImport.Basis.ForFile(reader, scale, basisText, out why) is not { } basis) return null;
            basis = basis with { OffsetX = offset.X, OffsetY = offset.Y, OffsetZ = offset.Z };

            var them = Box.Of(verts.Select(v => ((float) v.Pos.X, (float) v.Pos.Y, (float) v.Pos.Z)));

            // Each object placed by ITS OWN node, the same as the build does -- one basis applied
            // to all of them is how a shoulder pad ended up at the knees.
            var unit = scale;
            var ours = new List<(float, float, float)>();
            var vertices = 0;
            var triangles = 0;
            foreach (var obj in objects)
            {
                var b = MeshImport.Basis.ForFile(reader, scale, basisText, out _, null,
                                                 obj.Geometry, unit) ?? basis;
                b = b with { OffsetX = offset.X, OffsetY = offset.Y, OffsetZ = offset.Z };

                for (var i = 0; i + 2 < obj.Positions.Length; i += 3)
                    ours.Add(b.Apply((float) obj.Positions[i], (float) obj.Positions[i + 1],
                                     (float) obj.Positions[i + 2]));
                vertices += obj.VertexCount;
                triangles += obj.TriangleCorners().Count / 3;
            }
            var us = Box.Of(ours);

            // Two answers, because a mesh of a different shape can match its template at the
            // centre or at the bottom but never at both -- and which one is right depends on
            // whether the piece hangs from something or rests on it.
            var toCentre = (offset.X + them.Centre.X - us.Centre.X,
                            offset.Y + them.Centre.Y - us.Centre.Y,
                            offset.Z + them.Centre.Z - us.Centre.Z);
            var toRest = (toCentre.Item1, toCentre.Item2, offset.Z + them.Z0 - us.Z0);

            var notes = new List<string>();
            var r = them.Height > 0 ? us.Height / them.Height : 0;
            if (r is > 1.25f or (< 0.8f and > 0))
                notes.Add($"it is {(r > 1 ? "taller" : "shorter")} than the template by " +
                          $"{Math.Abs(1 - r) * 100:F0}%");
            if (Math.Abs(us.Centre.Y) > 2f)
                notes.Add($"it sits {Math.Abs(us.Centre.Y):F1} cm off the centre line, which is in " +
                          "the model rather than in the placement");

            // Counted across every object, so the summary agrees with what a build reports.
            return new Report(them, us, vertices, triangles,
                              scale, basis.Name, toCentre, toRest, notes);
        }
        catch (Exception e) { why = $"{Path.GetFileName(fbxPath)}: {e.Message}"; return null; }
    }
}
