using System.Numerics;

namespace UEToolkit.Core;

/// <summary>
/// A PLACEMENT'S WORLD MATRIX AS THE THREE NUMBERS AN FBX MODEL CARRIES: Lcl Translation, Lcl
/// Rotation and Lcl Scaling.
///
/// The Level Editor builds a placement as <c>Scale * Rotation * Translation</c> in System.Numerics'
/// ROW-vector convention (<c>LevelReader.Model</c>), in UE's space. An FBX Model applies its scale,
/// then its rotation (Euler XYZ, degrees -- the composition Rz * Ry * Rx, <see cref="FbxWriter.EulerXyz(double[,])"/>),
/// then its translation. Those are the same order, so the conversion is a DECOMPOSITION and nothing
/// more: the translation is the matrix's last row, the scale is the length of each of the first
/// three rows, and the rotation is what is left.
///
/// No change of basis happens here, on purpose. <see cref="FbxMeshExport.StaticGeometry"/> writes its
/// vertices in the space it is asked for: UE's own with RightHanded off, so a placement in UE's space is
/// exactly the transform that carries those vertices to where the game puts them. With RightHanded on (the
/// level export) the caller hands in P*M*P, the placement conjugated by the Y-negating basis, and the vertices
/// are written in that basis too: the scene is put into another handedness as ONE change applied to the vertices
/// and the placements together, not a second opinion about either.
///
/// A MIRRORED PLACEMENT (negative determinant) is the one case that needs care, and it is the case
/// that goes wrong silently. A mirror is not a rotation and has no Euler angles: handed to a routine
/// that wants a rotation, it comes out as SOME rotation, a plausible one, and the mesh is turned
/// where it should have been flipped. So the reflection is moved into the SCALE, where FBX can
/// state it (a negative axis), and only the proper rotation that remains is converted to angles.
/// Which axes go negative is a choice, since a flip of X and a flip of Y differ only by a half turn
/// -- see <see cref="Lcl"/> for the one made.
/// </summary>
public static class FbxPlacement
{
    private const double Epsilon = 1e-12;

