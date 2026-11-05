namespace UEToolkit.Core;

/// <summary>
/// TRANSLATE ANOTHER ENGINE'S BONE NAMES INTO THIS RIG'S.
///
/// An imported mesh's weights are matched to the template BY NAME and by nothing else, so a model
/// rigged anywhere but Unreal loses every weight it has and the whole thing hangs off bone zero as
/// one rigid piece. The weights are not bad and the geometry is not wrong -- the names simply do
/// not line up, and the usual answer is to throw the original weighting away and re-solve it with
/// Skin Wrap or a projection, which replaces an artist's work with a guess.
///
/// MEASURED, on a Battlefront II character scaled and retargeted onto this game's rig:
///
///     Scout body   30,820 vertices, 96 skin clusters
///     Zero Company 240 bones
///
/// Three quarters of that weighting is recoverable by renaming alone -- exactly, with nothing
/// interpolated. That is what this does.
///
/// The names are not Battlefront's invention. `Hips` / `Spine1` / `LeftForeArm` /
/// `LeftHandIndex1` is the Character Studio and Mixamo convention, which Frostbite follows and so
/// does most of what people drag in from elsewhere; Mixamo's `mixamorig:` prefix is stripped
/// rather than duplicated through every entry.
///
/// THREE TIERS, because they deserve different amounts of trust:
///
///   Alias   a bone-for-bone rename. `LeftForeArm` IS `lowerarm_l`. Exact.
///   Fold    a helper this rig has no equivalent for, put on the joint it supports.
///           `LeftElbowRoll` onto `lowerarm_l` -- the weight is kept, the corrective is not.
///   Merge   a bone with no known meaning at all, put on the nearest bone that did map.
///           `PROC_Bone8` is per-character, so only its position can speak for it.
///
/// TWO THINGS THIS DELIBERATELY DOES NOT DO. It never overrides a name that already matches the
/// template, so a rig that imports correctly today imports identically. And it only maps onto a
/// bone the template's SECTION actually holds, so a torso's bone map cannot acquire a finger --
/// when the aliased target is missing, the entry falls through to the merge rather than vanishing.
/// </summary>
public static class RigAliases
{
    /// <summary>How a cluster found its home, in the order the tiers are tried.</summary>
    public enum How
    {
        /// <summary>The name already matched a template bone. Untouched behaviour.</summary>
        Direct,
        /// <summary>A known humanoid bone this rig spells differently. Exact.</summary>
        Alias,
        /// <summary>A known helper bone, put on the joint it supports.</summary>
        Folded,
        /// <summary>No known meaning; put on the nearest bone that did map.</summary>
        Merged,
        /// <summary>Nowhere to put it. The weights are dropped, as they were before.</summary>
        None,
    }

    /// <param name="Slot">Index into the template's bone map, or -1 for <see cref="How.None"/>.</param>
    /// <param name="Into">For a merge, the cluster whose bone it was folded into.</param>
    /// <param name="Distance">
    /// For a merge, how far that cluster was, in the file's own units. Small is good, and large is
    /// the thing to look at: a bone folded halfway across the body is a name these tables should
    /// have known about.
    /// </param>
    public sealed record Mapped(string Cluster, int Slot, How How, string? Into = null,
                                float Distance = 0f);

    private static readonly Dictionary<string, string> Renames = BuildRenames();
    private static readonly Dictionary<string, string> Helpers = BuildHelpers();

    /// <summary>
    /// The bone this rig would call the same thing, or null for a name neither table knows.
    /// Case-insensitive, because `Head` and `head` are the same bone and an exporter's choice of
    /// capital is not information.
    /// </summary>
    public static string? For(string bone, out bool isHelper)
    {
        isHelper = false;
        if (string.IsNullOrEmpty(bone)) return null;

        // `mixamorig:Hips`, and the `mixamorig1:` a second import produces. The namespace says
        // which armature a bone came from, which the name mapping does not care about.
        var name = bone;
        var colon = name.LastIndexOf(':');
        if (colon >= 0 && colon + 1 < name.Length) name = name[(colon + 1)..];

        if (Renames.TryGetValue(name, out var exact)) return exact;
        if (Helpers.TryGetValue(name, out var onto)) { isHelper = true; return onto; }

        // A PHYSICS BONE NAMES THE JOINT IT HANGS OFF, so strip the suffix and fold onto that.
        //
        // A rule rather than more literals, because the family is open-ended: one character brought
        // `LeftArm_Phys_01`, the next brought `Spine2_Phys_Ext_01` and `Spine2_Phys_Ext_02`, and
        // there is no reason to think that is the end of it.
        //
        // IT MATTERS MORE THAN ITS NAME SUGGESTS. On a jump trooper, `Spine2_Phys_Ext_02` drives
        // 5,000 of the mesh's 14,103 vertices -- a third of the torso, not a helper. And the
        // positional fallback cannot rescue it: measured, those two sit at y 1.508 and 1.515 while
        // `Spine2` is at 1.257, so they stand 25 cm above the joint they belong to, up at shoulder
        // height on the centre line. The nearest bone that resolved was `LeftArm_Phys_01` at 0.198
        // against Spine2's 0.258, which would have put a third of the torso on `upperarm_l`. The
        // side rule is no defence either -- the name states no side.
        //
        // The NAME knows what position cannot: it says Spine2, so it goes on Spine2.
        var phys = name.IndexOf("_Phys", StringComparison.OrdinalIgnoreCase);
        if (phys > 0 && Renames.TryGetValue(name[..phys], out var physBase))
        { isHelper = true; return physBase; }

        return null;
    }

