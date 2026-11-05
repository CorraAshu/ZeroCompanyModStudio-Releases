using System.Numerics;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Animations.PSA;
using CUE4Parse_Conversion.Meshes.PSK;

namespace UEToolkit.App.Rendering;

/// <summary>
/// Bone hierarchy plus reference pose, and the maths to pose it from a CUE4Parse
/// animation. Kept separate from the mesh so one skeleton can drive many animations.
/// </summary>
public sealed partial class Skeleton
{
    public const int MaxBones = 1024;

    public readonly string[] Names;
    public readonly int[] Parents;
    private readonly Matrix4x4[] _refLocal;
    private readonly Matrix4x4[] _inverseBind;
    // Reference channels, used to seed sampling: a track only writes the channels it has
    // keys for, so anything unkeyed must fall back to the bind pose.
    private readonly FQuat[] _refRot;
    private readonly FVector[] _refPos;

    public int Count => Names.Length;
    public int InvertFailures { get; private set; }

    /// <summary>
    /// Identifies the RIG, not the instance. Each mesh part of a character builds its own
    /// Skeleton object, so comparing by reference says every part has a different rig and
    /// leaves all but one stuck in its rest pose during playback. Bone names and hierarchy
    /// are what actually decide whether a pose applies.
    /// </summary>
    public string Signature => _signature ??= BuildSignature();
    private string? _signature;

    private string BuildSignature()
    {
        var hash = new HashCode();
        hash.Add(Count);
        for (var i = 0; i < Count; i++)
        {
            hash.Add(Names[i], StringComparer.OrdinalIgnoreCase);
            hash.Add(Parents[i]);
        }
        return hash.ToHashCode().ToString("X8");
    }

    /// <summary>
    /// Bone transforms in model space for the last pose. Needed to attach static parts
    /// (helmets) to a bone — the skinning palette is inverse-bind * model and cannot be
    /// used for that.
    /// </summary>
    public Matrix4x4[] LastModel { get; private set; } = [];

