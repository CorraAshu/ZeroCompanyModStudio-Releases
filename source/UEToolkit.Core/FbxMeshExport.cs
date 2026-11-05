using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Meshes.PSK;
using static UEToolkit.Core.FbxWriter;

namespace UEToolkit.Core;

/// <summary>
/// A converted mesh as an FBX scene, built with no SDK. See <see cref="FbxWriter"/> for why.
///
/// The shape mirrors what `psk2fbx.py` produces, because that is the pipeline already proven in
/// the user's 3ds Max and being a second opinion about FBX would help nobody:
///
///   * control points are welded by <see cref="CVertexShare"/> -- the SAME class the PSK exporter
///     uses -- so a point here is a point there, and a mesh that comes apart in one comes apart in
///     the other rather than only in this
///   * normals, UVs and colours are per POLYGON VERTEX, so a hard edge stays hard
///   * V is flipped, which is the difference between UE's texture space and FBX's
///   * the Max axis system and centimetres are STATED, not left for an importer to guess
/// </summary>
public static class FbxMeshExport
{
    /// <param name="ExtraUvs">Write UV sets past the first. Some games put masking on UV1.</param>
    /// <param name="Creator">
    /// The application the FBX header names as its author. Null (the default) means whatever this
    /// build calls itself, see <see cref="CreatorName"/>: a file the Studio writes must not say it
    /// came from a program the person never downloaded.
    /// </param>
    /// <param name="RightHanded">
    /// Write the geometry in a RIGHT-HANDED space instead of UE's own. Off (the default) writes
    /// every vertex exactly as the game stores it, which is what every existing export has always
    /// done and what stays byte-identical.
    ///
    /// UE is LEFT-handed. The file's GlobalSettings declare the Max axis system, which is
    /// right-handed (Z up, front -Y), so raw UE vertices arrive in Blender or Max as a MIRROR IMAGE
    /// of the game: a scene built from them is mirrored as a whole, consistently, and a check that
    /// only compares positions cannot see it. Turning this on applies the one change of basis
    /// P = diag(1, -1, 1) to the geometry:
    ///
    ///   * Y is negated in every position and every normal this writes (tangents and binormals
    ///     too, if it ever writes any -- it writes none today), and
    ///   * nothing else: THE TRIANGLES KEEP THEIR CORNER ORDER. This was first specified as "and
    ///     reverse the winding", on the reasoning that a reflection turns a counter-clockwise face
    ///     clockwise. That is true, and it is why no reversal is wanted: UE winds its triangles
    ///     clockwise seen from outside (a left-handed rule), so the reflection is exactly what turns
    ///     them into the counter-clockwise a right-handed program calls front. MEASURED on
    ///     SM_Wall_R11_Base_A (9,576 triangles): the right-hand cross product of the corners agrees
    ///     with the normals for 0 of them in UE's own export, for all 9,576 with Y negated and the
    ///     order kept, and for 0 again if the order is also reversed (inside out). The check is
    ///     `basis_consistent` in the private `--levelfbxcheck` verb; it carries the reversed
    ///     version as a negative control.
    ///
    /// A scene that turns this on must put every PLACEMENT through the same basis, as
    /// P * M * P in System.Numerics' row-vector convention, before <see cref="FbxPlacement.Lcl"/>:
    /// the vertices then land at v * P * (P * M * P) = (v * M) * P, which is where the game puts
    /// them, seen from the right-handed side. See <see cref="LevelFbxExport"/>.
    ///
    /// <see cref="WriteSkeletal"/> with this on puts the WHOLE RIG in the same basis, or the skin would
    /// not hold: every bone's local transform as P * M * P (the translation with Y negated, the rotation
    /// conjugated, see <see cref="InBasis"/>), and from those the skin clusters' TransformLink and Transform
    /// (stored relative to the TransformLink, which for a mesh at the origin is its inverse: P * M^-1 * P) and the
    /// bind pose's matrices, which are the same chain composed. The mesh's own bind-pose matrix is the identity,
    /// which P leaves alone. The private `--rhfbxcheck` reads the Dump's file back and compares each of these with the
    /// game's reference skeleton walked by an independent route, and checks that the mesh at bind pose is not
    /// deformed by its own skin the way 3ds Max's importer evaluates it (`--maxskin`).
    /// </param>
    public sealed record Options(bool ExtraUvs = true, bool Colours = true, string? Creator = null,
                                 bool RightHanded = false);

    /// <summary>
    /// What an FBX written by this build says it was made with: <see cref="Branding.Product"/> and,
    /// when the build sets one, its version ("Zero Company Mod Studio 2.0.0"). The toolkit sets
    /// neither, so it keeps writing "UEToolkit" as it always did.
    /// </summary>
    public static string CreatorName =>
        (Branding.Product.Length > 0 ? Branding.Product : "UEToolkit") +
        (Branding.Version.Length > 0 ? " " + Branding.Version : "");

    private static string CreatorOf(Options options) => options.Creator ?? CreatorName;