    /// <summary>
    /// Decide where every cluster's weights go, before any of them are read.
    ///
    /// Done as a whole rather than per cluster because the last tier needs to know which clusters
    /// resolved: a bone with no counterpart is put on the NEAREST one that found a home, and
    /// "nearest" is not answerable until the tiers above have run over the lot.
    ///
    /// Distances are between bind poses, which the file states outright for every cluster -- no
    /// chain walking, no Euler order, no assumption about which way the rig stands. A cluster the
    /// exporter wrote no bind pose for cannot be placed, and is left alone rather than dropped onto
    /// whatever happens to be first.
    /// </summary>
    public static List<Mapped> Plan(IReadOnlyList<FbxReader.Influence> clusters,
                                    IReadOnlyList<string> templateBones)
    {
        var plan = new List<Mapped>(clusters.Count);
        var bones = templateBones.ToList();

        int Slot(string name) =>
            bones.FindIndex(b => b.Equals(name, StringComparison.OrdinalIgnoreCase));

        // ---------------------------------------------------------------- the named tiers
        foreach (var cluster in clusters)
        {
            var direct = Slot(cluster.Bone);
            if (direct >= 0) { plan.Add(new Mapped(cluster.Bone, direct, How.Direct)); continue; }

            // A TARGET THE SECTION DOES NOT CARRY IS NOT A MATCH. An arms mesh's bone map holds no
            // `upperarm_l`, so `LeftArm` has to fall through to the merge and land on the forearm
            // rather than being dropped for naming a bone that exists on the skeleton but not here.
            if (For(cluster.Bone, out var helper) is { } to && Slot(to) is var at && at >= 0)
            {
                plan.Add(new Mapped(cluster.Bone, at, helper ? How.Folded : How.Alias));
                continue;
            }

            plan.Add(new Mapped(cluster.Bone, -1, How.None));
        }

        // ---------------------------------------------------------------- the positional tier
        // Everything that found a home is a candidate to be merged into; how it got there does not
        // matter, only that the template can address it.
        var anchors = new List<(int Index, double X, double Y, double Z)>();
        for (var i = 0; i < clusters.Count; i++)
            if (plan[i].Slot >= 0 && clusters[i].LinkTranslation is { } at)
                anchors.Add((i, at.X, at.Y, at.Z));

        if (anchors.Count == 0) return plan;

        for (var i = 0; i < clusters.Count; i++)
        {
            if (plan[i].Slot >= 0) continue;
            if (clusters[i].LinkTranslation is not { } here) continue;

            // NEAREST MUST NOT CROSS THE BODY. Measured on the Scout's boots: the RIGHT knee is
            // 27 cm from the left knee and the left FOOT is 42 cm from it, so straight-line
            // nearest sent 2,114 vertices of the left boot onto `calf_r`. A bone whose name says
            // which side it is on may only land on a bone that says the same, or on one that says
            // nothing -- a spine or a pelvis is a fair home for either side.
            var mine = SideOf(clusters[i].Bone);

            var best = -1;
            var bestSq = double.MaxValue;
            for (var pass = 0; pass < 2 && best < 0; pass++)
                foreach (var (index, x, y, z) in anchors)
                {
                    // Second pass drops the side rule, because a cross-side home beats no home.
                    if (pass == 0 && mine != '\0')
                    {
                        var theirs = SideOf(bones[plan[index].Slot]);
                        if (theirs != '\0' && theirs != mine) continue;
                    }
                    var dx = x - here.X; var dy = y - here.Y; var dz = z - here.Z;
                    var sq = dx * dx + dy * dy + dz * dz;
                    if (sq < bestSq) { bestSq = sq; best = index; }
                }
            if (best < 0) continue;

            plan[i] = new Mapped(clusters[i].Bone, plan[best].Slot, How.Merged,
                                 clusters[best].Bone, (float) Math.Sqrt(bestSq));
        }

        return plan;
    }

