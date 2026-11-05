# CUE4Parse patches

Patches that must be applied to the CUE4Parse checkout for correct output. They live here
because CUE4Parse is an external dependency outside this repo — **a fresh clone or a
`git checkout` there silently discards them**, and the resulting failure looks like an art
bug rather than a missing patch.

Apply with:

```
git -C <path-to>/CUE4Parse apply <path-to>/UEToolkit/patches/<file>.patch
```

**Two of these touch `MeshConverter.cs`** — `mesh-lod-sections` and
`cue4parse-batch-gc`. Apply mesh-lod-sections first; the GC patch's hunks sit above its line range and
apply cleanly on top. Regenerating either with a bare `git diff` on that file will capture the
other one's hunks as well, which then conflict on the next apply.

---

## `cue4parse-gltf-bone-rotation.patch`

`Gltf.SwapYZ(FQuat)` swapped a quaternion's Y and Z **without negating the vector part**:

```csharp
public static FQuat SwapYZ(FQuat quat) => new (quat.X, quat.Z, quat.Y, quat.W);   // wrong
```

Swapping two axes is a **mirror** — determinant −1 — so conjugating a rotation by it gives a
rotation about the reflected axis by the **opposite angle**. The quaternion has to be permuted
*and* negated. Permuting alone leaves the angle unflipped, so every bone turns the wrong way about
the swapped axis.

**Why it went unnoticed through two releases:** the MESH was always right. Vertices, normals and
tangents go through the `FVector`/`Vector4` overloads, which are plain permutations and correct.
There is exactly **one call site** for the quaternion overload — a bone's local rotation — so the
model imported perfectly and only the armature was wrong. Nobody expands a 240-bone armature to
check it.

What people actually saw, three of them reporting it on the same afternoon: *"the armature is
twisted"*, *"all imports will bring the skeleton in upside down and twisted in on itself"*. The
spine, neck and head ran **downward** out of the hips; the feet finished above the shoulders.
Measured on the clone helmet, before and after:

| bone | broken | fixed |
|---|---|---|
| pelvis | 0.959 | 0.959 |
| spine_01 | 0.866 | 1.053 |
| head | **0.313** | **1.605** |
| foot_l | **1.822** | **0.096** |

**Skinning was never affected** — zero vertex drift between rest and posed, before and after — so
this moves the skeleton to where it belongs without moving the model.

**GUARDED: `--rigcheck`.** It exports a skeleton the way the Blender add-on does, walks the glTF's
own node hierarchy by hand, and asserts what anatomy demands. It fails loudly and names this patch
if the checkout is ever re-cloned. Proven in both directions: reverted the fix, watched it fail,
restored it, watched it pass.

Worth sending upstream.

---

## `cue4parse-batch-gc.patch`

`MeshConverter` calls `GC.Collect()` at the end of **every Nanite conversion** — a full blocking
collection, per mesh. The upstream comment says why: *"aggressively garbage collect since the asset
is re-parsed every time by FModel"*. That is correct for FModel, which converts one asset per click
and wants the memory back at once.

For a batch it is ruinous, and it degrades as the run proceeds because a gen2 collection costs
whatever the whole live heap costs. Measured across a 12,408-mesh scan, forced vs gated:

| at mesh | gen2 collections (forced) | gen2 (gated) | chunk time (forced) | chunk (gated) |
|---|---|---|---|---|
| 256 | 170 | **12** | 17.2 s | **7.4 s** |
| 1,024 | 650 | **138** | 55.0 s | **41.4 s** |
| 2,048 | 1,649 | **196** | 17.0 s | **2.2 s** |
| 2,304 | 1,904 | **350** | elapsed 4:21 | elapsed **2:32** |

That is roughly **one full gen2 collection per mesh**, on a heap that never grew past ~300 MB — so
it was never memory pressure, only forced collection. Before this the whole-game scan never
finished; a run stopped advancing for over an hour and was killed twice.

The patch makes it opt-out via `MeshConverter.AggressiveGarbageCollection`, **defaulting to true** so
FModel's behaviour is unchanged. `AtlasLibrary.Build` turns it off around the scan and restores it in
a `finally`.

