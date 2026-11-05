using System.IO.Compression;
using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Read a binary FBX far enough to import a skinned mesh.
///
/// Not a general FBX library and not trying to be. What a mod needs is one mesh, its UVs, and
/// which bones move it -- so this reads the node tree, pulls the arrays out of `Geometry`, follows
/// the `Connections` table to find which bone each skin cluster drives, and stops. Cameras,
/// lights, animation stacks, materials and textures are parsed as nodes and ignored.
///
/// The format is a tree of records: a header giving where the record ENDS, a list of typed
/// properties, then nested records, then thirteen zero bytes. Sizes are 32-bit up to version 7400
/// and 64-bit from 7500, which is the one thing that silently misreads an entire file if assumed.
///
/// Array properties may be zlib-deflated, flagged per array rather than per file.
/// </summary>
public sealed class FbxReader
{
    public sealed record Node(string Name, List<object> Properties, List<Node> Children)
    {
        public Node? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

        public IEnumerable<Node> All(string name)
        {
            foreach (var c in Children)
            {
                if (c.Name == name) yield return c;
                foreach (var g in c.All(name)) yield return g;
            }
        }

        public string? Text(int at = 0) =>
            at < Properties.Count && Properties[at] is string s ? s : null;
    }

    /// <summary>
    /// One bone a cluster drives, how much of each vertex it owns, and WHERE THE BONE WAS when
    /// the skin was bound.
    ///
    /// <paramref name="LinkTranslation"/> is the translation out of the cluster's TransformLink --
    /// the bone's world matrix at bind time, written by the exporter and needing no chain walking
    /// or Euler order to interpret. It matters because a skinned vertex is stored relative to the
    /// bind pose: if the file was skinned against a skeleton standing somewhere other than where
    /// the game's stands, every vertex is offset by the difference, and the mesh floats.
    /// </summary>
    public sealed record Influence(string Bone, int[] Vertices, double[] Weights,
                                   (double X, double Y, double Z)? LinkTranslation);

    /// <param name="MaterialIds">
    /// The material each POLYGON uses, as written by LayerElementMaterial. Empty when the file
    /// has no material layer at all, and a single entry when every polygon shares one -- FBX
    /// writes that as AllSame rather than repeating the number.
    /// </param>
    /// <summary>
    /// ONE PER-CORNER LAYER of a mesh -- colours, tangents, binormals -- with the two strings that
    /// say how to read it.
    ///
    /// Every layer in an FBX is addressed the same way and gets it wrong the same way: MAPPING
    /// says whether an entry is per control point or per corner, REFERENCE says whether to use
    /// that position directly or as an index into the values. Normals and UVs each grew their own
    /// copy of this logic and the normal one was wrong for a while -- it borrowed the UV index,
    /// which works whenever normals are Direct and silently shuffles them when they are not. New
    /// layers share one resolver so that cannot happen a third time.
    /// </summary>
    public sealed record Layer(double[] Values, int[] Index, string Mapping, string Reference, int Stride)
    {
        public static readonly Layer None = new([], [], "", "", 0);

        public bool Present => Values.Length > 0 && Stride > 0;

        /// <summary>Where this corner's values start, or -1 when the layer cannot answer.</summary>
        public int At(int control, int corner)
        {
            if (!Present) return -1;
            var at = Mapping.Contains("ControlPoint", StringComparison.Ordinal) ? control : corner;
            if (Reference.Contains("IndexToDirect", StringComparison.Ordinal))
            {
                if (at < 0 || at >= Index.Length) return -1;
                at = Index[at];
            }
            if (at < 0 || (at + 1) * Stride > Values.Length) return -1;
            return at * Stride;
        }

        public (float X, float Y, float Z) Vector(int control, int corner, (float, float, float) blank)
        {
            var at = At(control, corner);
            return at < 0 ? blank : ((float) Values[at], (float) Values[at + 1], (float) Values[at + 2]);
        }
    }