    /// <summary>
    /// 'l', 'r', or NUL for a name that does not say. Reads both conventions -- a `Left` anywhere
    /// in the source name, and the `_l` this rig suffixes -- so one helper serves both ends of the
    /// comparison.
    /// </summary>
    private static char SideOf(string bone)
    {
        if (string.IsNullOrEmpty(bone)) return '\0';
        if (bone.EndsWith("_l", StringComparison.OrdinalIgnoreCase)) return 'l';
        if (bone.EndsWith("_r", StringComparison.OrdinalIgnoreCase)) return 'r';
        if (bone.Contains("Left", StringComparison.OrdinalIgnoreCase)) return 'l';
        if (bone.Contains("Right", StringComparison.OrdinalIgnoreCase)) return 'r';
        return '\0';
    }

    /// <summary>Every rename this knows, for a test to assert against and a person to read.</summary>
    public static IReadOnlyDictionary<string, string> KnownRenames => Renames;

    /// <summary>Every helper fold this knows.</summary>
    public static IReadOnlyDictionary<string, string> KnownHelpers => Helpers;

    private static Dictionary<string, string> BuildRenames()
    {
        // THE SPINE IS THE ONE PLACE THE TWO CONVENTIONS DISAGREE ABOUT COUNT rather than
        // spelling: three joints there, five here. Taken from where a retarget actually puts the
        // bones and not from taste -- measured on the Scout, whose retargeted bind poses land on
        // this rig's joints to three decimals:
        //
        //     Hips   95.930  ->  pelvis    95.93
        //     Spine1 113.979 ->  spine_02  113.98
        //     Spine2 123.905 ->  spine_03  123.90
        //     Neck   148.671 ->  neck_01   148.64
        //     Neck1  154.713 ->  neck_02   154.69
        //
        // So `Spine2` is `spine_03`, not the `spine_04` this rig hangs its clavicles from. Sending
        // it to the anatomically nicer bone would put the weights where the geometry is not.
        var t = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hips"] = "pelvis",
            ["Spine"] = "spine_01",
            ["Spine1"] = "spine_02",
            ["Spine2"] = "spine_03",
            ["Spine3"] = "spine_04",
            ["Neck"] = "neck_01",
            ["Neck1"] = "neck_02",
            ["Head"] = "head",
        };

        foreach (var (side, s) in new[] { ("Left", "l"), ("Right", "r") })
        {
            t[$"{side}Shoulder"] = $"clavicle_{s}";
            t[$"{side}Arm"] = $"upperarm_{s}";
            t[$"{side}ForeArm"] = $"lowerarm_{s}";
            t[$"{side}Hand"] = $"hand_{s}";
            t[$"{side}UpLeg"] = $"thigh_{s}";
            t[$"{side}Leg"] = $"calf_{s}";
            t[$"{side}Foot"] = $"foot_{s}";
            t[$"{side}ToeBase"] = $"ball_{s}";

            // TWIST BONES EXIST ON BOTH RIGS, which is worth saying because this project assumed
            // otherwise until it read the skeleton. They are NOT numbered the same way, and the
            // numbering is inverted between the two limbs of THIS rig, so each one is measured
            // rather than reasoned about. Distances are from the joint the chain hangs off:
            //
            //     Scout  elbow -> Roll 8.35   Roll1 16.7   Roll2 25.6   (wrist 25.1)
            //     here   elbow -> twist_02 8.04   twist_01 16.13        (hand 24.22)
            //
            //     Scout  shoulder -> ArmRoll 10.3
            //     here   shoulder -> twist_01 8.65   twist_02 17.35
            //
            // So the FOREARM counts inward and the UPPER ARM counts outward, and mapping `Roll` to
            // `twist_01` on both -- which is what reading the names alone suggests -- puts the
            // forearm twist at the wrong end of the bone.
            t[$"{side}ArmRoll"] = $"upperarm_twist_01_{s}";
            t[$"{side}ForeArmRoll"] = $"lowerarm_twist_02_{s}";
            t[$"{side}ForeArmRoll1"] = $"lowerarm_twist_01_{s}";
            t[$"{side}UpLegRoll"] = $"thigh_twist_01_{s}";

            // The thumb is rigged from the metacarpal on both conventions, so Thumb1 is the base
            // joint and the numbering runs straight across.
            t[$"{side}HandThumb1"] = $"thumb_01_{s}";
            t[$"{side}HandThumb2"] = $"thumb_02_{s}";
            t[$"{side}HandThumb3"] = $"thumb_03_{s}";

            // THE FOUR FINGERS ARE FOUR JOINTS ON BOTH RIGS, not four against three. This one
            // carries `index_metacarpal_l` and its three siblings -- measured, after assuming for
            // a while that Unreal leaves the metacarpals out and that `Index0` had nowhere to go.
            // It has somewhere to go.
            foreach (var (their, ours) in new[] { ("Index", "index"), ("Middle", "middle"),
                                                  ("Ring", "ring"), ("Pinky", "pinky"),
                                                  ("Little", "pinky") })
            {
                t[$"{side}Hand{their}0"] = $"{ours}_metacarpal_{s}";
                t[$"{side}Hand{their}1"] = $"{ours}_01_{s}";
                t[$"{side}Hand{their}2"] = $"{ours}_02_{s}";
                t[$"{side}Hand{their}3"] = $"{ours}_03_{s}";
            }
        }