**It does not leak.** The Nanite page references are dropped either way — that line is untouched.
Only the *timing* of the collection changes, and the GC chooses it better than a fixed call site can.

---

## `mesh-lod-sections.patch`

Three defects in `MeshConverter.TryConvert` for skeletal meshes, all of which throw
`IndexOutOfRangeException` and surface as *"Index was outside the bounds of the array"* — a
message that reads like a corrupt asset and is not. Found on one game, where they cost
**4 of one character's 13 meshes, including both full-body meshes**.

**1. `LODModels` and `LODInfo` are separate arrays and need not agree in length.** The converter
walks `LODModels` and indexes `LODInfo` with the same counter. That game's full-body character mesh ships **7 LOD
models and 4 LODInfo entries**. `ScreenSize` is LOD-selection metadata nothing in the export path
consumes, so a missing entry is worth a default rather than losing the mesh.

**2. Sections are not guaranteed to be ordered by `BaseVertexIndex`.** The vertex walk advances to
the next section as the index passes each section's end, which is only correct while they are
sorted. Its LOD6 has base vertices **0, 1171, 657, 0**; vertices 657–1370 then take
the wrong section's `BoneMap` — 6 entries where 13 were needed — and die on `boneMap[boneIndex]`.
Now resolved from the sections' own ranges, and only when they are actually out of order, so an
ordered mesh takes the identical path it always did.

**3. A LOD whose influences do not fit its bone map is skipped, not fatal.** After the two fixes
above, LOD4 of that mesh still asks for bone index 10 of an 8-entry map with its sections correctly
ordered and fully covering the buffer — so the data itself is inconsistent, not the walk over it.
That mesh declares 4 LODInfo entries and 7 LOD models, and the trailing three are a second chain
that was never meant to be read. Converting LOD0–3 and warning about the rest gives a usable mesh;
throwing gives nothing, and every export path here defaults to LOD0 anyway.

Verified safe on the other games: `--regress` reports every other game checked
byte-identical across the change.

---

## `acl-legacy-1x.patch`

Decompression for **pre-2.0 ACL** animation, so UE4 titles that shipped against the older
Animation Compression Library plugin can be exported at all.

**The problem.** ACL 2.0 renamed its container from `CompressedClip` to `compressed_tracks`
and changed the buffer tag from `0xAC10AC10` to `0xAC11AC11`. The bundled library is 2.1 and
rejects the older tag outright — its own `buffer_tag.h` calls `compressed_clip` "deprecated,
no longer used". Some games store **every bone track** in the old format while
their curve data is already 2.x, so a single animation needs both decoders and all 17,789 of one
game's AnimSequences failed with `Invalid tag`.

The failure is easy to misread. `Invalid tag` is thrown by four unrelated parsers in this tree,
and it surfaced through `ConvertAdditive` — so it looked like a problem with the additive base
pose rather than with the codec. Confirming it took dumping the buffer header: the stored size
matched the buffer length exactly, which rules out a misread slice and leaves the decoder.

**What the patch adds.**

- `CUE4Parse-Natives/ACL/LegacyACL.cpp` — `nLegacyACL_IsValid`, `nLegacyACL_GetNumSamples`,
  `nLegacyACL_GetNumBones`, `nLegacyACL_GetSampleRate`, `nReadLegacyACLData`.
  `nReadLegacyACLData` deliberately takes the same arguments as `nReadACLData` even though the
  reference pose and track map are unused — 1.x has no bind-pose stripping — so the managed
  dispatch is one branch rather than two parallel code paths.
- `CUE4Parse/ACL/LegacyCompressedClip.cs` — the managed wrapper, mirroring `CompressedTracks`.
- `FACLCompressedAnimData.IsLegacy` / `GetLegacyClip()`, dispatching on the tag. Both formats
  arrive through the same codec class and the same field; only the tag separates them.
- `AnimConverter.ConvertSequence` — the legacy branch. Everything downstream is shared.