    public static bool WriteSkeletal(CSkeletalMesh mesh, string name, string path,
                                     Options? options, out string? why)
    {
        why = null;
        var lod = SkeletalLod(mesh);
        if (lod is null)
        { why = "the mesh has no usable LOD"; return false; }

        try
        {
            var opts = options ?? new Options();
            var scene = Scene(opts);
            var objects = scene.Objects;

            // WELDED THE SAME WAY THE PSK EXPORTER WELDS. Position, normal and a hash of the
            // weights all have to match, so a hard edge and a weight seam both stay split -- which
            // is what keeps this file and the PSK route describing the same mesh.
            var share = new CVertexShare();
            share.Prepare(lod.Verts!);
            foreach (var v in lod.Verts!)
                share.AddVertex(v.Position, v.Normal,
                    (uint) System.Collections.StructuralComparisons.StructuralEqualityComparer
                        .GetHashCode(v.Influences));

            var geometryId = scene.Ids.Next();
            var modelId = scene.Ids.Next();
            objects.Add(Geometry(geometryId, name, lod, share, opts));
            objects.Add(Model(modelId, name));
            scene.Connect(geometryId, modelId);
            scene.Connect(modelId, 0);

            Materials(scene, lod, modelId);

            // ---------------------------------------------------------- the rig
            // THE WHOLE RIG IS WRITTEN IN THE SAME BASIS AS THE MESH, or in none of it. The mesh is
            // already in it by here (Geometry read the option); the pieces below are every other
            // place a coordinate lives: each bone's local transform, the skin clusters' Transform and
            // TransformLink, and the bind pose. A rig converted in some of them and not the others
            // does not fail to load -- it loads and the skin moves at rest.
            var rightHanded = opts.RightHanded;
            var boneIds = new long[mesh.RefSkeleton.Count];
            for (var i = 0; i < mesh.RefSkeleton.Count; i++) boneIds[i] = scene.Ids.Next();

            for (var i = 0; i < mesh.RefSkeleton.Count; i++)
            {
                var bone = mesh.RefSkeleton[i];
                objects.Add(Limb(boneIds[i], bone, i == 0, scene, rightHanded));
                var parent = bone.ParentIndex;
                scene.Connect(boneIds[i], parent >= 0 && parent < boneIds.Length && parent != i
                    ? boneIds[parent] : 0);
            }

            Skin(scene, geometryId, modelId, mesh.RefSkeleton, boneIds, lod, share, rightHanded);
            BindPose(scene, modelId, mesh.RefSkeleton, boneIds, rightHanded);

            scene.Finish();
            Save(scene.Root, path);
            return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    public static bool WriteStatic(CStaticMesh mesh, string name, string path,
                                   Options? options, out string? why)
    {
        why = null;
        var lod = StaticLod(mesh);
        if (lod is null)
        { why = "the mesh has no usable LOD"; return false; }

        try
        {
            var opts = options ?? new Options();
            var scene = Scene(opts);

            var share = StaticShare(lod);

            var geometryId = scene.Ids.Next();
            var modelId = scene.Ids.Next();
            scene.Objects.Add(Geometry(geometryId, name, lod, share, opts));
            scene.Objects.Add(Model(modelId, name));
            scene.Connect(geometryId, modelId);
            scene.Connect(modelId, 0);
            Materials(scene, lod, modelId);

            scene.Finish();
            Save(scene.Root, path);
            return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    // ------------------------------------------------------------------ pieces a scene can be built from

    /// <summary>
    /// The level of detail a static mesh is exported at: the first that is not marked to be
    /// skipped, else the first, and null when that one has no vertices or no indices. WriteStatic
    /// and <see cref="StaticGeometry"/> both ask here, so they cannot choose differently.
    /// </summary>
    internal static CStaticMeshLod? StaticLod(CStaticMesh mesh)
    {
        var lod = mesh.LODs.FirstOrDefault(l => !l.SkipLod) ?? mesh.LODs.FirstOrDefault();
        if (lod?.Verts is not { Length: > 0 } || lod.Indices?.Value is not { Length: > 0 }) return null;
        return lod;
    }

    /// <summary>
    /// The level of detail <see cref="WriteSkeletal"/> exports and <see cref="SkeletalReferencePose"/>
    /// writes: the same choice, made once, so they cannot disagree.
    /// </summary>
    internal static CSkelMeshLod? SkeletalLod(CSkeletalMesh mesh)
    {
        var lod = mesh.LODs.FirstOrDefault(l => !l.SkipLod) ?? mesh.LODs.FirstOrDefault();
        if (lod?.Verts is not { Length: > 0 } || lod.Indices?.Value is not { Length: > 0 }) return null;
        return lod;
    }

    /// <summary>A static LOD's vertices welded by position and normal, as the PSK exporter welds them.</summary>
    private static CVertexShare StaticShare(CStaticMeshLod lod)
    {
        var share = new CVertexShare();
        share.Prepare(lod.Verts!);
        foreach (var v in lod.Verts!) share.AddVertex(v.Position, v.Normal);
        return share;
    }

    /// <summary>
    /// A static mesh's Geometry record, LOD0 EXACTLY as <see cref="WriteStatic"/> writes it -- the
    /// same level of detail chosen, the same weld, the same layers -- for a caller that builds its
    /// own scene (see <see cref="FbxStream"/>) and wants one Geometry that many Models share.
    ///
    /// The vertices are in UE's own space and its centimetres, untouched, unless
    /// <see cref="Options.RightHanded"/> is set: that is the geometry WriteStatic has always
    /// written, and the placement that goes with it (<see cref="FbxPlacement.Lcl"/>) is expressed in
    /// that same space. A change of handedness belongs to the scene as a whole -- the vertices AND
    /// the placements together -- which is why it is an option the scene turns on and never a
    /// choice made per mesh. See <see cref="Options.RightHanded"/> for what it changes.
    ///
    /// Throws <see cref="InvalidOperationException"/> when the mesh has no usable level of detail.
    /// </summary>
    public static Node StaticGeometry(long id, string name, CStaticMesh mesh, Options options)
    {
        var lod = StaticLod(mesh) ?? throw new InvalidOperationException("the mesh has no usable LOD");
        return Geometry(id, name, lod, StaticShare(lod), options);
    }

    /// <summary>
    /// A SKELETAL mesh's Geometry record as plain static geometry: its vertices as the game stores
    /// them (the reference pose), welded by position and normal like <see cref="StaticGeometry"/>,
    /// with no skin, no bones and no pose. For a scene that draws a rigged prop where the game does
    /// -- a level carries no animation state -- and wants a mesh, not a character.
    ///
    /// Throws <see cref="InvalidOperationException"/> when the mesh has no usable level of detail.
    /// </summary>
    public static Node SkeletalReferencePose(long id, string name, CSkeletalMesh mesh, Options options)
    {
        var lod = SkeletalLod(mesh) ?? throw new InvalidOperationException("the mesh has no usable LOD");
        var share = new CVertexShare();
        share.Prepare(lod.Verts!);
        foreach (var v in lod.Verts!) share.AddVertex(v.Position, v.Normal);
        return Geometry(id, name, lod, share, options);
    }

    /// <summary>
    /// A STAIRCASE OF SCENES, each one step fuller than the last.
    ///
    /// Written because "File is corrupted" is the only thing the Autodesk importer will say, and a
    /// 240-bone character is far too much to bisect by reasoning. Level 0 is the scaffold with no
    /// objects at all; each level adds one kind of thing. The first level the importer refuses
    /// names the fault.
    /// </summary>
    public static void WriteProbe(string path, int level)
    {
        var scene = Scene(new Options());

        // Level 6: two MODELS, one parented to the other. Does an object-to-object connection
        // work at all, or only a connection to the scene root?
        if (level == 6)
        {
            var aId = scene.Ids.Next();
            var bId = scene.Ids.Next();
            scene.Objects.Add(Model(aId, "parent"));
            scene.Objects.Add(Model(bId, "child"));
            scene.Connect(aId, 0);
            scene.Connect(bId, aId);
            scene.Finish();
            Save(scene.Root, path);
            return;
        }

        // Level 7: a model with a NULL node attribute, the simplest attribute there is.
        if (level == 7)
        {
            var mId = scene.Ids.Next();
            var aId = scene.Ids.Next();
            var attr = new Node("NodeAttribute").P(aId, Named("probe", "NodeAttribute"), "Null");
            attr.Add("TypeFlags").P("Null");
            scene.Objects.Add(attr);
            scene.Objects.Add(Model(mId, "probe"));
            scene.Connect(mId, 0);
            scene.Connect(aId, mId);
            scene.Finish();
            Save(scene.Root, path);
            return;
        }

        if (level >= 1)
        {
            var modelId = scene.Ids.Next();
            var modelNode = Model(modelId, "probe");
            scene.Connect(modelId, 0);

            if (level >= 2)
            {
                var geometryId = scene.Ids.Next();
                var g = new Node("Geometry").P(geometryId, Named("probe_mesh", "Geometry"), "Mesh");
                g.Add("Vertices").P(Arr(new double[] { 0, 0, 0, 10, 0, 0, 0, 0, 10 }));
                g.Add("PolygonVertexIndex").P(Arr(new[] { 0, 1, ~2 }));
                g.Add("GeometryVersion").P(124);

                // Level 5 is the barest possible mesh: points and polygons, no layers at all.
                // If that attaches and a layered one does not, the layer is the fault.
                if (level == 5)
                {
                    scene.Objects.Add(g);
                    scene.Connect(geometryId, modelId);
                    scene.Objects.Add(modelNode);
                    scene.Finish();
                    Save(scene.Root, path);
                    return;
                }

                var n = g.Add("LayerElementNormal").P(0);
                n.Add("Version").P(101);
                n.Add("Name").P("");
                n.Add("MappingInformationType").P("ByPolygonVertex");
                n.Add("ReferenceInformationType").P("Direct");
                n.Add("Normals").P(Arr(new double[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 }));

                var u = g.Add("LayerElementUV").P(0);
                u.Add("Version").P(101);
                u.Add("Name").P("UV0");
                u.Add("MappingInformationType").P("ByPolygonVertex");
                u.Add("ReferenceInformationType").P("Direct");
                u.Add("UV").P(Arr(new double[] { 0, 0, 1, 0, 0, 1 }));

                var m = g.Add("LayerElementMaterial").P(0);
                m.Add("Version").P(101);
                m.Add("Name").P("");
                m.Add("MappingInformationType").P("ByPolygon");
                m.Add("ReferenceInformationType").P("IndexToDirect");
                m.Add("Materials").P(Arr(new[] { 0 }));

                var layer = g.Add("Layer").P(0);
                layer.Add("Version").P(100);
                Element(layer, "LayerElementNormal", 0);
                Element(layer, "LayerElementUV", 0);
                Element(layer, "LayerElementMaterial", 0);

                scene.Objects.Add(g);
                scene.Connect(geometryId, modelId);
            }

            // The model AFTER its geometry, which is the order the SDK's own files use.
            scene.Objects.Add(modelNode);

            if (level >= 3)
            {
                var matId = scene.Ids.Next();
                var mat = new Node("Material").P(matId, Named("probe_mat", "Material"), "");
                mat.Add("Version").P(102);
                mat.Add("ShadingModel").P("phong");
                mat.Add("MultiLayer").P(0);
                mat.Add("Properties70");
                scene.Objects.Add(mat);
                scene.Connect(matId, modelId);
            }
        }

        scene.Finish();
        Save(scene.Root, path);
    }

    // ------------------------------------------------------------------ the scaffold

    private sealed class SceneBuilder
    {
        public required Node Root;
        public required Node Objects;
        public required Node Connections;
        public required Node Definitions;
        public required IdSource Ids;

        private readonly List<(long Child, long Parent)> _links = new();
        public void Connect(long child, long parent) => _links.Add((child, parent));

        public void Finish()
        {
            foreach (var (child, parent) in _links)
                Connections.Add("C").P("OO", child, parent);

            // The counts an importer reads before it reads the objects. Wrong ones are tolerated
            // by Max and complained about by Blender, and being right costs one pass.
            var byType = new Dictionary<string, int>();
            foreach (var o in Objects.Children)
                byType[o.Name] = byType.GetValueOrDefault(o.Name) + 1;

            // GlobalSettings counts as one, which is why the total is objects + 1 -- the SDK's own
            // file declares it alongside the rest.
            var built = DefinitionsNode(Objects.Children.Count + 1,
                byType.Select(kv => (kv.Key, (object) kv.Value)));
            foreach (var child in built.Children) Definitions.Add(child);
        }
    }

    /// <summary>
    /// The Definitions node: how many objects the file declares, and how many of each type, with
    /// the concrete class a type stands for. <paramref name="total"/> is the number written as the
    /// node's own Count -- the object count PLUS ONE, because the global settings are declared
    /// beside the rest -- and each count may be an int or an <see cref="FbxStream.Slot"/>, so a
    /// streamed file can write the section before the objects and patch the numbers after them.
    ///
    /// The scene builder's own Finish uses this, so a scene built as a tree and one streamed carry
    /// byte-identical definitions.
    /// </summary>
    public static Node DefinitionsNode(object total, IEnumerable<(string Type, object Count)> types)
    {
        var definitions = new Node("Definitions");
        definitions.Add("Version").P(100);
        definitions.Add("Count").P(total);
        definitions.Add("ObjectType").P("GlobalSettings").Add("Count").P(1);
        foreach (var (type, count) in types)
        {
            var entry = definitions.Add("ObjectType").P(type);
            entry.Add("Count").P(count);
            // THE CONCRETE CLASS THE TYPE STANDS FOR. Without it the importer creates the
            // object and then will not attach it to anything: a mesh and a node both arrive,
            // the connection between them is read, and the node still has no attribute. The
            // SDK's own files carry one for every type that becomes a node attribute, and
            // none for Deformer or Pose, which do not.
            if (Template(type) is { } cls)
                entry.Add("PropertyTemplate").P(cls).Add("Properties70");
        }
        return definitions;
    }

    /// <summary>
    /// The FBX class an object type instantiates, or null for the types that carry no template.
    /// Taken from what the SDK writes for the same types.
    /// </summary>
    private static string? Template(string objectType) => objectType switch
    {
        "Geometry" => "FbxMesh",
        "Model" => "FbxNode",
        "Material" => "FbxSurfacePhong",
        "Texture" => "FbxFileTexture",
        "Video" => "FbxVideo",
        "NodeAttribute" => "FbxSkeleton",
        _ => null,
    };

    /// <summary>Exposed only so the scene can hand out ids; see FbxWriter for why they start high.</summary>
    public sealed class IdSource
    {
        private long _next = 1_903_573_208_624;   // SDK-shaped ids, under test
        public long Next() => _next++;
    }

    /// <summary>
    /// EVERYTHING A SCENE HAS BEFORE ITS DEFINITIONS, as top-level nodes in the order the file
    /// carries them: the header extension, the file id, the creation time, the creator, the global
    /// settings (the axis system and the unit), the documents and the references.
    ///
    /// Built ONCE per call and handed back as a list -- not lazily -- because the header holds a
    /// time stamp read from the clock; a caller that writes the same header into two files, or
    /// compares one writer against another, must be able to hold the nodes still.
    /// <see cref="Scene"/> uses this too, so a streamed scene and one built here start identically.
    /// </summary>
    public static IEnumerable<Node> HeaderNodes(Options options)
    {
        var root = new Node("");     // only a place to collect the nodes; it is never written

        // EVERY TOP-LEVEL NODE THE SDK'S OWN FILE HAS.
        //
        // The first version of this had the header, the settings, the objects and the connections
        // -- which our reader was perfectly happy with, and which the Autodesk SDK opened and
        // found ZERO objects in. It reads the file in order and gives up quietly on a shape it
        // does not recognise, so "it parses" and "it imports" are different questions and only
        // one of them matters. These were compared against a file the SDK itself wrote.
        var header = root.Add("FBXHeaderExtension");
        header.Add("FBXHeaderVersion").P(1004);
        header.Add("FBXVersion").P(FbxWriter.Version);
        header.Add("EncryptionType").P(0);
        var stamp = header.Add("CreationTimeStamp");
        stamp.Add("Version").P(1000);
        stamp.Add("Year").P(DateTime.Now.Year);
        stamp.Add("Month").P(DateTime.Now.Month);
        stamp.Add("Day").P(DateTime.Now.Day);
        stamp.Add("Hour").P(DateTime.Now.Hour);
        stamp.Add("Minute").P(DateTime.Now.Minute);
        stamp.Add("Second").P(DateTime.Now.Second);
        stamp.Add("Millisecond").P(0);
        header.Add("Creator").P(CreatorOf(options));

        var info = header.Add("SceneInfo").P(Named("GlobalInfo", "SceneInfo"), "UserData");
        info.Add("Type").P("UserData");
        info.Add("Version").P(100);
        info.Add("MetaData").Add("Version").P(100);
        var infoProps = info.Add("Properties70");
        Prop(infoProps, "DocumentUrl", "KString", "Url", "");
        Prop(infoProps, "SrcDocumentUrl", "KString", "Url", "");
        Prop(infoProps, "Original", "Compound", "", "");
        Prop(infoProps, "Original|ApplicationName", "KString", "", CreatorOf(options));
        Prop(infoProps, "LastSaved", "Compound", "", "");
        Prop(infoProps, "LastSaved|ApplicationName", "KString", "", CreatorOf(options));

        // Half of a pair. The footer carries the other half and the importer checks them against
        // each other -- see FbxWriter.FooterId.
        root.Add("FileId").P(FbxWriter.Bytes(FbxWriter.FileId));
        root.Add("CreationTime").P(FbxWriter.CreationTime);
        root.Add("Creator").P(CreatorOf(options));

        // THE AXIS SYSTEM AND THE UNIT, STATED. FbxAxisSystem.Max is Z up, front -Y, right X --
        // which is what psk2fbx sets, and the reason a part written by either lands the same way
        // up in Max. An importer left to guess assumes Y up and rotates the whole character.
        var settings = root.Add("GlobalSettings");
        settings.Add("Version").P(1000);
        var p = settings.Add("Properties70");
        Prop(p, "UpAxis", "int", "Integer", 2);
        Prop(p, "UpAxisSign", "int", "Integer", 1);
        Prop(p, "FrontAxis", "int", "Integer", 1);
        Prop(p, "FrontAxisSign", "int", "Integer", -1);
        Prop(p, "CoordAxis", "int", "Integer", 0);
        Prop(p, "CoordAxisSign", "int", "Integer", 1);
        Prop(p, "OriginalUpAxis", "int", "Integer", 2);
        Prop(p, "OriginalUpAxisSign", "int", "Integer", 1);
        Prop(p, "UnitScaleFactor", "double", "Number", 1.0);
        Prop(p, "OriginalUnitScaleFactor", "double", "Number", 1.0);

        var documents = root.Add("Documents");
        documents.Add("Count").P(1);
        var doc = documents.Add("Document").P(1_903_572_949_792L, "Scene", "Scene");
        var docProps = doc.Add("Properties70");
        Prop(docProps, "SourceObject", "object", "", "");
        Prop(docProps, "ActiveAnimStackName", "KString", "", "");
        doc.Add("RootNode").P(0L);
        root.Add("References");

        return root.Children;
    }

    private static SceneBuilder Scene(Options options)
    {
        var root = new Node("");
        foreach (var header in HeaderNodes(options)) root.Add(header);

        var definitions = root.Add("Definitions");
        var objects = root.Add("Objects");
        var connections = root.Add("Connections");

        // An empty animation section, which the SDK's own files always carry.
        root.Add("Takes").Add("Current").P("");

        return new SceneBuilder
        {
            Root = root, Objects = objects, Connections = connections,
            Definitions = definitions, Ids = new IdSource(),
        };
    }

    internal static void Prop(Node properties, string name, string type, string sub, params object[] values)
        => properties.Add("P").P(new object[] { name, type, sub, "" }.Concat(values).ToArray());

    // ------------------------------------------------------------------ the mesh

    private static Node Geometry(long id, string name, CBaseMeshLod lod, CVertexShare share, Options options)
    {
        var verts = lod switch
        {
            CSkelMeshLod s => (CMeshVertex[]) s.Verts!,
            CStaticMeshLod t => t.Verts!,
            _ => throw new InvalidOperationException("unknown LOD kind"),
        };
        var indices = lod.Indices!.Value;

        // A DISTINCT NAME FROM THE MODEL's. The SDK's own files name the geometry separately
        // ("tri" and "tri_mesh"), and a geometry sharing its model's name imported as an
        // object that existed but was attached to nothing.
        var node = new Node("Geometry").P(id, Named(name + "_mesh", "Geometry"), "Mesh");

        // RIGHT-HANDED: the basis P = diag(1, -1, 1), see Options.RightHanded -- Y negated in the
        // positions here and the normals below, the corner order untouched. `0.0 - y` rather than
        // `-y` so a zero stays a positive zero and is not written as "-0".
        var rh = options.RightHanded;
        var points = new double[share.Points.Count * 3];
        for (var i = 0; i < share.Points.Count; i++)
        {
            points[i * 3 + 0] = share.Points[i].X;
            points[i * 3 + 1] = rh ? 0.0 - share.Points[i].Y : share.Points[i].Y;
            points[i * 3 + 2] = share.Points[i].Z;
        }
        node.Add("Vertices").P(Arr(points));

        // Triangles walked SECTION BY SECTION, because the material of a polygon is decided by
        // which section it came from and nothing else records that.
        var sections = lod.Sections?.Value ?? [];
        var corners = new List<int>(indices.Length);
        var wedges = new List<int>(indices.Length);
        var polyMaterial = new List<int>(indices.Length / 3);

        for (var s = 0; s < Math.Max(sections.Length, 1); s++)
        {
            var first = sections.Length > 0 ? sections[s].FirstIndex : 0;
            var faces = sections.Length > 0 ? sections[s].NumFaces : indices.Length / 3;
            for (var f = 0; f < faces; f++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var wedge = (int) indices[first + f * 3 + c];
                    if (wedge < 0 || wedge >= share.WedgeToVert.Count) { wedge = 0; }
                    wedges.Add(wedge);
                    var point = share.WedgeToVert[wedge];
                    // The last corner of a polygon is written as its bitwise complement, which is
                    // both the index and the end marker.
                    corners.Add(c == 2 ? ~point : point);
                }
                polyMaterial.Add(s);
            }
        }
        node.Add("PolygonVertexIndex").P(Arr(corners.ToArray()));
        node.Add("GeometryVersion").P(124);

        // ---------------------------------------------------------- normals
        var normals = new double[wedges.Count * 3];
        for (var i = 0; i < wedges.Count; i++)
        {
            var n = verts[wedges[i]].Normal;
            normals[i * 3 + 0] = n.X;
            normals[i * 3 + 1] = rh ? 0.0 - n.Y : n.Y;
            normals[i * 3 + 2] = n.Z;
        }
        var normalLayer = node.Add("LayerElementNormal").P(0);
        normalLayer.Add("Version").P(101);
        normalLayer.Add("Name").P("");
        normalLayer.Add("MappingInformationType").P("ByPolygonVertex");
        normalLayer.Add("ReferenceInformationType").P("Direct");
        normalLayer.Add("Normals").P(Arr(normals));

        // ---------------------------------------------------------- UVs
        var uv = new double[wedges.Count * 2];
        for (var i = 0; i < wedges.Count; i++)
        {
            var t = verts[wedges[i]].UV;
            uv[i * 2 + 0] = t.U;
            // V FLIPPED. UE measures from the top and FBX from the bottom; a file written without
            // this is upside down in every texture and looks like a mis-authored map.
            uv[i * 2 + 1] = 1.0 - t.V;
        }
        var uvLayer = node.Add("LayerElementUV").P(0);
        uvLayer.Add("Version").P(101);
        uvLayer.Add("Name").P("UV0");
        uvLayer.Add("MappingInformationType").P("ByPolygonVertex");
        uvLayer.Add("ReferenceInformationType").P("Direct");
        uvLayer.Add("UV").P(Arr(uv));

        var extraUvSets = 0;
        if (options.ExtraUvs && lod.ExtraUV?.Value is { Length: > 0 } extra)
        {
            for (var k = 0; k < extra.Length && k + 1 < lod.NumTexCoords; k++)
            {
                var set = extra[k];
                if (set.Length < verts.Length) continue;
                var more = new double[wedges.Count * 2];
                for (var i = 0; i < wedges.Count; i++)
                {
                    more[i * 2 + 0] = set[wedges[i]].U;
                    more[i * 2 + 1] = 1.0 - set[wedges[i]].V;
                }
                var e = node.Add("LayerElementUV").P(k + 1);
                e.Add("Version").P(101);
                e.Add("Name").P($"UV{k + 1}");
                e.Add("MappingInformationType").P("ByPolygonVertex");
                e.Add("ReferenceInformationType").P("Direct");
                e.Add("UV").P(Arr(more));
                extraUvSets++;
            }
        }

        // ---------------------------------------------------------- colours
        var hasColours = false;
        if (options.Colours && lod.VertexColors is { Length: > 0 } colours)
        {
            var rgba = new double[wedges.Count * 4];
            for (var i = 0; i < wedges.Count; i++)
            {
                var c = wedges[i] < colours.Length ? colours[wedges[i]] : new FColor(255, 255, 255, 255);
                rgba[i * 4 + 0] = c.R / 255.0;
                rgba[i * 4 + 1] = c.G / 255.0;
                rgba[i * 4 + 2] = c.B / 255.0;
                rgba[i * 4 + 3] = c.A / 255.0;
            }
            var e = node.Add("LayerElementColor").P(0);
            e.Add("Version").P(101);
            e.Add("Name").P("Col0");
            e.Add("MappingInformationType").P("ByPolygonVertex");
            e.Add("ReferenceInformationType").P("Direct");
            e.Add("Colors").P(Arr(rgba));
            hasColours = true;
        }

        // ---------------------------------------------------------- materials
        var materialLayer = node.Add("LayerElementMaterial").P(0);
        materialLayer.Add("Version").P(101);
        materialLayer.Add("Name").P("");
        materialLayer.Add("MappingInformationType").P("ByPolygon");
        materialLayer.Add("ReferenceInformationType").P("IndexToDirect");
        materialLayer.Add("Materials").P(Arr(polyMaterial.ToArray()));

        // ---------------------------------------------------------- the layer table
        var layer = node.Add("Layer").P(0);
        layer.Add("Version").P(100);
        Element(layer, "LayerElementNormal", 0);
        Element(layer, "LayerElementUV", 0);
        if (hasColours) Element(layer, "LayerElementColor", 0);
        Element(layer, "LayerElementMaterial", 0);

        // Extra UV sets live on their OWN layers, which is where a DCC looks for a second channel.
        for (var k = 0; k < extraUvSets; k++)
        {
            var more = node.Add("Layer").P(k + 1);
            more.Add("Version").P(100);
            Element(more, "LayerElementUV", k + 1);
        }

        return node;
    }

    private static void Element(Node layer, string type, int index)
    {
        var e = layer.Add("LayerElement");
        e.Add("Type").P(type);
        e.Add("TypedIndex").P(index);
    }

    private static Node Model(long id, string name) => ModelNode(id, name, [0.0, 0.0, 0.0], [0.0, 0.0, 0.0], [1.0, 1.0, 1.0]);

    /// <summary>
    /// A mesh Model with its local transform: Lcl Translation, Lcl Rotation (Euler XYZ, degrees)
    /// and Lcl Scaling. <see cref="Model"/> -- every model this class has always written -- is this
    /// with the identity; a placed copy of a shared Geometry is this with
    /// <see cref="FbxPlacement.Lcl"/>'s three arrays. Each array must have three elements.
    /// </summary>
    public static Node ModelNode(long id, string name, double[] t, double[] rDeg, double[] s)
    {
        if (t.Length != 3 || rDeg.Length != 3 || s.Length != 3)
            throw new ArgumentException("translation, rotation and scale each take exactly three numbers");

        var node = new Node("Model").P(id, Named(name, "Model"), "Mesh");
        node.Add("Version").P(232);
        var p = node.Add("Properties70");
        // DEFAULTATTRIBUTEINDEX IS NOT OPTIONAL.
        //
        // It names which of a node's connected attributes the node actually uses, and without it
        // the importer does everything else right and then hands you a node with no attribute:
        // the mesh object exists, the connection is read, and GetNodeAttribute() returns nothing.
        // Every count in the file looks correct. Found by grafting our payloads one at a time into
        // a file the SDK wrote -- our geometry was fine, our model was not.
        Prop(p, "DefaultAttributeIndex", "int", "Integer", 0);
        Prop(p, "Lcl Translation", "Lcl Translation", "", t[0], t[1], t[2]);
        Prop(p, "Lcl Rotation", "Lcl Rotation", "", rDeg[0], rDeg[1], rDeg[2]);
        Prop(p, "Lcl Scaling", "Lcl Scaling", "", s[0], s[1], s[2]);
        node.Add("Shading").P(true);
        node.Add("Culling").P("CullingOff");
        return node;
    }

    private static void Materials(SceneBuilder scene, CBaseMeshLod lod, long modelId)
    {
        var sections = lod.Sections?.Value ?? [];
        // A mesh with no sections still needs one slot, or the material layer indexes nothing.
        var names = sections.Length > 0
            ? sections.Select((s, i) => s.MaterialName ?? $"material_{i}").ToArray()
            : ["material_0"];

        foreach (var name in names)
        {
            var id = scene.Ids.Next();
            var m = new Node("Material").P(id, Named(name, "Material"), "");
            m.Add("Version").P(102);
            m.Add("ShadingModel").P("phong");
            m.Add("MultiLayer").P(0);
            var p = m.Add("Properties70");
            Prop(p, "DiffuseColor", "Color", "", 0.8, 0.8, 0.8);
            scene.Objects.Add(m);
            // Order matters: the material layer indexes these by the order they connect in.
            scene.Connect(id, modelId);
        }
    }

    // ------------------------------------------------------------------ the rig

    /// <summary>
    /// A bone's LOCAL rotation and translation as the file is to carry them. With
    /// <paramref name="rightHanded"/> that is P * M * P for P = diag(1, -1, 1) (see
    /// <see cref="Options.RightHanded"/>): the translation with Y negated, and the rotation CONJUGATED by
    /// the reflection. For a rotation that is a sign change of the quaternion, not a reflection of it
    /// (a reflection has no quaternion): turning by an angle about an axis, seen in a mirror in Y, is turning
    /// by the same angle about the mirrored axis with the sense reversed, and an axis is a pseudo-vector, so
    /// (x, y, z, w) becomes (-x, y, -z, w). Verified element by element, against P * R * P, by the private
    /// `--rhfbxcheck`.
    ///
    /// ONE definition, used by the bone nodes and by <see cref="ModelSpaceOf"/> (hence the skin clusters'
    /// TransformLink and the bind pose), so the three cannot be converted differently:
    /// P (A B) P = (P A P) (P B P), which is why a chain of conjugated locals composes to the conjugated model matrix.
    /// </summary>
    private static (FQuat Rotation, FVector Position) InBasis(CSkelMeshBone bone, bool rightHanded) =>
        rightHanded
            ? (new FQuat(-bone.Orientation.X, bone.Orientation.Y, -bone.Orientation.Z, bone.Orientation.W),
               new FVector(bone.Position.X, 0f - bone.Position.Y, bone.Position.Z))
            : (bone.Orientation, bone.Position);

    private static Node Limb(long id, CSkelMeshBone bone, bool isRoot, SceneBuilder scene, bool rightHanded)
    {
        var attributeId = scene.Ids.Next();
        var attribute = new Node("NodeAttribute")
            .P(attributeId, Named(bone.Name.Text, "NodeAttribute"), isRoot ? "Root" : "LimbNode");
        attribute.Add("TypeFlags").P("Skeleton");
        scene.Objects.Add(attribute);

        var node = new Node("Model").P(id, Named(bone.Name.Text, "Model"), "LimbNode");
        node.Add("Version").P(232);
        var p = node.Add("Properties70");

        Prop(p, "DefaultAttributeIndex", "int", "Integer", 0);

        var (rotation, position) = InBasis(bone, rightHanded);
        var (rx, ry, rz) = EulerXyz(rotation);
        Prop(p, "Lcl Translation", "Lcl Translation", "",
            (double) position.X, (double) position.Y, (double) position.Z);
        Prop(p, "Lcl Rotation", "Lcl Rotation", "", rx, ry, rz);
        Prop(p, "Lcl Scaling", "Lcl Scaling", "", 1.0, 1.0, 1.0);

        scene.Connect(attributeId, id);
        return node;
    }

    private static void Skin(SceneBuilder scene, long geometryId, long modelId,
                             IReadOnlyList<CSkelMeshBone> bones, long[] boneIds,
                             CSkelMeshLod lod, CVertexShare share, bool rightHanded)
    {
        var skinId = scene.Ids.Next();
        var skin = new Node("Deformer").P(skinId, Named("Skin", "Deformer"), "Skin");
        skin.Add("Version").P(100);
        skin.Add("Link_DeformAcuracy").P(50.0);
        scene.Objects.Add(skin);
        scene.Connect(skinId, geometryId);

        // Weights are per CONTROL POINT, and a point stands for one or more wedges that share a
        // position, a normal and a weight hash -- so any one of them answers for all of them.
        var perBone = new Dictionary<int, (List<int> Points, List<double> Weights)>();
        for (var point = 0; point < share.Points.Count; point++)
        {
            var wedge = share.VertToWedge.Value[point];
            if (wedge < 0 || wedge >= lod.Verts!.Length) continue;
            foreach (var influence in lod.Verts[wedge].Influences)
            {
                if (influence.Weight <= 0) continue;
                if (influence.Bone >= boneIds.Length) continue;
                if (!perBone.TryGetValue(influence.Bone, out var list))
                    perBone[influence.Bone] = list = (new List<int>(), new List<double>());
                list.Points.Add(point);
                list.Weights.Add(influence.Weight);
            }
        }

        var model = ModelSpaceOf(bones, rightHanded);
        foreach (var (bone, list) in perBone)
        {
            var clusterId = scene.Ids.Next();
            var cluster = new Node("Deformer")
                .P(clusterId, Named(bones[bone].Name.Text, "SubDeformer"), "Cluster");
            cluster.Add("Version").P(100);
            cluster.Add("UserData").P("", "");
            cluster.Add("Indexes").P(Arr(list.Points.ToArray()));
            cluster.Add("Weights").P(Arr(list.Weights.ToArray()));
            // A CLUSTER'S Transform IS STORED RELATIVE TO ITS TransformLink. That is the whole of the 3ds Max explosion.
            //
            // The SDK's API speaks in global matrices: TransformMatrix is where the MESH stood when the skin was bound,
            // TransformLinkMatrix where the BONE stood. The FILE does not: the SDK's reader hands the API
            // TransformMatrix = TransformLink * (the file's Transform), and its writer stores
            // TransformLink^-1 * TransformMatrix. (Checked against the SDK itself, 2020.3.9: a scene given TransformMatrix = the
            // mesh's global transform and TransformLinkMatrix = the bone's global transform, written by FbxExporter, stores
            // Transform = the inverse of TransformLink, with no associate model. Blender writes the same, and its file is why this
            // was found.) This writer used to put the API value in the file: the identity, the mesh sitting at the origin.
            // Read back, that is a mesh that stood AT THE BONE when it was bound. 3ds Max builds each bone's bind matrix from
            // the cluster, so every bind matrix was wrong by the bone's whole pose and the skin was carried by the full bone
            // matrix: measured in Max 2027, 162.6 cm on the worst vertex, 16,804 of 16,804 vertices moved. Blender never showed
            // it: it rebuilds the rest pose from TransformLink and the bind pose, and those were right.
            //
            // So: Transform = TransformLink^-1 * (the mesh node's world transform when bound: the identity here).
            cluster.Add("Transform").P(Arr(InverseRigid(model[bone])));
            // TransformLink is also what our own reader takes out of a file (FbxReader.ReadSkin) to decide whether an
            // import is offset, so it stays the bone's bind matrix in model space.
            cluster.Add("TransformLink").P(Arr(model[bone]));
            scene.Objects.Add(cluster);
            scene.Connect(clusterId, skinId);
            scene.Connect(boneIds[bone], clusterId);
        }
    }

    private static void BindPose(SceneBuilder scene, long modelId,
                                 IReadOnlyList<CSkelMeshBone> bones, long[] boneIds, bool rightHanded)
    {
        var model = ModelSpaceOf(bones, rightHanded);
        var pose = new Node("Pose").P(scene.Ids.Next(), Named("BindPose", "Pose"), "BindPose");
        pose.Add("Type").P("BindPose");
        pose.Add("Version").P(100);
        pose.Add("NbPoseNodes").P(bones.Count + 1);

        var meshNode = pose.Add("PoseNode");
        meshNode.Add("Node").P(modelId);
        meshNode.Add("Matrix").P(Arr(IdentityMatrix()));

        for (var i = 0; i < bones.Count; i++)
        {
            var n = pose.Add("PoseNode");
            n.Add("Node").P(boneIds[i]);
            n.Add("Matrix").P(Arr(model[i]));
        }
        scene.Objects.Add(pose);
    }

    // ------------------------------------------------------------------ shared maths

    private static double[] IdentityMatrix() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary>
    /// The inverse of a rigid matrix in the layout the FBX stores (row vectors, translation in elements 12..14): the
    /// rotation block transposed and the translation turned by it and negated. Every bone matrix this class writes is
    /// a rotation and a translation (scale 1), so the transpose is the inverse to the last bit of the Euler angles.
    /// If bone scale is ever written into that chain, this stops being the inverse: use a general one there.
    /// </summary>
    private static double[] InverseRigid(double[] m)
    {
        var r = new double[16];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            r[i * 4 + j] = m[j * 4 + i];
        for (var j = 0; j < 3; j++)
            r[12 + j] = -(m[12] * r[0 * 4 + j] + m[13] * r[1 * 4 + j] + m[14] * r[2 * 4 + j]);
        r[15] = 1;
        return r;
    }

    /// <summary>
    /// Each bone's bind matrix in model space, from the reference skeleton's locals. With
    /// <paramref name="rightHanded"/> each is P * M * P (see <see cref="InBasis"/>): the matrices the skin
    /// clusters' TransformLink and the bind pose carry in a right-handed file.
    /// </summary>
    public static double[][] ModelSpaceOf(IReadOnlyList<CSkelMeshBone> bones, bool rightHanded = false)
    {
        var model = new double[bones.Count][];
        for (var i = 0; i < bones.Count; i++)
        {
            var (rotation, position) = InBasis(bones[i], rightHanded);
            var local = Local(rotation, position);
            var p = bones[i].ParentIndex;
            model[i] = p >= 0 && p < i ? Compose(local, model[p]) : local;
        }
        return model;
    }

    public static double[] Local(FQuat rotation, FVector translation)
    {
        var (rx, ry, rz) = EulerXyz(rotation);
        var m = FromEulerXyz(rx, ry, rz);
        m[12] = translation.X;
        m[13] = translation.Y;
        m[14] = translation.Z;
        return m;
    }

    /// <summary>
    /// Euler XYZ in degrees back to a matrix, in the same layout <see cref="Local"/> writes.
    ///
    /// Deliberately built from the ANGLES rather than from the quaternion: this is what the check
    /// uses, and a check that skips the conversion it is checking proves nothing.
    /// </summary>
    public static double[] FromEulerXyz(double xDeg, double yDeg, double zDeg)
    {
        const double rad = Math.PI / 180.0;
        double cx = Math.Cos(xDeg * rad), sx = Math.Sin(xDeg * rad);
        double cy = Math.Cos(yDeg * rad), sy = Math.Sin(yDeg * rad);
        double cz = Math.Cos(zDeg * rad), sz = Math.Sin(zDeg * rad);

        // R = Rz * Ry * Rx, column-vector, then written row-major with translation last.
        double r00 = cz * cy, r01 = cz * sy * sx - sz * cx, r02 = cz * sy * cx + sz * sx;
        double r10 = sz * cy, r11 = sz * sy * sx + cz * cx, r12 = sz * sy * cx - cz * sx;
        double r20 = -sy, r21 = cy * sx, r22 = cy * cx;

        return [r00, r10, r20, 0, r01, r11, r21, 0, r02, r12, r22, 0, 0, 0, 0, 1];
    }

    public static double[] Compose(double[] a, double[] b)
    {
        var m = new double[16];
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
            m[r * 4 + c] = a[r * 4 + 0] * b[0 * 4 + c] + a[r * 4 + 1] * b[1 * 4 + c] +
                           a[r * 4 + 2] * b[2 * 4 + c] + a[r * 4 + 3] * b[3 * 4 + c];
        return m;
    }
}