    public sealed record Mesh(string Name, double[] Positions, int[] PolygonVertexIndex,
                              double[] Normals, int[] NormalIndex,
                              string NormalMapping, string NormalReference,
                              double[] Uvs, int[] UvIndex, string UvMapping, string UvReference,
                              List<Influence> Influences,
                              int[] MaterialIds, string MaterialMapping,
                              Layer? Colours = null, Layer? Tangents = null, Layer? Binormals = null,
                              int Index = 0, int UvLayers = 1)
    {
        /// <summary>
        /// WHICH GEOMETRY IN THE FILE THIS IS, which is not its position in the returned list.
        ///
        /// <see cref="FbxReader.ReadMeshes"/> skips geometries it cannot read -- a blendshape
        /// target is a Geometry node with no PolygonVertexIndex, and there is one per shape -- so
        /// the list is COMPACTED and a caller counting list positions asks for the wrong node.
        /// Every object after the first shape would be placed by a transform belonging to
        /// something else, silently, which is the fault that put a shoulder pad at the knees
        /// arrived at from a third direction.
        /// </summary>
        public int Geometry => Index;

        /// <summary>The author's own vertex colours, RGBA per entry. Absent on most exports.</summary>
        public Layer Colour => Colours ?? Layer.None;

        /// <summary>The file's own tangent basis, which is better than one computed from UVs.</summary>
        public Layer Tangent => Tangents ?? Layer.None;

        /// <summary>Needed only for the tangent's HANDEDNESS, which a tangent alone cannot say.</summary>
        public Layer Binormal => Binormals ?? Layer.None;

        public int VertexCount => Positions.Length / 3;

        /// <summary>
        /// How many materials this mesh actually uses, which is not the same as the highest id:
        /// an exporter can leave a slot assigned to no polygon at all.
        /// </summary>
        public int MaterialCount =>
            MaterialIds.Length == 0 ? 1 : MaterialIds.Distinct().Count();

        /// <summary>
        /// The material of polygon <paramref name="poly"/>.
        ///
        /// AllSame means the layer holds ONE number for the whole mesh, so every polygon reads
        /// entry zero. Anything else is one entry per polygon, and a file that runs short is
        /// treated as material zero rather than throwing -- a mesh with a truncated layer should
        /// import wrong in a visible way, not fail to import at all.
        /// </summary>
        public int MaterialOf(int poly)
        {
            if (MaterialIds.Length == 0) return 0;
            if (MaterialMapping.Contains("AllSame", StringComparison.OrdinalIgnoreCase))
                return MaterialIds[0];
            return poly >= 0 && poly < MaterialIds.Length ? MaterialIds[poly] : 0;
        }

        /// <summary>
        /// Triangle corners, as indices into <see cref="Positions"/>.
        ///
        /// FBX stores polygons as a flat index run where the LAST corner of each polygon is
        /// written as `~i` -- a negative number that both gives the index and marks the end. So a
        /// quad and a triangle are the same array with different run lengths, and anything with
        /// more than three corners is fanned here rather than left for a caller to trip over.
        /// </summary>
        public List<int> Triangles() => TriangleCorners().Select(c => c.Vertex).ToList();

        /// <summary>
        /// The same triangles, each corner paired with its POSITION IN THE POLYGON RUN.
        ///
        /// Both numbers are needed and they index different things. Normals and UVs are usually
        /// stored ByPolygonVertex -- one per corner, so that two faces meeting at a hard edge can
        /// disagree -- and those are addressed by the corner's ordinal in the flat run. Positions
        /// and skin weights are per CONTROL POINT. Returning only the first is what makes an
        /// importer average the normals of a hard edge and produce a mesh that looks melted.
        /// </summary>
        public List<(int Vertex, int Corner)> TriangleCorners() =>
            TriangleCornersWithPolygon().Select(c => (c.Vertex, c.Corner)).ToList();

        /// <summary>
        /// The same triangles again, each corner also carrying the POLYGON it was fanned from.
        ///
        /// Needed because a material is assigned per polygon, not per corner or per triangle: a
        /// quad split into two triangles contributes both to whichever material the quad had.
        /// </summary>
        public List<(int Vertex, int Corner, int Polygon)> TriangleCornersWithPolygon()
        {
            var tris = new List<(int, int, int)>();
            var poly = new List<(int Vertex, int Corner)>();
            var polygon = 0;
            for (var at = 0; at < PolygonVertexIndex.Length; at++)
            {
                var raw = PolygonVertexIndex[at];
                var last = raw < 0;
                poly.Add((last ? ~raw : raw, at));
                if (!last) continue;
                for (var i = 2; i < poly.Count; i++)
                {
                    tris.Add((poly[0].Vertex, poly[0].Corner, polygon));
                    tris.Add((poly[i - 1].Vertex, poly[i - 1].Corner, polygon));
                    tris.Add((poly[i].Vertex, poly[i].Corner, polygon));
                }
                poly.Clear();
                polygon++;
            }
            return tris;
        }
    }

    public Node Root { get; private set; } = new("", new(), new());
    public int Version { get; private set; }

    private readonly byte[] _d;
    private readonly bool _wide;

    private FbxReader(byte[] data, int version)
    {
        _d = data;
        Version = version;
        _wide = version >= 7500;
    }