**The build needs a second ACL.** The 1.3 sources are not in the patch (thousands of files);
extract them from the ACL submodule, which already has the tag:

```
cd CUE4Parse-Natives/ACL/external
mkdir acl1 && (cd acl && git archive v1.3.5) | tar -x -C acl1
(cd acl/external/rtm && git archive f4ec074cc4e5f07c26f71cf7cdf3c5644ec0da46) | tar -x -C acl1/external/rtm
```

CMake then picks it up automatically (`if (EXISTS .../acl1/includes)`).

**Two things about that build are load-bearing.** The legacy code is a separate static target,
not another file in the main one, because both libraries answer to `<acl/core/...>` and cannot
share an include path — and its include list is `set_property(... INCLUDE_DIRECTORIES ...)`
rather than `target_include_directories`, because a target inherits the directory's
`include_directories()` at creation **and those come first**. Get that wrong and it still
compiles: the 2.x headers are simply used as if they were 1.3. Second, the target is linked
with `/WHOLEARCHIVE` — nothing in C++ references these symbols, so the linker would otherwise
drop the object and produce a DLL that builds clean and exports nothing.

The two libraries coexist in one binary only because ACL 2.x puts everything in an inline
version namespace (`acl::v2_1_0::`), leaving 1.3's plain `acl::` symbols unambiguous.

**Verified**, not assumed: `--psacheck` re-reads the written `.psa` and checks the keys. A
hair-attachment animation decodes to 544 of 633 bones moving across 49 frames with unit
quaternions and no NaNs. Another game's ACL **2.x** animations were re-exported after the change and
are unaffected. A decompressor that returns nothing still writes a structurally perfect PSA, so
"it exported" was never the test.

**This patch also carries an unrelated additive fix**, found while checking whether the static
additives above were the legacy decoder's fault (they were not — six-frame directional lean
poses genuinely hold, and `ADD_NAV_D/F/L/R` differ from one another, which proves the delta
was being applied). Sweeping another game for a control turned up a real bug in the same function:

```
referencePoses[refPoseType switch { ABPT_AnimScaled => frameIndex % maxRefPosFrame,
                                    _ => refFrameIndex }]     // -> 0
```

`ABPT_AnimScaled` is the only base-pose type that yields a pose **per frame**. Every other
branch returns `new FCompactPose[1]` with `refFrameIndex` already baked in by the loader, so
indexing it by `refFrameIndex` a second time is wrong — and throws the moment that index is not
zero. One game's first-person ammunition animation (`ABPT_RefPose`, `refFrameIndex 1`) died with
`IndexOutOfRangeException` on every export; it now writes 101 frames with all 7 bones moving.
This is a CUE4Parse bug affecting any game, and is worth upstreaming.

---

## `material-param-names.patch`

Adds one game's texture-parameter names to `CMaterialParams2`. Written against upstream's commit
`cc79bf64`, the commit the clean build checks out.

It also carries the virtual-texture fix for tiles with no stored data (`TextureDecoder.cs`,
`FVirtualTextureBuiltData.cs`): a missing tile used to be filled with the chunk's first bytes, a block of
repeating garbage that throws nothing. `cue4parse-vt-missing-tiles.patch` holds the same three hunks on
their own, so it is NOT applied on top of this one (applying both fails at the second copy).

**The problem.** `CMaterialParams2` matches material parameters against a hardcoded list of
~437 names. That game uses `Diffuse Map` and `ORME Control Map`, neither of which was in it
(`Normal Map` was).

**Why it wasn't obvious.** `UMaterial.GetParams` has a regex fallback that scans the base
material's *referenced textures* by texture name and takes the first match. In preview that
made diffuse resolve by luck — but bound the **wrong** specular map: a character got a shared
tiling detail texture instead of its own specular map. Models therefore looked *wrongly*
textured rather than untextured.

On **export** it is worse. The default `EMaterialFormat.AllLayersNoRef` skips both
`ReferencedTextures` and the fallback loop entirely, so named binding is the only mechanism
that can work — and diffuse bound nothing at all.

**Names added:**