    /// <summary>
    /// <paramref name="worldUe"/> as Lcl Translation, Lcl Rotation (Euler XYZ in degrees) and Lcl
    /// Scaling, in the same space <see cref="FbxMeshExport.StaticGeometry"/> writes vertices in.
    ///
    /// For every matrix of the form scale * rotation * translation, <c>FbxMeshExport.FromEulerXyz(R)</c>
    /// with each row scaled by <c>S</c> and the translation <c>T</c> put last gives the matrix back
    /// (to float precision, which is all the input has).
    ///
    /// MIRRORS. When the determinant is negative the result carries an ODD number of negative
    /// scales and a proper rotation; the matrix keeps its negative determinant when rebuilt. The
    /// sign pattern is the one that leaves the SMALLEST rotation -- of the four that mirror (X, Y or Z
    /// negated, or all three), the one whose rotation has the largest trace. That recovers what was
    /// authored whenever the authored rotation is a quarter turn or less: scale (-1, 1, 1) turned 30
    /// degrees about Z comes back as exactly that, rather than as scale (1, 1, -1) with an extra half
    /// turn folded into the angles. (A larger authored turn may come back as an equivalent form with
    /// a different mirrored axis -- the same matrix either way.) Ties go to X.
    ///
    /// NOT REPRESENTED: shear. A matrix whose rows are not at right angles (a non-uniform scale
    /// applied after a rotation) has no scale/rotation form; the rotation returned is the nearest
    /// right-angled frame, taking the first row as given. Placements from the editor are built from
    /// translation, rotation and scale separately and never shear.
    ///
    /// A zero scale is allowed (a flattened mesh): the axis keeps scale 0 and a rotation is still
    /// returned. A matrix with a NaN or infinite element throws <see cref="ArgumentException"/> -- an
    /// FBX holding one cannot be opened, and the caller can name the placement.
    /// </summary>
    public static (double[] T, double[] RDeg, double[] S) Lcl(Matrix4x4 worldUe)
    {
        var m = worldUe;
        double[] e =
        [
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
        ];
        if (!e.All(double.IsFinite))
            throw new ArgumentException("the placement matrix has a NaN or infinite element", nameof(worldUe));

        // The rows are the images of the three axes (row-vector convention): row i is axis i,
        // scaled and turned.
        V r0 = new(e[0], e[1], e[2]), r1 = new(e[4], e[5], e[6]), r2 = new(e[8], e[9], e[10]);
        double l0 = r0.Length, l1 = r1.Length, l2 = r2.Length;
        var det = r0.Dot(r1.Cross(r2));

        // The sign each axis's scale carries. A proper matrix keeps them all positive.
        double f0 = 1, f1 = 1, f2 = 1;
        if (det < 0 && l0 > Epsilon && l1 > Epsilon && l2 > Epsilon)
        {
            // Four ways to put the reflection into the scale. Take the one whose remaining
            // rotation turns the least: trace(R) = 1 + 2 cos(angle), largest for the smallest angle.
            (double X, double Y, double Z)[] patterns = [(-1, 1, 1), (1, -1, 1), (1, 1, -1), (-1, -1, -1)];
            var best = double.NegativeInfinity;
            foreach (var (px, py, pz) in patterns)
            {
                var trace = r0.X / (px * l0) + r1.Y / (py * l1) + r2.Z / (pz * l2);
                if (trace > best + 1e-12) { best = trace; f0 = px; f1 = py; f2 = pz; }
            }
        }

        // The rotation frame: each row divided by its signed scale. A zero-length row has no
        // direction, so it is rebuilt from the other two (a rotation's axes are the cross products
        // of each other); with two or three of them missing there is nothing to go on.
        var a0 = l0 > Epsilon ? r0 / (f0 * l0) : V.Zero;
        var a1 = l1 > Epsilon ? r1 / (f1 * l1) : V.Zero;
        var a2 = l2 > Epsilon ? r2 / (f2 * l2) : V.Zero;
        var missing = (l0 > Epsilon ? 0 : 1) + (l1 > Epsilon ? 0 : 1) + (l2 > Epsilon ? 0 : 1);
        if (missing >= 2) { a0 = new V(1, 0, 0); a1 = new V(0, 1, 0); a2 = new V(0, 0, 1); }
        else if (l0 <= Epsilon) a0 = a1.Cross(a2).Unit();
        else if (l1 <= Epsilon) a1 = a2.Cross(a0).Unit();
        else if (l2 <= Epsilon) a2 = a0.Cross(a1).Unit();

        // Right-angled and unit: Gram-Schmidt on the first two, the third as their cross product,
        // which is also what makes it a rotation and not a mirror whatever the input looked like.
        // For a matrix with no shear this changes nothing.
        var u0 = a0.Unit();
        var u1 = (a1 - u0 * a1.Dot(u0)).Unit();
        if (u1.Length < 0.5) u1 = u0.Perpendicular();
        var u2 = u0.Cross(u1);

        // `Rotation` in FbxWriter is the COLUMN-vector matrix, the transpose of the rows above.
        var rotation = new double[3, 3]
        {
            { u0.X, u0.Y, u0.Z },
            { u1.X, u1.Y, u1.Z },
            { u2.X, u2.Y, u2.Z },
        };
        // rows of `rotation` are the row-vector matrix; EulerXyz wants its transpose.
        var column = new double[3, 3];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++) column[i, j] = rotation[j, i];

        var (rx, ry, rz) = FbxWriter.EulerXyz(column);

        // `+ 0.0` turns a negative zero into a positive one, which would otherwise be written into
        // the file as "-0".
        return ([e[12] + 0.0, e[13] + 0.0, e[14] + 0.0],
                [rx + 0.0, ry + 0.0, rz + 0.0],
                [f0 * l0 + 0.0, f1 * l1 + 0.0, f2 * l2 + 0.0]);
    }

    /// <summary>A double-precision 3-vector, only as much as the decomposition above needs.</summary>
    private readonly record struct V(double X, double Y, double Z)
    {
        public static readonly V Zero = new(0, 0, 0);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double Dot(V o) => X * o.X + Y * o.Y + Z * o.Z;
        public V Cross(V o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
        public V Unit() { var l = Length; return l > Epsilon ? new V(X / l, Y / l, Z / l) : Zero; }
        public static V operator *(V v, double k) => new(v.X * k, v.Y * k, v.Z * k);
        public static V operator /(V v, double k) => new(v.X / k, v.Y / k, v.Z / k);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        /// <summary>Some unit vector at right angles to this one.</summary>
        public V Perpendicular()
        {
            var axis = Math.Abs(X) < 0.9 ? new V(1, 0, 0) : new V(0, 1, 0);
            return Cross(axis).Unit();
        }
    }
}