        return t;
    }

    private static Dictionary<string, string> BuildHelpers()
    {
        // CORRECTIVE AND PHYSICS BONES, which this rig has its own set of and which no rename can
        // line up: they are authored per studio, they sit at a joint rather than along a limb, and
        // their whole job is to move a little differently from the joint they hang off. What is
        // worth keeping is the WEIGHT, not the correction -- so each one goes onto the joint it
        // supports and the mesh stays attached to the right part of the body.
        //
        // Every position below was read out of the Scout's own bind poses, not assumed:
        //
        //     LeftArmpit    is AT LeftArm            LeftHipsRoll is AT LeftUpLeg
        //     LeftKneeRoll  is AT LeftLeg            LeftKneeLow  is 2.9 below it
        //     HeadRoll      is 3.3 below Head        ForeArmRoll2 is 0.5 from LeftHand
        //
        // These are Frostbite's names. A rig from somewhere else brings its own, and anything this
        // does not know still lands on the nearest bone that mapped -- worse, but never dropped.
        var t = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HeadRoll"] = "head",
        };

        foreach (var (side, s) in new[] { ("Left", "l"), ("Right", "r") })
        {
            // A third forearm twist, sitting 0.5 from the wrist. This rig has two, so the one
            // nearest the hand becomes the hand.
            t[$"{side}ForeArmRoll2"] = $"hand_{s}";

            // A FIFTH JOINT ON A FINGER IS A TIP, and this rig stops at the third. The thumb was
            // the known case; a jump trooper brought `LeftHandIndex4` and `RightHandIndex4` as
            // well, so it is the whole hand rather than one digit.
            t[$"{side}HandThumb4"] = $"thumb_03_{s}";
            foreach (var (their, ours) in new[] { ("Index", "index"), ("Middle", "middle"),
                                                  ("Ring", "ring"), ("Pinky", "pinky"),
                                                  ("Little", "pinky") })
                t[$"{side}Hand{their}4"] = $"{ours}_03_{s}";

            t[$"{side}Armpit"] = $"upperarm_{s}";
            t[$"{side}DeltoidBulge"] = $"upperarm_{s}";
            t[$"{side}ArmBend"] = $"lowerarm_{s}";
            t[$"{side}ElbowRoll"] = $"lowerarm_{s}";
            // `{side}Arm_Phys_01` is not listed: the _Phys rule in For() derives it from `LeftArm`
            // and reaches the same `upperarm_l`, so a literal here would be a second copy of one
            // answer and somewhere for the two to disagree later.

            t[$"{side}HipsRoll"] = $"thigh_{s}";
            t[$"{side}KneeRoll"] = $"calf_{s}";

            // ABOVE THE KNEE AND BELOW IT ARE DIFFERENT BONES. Measured on a jump trooper against
            // its own knee: `LeftLeg` 0.506, `KneeUp` 0.564 (58 above), and on the Scout `KneeLow`
            // 0.477 (29 below). So one is thigh and the other calf, and reading them as a pair is
            // what makes the split obvious -- taken alone, either name would have gone to the joint.
            t[$"{side}KneeUp"] = $"thigh_{s}";
            t[$"{side}KneeLow"] = $"calf_{s}";
        }

        return t;
    }
}