| Slot | Names |
|---|---|
| `Diffuse[0]` | `Worm Color`, `COLOR`, `Diffuse Map`, `Diffuse Texture`, `Diffuse Flipbook Texture`, `Diffuse Alpha Texture`, `Mesh - CO` |
| `Diffuse[1]` | `Diffuse Texture Layer 2` |
| `Normals[0]` | `Background Normal Map`, `Worm Normal/Roughness` |
| `SpecularMasks[0]` | `ORME Control Map`, `ORHG Map`, `Background ORME Map` |
| `Emissive[0]` | `Emissive Texture`, `Emissive Flipbook Texture` |

**Measured effect** across 1,268 material instances and 299 character/weapon/vehicle meshes:

| | before | after |
|---|---|---|
| Diffuse bound | 20.1% | **50.5%** |
| Specular bound | 14.7% | **22.0%** |
| Mesh slots with diffuse **and** normal | — | **85.2%** |

**Deliberately not mapped:** detail maps (`1./2./3. Detail Map Normal|ORME`,
`Detail Maps Mask`), alpha/opacity masks, and FX parameters (flipbook motion-vector,
`UV Distortion`, `Noise_*`, lens flare). These are not base PBR slots and forcing them into
diffuse/normal/specular would produce wrong output.

The residual ~12% of mesh slots with no diffuse are genuinely texture-less effect shaders —
an active-camouflage refraction material, and the hair materials (hair shading model, uses
`Tangents Map`/`Attributes Map`). Not a defect.

This patch is small and general enough to be worth upstreaming to
[FabianFG/CUE4Parse](https://github.com/FabianFG/CUE4Parse).

## `acl-codec-from-handle.patch`

**Without it, every one of one game's 2,804 animations fails.**

`UAnimSequence.SerializeCompressedData3` resolves the bone codec by loading the
`BoneCompressionSettings` asset and asking it for `BoneCodecDDCHandle`. That game cooks its
AnimSequences with only `NumFrames` and `TrackToSkeletonMapTable` as properties — there is no
`BoneCompressionSettings` to load — so the lookup returns null, `CompressedDataStructure` is never
allocated, and the failure surfaces much later in `AnimConverter` as
`Unsupported compressed data type ` **with nothing after the space**, because the message appends
the type name of a null.

The handle survives and names the codec: `AnimBoneCompressionCodec_ACL_0`. The patch trusts it and
allocates `UAnimBoneCompressionCodec_ACLSafe`, which is precisely what the pre-4.22 path in the same
file already does when a `KeyEncodingFormat` begins with "ACL".

Verified: one animation decodes as ACL 2.x to 18,483 keys — exactly 303 bones x 61
frames — with 2,908 distinct rotations, ~1 m of positional travel and zero non-unit quaternions.

## `actorx-fixed-width-name-clamp.patch`

**Any asset whose name is longer than a PSK/PSA fixed-width field was silently lost.**

`ActorXUtils.Write(string, int)` copied the SOURCE length into a fixed buffer:

    var padded = new byte[len];                            // 64
    var bytes  = Encoding.UTF8.GetBytes(value);            // 70
    Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);   // throws

The throw propagates out of `AnimInfoBinary.Serialize` and the whole export of that asset
fails. Five fields are affected -- `AnimInfoBinary.Name`/`.Group`, `FNamedBoneBinary.Name`,
`VBone.Name` (so **PSK and PSA bone names too**) and `VChunkHeader.ChunkId`.

Found on Zero Company, which loses ~2.4% of its animations to it -- every one a facial clip
whose asset name is the sequence name twice over:
`A_Hawks_FacialIdle_FlabberGasted_Anim_A_Hawks_FacialIdle_FlabberGasted`, 70 chars against a
64-byte field. Across 6,658 animations that is roughly 160 clips.

**It is not Zero Company specific.** Other games simply have shorter names; the same
call site would drop any of their assets given a long enough one, and `--regress` confirms every
other game is byte-identical with the patch applied.

The clamp is to `len-1`, not `len`, because these fields are NUL-terminated and a reader that
runs past the end is a worse bug than a shortened label. Truncation can in principle collide
two names sharing their first 63 bytes; that is the right trade, since the field is metadata
and the FILE name is what identifies an export.