    public static FbxReader Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 27 ||
            Encoding.ASCII.GetString(data, 0, 18) != "Kaydara FBX Binary")
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} is not a binary FBX. Export it as BINARY rather than " +
                "ASCII -- both have the same extension and only one of them is this format.");

        var version = BitConverter.ToInt32(data, 23);
        var r = new FbxReader(data, version);
        r.Root = new Node("", new List<object>(), r.ReadChildren(27, data.Length));
        return r;
    }

    // ------------------------------------------------------------------ the node tree

    private List<Node> ReadChildren(int pos, int end)
    {
        var kids = new List<Node>();
        var headerSize = (_wide ? 24 : 12) + 1;

        while (pos + headerSize <= end)
        {
            long endOffset, numProps, propLen;
            if (_wide)
            {
                endOffset = BitConverter.ToInt64(_d, pos);
                numProps = BitConverter.ToInt64(_d, pos + 8);
                propLen = BitConverter.ToInt64(_d, pos + 16);
            }
            else
            {
                endOffset = BitConverter.ToUInt32(_d, pos);
                numProps = BitConverter.ToUInt32(_d, pos + 4);
                propLen = BitConverter.ToUInt32(_d, pos + 8);
            }

            // A zero record is the list terminator, not a record. Reading past it walks into the
            // thirteen null bytes and produces a node with an empty name and no end.
            if (endOffset == 0) break;

            var nameLen = _d[pos + headerSize - 1];
            var name = Encoding.ASCII.GetString(_d, pos + headerSize, nameLen);

            var at = pos + headerSize + nameLen;
            var props = new List<object>();
            for (long i = 0; i < numProps; i++) props.Add(ReadProperty(ref at));

            var childStart = pos + headerSize + nameLen + (int) propLen;
            var children = childStart < endOffset ? ReadChildren(childStart, (int) endOffset)
                                                  : new List<Node>();

            kids.Add(new Node(name, props, children));
            pos = (int) endOffset;
        }
        return kids;
    }

    private object ReadProperty(ref int at)
    {
        var type = (char) _d[at++];
        switch (type)
        {
            case 'Y': { var v = BitConverter.ToInt16(_d, at); at += 2; return (int) v; }
            case 'C': { var v = _d[at]; at += 1; return v != 0; }
            case 'I': { var v = BitConverter.ToInt32(_d, at); at += 4; return v; }
            case 'F': { var v = BitConverter.ToSingle(_d, at); at += 4; return (double) v; }
            case 'D': { var v = BitConverter.ToDouble(_d, at); at += 8; return v; }
            case 'L': { var v = BitConverter.ToInt64(_d, at); at += 8; return v; }

            case 'S':
            case 'R':
            {
                var len = BitConverter.ToInt32(_d, at); at += 4;
                // Names are stored "Object\0\x01Class". The separator is not printable and would
                // otherwise end up inside every name this returns.
                var s = Encoding.UTF8.GetString(_d, at, len).Replace("\0", "::");
                at += len;
                return s;
            }

            case 'f': return ReadArray(ref at, 4, raw => Doubles(raw, 4));
            case 'd': return ReadArray(ref at, 8, raw => Doubles(raw, 8));
            case 'i': return ReadArray(ref at, 4, Ints);
            case 'l': return ReadArray(ref at, 8, raw => LongsAsInts(raw));
            case 'b': return ReadArray(ref at, 1, raw => raw.Select(b => (int) b).ToArray());

            default:
                throw new InvalidDataException($"unknown FBX property type '{type}'");
        }
    }

    private object ReadArray(ref int at, int elementSize, Func<byte[], object> decode)
    {
        var count = BitConverter.ToInt32(_d, at);
        var encoding = BitConverter.ToInt32(_d, at + 4);
        var compressed = BitConverter.ToInt32(_d, at + 8);
        at += 12;

        var raw = new byte[compressed];
        Array.Copy(_d, at, raw, 0, compressed);
        at += compressed;

        if (encoding == 1)
        {
            // zlib, per array rather than per file, so both forms turn up in one document.
            using var input = new MemoryStream(raw, 2, raw.Length - 2);   // skip the zlib header
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(count * elementSize);
            inflate.CopyTo(output);
            raw = output.ToArray();
        }
        return decode(raw);
    }

    private static double[] Doubles(byte[] raw, int size)
    {
        var n = raw.Length / size;
        var v = new double[n];
        for (var i = 0; i < n; i++)
            v[i] = size == 4 ? BitConverter.ToSingle(raw, i * 4) : BitConverter.ToDouble(raw, i * 8);
        return v;
    }

    private static int[] Ints(byte[] raw)
    {
        var v = new int[raw.Length / 4];
        for (var i = 0; i < v.Length; i++) v[i] = BitConverter.ToInt32(raw, i * 4);
        return v;
    }

    private static int[] LongsAsInts(byte[] raw)
    {
        var v = new int[raw.Length / 8];
        for (var i = 0; i < v.Length; i++) v[i] = (int) BitConverter.ToInt64(raw, i * 8);
        return v;
    }

    // ------------------------------------------------------------------ what a mesh needs

    /// <summary>
    /// What the file says about its own axes and units.
    ///
    /// Every FBX declares this in GlobalSettings, and it is the ONLY honest answer to "which way
    /// is up": derived from the bounding box, a helmet and a boot disagree, and a helmet exported
    /// from a different package disagrees with itself. The exporter wrote down what it meant.
    ///
    /// UpAxis / FrontAxis / CoordAxis are 0, 1, 2 for X, Y, Z, each with a Sign of +1 or -1.
    /// UnitScaleFactor is centimetres per file unit -- 1.0 for a file already in centimetres,
    /// 100.0 for one in metres. Unreal works in centimetres.
    /// </summary>
    public Dictionary<string, double> GlobalSettings()
    {
        var found = new Dictionary<string, double>(StringComparer.Ordinal);
        var props = Root.Child("GlobalSettings")?.Child("Properties70");
        if (props is null) return found;

        foreach (var p in props.Children.Where(c => c.Name == "P"))
        {
            if (p.Text() is not { } key) continue;
            // A property is [name, type, subtype, flags, value...]. The value is whatever numeric
            // property follows the four strings, so it is taken by TYPE rather than by position --
            // some writers add a sixth field and some do not.
            var value = p.Properties.Skip(4).FirstOrDefault(v => v is double or int or long);
            if (value is null) continue;
            found[key] = value switch
            {
                double d => d,
                int i => i,
                long l => l,
                _ => 0,
            };
        }
        return found;
    }

    /// <summary>
    /// The transform on the node that owns the geometry.
    ///
    /// This is where scaling an object in a modelling package ACTUALLY GOES. Scale a mesh in Max
    /// and the vertices do not move -- the node's Lcl Scaling changes and everything downstream is
    /// expected to honour it. An importer that reads only `Geometry/Vertices` therefore ignores
    /// the artist's scale completely, and the mesh comes out at whatever size it was modelled at
    /// no matter how many times they resize and re-export. That is a maddening thing to debug from
    /// the far side, because nothing about the file looks wrong.
    ///
    /// Returned separately from the vertices rather than pre-multiplied, so a caller can say what
    /// it found and a person can see whether it matches what they did.
    /// </summary>
    /// <summary>Scale, translation and rotation on the node that owns the geometry.</summary>
    public sealed record NodeTransform((double X, double Y, double Z) Scale,
                                       (double X, double Y, double Z) Translation,
                                       (double X, double Y, double Z) Rotation,
                                       (double X, double Y, double Z) PreRotation,
                                       (double X, double Y, double Z) PostRotation);

    /// <summary>
    /// The whole transform on the geometry's node.
    ///
    /// Moving an object in a modelling package sets the TRANSLATION and leaves the vertices where
    /// they were, exactly as resizing sets the scale. An importer reading only the vertices
    /// therefore ignores both, and the artist watches their corrections have no effect with
    /// nothing in the file looking wrong.
    /// </summary>
    /// <param name="index">Which object's node. Every object in a scene has its own, and a piece
    /// modelled in place carries its placement THERE rather than in its vertices.</param>
    public NodeTransform? MeshNodeTransform(int index = 0)
    {
        var model = GeometryOwner(index);
        if (model is null) return null;

        var scale = (1.0, 1.0, 1.0);
        var translation = (0.0, 0.0, 0.0);
        var rotation = (0.0, 0.0, 0.0);
        var preRotation = (0.0, 0.0, 0.0);
        var postRotation = (0.0, 0.0, 0.0);

        foreach (var p in model.Child("Properties70")?.Children.Where(p => p.Name == "P") ?? [])
        {
            var nums = p.Properties.Skip(4).OfType<double>().Take(3).ToArray();
            if (nums.Length != 3) continue;
            switch (p.Text())
            {
                case "Lcl Scaling": scale = (nums[0], nums[1], nums[2]); break;
                case "Lcl Translation": translation = (nums[0], nums[1], nums[2]); break;
                case "Lcl Rotation": rotation = (nums[0], nums[1], nums[2]); break;

                // PreRotation is HALF THE ROTATION and reading only the other half is worse than
                // reading neither. A modelling package exporting a Z-up scene as Y-up writes the
                // conversion here and leaves `Lcl Rotation` holding the object's own placement --
                // and the two frequently cancel. A DLT-20A carried PreRotation -90 and Lcl
                // Rotation +90 about the same axis: net nothing, and we applied the +90 alone,
                // which stood the gun's grip six centimetres out of the hand holding it.
                case "PreRotation": preRotation = (nums[0], nums[1], nums[2]); break;
                case "PostRotation": postRotation = (nums[0], nums[1], nums[2]); break;
            }
        }
        return new NodeTransform(scale, translation, rotation, preRotation, postRotation);
    }

    /// <summary>
    /// EVERY numeric property on the geometry's node, named.
    ///
    /// `Lcl Translation`, `Lcl Rotation` and `Lcl Scaling` are three of about a dozen things that
    /// can move a mesh, and reading only those is how a gun came in fifteen centimetres behind its
    /// own grip. A modelling package with an offset pivot writes `GeometricTranslation`, or a
    /// `ScalingPivot`, or a `PreRotation`, and each of them is silent in exactly the same way: the
    /// vertices do not move, so nothing in the file looks wrong.
    ///
    /// The multiplier is what makes this dangerous rather than merely wrong. These are in FILE
    /// units, and this game's meshes carry a node scale near 100 -- so a pivot offset of a tenth
    /// of a unit is eleven centimetres, and one small enough to look like rounding is still a
    /// hand's width.
    /// </summary>
    /// <param name="index">Which object's node. EVERY object in a scene has one of its own, and
    /// reading only the first is how a shoulder pad ends up at the knees.</param>
    public List<(string Name, double[] Values)> MeshNodeProperties(int index = 0)
    {
        var found = new List<(string, double[])>();
        if (GeometryOwner(index) is not { } model) return found;
        foreach (var p in model.Child("Properties70")?.Children.Where(p => p.Name == "P") ?? [])
        {
            var nums = p.Properties.Skip(4).OfType<double>().ToArray();
            if (nums.Length > 0) found.Add((p.Text() ?? "", nums));
        }
        return found;
    }

    /// <summary>
    /// WHAT THE ARTIST CALLED THIS OBJECT.
    ///
    /// The name is on the MODEL, not on the geometry: a Geometry record is written
    /// `"\0\x01Geometry"` with nothing before the separator, so reading it gives an empty string
    /// every time. The Model beside it holds `"Backpack\0\x01Model"`.
    ///
    /// It matters because it is the only thing that tells an author WHICH PIECE a material slot
    /// belongs to. "Material 2 -- object 2 in your file" is a guessing game on a torso carrying a
    /// backpack and a shoulder pad; "Material 2 -- Backpack" is not.
    /// </summary>
    private string NameOf(Node geo, int index)
    {
        var fromModel = (GeometryOwner(index)?.Text(1) ?? "").Split("::")[0].Trim();
        if (fromModel.Length > 0) return fromModel;

        var fromGeometry = (geo.Text(1) ?? "").Split("::")[0].Trim();
        return fromGeometry.Length > 0 ? fromGeometry : $"object {index + 1}";
    }

    /// <param name="index">Which geometry's owning Model. Zero is the first, which is what a
    /// single-object file has always used.</param>
    private Node? GeometryOwner(int index = 0)
    {
        var objects = Root.Child("Objects");
        var connections = Root.Child("Connections");
        if (objects is null || connections is null) return null;

        var geometry = objects.Children.Where(c => c.Name == "Geometry").Skip(index).FirstOrDefault();
        if (geometry?.Properties.FirstOrDefault() is not long geometryId) return null;

        long modelId = 0;
        foreach (var c in connections.Children)
        {
            if (c.Properties.Count < 3) continue;
            if (c.Properties[1] is not long a || c.Properties[2] is not long b) continue;
            if (a == geometryId) { modelId = b; break; }
        }
        if (modelId == 0) return null;

        return objects.Children.FirstOrDefault(
            c => c.Name == "Model" && c.Properties.FirstOrDefault() is long id && id == modelId);
    }

    public (double X, double Y, double Z)? MeshNodeScale()
    {
        var objects = Root.Child("Objects");
        var connections = Root.Child("Connections");
        if (objects is null || connections is null) return null;

        var geometry = objects.Children.FirstOrDefault(c => c.Name == "Geometry");
        if (geometry?.Properties.FirstOrDefault() is not long geometryId) return null;

        // The geometry is the CHILD of the model that draws it, which reads backwards and is what
        // the format does -- the same indirection the skin clusters go through.
        long modelId = 0;
        foreach (var c in connections.Children)
        {
            if (c.Properties.Count < 3) continue;
            if (c.Properties[1] is not long a || c.Properties[2] is not long b) continue;
            if (a == geometryId) { modelId = b; break; }
        }
        if (modelId == 0) return null;

        var model = objects.Children.FirstOrDefault(
            c => c.Name == "Model" && c.Properties.FirstOrDefault() is long id && id == modelId);
        if (model is null) return null;

        foreach (var p in model.Child("Properties70")?.Children.Where(p => p.Name == "P") ?? [])
            if (p.Text() == "Lcl Scaling")
            {
                var nums = p.Properties.Skip(4).OfType<double>().Take(3).ToArray();
                if (nums.Length == 3) return (nums[0], nums[1], nums[2]);
            }

        // A NODE THAT OMITS Lcl Scaling STATES IDENTITY. The format's default is (1,1,1), and a
        // modelling package writes nothing at all when nothing was scaled -- which is the usual
        // case, and the case after a Reset XForm.
        //
        // Returning null here meant "the file says nothing", and the caller then fell back to this
        // game's measured 102.186 cm per unit. So a mesh already in centimetres was multiplied by
        // a hundred, failed the size check, and the IMPORT WAS REFUSED -- leaving a mod that built
        // cleanly, installed, and showed the untouched duplicate in game. The two readers
        // disagreed: MeshNodeTransform has always defaulted this to 1, and only this one did not.
        return (1, 1, 1);
    }

    /// <summary>
    /// Every bone in the file, at its position in the scene.
    ///
    /// A Model node states its translation RELATIVE TO ITS PARENT, so the chain has to be walked
    /// to get anywhere -- and the chain lives in the Connections table, not in the node tree,
    /// which is the same indirection the skin clusters go through.
    ///
    /// Rotation and scale on intermediate joints are ignored deliberately. This exists to answer
    /// "which axis is which", and for that a set of point CORRESPONDENCES between two copies of
    /// the same skeleton is enough; composing the full transforms would be a second place for a
    /// quaternion convention to be wrong, which is a fault this project has already paid for once.
    /// </summary>
    public Dictionary<string, (double X, double Y, double Z)> BoneWorldPositions()
    {
        var found = new Dictionary<string, (double, double, double)>(StringComparer.Ordinal);

        var objects = Root.Child("Objects");
        var connections = Root.Child("Connections");
        if (objects is null || connections is null) return found;

        var name = new Dictionary<long, string>();
        var local = new Dictionary<long, (double X, double Y, double Z)>();

        foreach (var c in objects.Children.Where(c => c.Name == "Model"))
        {
            if (c.Properties.FirstOrDefault() is not long id) continue;
            name[id] = (c.Text(1) ?? "").Split("::")[0];

            var t = (0.0, 0.0, 0.0);
            foreach (var p in c.Child("Properties70")?.Children.Where(p => p.Name == "P") ?? [])
                if (p.Text() == "Lcl Translation")
                {
                    var nums = p.Properties.Skip(4).OfType<double>().Take(3).ToArray();
                    if (nums.Length == 3) t = (nums[0], nums[1], nums[2]);
                }
            local[id] = t;
        }

        var parent = new Dictionary<long, long>();
        foreach (var c in connections.Children)
        {
            if (c.Properties.Count < 3) continue;
            if (c.Properties[1] is not long child || c.Properties[2] is not long owner) continue;
            if (local.ContainsKey(child) && local.ContainsKey(owner)) parent[child] = owner;
        }

        foreach (var id in local.Keys)
        {
            double x = 0, y = 0, z = 0;
            var at = id;
            // Bounded, because a malformed file can have a cycle and an unbounded walk here would
            // hang the tool rather than report a bad file.
            for (var depth = 0; depth < 256; depth++)
            {
                var t = local[at];
                x += t.X; y += t.Y; z += t.Z;
                if (!parent.TryGetValue(at, out var up) || up == at) break;
                at = up;
            }
            if (name.TryGetValue(id, out var n) && n.Length > 0) found[n] = (x, y, z);
        }
        return found;
    }

    /// <summary>
    /// The first geometry in the file, with its skin resolved to bone names.
    ///
    /// Returns null with a reason rather than a half-built mesh: a mesh imported without its skin
    /// is a helmet that sits at the origin while the character walks away, which looks like a
    /// placement bug and is actually a missing deformer.
    /// </summary>
    /// <summary>
    /// Every mesh in the file, in the order the file holds them.
    ///
    /// A scene routinely keeps a body and the pieces bolted to it as SEPARATE OBJECTS -- which is
    /// how somebody would naturally build a shoulder pad -- and reading only the first silently
    /// drops the rest. It looked exactly like a single-material export until the objects were
    /// counted.
    /// </summary>
    /// <summary>
    /// How many BLENDSHAPE TARGETS the file carries, which this importer does not use.
    ///
    /// A target is a Geometry node of class "Shape" holding only the vertices it moves. Nothing in
    /// this game's clothing has a morph target -- every mesh measured reports none -- so there are
    /// no morph buffers in any template to put one into, and carrying them would be structural
    /// work with nothing at the end of it. Counted so the build can SAY that: an author who built
    /// shapes in their modelling package can see them and needs to be told they went nowhere.
    /// </summary>
    public int MorphTargets =>
        Root.Child("Objects")?.Children.Count(c => c.Name == "Geometry" && c.Text(2) == "Shape") ?? 0;

    public List<Mesh> ReadMeshes(out string? why)
    {
        why = null;
        var found = new List<Mesh>();

        var objects = Root.Child("Objects");
        if (objects is null) { why = "no Objects section"; return found; }

        var count = objects.Children.Count(c => c.Name == "Geometry");
        for (var i = 0; i < count; i++)
        {
            var one = ReadMesh(out var oneWhy, i);
            if (one is not null) found.Add(one);
            else why ??= oneWhy;
        }

        // The list is COMPACTED -- see Mesh.Geometry -- so nothing downstream may use a list
        // position as a node index. This is the guard that says so if it ever stops being true.
        for (var i = 0; i < found.Count; i++)
            if (found[i].Geometry < i)
                throw new InvalidDataException(
                    $"geometry {found[i].Geometry} came back at list position {i}");

        if (found.Count == 0) why ??= "no geometry could be read";
        return found;
    }

    /// <param name="index">Which geometry to read. Zero is the first, which is what the single
    /// mesh path has always used.</param>
    public Mesh? ReadMesh(out string? why, int index = 0)
    {
        why = null;

        var objects = Root.Child("Objects");
        if (objects is null) { why = "no Objects section"; return null; }

        var geo = objects.Children.Where(c => c.Name == "Geometry").Skip(index).FirstOrDefault();
        if (geo is null) { why = "no Geometry in the file"; return null; }

        if (geo.Child("Vertices")?.Properties.FirstOrDefault() is not double[] positions)
        { why = "the geometry has no Vertices array"; return null; }
        if (geo.Child("PolygonVertexIndex")?.Properties.FirstOrDefault() is not int[] poly)
        { why = "the geometry has no PolygonVertexIndex array"; return null; }

        var normalNode = geo.Child("LayerElementNormal");
        var normals = normalNode?.Child("Normals")?.Properties.FirstOrDefault() as double[] ?? [];
        // Normals get their OWN index array when they are stored IndexToDirect. The first version
        // of this reused the UV index for both, which happens to work whenever normals are Direct
        // -- which they are in the file that was tested -- and silently shuffles them when they
        // are not.
        var normalIndex = normalNode?.Child("NormalsIndex")?.Properties.FirstOrDefault() as int[] ?? [];

        var uvNode = geo.Child("LayerElementUV");
        var uvs = uvNode?.Child("UV")?.Properties.FirstOrDefault() as double[] ?? [];
        var uvIndex = uvNode?.Child("UVIndex")?.Properties.FirstOrDefault() as int[] ?? [];

        // The material layer. Absent on a single-material export from some tools, which is why
        // an empty array means "all one material" rather than "no materials".
        var matNode = geo.Child("LayerElementMaterial");
        var materials = matNode?.Child("Materials")?.Properties.FirstOrDefault() as int[] ?? [];

        // COLOURS AND TANGENTS, both of which were being invented rather than read. A mesh's
        // vertex colours were written as white whatever the file said, and its tangent basis was
        // recomputed from the UVs -- which is a fair approximation and is not what the artist saw.
        static Layer Read(Node? node, string values, string index, int stride) =>
            node is null ? Layer.None
            : new Layer(node.Child(values)?.Properties.FirstOrDefault() as double[] ?? [],
                        node.Child(index)?.Properties.FirstOrDefault() as int[] ?? [],
                        node.Child("MappingInformationType")?.Text() ?? "",
                        node.Child("ReferenceInformationType")?.Text() ?? "",
                        stride);

        // HOW MANY UV LAYERS THE FILE HAS. Only the first is read, and that is a statement about
        // this game rather than a limitation worth fixing: every mesh in it -- clothing, droids,
        // weapons -- carries exactly one, so there is no second channel for anything to sample.
        // Counted so the build can SAY that, instead of quietly using layer one and leaving the
        // author to wonder where the other went.
        var uvLayers = Math.Max(1, geo.Children.Count(c => c.Name == "LayerElementUV"));

        var colours = Read(geo.Child("LayerElementColor"), "Colors", "ColorIndex", 4);
        var tangents = Read(geo.Child("LayerElementTangent"), "Tangents", "TangentsIndex", 3);
        var binormals = Read(geo.Child("LayerElementBinormal"), "Binormals", "BinormalsIndex", 3);

        // The geometry's own id, which is what says which clusters belong to it.
        var geometryId = geo.Properties.FirstOrDefault() as long? ?? 0;
        var influences = ReadSkin(objects, geometryId);

        return new Mesh(
            NameOf(geo, index),
            positions, poly,
            normals, normalIndex,
            normalNode?.Child("MappingInformationType")?.Text() ?? "",
            normalNode?.Child("ReferenceInformationType")?.Text() ?? "",
            uvs, uvIndex,
            uvNode?.Child("MappingInformationType")?.Text() ?? "",
            uvNode?.Child("ReferenceInformationType")?.Text() ?? "",
            influences,
            materials,
            matNode?.Child("MappingInformationType")?.Text() ?? "",
            colours, tangents, binormals, index, uvLayers);
    }

    /// <summary>
    /// Every skin cluster, paired with the bone it drives.
    ///
    /// The pairing is the awkward part: a cluster does not name its bone, it is CONNECTED to it in
    /// a separate table keyed by 64-bit object ids. So the ids are indexed first and the
    /// connections walked afterwards -- and a cluster whose bone cannot be resolved is dropped
    /// rather than guessed at, because a wrong bone moves the mesh somewhere plausible.
    /// </summary>
    /// <param name="geometryId">
    /// WHICH GEOMETRY'S SKIN TO READ. Every cluster in the file used to be returned for every
    /// mesh in it, because the chain from a cluster back to the geometry it deforms was never
    /// walked -- and for a single-object file that is harmless, since every cluster does belong to
    /// the one geometry there.
    ///
    /// For two objects it is ruinous and it is SILENT. Both objects get both objects' clusters, so
    /// every vertex carries roughly twice the weight it should: a clone torso came out with
    /// `shoulder_pad_l=1.00` laid on top of an already-complete body set summing to 1.00, and the
    /// pauldron came out wearing the BODY's weighting, taken from whatever control point shared
    /// its index. Renormalising then halves everything real, so a chest vertex ends up half driven
    /// by a shoulder pad and a pad vertex by the hips. It looks fine in a bind pose and tears open
    /// the moment anything moves, which is exactly how it presented.
    ///
    /// The chain the format actually states is Geometry &lt;- Skin &lt;- Cluster &lt;- Bone, and
    /// connections are (child, parent). So a cluster's owner is its parent's parent.
    /// </param>
    private List<Influence> ReadSkin(Node objects, long geometryId)
    {
        var modelNames = new Dictionary<long, string>();
        var clusters = new Dictionary<long, Node>();
        var skins = new HashSet<long>();

        foreach (var c in objects.Children)
        {
            if (c.Properties.FirstOrDefault() is not long id) continue;
            if (c.Name == "Model")
                modelNames[id] = (c.Text(1) ?? "").Split("::")[0];
            else if (c.Name == "Deformer" && c.Text(2) == "Cluster")
                clusters[id] = c;
            else if (c.Name == "Deformer" && c.Text(2) == "Skin")
                skins.Add(id);
        }

        var found = new List<Influence>();
        var connections = Root.Child("Connections");
        if (connections is null) return found;

        // Every child's parent, so the two hops from a cluster to its geometry can be walked.
        var parentOf = new Dictionary<long, long>();
        foreach (var c in connections.Children)
        {
            if (c.Properties.Count < 3) continue;
            if (c.Properties[1] is long ch && c.Properties[2] is long pa) parentOf.TryAdd(ch, pa);
        }

        // Which clusters this geometry's skin owns. A file whose chain does not resolve at all --
        // an exporter that omits the Skin, or a shape this cannot follow -- keeps the old
        // behaviour ONLY when there is a single geometry, where it is provably harmless. With
        // several, guessing is what caused the fault, so nothing is guessed.
        var mine = new HashSet<long>();
        foreach (var id in clusters.Keys)
            if (parentOf.TryGetValue(id, out var skin) && skins.Contains(skin) &&
                parentOf.TryGetValue(skin, out var geom) && geom == geometryId)
                mine.Add(id);

        var single = objects.Children.Count(c => c.Name == "Geometry") == 1;
        var filter = mine.Count > 0 || !single;

        foreach (var c in connections.Children)
        {
            if (c.Properties.Count < 3) continue;
            if (c.Properties[1] is not long a || c.Properties[2] is not long b) continue;

            // A connection is (child, parent). A bone is the CHILD of the cluster it drives, which
            // reads backwards and is what the format does.
            Node? cluster = null;
            string? bone = null;
            if (clusters.TryGetValue(b, out var cb) && modelNames.TryGetValue(a, out var na))
            { cluster = cb; bone = na; }
            else if (clusters.TryGetValue(a, out var ca) && modelNames.TryGetValue(b, out var nb))
            { cluster = ca; bone = nb; }
            if (cluster is null || bone is null) continue;

            // The cluster has to belong to THIS geometry. See geometryId -- this one line is the
            // difference between a two-object import that deforms and one that tears apart.
            if (filter && cluster.Properties.FirstOrDefault() is long clusterId && !mine.Contains(clusterId))
                continue;

            var idx = cluster.Child("Indexes")?.Properties.FirstOrDefault() as int[] ?? [];
            var wts = cluster.Child("Weights")?.Properties.FirstOrDefault() as double[] ?? [];
            if (idx.Length == 0) continue;

            // TransformLink is a 4x4 in column-major order, so the translation is elements
            // 12, 13, 14. Taken straight out of the file rather than composed from the node
            // chain: it is the exporter's own statement of where the bone stood, with no
            // rotation order or parent walk to get wrong.
            (double, double, double)? link = null;
            if (cluster.Child("TransformLink")?.Properties.FirstOrDefault() is double[] { Length: 16 } m)
                link = (m[12], m[13], m[14]);

            found.Add(new Influence(bone, idx, wts, link));
        }
        return found;
    }
}