    public int IndexOf(string boneName)
    {
        for (var i = 0; i < Count; i++)
            if (string.Equals(Names[i], boneName, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Find a bone by a bare name, allowing for a rig's side suffixes. A rig can call the
    /// helmet mount "Helmet" plus a side tag, so an exact match on the region name "helmet" misses
    /// it and the part ends up on whatever the fallback list reaches first.
    /// </summary>
    public int IndexOfLoose(string bareName)
    {
        var exact = IndexOf(bareName);
        if (exact >= 0) return exact;

        foreach (var suffix in new[] { "_M", "_L", "_R" })
        {
            var i = IndexOf(bareName + suffix);
            if (i >= 0) return i;
        }
        for (var i = 0; i < Count; i++)
            if (Names[i].StartsWith(bareName, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
    public string LastPoseReport = "";
    public string LastMapReport = "";

    /// <summary>
    /// THE RIG ALONE, off a skeletal mesh, without converting a single vertex.
    ///
    /// <see cref="RenderMesh.FromSkeletal"/> gets its skeleton as a by-product of a full mesh
    /// conversion, which is the right trade when the geometry is wanted anyway. It is the wrong
    /// one when it is not: previewing an author's own FBX needs the TEMPLATE's bone names and
    /// bind pose so their weights have something to index, and converting the template's
    /// geometry to reach them would decompress every LOD of a mesh that is about to be thrown
    /// away.
    ///
    /// The fields are the same ones CUE4Parse's own converter copies into CSkelMeshBone, read
    /// from the same place, so a rig built here and one built there are identical.
    /// </summary>
    public static Skeleton? From(CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh mesh)
    {
        var info = mesh.ReferenceSkeleton?.FinalRefBoneInfo;
        var pose = mesh.ReferenceSkeleton?.FinalRefBonePose;
        if (info is null || pose is null || info.Length == 0) return null;

        var bones = new List<CSkelMeshBone>(info.Length);
        for (var i = 0; i < info.Length && i < pose.Length; i++)
            bones.Add(new CSkelMeshBone
            {
                Name = info[i].Name,
                ParentIndex = info[i].ParentIndex,
                Position = pose[i].Translation,
                Orientation = pose[i].Rotation,
            });
        return bones.Count == 0 ? null : new Skeleton(bones);
    }

    public Skeleton(IReadOnlyList<CSkelMeshBone> bones)
    {
        var n = Math.Min(bones.Count, MaxBones);
        Names = new string[n];
        Parents = new int[n];
        _refLocal = new Matrix4x4[n];
        _inverseBind = new Matrix4x4[n];
        _refRot = new FQuat[n];
        _refPos = new FVector[n];

        for (var i = 0; i < n; i++)
        {
            Names[i] = bones[i].Name.Text;
            Parents[i] = bones[i].ParentIndex;
            _refPos[i] = bones[i].Position;
            _refRot[i] = bones[i].Orientation;
            _refLocal[i] = Compose(bones[i].Position, bones[i].Orientation, FVector.OneVector);
        }

        // Bind pose in model space; inverse of it takes a vertex from model space into
        // bone space so the animated transform can put it back.
        var refModel = new Matrix4x4[n];
        for (var i = 0; i < n; i++)
        {
            var p = Parents[i];
            refModel[i] = p >= 0 && p < i ? _refLocal[i] * refModel[p] : _refLocal[i];
            if (!Matrix4x4.Invert(refModel[i], out _inverseBind[i]))
            {
                _inverseBind[i] = Matrix4x4.Identity;
                InvertFailures++;
            }
        }
    }

    /// <summary>
    /// UE quaternions map straight onto System.Numerics here because all skinning maths
    /// stays in UE space (the basis change happens in the model matrix). Conjugating them
    /// scatters the mesh — and the bind-pose self-test cannot catch it, because the
    /// conjugation cancels out against the inverse bind and only shows up under animation.
    /// </summary>
    public static bool ConjugateQuats = false;

    private static Matrix4x4 Compose(FVector pos, FQuat rot, FVector scale)
    {
        var q = ConjugateQuats
            ? new Quaternion(-rot.X, -rot.Y, -rot.Z, rot.W)
            : new Quaternion(rot.X, rot.Y, rot.Z, rot.W);
        return Matrix4x4.CreateScale(scale.X, scale.Y, scale.Z)
               * Matrix4x4.CreateFromQuaternion(q)
               * Matrix4x4.CreateTranslation(pos.X, pos.Y, pos.Z);
    }

    /// <summary>
    /// Self-test: running the reference pose through the same composition must yield an
    /// identity palette. If it does not, the hierarchy/bind maths is wrong, not the anim.
    /// </summary>
    public string SelfTest()
    {
        var model = new Matrix4x4[Count];
        float maxT = 0, maxDev = 0;
        for (var i = 0; i < Count; i++)
        {
            var parent = Parents[i];
            model[i] = parent >= 0 && parent < i ? _refLocal[i] * model[parent] : _refLocal[i];
            var pal = _inverseBind[i] * model[i];
            maxT = MathF.Max(maxT, pal.Translation.Length());
            maxDev = MathF.Max(maxDev, MathF.Abs(pal.M11 - 1) + MathF.Abs(pal.M22 - 1) + MathF.Abs(pal.M33 - 1));
        }
        return $"selftest maxT={maxT:F4} maxDiagDev={maxDev:F4} (both should be ~0)";
    }

    private Matrix4x4[]? _refModelCache;

    private Matrix4x4[] BuildRefModel()
    {
        var m = new Matrix4x4[Count];
        for (var i = 0; i < Count; i++)
        {
            var p = Parents[i];
            m[i] = p >= 0 && p < i ? _refLocal[i] * m[p] : _refLocal[i];
        }
        return m;
    }

    /// <summary>
    /// Skinning palette from an external pose source (see <see cref="UEToolkit.Core.IPoseSource"/>).
    ///
    /// <paramref name="map"/> sends each bone to the source's node of the same name, or -1. A
    /// source of this kind keeps OBJECT-space transforms, so there is no hierarchy to walk: each
    /// matched bone's model transform is its own bind transform composed with the displacement the
    /// source reports for its node. Bones with no matching node keep their bind pose by chaining
    /// from their parent, so partial rigs still animate.
    /// </summary>
    public Matrix4x4[] PoseFromDisplacements(int[] map, int nodeCount, Func<int, Matrix4x4> displacement)
    {
        _refModelCache ??= BuildRefModel();

        var palette = new Matrix4x4[Count];
        var model = new Matrix4x4[Count];
        for (var i = 0; i < Count; i++)
        {
            var j = i < map.Length ? map[i] : -1;
            if (j >= 0 && j < nodeCount)
            {
                // The displacement is the bone's motion away from its OWN bind pose, restated in
                // this skeleton's space. Composing it onto this skeleton's bind is equivalent to
                // taking the source's object transform outright when the two rigs agree, but also
                // tolerates a mesh whose bind differs slightly from the source's, where the direct
                // form would not.
                model[i] = _refModelCache[i] * displacement(j);
                palette[i] = _inverseBind[i] * model[i];
                continue;
            }
            var p = Parents[i];
            model[i] = p >= 0 && p < i ? _refLocal[i] * model[p] : _refModelCache[i];
            palette[i] = _inverseBind[i] * model[i];
        }
        LastModel = model;
        return palette;
    }

    /// <summary>Bind-pose model transform of a bone.</summary>
    public Matrix4x4 BindModel(int index) =>
        index < 0 || index >= Count ? Matrix4x4.Identity : RestModel(index);

    private Matrix4x4 RestModel(int index)
    {
        var m = _refLocal[index];
        for (var p = Parents[index]; p >= 0 && p < Count; p = Parents[p]) m *= _refLocal[p];
        return m;
    }

    /// <summary>
    /// Skinning palette that drives THIS mesh from another mesh's skeleton, bone for bone.
    ///
    /// Some games bind the parts of one character in DIFFERENT poses. A body and an undersuit
    /// worn beneath it can share all 86 bone names but 24 of them differ in bind — the root by
    /// 8.6 cm, the knees and elbows by 10 to 34 degrees. Rendering each part at its own bind
    /// therefore pulls them apart, and the seam lands at the neck, which is the "see-through" the
    /// undersuit's collar appeared to have.
    ///
    /// The game never does that: one skeleton poses every part, and each part divides by its
    /// OWN bind. So does 3ds Max, which is why importing the undersuit second fixes the body —
    /// the second import unifies the skeleton — and importing it first does not.
    ///
    /// Note this is why a matching Signature was not enough to catch it: names and hierarchy
    /// agree, only the bind transforms differ.
    /// </summary>
    public Matrix4x4[] PaletteOn(Skeleton host)
    {
        var m = new Matrix4x4[Count];
        var posed = host.LastModel.Length == host.Count;
        for (var i = 0; i < Count; i++)
        {
            var b = host.IndexOf(Names[i]);
            if (b < 0 || !Matrix4x4.Invert(BindModel(i), out var inv))
            {
                m[i] = Matrix4x4.Identity;
                continue;
            }
            m[i] = inv * (posed ? host.LastModel[b] : host.BindModel(b));
        }
        return m;
    }

    /// <summary>Skinning palette for the reference pose (identity everywhere).</summary>
    public Matrix4x4[] RestPalette()
    {
        // Model-space rest transforms, so attachments still resolve with no animation.
        var model = new Matrix4x4[Count];
        for (var i = 0; i < Count; i++)
        {
            var p = Parents[i];
            model[i] = p >= 0 && p < i ? _refLocal[i] * model[p] : _refLocal[i];
        }
        LastModel = model;

        var m = new Matrix4x4[Count];
        Array.Fill(m, Matrix4x4.Identity);
        return m;
    }

    /// <summary>
    /// Skinning palette for a sequence at the given frame. Tracks are indexed by the
    /// sequence's own bone order, which may not match the mesh's, so bones are matched by
    /// name and anything unmatched falls back to the reference pose.
    /// </summary>
    public Matrix4x4[] Pose(CAnimSequence seq, float frame, int[] trackForBone)
    {
        var local = new Matrix4x4[Count];
        for (var i = 0; i < Count; i++)
        {
            var t = trackForBone[i];
            if (t < 0 || t >= seq.Tracks.Count || !seq.Tracks[t].HasKeys())
            {
                local[i] = _refLocal[i];
                continue;
            }

            // Seed from the bind pose. Seeding with default(FQuat) would leave rotation-only
            // tracks with a zero quaternion and a zero position, collapsing the mesh.
            var q = _refRot[i];
            var p = _refPos[i];
            var s = FVector.OneVector;
            seq.Tracks[t].GetBoneTransform(frame, seq.NumFrames, ref q, ref p, ref s);
            if (s.X == 0 && s.Y == 0 && s.Z == 0) s = FVector.OneVector;
            local[i] = Compose(p, q, s);
        }

        var model = new Matrix4x4[Count];
        var palette = new Matrix4x4[Count];
        for (var i = 0; i < Count; i++)
        {
            var parent = Parents[i];
            model[i] = parent >= 0 && parent < i ? local[i] * model[parent] : local[i];
            palette[i] = _inverseBind[i] * model[i];
        }
        LastModel = model;

        float maxT = 0, maxRef = 0;
        for (var i = 0; i < Count; i++)
        {
            maxT = MathF.Max(maxT, palette[i].Translation.Length());
            maxRef = MathF.Max(maxRef, model[i].Translation.Length());
        }
        LastPoseReport = $"invertFail={InvertFailures} maxPaletteT={maxT:F1} maxModelT={maxRef:F1}";
        return palette;
    }

    /// <summary>Map each mesh bone to a track index in the sequence, by name.</summary>
    public int[] BuildTrackMap(CAnimSequence seq)
    {
        var map = new int[Count];
        Array.Fill(map, -1);

        // CUE4Parse orders tracks to match the skeleton's reference bone list, so index
        // alignment is the common case; names are the fallback when it is not.
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var refBones = seq.OriginalSequence.Skeleton?.Load<CUE4Parse.UE4.Assets.Exports.Animation.USkeleton>()
            ?.ReferenceSkeleton.FinalRefBoneInfo;
        if (refBones is not null)
            for (var i = 0; i < refBones.Length; i++)
                byName[refBones[i].Name.Text] = i;

        var identity = true;
        var byNameHits = 0;
        for (var i = 0; i < Count; i++)
        {
            if (byName.TryGetValue(Names[i], out var t)) { map[i] = t; byNameHits++; }
            // INDEX ALIGNMENT ONLY WHEN THERE IS NO NAME TABLE TO CONSULT.
            //
            // A bone the animation's skeleton genuinely does not have must stay at its BIND pose,
            // where it rides its parent and looks right. Handing it whatever track sits at its
            // index drives it with an unrelated bone's motion, and the further the two rigs differ
            // in size the more absurd the pairing: borrowing a 632-bone humanoid animation for a
            // 240-bone armour kit drove `offset_shoulder_pad_l` from `spine_05` and
            // `offset_shoulder_pad_r` from `ball_r` -- the ball of the right FOOT. The user saw
            // their shoulder pads hanging off the model and said so; --posecheck named the pairs.
            //
            // When refBones IS null the index is the only thing there is, and CUE4Parse orders
            // tracks to match the skeleton's bone list, so it remains the right guess there.
            else if (refBones is null && i < seq.Tracks.Count) map[i] = i;
            if (map[i] != i) identity = false;
        }

        var sample = string.Join(", ", Enumerable.Range(0, Math.Min(5, Count))
            .Select(i => $"{Names[i]}->{map[i]}"));
        var unmapped = map.Count(x => x < 0);
        LastMapReport = $"identityMap={identity} byName={byNameHits}/{Count} " +
                        $"refBones={(refBones?.Length.ToString() ?? "null")} tracks={seq.Tracks.Count}" +
                        (unmapped > 0 ? $" | {unmapped} bone(s) at bind" : "") + $" | {sample}";
        return map;
    }
}