**Diagnosis note:** the first theory was an off-by-one in the ACL decoder, because the failing
clip reported `frames=215 samples=214` and the innermost stack frame was `Buffer.BlockCopy`.
Both were red herrings -- `--trace` gave the full stack and named `AnimInfoBinary.Serialize`
in one shot. The exported file carries all 215 frames (135,880 keys / 632 bones = 215 exactly),
so nothing was ever lost to the frame count.

## `additive-pose-bone-count-clamp.patch`

**Additive animations whose base pose is a DIFFERENT animation could not export at all.**

Two call sites, one root cause: when the base pose and the additive pose carry different bone
counts, each loop is bounded by one and indexes the other.

    // FAnimationRuntime.AccumulateLocalSpaceAdditivePoseInternal
    for (int i = 0; i < basePose.Bones.Length; i++)
        basePose.Bones[i].Accumulate(additivePose.Bones[i]...)     // additive is shorter

    // FCompactPose.PushTransformAtFrame
    Debug.Assert(dstTracks.Count == Bones.Length);                 // compiled out in Release
    for (int i = 0; i < Bones.Length; ++i)
        dstTracks[i].KeyQuat[frame] = ...                          // dstTracks is shorter

The counts only differ when `RefPoseType` is not `ABPT_RefPose` -- i.e. the base is another
sequence rather than the skeleton's rest pose. `ConvertAdditive` sizes `Tracks` from the
ADDITIVE pose and then pushes the REFERENCE pose through them, and the reference is the longer.

Found on Zero Company: all eight `A_2HRifle_Peek_F_*` clips
(`AAT_RotationOffsetMeshSpace` / `ABPT_AnimFrame` over `A_2HRifle_Peek_F_CC_ref`) failed, 8 of
5,298 in the Characters pass. Fixing only the first site moved the exception rather than
removing it -- the error type changed from `IndexOutOfRangeException` on an array to
`ArgumentOutOfRangeException` on a `List`, which is what showed there was a second site.

**Clamping is the correct semantics, not a guard.** A bone with no additive track has no delta,
so leaving the base transform untouched is exactly what an absent track means; and a track
exists only for a bone the clip animates, so a pose bone with no track has nowhere to go.
Writing either would fabricate animation the game does not contain.

**The `Debug.Assert` stated the violated invariant precisely and was compiled out of Release**,
so the failure surfaced as an opaque `List.get_Item` throw instead of a bone-count mismatch.
That assert is left in place -- it is right, and it is the fastest description of the bug.

---

## `actorx-socket-bones-per-lod.patch`

Two defects in `ActorXMesh`, both in how sockets become bones. Together they made the file a
user naturally imports — `SK_Name.psk` — the worst file in the export.

**1. The socket list was appended to the mesh's own `RefSkeleton`.**
`MeshExporter` hands the SAME `convertedMesh.RefSkeleton` to every LOD, and
`ExportSkeletalSockets` did `bones.Add(...)` straight onto it. So each LOD shipped the previous
LOD's sockets as well:

| file | bones written | B2 has 54 real bones + 10 sockets |
|---|---|---|
| `SK_B2_BattleDroid_LOD0.psk` | 64 | correct |
| `..._LOD1.psk` | 74 | +1 stale copy |
| `..._LOD2.psk` | 84 | +2 |
| `SK_B2_BattleDroid.psk` | **94** | +3, and **10 duplicate bone names** |

A skeleton with two bones of the same name cannot be resolved by name, which is how every
importer and every `.psa` binds. Measured on the shipped 199 GB export: **169 character and 123
weapon meshes** carried duplicated bones. Copying the list costs one `List` per LOD; sockets are
appended after every real bone, so no skin weight's bone index moves.

**2. A socket may share its name with a bone — usually the bone it hangs off.**
UE allows that; a PSK does not. **121 weapon meshes** shipped two bones called `muzzle`
(`SK_Rifle_Auromae_Barrel_01`: `root`, `muzzle`, `muzzle`), the second parented to the first. The
socket is kept, because its relative transform is real data, and renamed to `muzzle_socket`.

Note the second defect is **not** caused by the first: it reproduces at LOD0, where nothing has
accumulated. That is why the LOD0 files were clean for characters and not for weapons, and it is
what the bone-count table above cannot show on its own.

---

## `acl-refpose-out-of-range.patch`

**Applies on top of `acl-legacy-1x.patch`** — both touch `AnimConverter.cs`. Regenerate this one
by diffing against a copy taken *after* the legacy patch, or a bare `git diff` will swallow its
hunks too.

The native ACL writer fills a **default** rotation/translation/scale sub-track from the reference
pose:

```cpp
return UEVector3ToACL(RefPoses[TrackToBoneMapping[trackIndex].BoneTreeIndex].Translation);
```

Nothing bounds that index, and it is not always in range. Every `B2_BattleDroid` clip in Zero
Company ships a **56-entry track map whose last two entries name bones 56 and 57**, against a
skeleton whose cooked `ReferenceSkeleton` holds 56 bones (0–55) — the animations were compressed
with two virtual bones appended, which the cooked skeleton does not carry. Those two lookups read
**past the end of a pinned managed array**, and whatever lies there is written into the pose.

Fixed by padding the reference pose with identity out to the highest bone index the map names, so
an out-of-range track resolves to something defined instead of heap noise.

**This is not the cause of Zero Company's one bad foot, and the patch does not claim to be.**
`A_2XBlaster_Walk` has the B2's `foot_r` pinned at `ik_foot_r`'s rest position for all 28 frames,
69.8 units from where the other 287 clips put it. That value survives poisoning *both* candidate
reference-pose entries and re-exporting unchanged, so it comes out of the game's own compressed
stream, not from any lookup here. It ships unnoticed because the rig carries `ik_foot_r` and the
game solves foot IK at runtime; nothing reads the baked translation. `tools/psarigidfix.py`
reports and repairs it from the clips that agree.

The bounds bug is real regardless, and it is the kind that produces exactly this class of
symptom — which is why it was worth ruling in or out by experiment rather than by reading.

---

## `wwise-zero-eventid-bank-walk.patch`

**Zero Company shipped with 42% of its audio unreachable, and the extraction reported success.**

An `UAkAudioEvent` resolves one of two ways. Events carrying direct **Media** are read straight
out; events carrying only **SoundBanks** are resolved by walking the bank's HIRC hierarchy from
the event's id. `ProcessSoundBankCookedData` passed `eventData.EventId` into that walk without
checking it — and for every weapon event in this game that id is **0**. The walk looks up
hierarchy node 0, finds nothing, and returns silently.

The split falls exactly along how the audio was authored:

| folder | before | after |
|---|---|---|
| Weapons | 8 | **1,343** |
| Characters | 107 | **4,496** |
| UI | 19 | **689** |
| Environment | 395 | **1,970** |
| VO | 17,374 | **22,152** |
| **total** | **18,492** | **32,196** |

VO looked healthy because voice lines carry direct media and never take the bank path at all.
Everything else is mostly bank-resident, so it silently produced a handful of files and a folder
that looked plausible.

The id is recoverable: a Wwise id is the FNV-1 hash of the name, which the **other** branch of
`ExtractAudioEventSounds` already falls back to when there is no cooked data. This does the same
rather than trusting a zero.

**How it hid:** the run printed `18,492 written, 0 failed` and produced 19 GB. The counter that
mattered was beside it — `yielded no audio`, which for weapons alone was 224 of 237 events. A
zero failure count is not evidence of completeness when the failure mode is a silent empty result.

Also fixed alongside, in `UEToolkit.Core/WwiseBridge.cs` (in-repo, no patch needed): 102 VO lines
whose paths exceed 260 characters. vgmstream is a native CLI and cannot open them, so conversion
now happens beside the `.wem` and the result is moved with a `\?\` prefix. That prefix demands a
fully-qualified path with backslashes only; malformed, it reports "syntax is incorrect" rather
than anything about length.
