using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Read and write the geometry of a cooked skeletal mesh, in place, inside the package.
///
/// This is the only thing in the builder that CREATES geometry rather than rearranging assets the
/// game already ships, and it is the one where being nearly right is worthless: every field is
/// positional, so a single wrong width does not throw, it shifts everything after it and the mesh
/// renders as a spray of triangles through the floor.
///
/// So the whole thing is built around one test. <see cref="Read"/> parses a LOD into fields and
/// <see cref="Write"/> puts them back; running the game's own mesh through both has to return the
/// IDENTICAL bytes. Nothing here is trusted until that passes, and once it does, replacing the
/// geometry is a matter of handing Write different numbers.
///
/// WHAT IS REBUILT AND WHAT IS DROPPED. The index buffer, positions, tangents, UVs, skin weights
/// and vertex colours are written from the imported mesh. Three things that the cooker sizes BY
/// VERTEX COUNT are written empty rather than carried over, because a stale one is worse than none:
///
///   ray tracing     an offline acceleration structure built for the old triangles
///   half-edge       a per-vertex adjacency table for mesh deformers
///   attributes      per-vertex custom data; the meshes here have none anyway
///
/// Keeping any of them would point the engine at vertices that no longer exist.
///
/// MORPH TARGETS ARE THE EXCEPTION, and used to be the fourth entry on that list. The reasoning
/// above is right when the vertex count changes and wrong when it does not: on a HEAD the deltas
/// are the entire facial animation, several hundred curves of it, and dropping them silently costs
/// far more than a stale table would. They are per-vertex and index-ordered, so:
///
///   imported count == this LOD's count   the block is copied through byte for byte
///   imported count != this LOD's count   the deltas address vertices that are gone; dropped,
///                                        and SAID in the build notes, never silently
///
/// The count is the only test available -- nothing in the file can tell whether two meshes of
/// equal size are the same mesh -- so this is exactly as safe as the author's own workflow, which
/// is to edit the VERTICES of an existing head rather than skin a new one.
///
/// CLOTH IS THE SECOND EXCEPTION, added once it turned out this game has any. It is kept or
/// dropped by that same count, for the same reason, and it is the one that cannot be got half
/// right: a garment's simulation is per-section mapping in the HEADER and a vertex buffer in the
/// STREAMED block, and the engine reads the second only because the first said it was there. So
/// they move together -- see <see cref="ClothFor"/> and <see cref="Uncloth"/> -- and a first
/// attempt that wrote one without the other produced a package whose header round-tripped
/// perfectly and whose geometry read back as zero vertices.
/// </summary>
public static class CookedMesh
{
    // ------------------------------------------------------------------ the shapes on the wire

    /// <summary>
    /// One section: which material it draws with, which triangles, and which bones move them.
    ///
    /// Byte-for-byte in the order UE writes it. `ReadBoolean` is FOUR bytes here and not one --
    /// UE serialises a bool as int32 -- which is the single easiest field width to get wrong and
    /// the hardest to notice, because a mesh read three bytes short still parses for a while.
    /// </summary>
    public sealed record Section(byte GlobalStrip, byte ClassStrip, short MaterialIndex,
                                 int BaseIndex, int NumTriangles, int RecomputeTangent,
                                 byte RecomputeMask, int CastShadow, int VisibleInRayTracing,
                                 uint BaseVertexIndex, ushort[] BoneMap, int NumVertices,
                                 int MaxBoneInfluences, short CorrespondClothAssetIndex,
                                 byte[] ClothingSectionData, int[] DupVert, byte[] DupVertIndex,
                                 int Disabled, byte[] ClothMapping, int ClothVertices)
    {
        /// <summary>
        /// Whether this section is simulated, by the SAME test the engine uses -- any cloth
        /// mapping entry at all, not a valid asset index.
        ///
        /// It decides whether a cloth vertex buffer follows the colours in the streamed block, so
        /// getting it wrong shifts every field after it and the LOD reads as garbage. That is
        /// exactly what "the LOD carries 4194309 skin weight profile(s)" was: the first four bytes
        /// of a cloth buffer read as a profile count.
        /// </summary>
        public bool HasCloth => ClothVertices > 0;
    }

    public sealed record LodHeader(byte GlobalStrip, byte ClassStrip, int IsLodCookedOut,
                                   int Inlined, short[] RequiredBones, Section[] Sections,
                                   short[] ActiveBoneIndices);

    /// <summary>
    /// The LOD's compressed morph targets, as a RANGE rather than as contents.
    ///
    /// Every byte of it is quantised deltas addressed by bit offsets, and nothing here wants to
    /// decode them -- the only two useful things to do with the block are step over it and copy it
    /// through whole. The counts are carried because they are what the parse is checked against
    /// and what a probe has to print.
    /// </summary>
    /// <param name="At">Where the `bSerializeCompressedMorphTargets` flag sits in the package.</param>
    /// <param name="Length">Flag included, through to the last of the two precisions.</param>
    /// <param name="Targets">Morph targets in this LOD -- the length of all four per-morph arrays.</param>
    /// <param name="Batches">`NumTotalBatches`, the GPU work the deltas are grouped into.</param>
    /// <param name="DataBytes">The packed delta blob alone, which is nearly all of Length.</param>
    public sealed record Morph(int At, int Length, int Targets, int Batches, int DataBytes);

    /// <summary>
    /// How the LOD's skin weights are stored, which the writer has to match exactly.
    ///
    /// Read and reported rather than assumed, because one of these decides whether a mesh can have
    /// more than 256 bones in a section at all -- and the answer was taken for granted once
    /// already, in the writer, which hard-codes 8-bit indices.
    /// </summary>
    /// <param name="Variable">One run per vertex sized to its own influences, not a fixed four.</param>
    /// <param name="MaxInfluences">Bones a single vertex may be driven by.</param>
    /// <param name="Bits16Index">Bone indices are two bytes, so a section may name up to 65,536.</param>
    /// <param name="Bits16Weight">Weights are two bytes rather than one.</param>
    public sealed record Skin(bool Variable, int MaxInfluences, bool Bits16Index, bool Bits16Weight);

    /// <summary>A LOD located in the package: where it starts, how long it is, what it says.</summary>
    /// <param name="ClothStart">Where the cloth vertex buffer sits, or where it would if there
    /// were one. <paramref name="ClothLength"/> is 0 on a mesh that is not simulated.</param>
    public sealed record Lod(int Start, int Length, LodHeader Header,
                             int StreamedStart, int StreamedLength,
                             int NumVertices, int NumTexCoords, int IndexCount,
                             Morph? Morph = null, Skin? Skin = null,
                             int ClothStart = 0, int ClothLength = 0)
    {
        /// <summary>Whether this LOD is cloth-simulated, which its sections decide.</summary>
        public bool HasCloth => ClothLength > 0;

        public string Describe =>
            $"{NumVertices:N0} verts, {IndexCount:N0} indices, {Header.Sections.Length} section(s), " +
            $"{Length:N0} bytes at {Start:N0}";
    }

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// How far back from the geometry the LOD header is looked for.
    ///
    /// A header is sections, their bone maps, and the LOD's required-bone list -- so its size
    /// grows with the SKELETON, not with the mesh. A weapon has one bone and a header of a few
    /// dozen bytes; a character head has 745 bones and seven sections, and its header does not fit
    /// in the four kilobytes this used to allow.
    ///
    /// THE SKELETON IS NOT THE ONLY THING THAT GROWS IT: a section's cloth mapping is 64 bytes per
    /// SIMULATED VERTEX and lives in the header too. `SK_HAAM_Pyk003D_TORS` has 4,395 of them in
    /// one section -- 281 KB of mapping -- so its header did not fit in the quarter megabyte this
    /// allowed either, and it was the one mesh of 892 still reporting NOT LOCATED after the cloth
    /// reader was written. Raising this costs almost nothing: `Worth` rejects a candidate on two
    /// int32 comparisons, and only a candidate that passes both is parsed.
    /// </summary>
    private const int HeaderWindow = 4194304;

    /// <summary>
    /// Find every LOD in a package's bytes, given what the parsed mesh says they should contain.
    ///
    /// The counts are not decoration. This locates LOD 0 by searching a window for a position that
    /// parses into a header ending exactly where the geometry begins -- and a search that only
    /// checks "it parsed" will happily settle on a position a few bytes off whose arrays happen to
    /// be self-consistent. Requiring the parsed vertex and bone counts to match the ones CUE4Parse
    /// read independently is what makes the answer a match rather than a coincidence.
    /// </summary>
    public static List<Lod>? Read(byte[] package, IReadOnlyList<int> vertexCounts,
                                  IReadOnlyList<int> indexCounts, int requiredBones,
                                  bool hasVertexColours, out string? why)
    {
        why = null;
        if (vertexCounts.Count == 0) { why = "the mesh has no LODs"; return null; }

        // LOD 0's index buffer, by the header the cooker must have written for it. The count makes
        // this effectively unique in a package: 20,511 as a little-endian int does not occur by
        // accident next to a 2 and a 2.
        var anchor = FindIndexBuffer(package, indexCounts[0], out var stride);
        if (anchor < 0)
        { why = $"no index buffer of {indexCounts[0]:N0} entries in the package"; return null; }

        // Backwards from the geometry to the top of the LOD. Scanned rather than computed, then
        // checked against the counts -- computing it means encoding every field width twice, and
        // the second copy is the one that goes stale.
        var streamedStart = anchor - 2;              // the streamed block's own strip flags
        var start = -1;

        // WHY the scan failed, kept as it goes. "Could not be located" on its own does not say
        // whether the header is outside the window, or sitting right there and disagreeing about
        // its counts -- and those are opposite problems. A head fails here and the message was
        // the same either way.
        string? nearest = null;

        var from = Math.Max(0, streamedStart - HeaderWindow);

        // A CHEAP REJECT BEFORE THE EXPENSIVE ONE, and on a head it is the difference between
        // usable and not.
        //
        // The window is a quarter of a megabyte, so the loop below runs a quarter of a million
        // times, and TryReadHeader is not free: it allocates a short[] of every required bone and
        // a Section record with its own bone map for each of seven sections, at EVERY byte offset.
        // On a 745-bone head that took minutes and tens of gigabytes of garbage to find a header
        // sitting a few tens of kilobytes away -- the 4 KB window this replaced was never large
        // enough to expose it.
        //
        // Two int32s at fixed offsets settle it without allocating anything: a header's `Inlined`
        // flag is 1 and the count that follows is the LOD's required-bone list, which for a head
        // is 745 and does not occur by accident. Both are conditions the loop below ALREADY
        // requires, so nothing that could have matched is skipped.
        bool Worth(int c) =>
            c + 14 <= package.Length &&
            BitConverter.ToInt32(package, c + 6) == 1 &&
            BitConverter.ToInt32(package, c + 10) == requiredBones;

        for (var candidate = from; candidate < streamedStart; candidate++)
        {
            if (!Worth(candidate)) continue;
            var at = candidate;
            if (!TryReadHeader(package, ref at, out var header)) continue;
            if (at + 4 != streamedStart) continue;   // the buffersSize field sits between them

            // Past here it IS a header ending where the geometry begins, so everything below is a
            // disagreement about contents rather than a failure to find one.
            if (header.RequiredBones.Length != requiredBones)
            { nearest = $"{header.RequiredBones.Length} required bones against the mesh's {requiredBones}"; continue; }
            // EVERY SECTION, SUMMED. Sections PARTITION the vertex buffer -- section 0 of a
            // two-section LOD holds 1,961 of 4,827 vertices and the rest are section 1's -- so
            // comparing section zero's count against the LOD's total is only ever true for a
            // single-section mesh. Every mesh this tool had imported was single-section, so the
            // check looked right and quietly refused everything else: the Clo010 legs (2 sections)
            // failed here, and so does every head in the game (7). The import was then SKIPPED and
            // the mod shipped the untouched template, reporting the template's own vertex count
            // back as a successful read.
            if (header.Sections.Length == 0) { nearest = "no sections"; continue; }
            if (header.Sections.Sum(x => x.NumVertices) != vertexCounts[0])
            {
                nearest = $"{header.Sections.Length} section(s) summing to " +
                          $"{header.Sections.Sum(x => x.NumVertices):N0} vertices against the " +
                          $"mesh's {vertexCounts[0]:N0}";
                continue;
            }
            if (header.Inlined != 1) { nearest = $"an inline flag of {header.Inlined}, not 1"; continue; }
            start = candidate;
            break;
        }
        if (start < 0)
        {
            // NOTHING WAS FOUND, so now the slow scan is worth its cost -- and only now.
            //
            // The prefilter above rejects on the very things that make the best diagnostic
            // ("a header ends there but claims 240 required bones, not 745"), so on failure the
            // exhaustive walk runs to say WHICH of the two problems this is. Seconds spent on a
            // path that is about to give up beats a message that cannot tell "it is not in the
            // window" from "it is right there and disagrees".
            for (var candidate = from; candidate < streamedStart && nearest is null; candidate++)
            {
                var at = candidate;
                if (!TryReadHeader(package, ref at, out var header)) continue;
                if (at + 4 != streamedStart) continue;
                nearest = header.RequiredBones.Length != requiredBones
                    ? $"{header.RequiredBones.Length} required bones against the mesh's {requiredBones}"
                    : header.Sections.Length == 0
                        ? "no sections"
                        : header.Sections.Sum(x => x.NumVertices) != vertexCounts[0]
                            ? $"{header.Sections.Length} section(s) summing to " +
                              $"{header.Sections.Sum(x => x.NumVertices):N0} vertices against the " +
                              $"mesh's {vertexCounts[0]:N0}"
                            : $"an inline flag of {header.Inlined}, not 1";
            }

            why = "the LOD header could not be located ahead of the geometry" +
                  (nearest is null
                       ? $" -- nothing in the {HeaderWindow:N0} bytes before it parses as a header " +
                         "that ends there, so either it is further back than that or it does not parse"
                       : $" -- the header that ends there has {nearest}");
            return null;
        }

        var lods = new List<Lod>();
        var pos = start;
        for (var i = 0; i < vertexCounts.Count; i++)
        {
            var lodStart = pos;
            if (!TryReadHeader(package, ref pos, out var header))
            { why = $"LOD {i} header did not parse at {lodStart:N0}"; return null; }

            pos += 4;                                 // buffersSize, rewritten on the way out
            var geometryStart = pos;
            // Whether a cloth buffer follows the colours is a fact about the SECTIONS, and the
            // header carrying them has just been parsed -- so it is known before the walk that
            // depends on it, which is the only reason this does not need a second pass.
            var lodHasCloth = header.Sections.Any(x => x.HasCloth);
            if (!TrySkipStreamed(package, ref pos, hasVertexColours, lodHasCloth,
                                 out var verts, out var uvSets,
                                 out var indices, out var morph, out var skin,
                                 out var clothAt, out var clothLen, out var streamWhy))
            { why = $"LOD {i} geometry: {streamWhy}"; return null; }

            if (verts != vertexCounts[i])
            { why = $"LOD {i} parsed {verts:N0} vertices, the mesh says {vertexCounts[i]:N0}"; return null; }

            lods.Add(new Lod(lodStart, pos - lodStart, header, geometryStart, pos - geometryStart,
                             verts, uvSets, indices, morph, skin, clothAt, clothLen));
        }
        return lods;
    }

    // ------------------------------------------------------------------ the material slots

    /// <summary>
    /// Where a skeletal mesh's material slots live inside its export, found WITHOUT parsing what
    /// is inside one.
    ///
    /// They are not a property. `--props` on a mesh shows no Materials at all, because
    /// USkeletalMesh reads them straight out of the archive after the property bag:
    /// `SkeletalMaterials = Ar.ReadArray(() =&gt; new FSkeletalMaterial(Ar))`. So the proven
    /// property-array growth path does not apply, and this is the same kind of work the LOD
    /// editing is -- find a run in the blob and splice it.
    ///
    /// AN EARLIER VERSION TRIED TO PARSE AN ENTRY and could not: derived from CUE4Parse's reader
    /// it came to 35 bytes, and the real one is 44. The version gates that decide which fields a
    /// cooked FSkeletalMaterial carries are not something to infer from one engine build.
    ///
    /// It does not matter. Growing the array needs three things -- where it starts, where it
    /// ends, and one entry's bytes to clone -- and all three can be had from landmarks that ARE
    /// certain:
    ///
    ///   the SLOT NAME, as an index into this package's own name table, four bytes into the
    ///   first entry. Specific to this mesh, so it cannot coincide the way a count can.
    ///
    ///   the BONE COUNT, which is the int32 FReferenceSkeleton begins with, and which follows
    ///   the last entry immediately.
    ///
    /// The entry size then falls out as (end - start - 4) / count, and a size that does not
    /// divide evenly means the landmarks were wrong -- which is checked rather than assumed.
    /// </summary>
    public sealed record MaterialArray(int Start, int End, int Count, int EntrySize)
    {
        public int FirstEntry => Start + 4;

        /// <summary>Where entry <paramref name="i"/> begins.</summary>
        public int EntryAt(int i) => FirstEntry + i * EntrySize;
    }

    public static MaterialArray? FindMaterials(byte[] data, int count, int boneCount,
                                               int firstSlotName, out string? why)
    {
        why = null;
        if (count <= 0) { why = "the mesh reports no material slots"; return null; }
        if (firstSlotName < 0) { why = "the first slot's name is not in the package name table"; return null; }

        // THE END LANDMARK IS THE BONE COUNT, so it is only as distinctive as that number.
        //
        // A weapon part has one or two bones, and a lone 1 occurs constantly -- the IQA-11's
        // barrel located a 12-byte entry with one floor and a 32-byte one with another, neither
        // matching the 44 bytes every real mesh here uses. Rather than tune the floor until the
        // wrong answer disappears, this refuses outright below a count that could be a
        // coincidence, and says so.
        //
        // It costs nothing today: every weapon mesh measured in this game is single-material, so
        // nothing needs its array grown. Clothing and heads -- the meshes that do -- carry
        // hundreds of bones.
        if (boneCount < 8)
        {
            why = $"only {boneCount} bone(s): too few for the end of the array to be found safely";
            return null;
        }

        for (var name = 0; name + 4 <= data.Length; name++)
        {
            if ((BitConverter.ToUInt32(data, name) & 0x3FFFFFFF) != (uint) firstSlotName) continue;

            // The name sits four bytes into the first entry, and the entry four bytes past the
            // count -- so a hit fixes where the array must start, if it starts anywhere.
            var start = name - 8;
            if (start < 0 || BitConverter.ToInt32(data, start) != count) continue;

            // The bone count ends it. Bounded because a wrong landmark should fail, not wander
            // through a megabyte of vertices looking for a number that happens to match.
            for (var end = start + 8; end + 4 <= data.Length && end < start + 8192; end += 4)
            {
                if (BitConverter.ToInt32(data, end) != boneCount) continue;
                var span = end - start - 4;
                if (span <= 0 || span % count != 0) continue;

                // A FLOOR on the entry size, because the end landmark is only as distinctive as
                // the bone count. A weapon part has ONE bone, and a lone 1 turns up everywhere --
                // the IQA-11's barrel "located" a 12-byte entry, which cannot be one: a
                // FSkeletalMaterial carries at least a material reference, a name and the UV
                // channel info, and that is thirty bytes before anything optional.
                //
                // Failing here is the right outcome. A mesh whose array cannot be found safely
                // must say so, not hand back an offset that would splice bytes into the middle of
                // something else.
                if (span / count < 30) continue;

                return new MaterialArray(start, end, count, span / count);
            }
        }

        why = $"no material array for {count} slot(s) named #{firstSlotName} before {boneCount:N0} bones";
        return null;
    }

    /// <summary>
    /// Give a mesh one more material slot, by CLONING one it already has.
    ///
    /// Cloning rather than composing is the whole trick. A FSkeletalMaterial carries fields whose
    /// presence depends on version gates this build cannot reliably infer -- the attempt to
    /// compose one from CUE4Parse's reader produced 35 bytes where the truth is 44 -- but an
    /// entry that the game itself wrote is correct by construction, whatever it contains. Only
    /// the material reference is patched, and that is the first four bytes of every entry
    /// regardless of what follows.
    ///
    /// The slot NAME is left as the clone's, and <see cref="NameSlots"/> corrects it afterwards.
    /// It has to be a separate pass: a name that is not in the package's map yet has to be
    /// appended to it, and appending moves every byte after the name batch -- including this
    /// array. Naming here would invalidate the offset the next clone is spliced at.
    ///
    /// AN EARLIER VERSION SAID DUPLICATE SLOT NAMES WERE HARMLESS, on the grounds that a cooked
    /// section names its material by INDEX. That is true of rendering and false of everything
    /// else: a customization fragment targets `MaterialSlotNames`, so a duplicate name is how the
    /// torso's tint mask ended up on the backpack.
    ///
    /// <paramref name="materialImport"/> is the FPackageIndex the new slot should point at,
    /// which for a material the mesh did not previously reference comes from
    /// <see cref="ZenPackage.AddPackageImport"/>.
    /// </summary>
    public static byte[]? AddMaterialSlot(byte[] package, MaterialArray at, int materialImport,
                                          out string? why)
    {
        why = null;
        if (at.Count <= 0) { why = "the array holds no entry to clone"; return null; }
        if (at.End > package.Length) { why = "the array runs past the end of the package"; return null; }

        var oldLength = at.End - at.Start;
        var block = new byte[oldLength + at.EntrySize];

        // count + 1, then every entry it already had, then the clone.
        BitConverter.GetBytes(at.Count + 1).CopyTo(block, 0);
        Array.Copy(package, at.FirstEntry, block, 4, at.EntrySize * at.Count);

        var clone = 4 + at.EntrySize * at.Count;
        Array.Copy(package, at.EntryAt(0), block, clone, at.EntrySize);
        BitConverter.GetBytes(materialImport).CopyTo(block, clone);

        // Through SpliceExportData, which is what corrects the export map for the new length --
        // writing the bytes in place would leave every offset after this export describing the
        // wrong thing.
        return ZenPackage.SpliceExportData(package, at.Start, oldLength, block);
    }

    /// <summary>
    /// The most influences any vertex actually uses, which is what the section must declare.
    ///
    /// Counted rather than taken from the array length: the arrays are padded to a fixed width and
    /// the padding is not stored, so a mesh whose deepest vertex uses five influences declares
    /// five and not twelve. The section and the buffer have to agree or the mesh is read as a
    /// different shape entirely.
    /// </summary>
    private static int MaxUsedInfluences(IReadOnlyList<MeshImport.Vertex> vertices)
    {
        var most = 0;
        foreach (var v in vertices)
        {
            var last = 0;
            for (var k = 0; k < v.Weights.Length; k++) if (v.Weights[k] > 0) last = k + 1;
            if (last > most) most = last;
        }
        return Math.Max(4, most);
    }

    private static int FindIndexBuffer(byte[] data, int count, out byte stride)
    {
        foreach (var size in new byte[] { 2, 4 })
        {
            var want = new byte[9];
            want[0] = size;
            BitConverter.GetBytes((int) size).CopyTo(want, 1);
            BitConverter.GetBytes(count).CopyTo(want, 5);

            for (var i = 0; i + want.Length <= data.Length; i++)
                if (data.AsSpan(i, want.Length).SequenceEqual(want)) { stride = size; return i; }
        }
        stride = 0;
        return -1;
    }

    private static bool TryReadHeader(byte[] d, ref int at, out LodHeader header)
    {
        header = null!;
        try
        {
            var r = new Reader(d, at);
            var globalStrip = r.Byte();
            var classStrip = r.Byte();
            var cookedOut = r.Int();
            var inlined = r.Int();
            if (cookedOut is not (0 or 1) || inlined is not (0 or 1)) return false;

            var required = r.Shorts();
            if (required.Length is < 0 or > 65536) return false;

            var sectionCount = r.Int();
            if (sectionCount is < 0 or > 256) return false;
            var sections = new Section[sectionCount];
            for (var i = 0; i < sectionCount; i++)
            {
                if (!TryReadSection(r, out var s)) return false;
                sections[i] = s;
            }

            var active = r.Shorts();
            if (active.Length is < 0 or > 65536) return false;

            header = new LodHeader(globalStrip, classStrip, cookedOut, inlined, required, sections, active);
            at = r.At;
            return true;
        }
        catch { return false; }
    }

    private static bool TryReadSection(Reader r, out Section section)
    {
        section = null!;
        var globalStrip = r.Byte();
        var classStrip = r.Byte();
        var material = r.Short();
        var baseIndex = r.Int();
        var numTriangles = r.Int();
        var recompute = r.Int();
        var recomputeMask = r.Byte();
        var castShadow = r.Int();
        var rayTracing = r.Int();
        var baseVertex = r.UInt();

        // CLOTH MAPPING: an array OF ARRAYS, one per LOD bias, 64 bytes per cloth vertex.
        //
        // THE COMMENT HERE USED TO SAY "nothing wearable in this game has any". That was wrong,
        // and it is why nobody looked for two releases. `SK_HAAM_Coi002A_TORS` -- an ordinary
        // Coil armour torso -- carries a full Chaos cloth setup, and so do a dozen other kits.
        // The same kit's ARMS read fine, which is what made the claim look true.
        //
        // The block is CAPTURED rather than skipped now, for two reasons: whether a section has
        // cloth decides whether a whole extra vertex buffer follows the colours in the streamed
        // block further down (see TrySkipStreamed), and the writer has to be able to put these
        // exact bytes back or the header will not round-trip.
        var clothAt = r.At;
        var clothLods = r.Int();
        if (clothLods is < 0 or > 64) return false;
        var clothVertices = 0;
        for (var i = 0; i < clothLods; i++)
        {
            var inner = r.Int();
            if (inner < 0) return false;
            clothVertices += inner;
            r.Skip(inner * 64);
        }
        var clothMapping = r.Since(clothAt);

        var boneMap = r.UShorts();
        if (boneMap.Length is < 0 or > 65536) return false;
        var numVertices = r.Int();
        var maxInfluences = r.Int();
        var clothAsset = r.Short();
        var clothingSection = r.Bytes(20);

        // Duplicated-vertex tables, present unless the class strip flag says otherwise. They are
        // per-vertex and so are dropped on a rewrite; here they only have to be measured.
        int[] dupVert = [];
        byte[] dupIndex = [];
        if ((classStrip & 1) == 0)
        {
            var n = r.Int();
            if (n is < 0 or > 1 << 24) return false;
            dupVert = new int[n];
            for (var i = 0; i < n; i++) dupVert[i] = r.Int();
            var m = r.Int();
            if (m is < 0 or > 1 << 24) return false;
            dupIndex = r.Bytes(m * 8);
        }

        var disabled = r.Int();
        if (disabled is not (0 or 1)) return false;

        section = new Section(globalStrip, classStrip, material, baseIndex, numTriangles, recompute,
                              recomputeMask, castShadow, rayTracing, baseVertex, boneMap,
                              numVertices, maxInfluences, clothAsset, clothingSection,
                              dupVert, dupIndex, disabled, clothMapping, clothVertices);
        return true;
    }

    /// <summary>
    /// Walk the streamed geometry without keeping it, to learn where it ends.
    ///
    /// Only the end matters for a rewrite -- the contents are being replaced -- but the walk has
    /// to be exact, because the end is the sum of every field before it.
    /// </summary>
    private static bool TrySkipStreamed(byte[] d, ref int at, bool hasColours, bool hasCloth,
                                        out int numVertices, out int numTexCoords,
                                        out int indexCount, out Morph? morph, out Skin? skin,
                                        out int clothStart, out int clothLength, out string? why)
    {
        clothStart = clothLength = 0;
        numVertices = numTexCoords = indexCount = 0;
        morph = null;
        skin = null;
        why = null;
        try
        {
            var r = new Reader(d, at);
            r.Skip(2);                                          // the streamed block's strip flags

            r.Byte();                                           // index size
            indexCount = r.BulkArray();                         // indices

            r.Int();                                            // position stride
            numVertices = r.Int();
            r.BulkArray();                                      // positions

            r.Skip(2);                                          // static mesh VB strip flags
            numTexCoords = r.Int();
            var vbVertices = r.Int();
            r.Int();                                            // bUseFullPrecisionUVs
            r.Int();                                            // bUseHighPrecisionTangentBasis
            r.BulkArray();                                      // tangents
            r.BulkArray();                                      // UVs
            if (vbVertices != numVertices)
            { why = $"the tangent buffer has {vbVertices:N0} vertices and the positions have {numVertices:N0}"; return false; }

            r.Skip(2);                                          // skin weight data strip flags
            var variable = r.Int();                             // bVariableBonesPerVertex
            var influences = r.Int();                           // MaxBoneInfluences
            r.Int();                                            // NumBones
            r.Int();                                            // NumVertices
            var wide16 = r.Int();                               // bUse16BitBoneIndex
            var wideWeight = r.Int();                           // bUse16BitBoneWeight
            skin = new Skin(variable != 0, influences, wide16 != 0, wideWeight != 0);
            r.BulkArray();                                      // the weight blob

            r.Skip(2);                                          // skin weight lookup strip flags
            r.Int();                                            // NumLookupVertices
            r.BulkArray();                                      // the lookup table

            if (hasColours)
            {
                r.Skip(2);                                      // colour strip flags
                r.Int();                                        // stride
                var colourVerts = r.Int();
                // Asymmetric on purpose: an empty colour buffer writes no bulk header at all.
                if (colourVerts > 0) r.BulkArray();
            }

            // THE CLOTH VERTEX BUFFER, which sits between the colours and the skin weight
            // profiles and was the whole reason a cloth torso read as garbage.
            //
            // Written only when a section has cloth mapping, so it cannot be detected from the
            // bytes here -- the caller has to say, off the header it already parsed. Its shape,
            // from FSkeletalMeshVertexClothBuffer:
            //
            //     strip flags        2 bytes
            //     the mapping        a bulk array, skipped whole
            //     index mapping      an int count then that many ulongs      (CompactClothVertexBuffer)
            //     LOD bias           the same count again, four bytes each   (AddClothMappingLODBias)
            //
            // Both version gates are long past in UE 5.6, so both tails are always present here.
            // Captured rather than stepped over: carrying the block through unchanged is the only
            // way an import can keep a garment simulating, and a length alone cannot do that.
            clothStart = r.At;
            if (hasCloth)
            {
                r.Skip(2);                                      // cloth buffer strip flags
                r.BulkArray();                                  // the mesh-to-mesh mapping
                var mappings = r.Int();                         // ClothIndexMapping
                if (mappings is < 0 or > 1 << 20)
                { why = $"the cloth buffer claims {mappings:N0} index mapping(s)"; return false; }
                r.Skip(mappings * 8);
                r.Skip(mappings * 4);                           // the LOD bias array beside it
            }
            clothLength = r.At - clothStart;

            var profiles = r.Int();                             // skin weight profiles
            if (profiles != 0)
            {
                why = $"the LOD carries {profiles} skin weight profile(s)" +
                      (hasCloth ? "" : " -- if this mesh is simulated, its cloth buffer was not " +
                                       "expected here and everything after it has shifted");
                return false;
            }

            r.Skip(24);                                         // FRayTracingGeometryOfflineDataHeader
            r.BulkArray();                                      // its payload

            // COMPRESSED MORPH TARGETS -- the facial animation, and the reason a head could not be
            // imported at all.
            //
            // The int here is a FLAG and not a count: UE writes `bSerializeCompressedMorphTargets`
            // as an int32 bool. Every head sets it, so the old refusal ("the LOD carries compressed
            // morph targets") fired on the flag and the walk stopped one field into the block.
            //
            // FMorphTargetVertexInfoBuffers, in the order UE serialises it -- taken from
            // CUE4Parse's own reader, which parses these heads successfully, not from memory:
            //
            //   TArray<uint32>    MorphData                  the packed, quantised deltas
            //   TArray<FVector4f> MinimumValuePerMorph       16 bytes each -- FLOATS, not doubles
            //   TArray<FVector4f> MaximumValuePerMorph
            //   TArray<uint32>    BatchStartOffsetPerMorph   one per morph target
            //   TArray<uint32>    BatchesPerMorph            one per morph target
            //   int32             NumTotalBatches
            //   float             PositionPrecision
            //   float             TangentZPrecision
            //
            // The deltas inside MorphData are addressed by an offset RELATIVE to the start of that
            // blob, so the block survives being copied somewhere else in the package unchanged --
            // which is what makes carrying it through possible at all.
            var morphAt = r.At;
            if (r.Int() != 0)
            {
                var words = r.Items(4);                         // MorphData
                var minima = r.Items(16);                       // MinimumValuePerMorph
                var maxima = r.Items(16);                       // MaximumValuePerMorph
                var starts = r.Items(4);                        // BatchStartOffsetPerMorph
                var perMorph = r.Items(4);                      // BatchesPerMorph
                var batches = r.Int();                          // NumTotalBatches
                r.Skip(8);                                      // the two precisions

                // FOUR ARRAYS THAT MUST AGREE. Each of these is one entry per morph target, so a
                // walk that has drifted by a field reads four different numbers here. This is the
                // same standard the rest of the file holds itself to: a parse that is merely
                // self-consistent is a coincidence until something independent agrees with it.
                if (minima != maxima || minima != starts || minima != perMorph)
                {
                    why = $"the compressed morph block reads {minima}, {maxima}, {starts} and " +
                          $"{perMorph} morph targets in its four arrays, which cannot all be right";
                    return false;
                }
                morph = new Morph(morphAt, r.At - morphAt, minima, batches, words * 4);
            }

            var attributes = r.Int();
            if (attributes != 0) { why = $"the LOD carries {attributes} vertex attribute buffer(s)"; return false; }

            r.Byte();                                           // mesh deformer strip flags
            var deformerClass = r.Byte();
            if ((deformerClass & 1) == 0)
            {
                r.Ints();                                       // VertexToEdgeData
                r.Ints();                                       // EdgeToTwinEdgeData
            }

            at = r.At;
            return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// A whole LOD, header and geometry, from an imported mesh.
    ///
    /// The header is the template's with three numbers changed -- the section's triangle count,
    /// vertex count and bone map -- because everything else in it describes the SKELETON, which
    /// the imported mesh shares. Rebuilding those from the FBX would mean deciding which of the
    /// game's 240 bones are required by a helmet, and the template already knows.
    /// </summary>
    /// <param name="materialSlots">
    /// How many materials the ASSET has slots for. Sections are written per material either way;
    /// this only decides how far a material index is allowed to reach.
    /// </param>
    /// <param name="boneMapUse">
    /// THE BONE MAP THE VERTICES WERE INDEXED AGAINST, which is LOD ZERO's.
    ///
    /// A section's bone indices are positions in ITS OWN bone map, and the game orders that map
    /// differently in every LOD -- index 3 is `upperarm_tricep_l` at LOD 0 and
    /// `upperarm_twist_02_l` at LOD 1 on the very first mesh this was checked against. The
    /// importer resolves bone NAMES once, against LOD 0, and then the same geometry is written
    /// into every LOD; so taking each LOD's own map from its own template left LODs 1 and up
    /// driving every vertex from a bone it did not mean. It deforms, so it does not look broken --
    /// it looks like bad weights, at distance, where nobody is looking closely enough to tell.
    /// </param>
    /// <param name="morph">
    /// THE COMPRESSED MORPH BLOCK TO CARRY THROUGH, or null to write none.
    ///
    /// Bytes rather than anything parsed, because the only correct thing to do with quantised
    /// per-vertex deltas is to move them unaltered. It is the caller's job to have decided that
    /// they still describe these vertices -- see <see cref="Import"/>, which is the only place
    /// that knows both counts.
    /// </param>
    public static byte[] Write(LodHeader template, MeshImport.Result mesh, int boneMapSize,
                               bool hasVertexColours, int numTexCoords = 1, int materialSlots = 1,
                               ushort[]? boneMapUse = null, byte[]? morph = null,
                               byte[]? cloth = null)
    {
        // From the map the VERTICES were indexed against, not from this LOD's own -- see
        // boneMapUse. Falling back to this LOD's template only when the caller names none, which
        // is what a single-LOD write does.
        var source = boneMapUse is { Length: > 0 } ? boneMapUse : template.Sections[0].BoneMap;
        var boneMap = new ushort[Math.Max(1, boneMapSize)];
        for (var i = 0; i < boneMap.Length; i++)
            boneMap[i] = i < source.Length ? source[i] : (ushort) i;

        // ONE SECTION PER MATERIAL the file used. A section is a CONTIGUOUS run of the index
        // buffer -- a first index and a triangle count -- which is exactly what MeshImport has
        // already arranged by sorting the triangles into material order.
        //
        // A single-material import produces one group covering everything, so this writes the
        // same single section it always did.
        var groups = mesh.Groups.Count > 0
            ? mesh.Groups
            : [new MeshImport.Group(0, 0, mesh.Triangles)];

        // The MATERIAL INDEX is clamped to what the asset actually has slots for. Growing that
        // array is a separate job; until it is done, an extra material shares the last real slot
        // rather than pointing past the end of the array -- which is a mesh the game refuses to
        // load, and a far worse outcome than a piece wearing the wrong paint.
        var slots = Math.Max(1, materialSlots);

        var sections = new Section[groups.Count];
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            sections[i] = template.Sections[Math.Min(i, template.Sections.Length - 1)] with
            {
                BaseIndex = g.FirstIndex,
                NumTriangles = g.Triangles,
                BaseVertexIndex = 0,
                BoneMap = boneMap,
                NumVertices = mesh.Vertices.Count,
                // What the imported vertices actually carry, so the section and the buffer agree.
                MaxBoneInfluences = MaxUsedInfluences(mesh.Vertices),
                MaterialIndex = (short) Math.Min(g.Material, slots - 1),
                // Per-vertex tables for vertices that no longer exist. Written empty rather than
                // carried, and the strip flag left alone so the empty arrays are still expected.
                DupVert = [],
                DupVertIndex = [],
            };
        }

        var header = template with { Sections = sections };

        var geometry = Geometry(mesh, hasVertexColours, numTexCoords, morph, cloth);

        var w = new Writer();
        WriteHeader(w, header);
        w.Int(geometry.Length);                                  // buffersSize
        w.Raw(geometry);
        return w.ToArray();
    }

    /// <summary>
    /// Just the header, so a reader can be checked against a writer.
    ///
    /// The header is the half of a LOD that is READ from the template and written back unchanged
    /// apart from three numbers, so it is the half where a round trip means something: feed the
    /// game's own bytes through Read and this, and anything but an exact match is a field this
    /// code has the wrong width for.
    /// </summary>
    public static byte[] HeaderBytes(LodHeader h)
    {
        var w = new Writer();
        WriteHeader(w, h);
        return w.ToArray();
    }

    private static void WriteHeader(Writer w, LodHeader h)
    {
        w.Byte(h.GlobalStrip);
        w.Byte(h.ClassStrip);
        w.Int(h.IsLodCookedOut);
        w.Int(h.Inlined);
        w.Shorts(h.RequiredBones);

        w.Int(h.Sections.Length);
        foreach (var s in h.Sections)
        {
            w.Byte(s.GlobalStrip);
            w.Byte(s.ClassStrip);
            w.Short(s.MaterialIndex);
            w.Int(s.BaseIndex);
            w.Int(s.NumTriangles);
            w.Int(s.RecomputeTangent);
            w.Byte(s.RecomputeMask);
            w.Int(s.CastShadow);
            w.Int(s.VisibleInRayTracing);
            w.UInt(s.BaseVertexIndex);
            // THE SECTION'S CLOTH MAPPING, back exactly as it was read.
            //
            // This used to write a hard 0, on the belief that nothing here has cloth. It does, and
            // a hard 0 has two costs: the header stops round-tripping, so Import refuses every
            // simulated mesh with "this build reads and writes the LOD header differently" -- a
            // sentence about the tool, for a mesh the tool simply did not understand -- and it
            // would silently unbind a garment from its simulation if it ever got past that.
            //
            // Whether the mapping SHOULD survive a geometry replacement is a separate question,
            // decided by the caller and not here: this writes back what it was handed. See
            // Geometry, which hands it an emptied section when the vertices no longer match.
            w.Raw(s.ClothMapping);
            w.UShorts(s.BoneMap);
            w.Int(s.NumVertices);
            w.Int(s.MaxBoneInfluences);
            w.Short(s.CorrespondClothAssetIndex);
            w.Raw(s.ClothingSectionData);
            if ((s.ClassStrip & 1) == 0)
            {
                w.Int(s.DupVert.Length);
                foreach (var v in s.DupVert) w.Int(v);
                w.Int(s.DupVertIndex.Length / 8);
                w.Raw(s.DupVertIndex);
            }
            w.Int(s.Disabled);
        }

        w.Shorts(h.ActiveBoneIndices);
    }

    /// <summary>The streamed block: every buffer the GPU draws from, in the order UE reads them.</summary>
    private static byte[] Geometry(MeshImport.Result mesh, bool hasVertexColours, int numTexCoords,
                                   byte[]? morph = null, byte[]? cloth = null)
    {
        var w = new Writer();
        var n = mesh.Vertices.Count;

        w.Byte(0); w.Byte(0);                                    // strip flags

        // Indices, 16-bit when they fit. Not an optimisation: a 32-bit buffer on a small mesh is
        // correct, and this matches what the cooker writes so a round trip stays comparable.
        var wide = n > ushort.MaxValue;
        w.Byte((byte) (wide ? 4 : 2));
        w.Int(wide ? 4 : 2);
        w.Int(mesh.Indices.Count);
        foreach (var i in mesh.Indices) { if (wide) w.Int(i); else w.UShort((ushort) i); }

        w.Int(12);                                               // position stride
        w.Int(n);
        w.Int(12);
        w.Int(n);
        foreach (var v in mesh.Vertices) { w.Float(v.X); w.Float(v.Y); w.Float(v.Z); }

        w.Byte(0); w.Byte(0);                                    // static mesh VB strip flags
        w.Int(numTexCoords);
        w.Int(n);
        w.Int(1);                                                // full precision UVs
        w.Int(0);                                                // packed tangents
        w.Int(8);                                                // tangent element: two packed normals
        w.Int(n);
        foreach (var v in mesh.Vertices)
        {
            w.UInt(PackNormal(v.TX, v.TY, v.TZ, 1));
            w.UInt(PackNormal(v.NX, v.NY, v.NZ, v.TSign));
        }
        // The element here is ONE UV, not one vertex's worth of them: the count is vertices times
        // sets and the size is eight bytes flat. Writing the size as eight-times-sets passes for a
        // mesh with one UV set and fails on any other, which is the kind of bug that ships.
        w.Int(8);
        w.Int(n * numTexCoords);
        foreach (var v in mesh.Vertices)
            for (var t = 0; t < numTexCoords; t++) { w.Float(v.U); w.Float(v.V); }

        // ------------------------------------------------------------------ skin weights
        //
        // THE STRIDE IS NOT WHATEVER THIS DECLARES. A FIXED-layout buffer stores FOUR influences
        // per vertex, or EIGHT when MaxBoneInfluences is above four -- never any other number.
        // Declaring twelve and writing twelve produced a mesh that parsed, reported twelve, and
        // exploded into black spikes in game: the reader took eight bytes per vertex from a
        // twenty-four byte record, so every vertex after the first read its neighbour's data and
        // the error compounded down the whole buffer.
        //
        // Above eight there is only one correct answer, and it is the one the game's own meshes
        // use: the VARIABLE layout. Each vertex stores exactly as many influences as it has, packed
        // end to end, and a lookup table says where each vertex's run begins and how long it is.
        // That has no fixed ceiling at all, which is what a mesh skinned in a modelling package
        // actually needs.
        var wanted = mesh.Vertices.Count > 0 ? mesh.Vertices[0].Bones.Length : 4;

        // How many influences each vertex ACTUALLY uses -- trailing zero weights are padding, and
        // in the variable layout padding is not stored. At least one, because a vertex with no
        // weights at all still has to be driven by something.
        var used = new int[n];
        for (var i = 0; i < n; i++)
        {
            var v = mesh.Vertices[i];
            var last = 0;
            for (var k = 0; k < v.Weights.Length; k++) if (v.Weights[k] > 0) last = k + 1;
            used[i] = Math.Max(1, last);
        }

        var maxUsed = n == 0 ? 4 : used.Max();
        // Under five, the fixed four-influence layout is exactly right and is what every mod built
        // before this wrote. Left alone so nothing that already works changes by a single byte.
        var variable = maxUsed > 4;
        var influences = variable ? maxUsed : 4;

        // HOW WIDE A BONE INDEX IS, decided by the data and not by a constant.
        //
        // A cooked skin weight buffer stores each bone index in ONE byte unless
        // `bUse16BitBoneIndex` is set, and the importer used to leave it clear -- which capped a
        // section at 256 bones and refused every head in this game. Measured with `--meshlods`:
        // a head is "16-bit bones" with 639 in its first section, a helmet is "8-bit bones" with
        // one. So the width is whatever this mesh actually needs.
        //
        // Asking the VERTICES rather than the bone map keeps every mesh that already works
        // byte-identical: a helmet's indices all fit in a byte, so it still writes the 8-bit form
        // it always did, and the round trip that proves this file correct is undisturbed.
        var wideBones = false;
        foreach (var v in mesh.Vertices)
            foreach (var b in v.Bones) if (b > byte.MaxValue) { wideBones = true; break; }

        // Bytes ONE INFLUENCE occupies: its index, plus its weight.
        var perInfluence = (wideBones ? 2 : 1) + 1;

        w.Byte(0); w.Byte(0);                                    // skin weight data strip flags
        w.Int(variable ? 1 : 0);                                 // bVariableBonesPerVertex
        w.Int(influences);                                       // MaxBoneInfluences
        w.Int(variable ? used.Sum() : n * 4);                    // NumBones -- influences in total
        w.Int(n);                                                // NumVertices
        w.Int(wideBones ? 1 : 0);                                // bUse16BitBoneIndex
        w.Int(0);                                                // 8-bit weights
        w.Int(1);                                                // the blob is measured in bytes

        if (!variable)
        {
            w.Int(n * 4 * perInfluence);                         // four bones and four weights each
            // One record PER VERTEX -- every bone index then every weight -- not all the indices
            // followed by all the weights. Interleaved the other way the mesh still loads, and
            // every vertex is driven by whatever bone the NEXT vertex should have used.
            foreach (var v in mesh.Vertices) { w.Bones(v.Bones, 4, wideBones); w.Raw(v.Weights, 4); }

            w.Byte(0); w.Byte(0);                                // skin weight lookup strip flags
            w.Int(0);                                            // no lookup vertices
            w.Int(4);
            w.Int(0);
        }
        else
        {
            w.Int(used.Sum() * perInfluence);                    // an index and a weight each
            for (var i = 0; i < n; i++)
            {
                var v = mesh.Vertices[i];
                w.Bones(v.Bones, used[i], wideBones);
                w.Raw(v.Weights, used[i]);
            }

            // THE LOOKUP: one entry per vertex, the byte offset of its run shifted up eight with
            // the run's length in the low byte. The offset is measured in BYTES from the start of
            // the blob, so it advances by two per influence -- an index and a weight.
            w.Byte(0); w.Byte(0);                                // skin weight lookup strip flags
            w.Int(n);                                            // NumLookupVertices
            w.Int(4);                                            // one uint each
            w.Int(n);
            var at = 0;
            for (var i = 0; i < n; i++)
            {
                w.UInt((uint) ((at << 8) | (used[i] & 0xFF)));
                // BYTES, so this has to move by whatever an influence costs. It advanced by two
                // unconditionally, which is right only while a bone index is one byte -- with
                // 16-bit indices every vertex after the first would have read its neighbour's run.
                at += used[i] * perInfluence;
            }
        }

        if (hasVertexColours)
        {
            w.Byte(0); w.Byte(0);
            w.Int(4);                                            // stride
            w.Int(n);
            w.Int(4);
            w.Int(n);
            // THE COLOUR THE FILE GAVE EACH VERTEX, in FColor's own order: B, G, R, A.
            //
            // This wrote white for every vertex whatever the file said. White is the value that
            // changes nothing -- a material multiplies by it -- so it is the right DEFAULT and was
            // the wrong answer for a mesh whose author painted it. Black here would silently
            // darken the whole mesh and read as a lighting problem, which is why the default
            // matters as much as the reading.
            foreach (var v in mesh.Vertices) { w.Byte(v.CB); w.Byte(v.CG); w.Byte(v.CR); w.Byte(v.CA); }
        }

        // THE CLOTH VERTEX BUFFER, carried through whole or absent entirely.
        //
        // Its presence is not a flag anywhere in the stream -- the engine decides whether to read
        // one from whether a SECTION carries cloth mapping. So this and the header have to agree,
        // and the caller is what keeps them agreeing: it passes this block and the original
        // sections together, or null and Uncloth(header) together. Passing this without the
        // mapping writes bytes nothing will read; passing the mapping without this makes the
        // engine read the profiles, the ray tracing header and the morph targets out of the
        // middle of a buffer that is not there.
        if (cloth is { Length: > 0 }) w.Raw(cloth);

        w.Int(0);                                                // no skin weight profiles

        for (var i = 0; i < 24; i++) w.Byte(0);                  // ray tracing header, cleared
        w.Int(1); w.Int(0);                                      // and no offline payload

        // THE MORPH TARGETS, kept or dropped -- the one thing here that is not always empty.
        //
        // The block already begins with its own `bSerializeCompressedMorphTargets` flag, so
        // carrying it is the whole write and there is nothing to prepend.
        if (morph is { Length: > 0 }) w.Raw(morph);
        else w.Int(0);                                           // no compressed morph targets

        w.Int(0);                                                // no vertex attribute buffers

        w.Byte(0); w.Byte(1);                                    // half-edge data stripped
        return w.ToArray();
    }

    /// <summary>
    /// A direction in the four bytes UE stores it in.
    ///
    /// The XOR with 0x80808080 is not decoration -- UE stores these biased, and CUE4Parse undoes
    /// it on the way in. A writer that skips it produces normals pointing the opposite way, which
    /// lights the mesh from inside.
    /// </summary>
    private static uint PackNormal(float x, float y, float z, float w)
    {
        // The exact inverse of the decode, which is `byte / 127.5 - 1`. So -1 stores as 0, +1 as
        // 255, and 0 near the middle -- a BIASED byte, not a signed one.
        //
        // The first version wrote `round(v * 127)`, the signed form, and it is wrong in the way
        // that is hardest to catch: it produces plausible bytes, the mesh parses, the vertex count
        // and positions and UVs all verify, and a normal of +1 reads back as -0.004. Every normal
        // on the mesh was near zero. What let it through was a round-trip test that compared
        // positions and UVs and never looked at a normal -- which is why one now does.
        static uint B(float v) =>
            (uint) (byte) Math.Clamp((int) MathF.Round((Math.Clamp(v, -1f, 1f) + 1f) * 127.5f), 0, 255);
        return (B(x) | (B(y) << 8) | (B(z) << 16) | (B(w) << 24)) ^ 0x80808080u;
    }

    /// <summary>
    /// THE ONE RULE for whether a LOD's compressed morph block is carried into the rewritten
    /// mesh, and the bytes to write when it is.
    ///
    /// Public, and shared, because it was nearly not: the importer decides this inside
    /// <see cref="Import"/>, while `--meshswap` -- the command used to PROVE an import is
    /// correct -- calls <see cref="Write"/> directly and so tested a route no mod ever takes. That
    /// is the same shape of fault `--pskcompare` was built to catch. One rule with two callers
    /// means the proof is a proof of the thing that ships.
    ///
    /// Two cases, and the first is most of them:
    ///
    ///   NO DELTAS       carried unconditionally. Measured on both of this game's heads: LOD 0
    ///                   holds ~4,500 batches and LODs 1 to 3 hold the same morph target NAMES and
    ///                   none of the data -- the game drops facial animation past the nearest LOD
    ///                   itself. What is left is per-MORPH (two bounds and two counters each) and
    ///                   addresses no vertex, so no vertex count can invalidate it.
    ///
    ///   WITH DELTAS     only into LOD 0, and only when the imported vertex count matches. Every
    ///                   LOD is given LOD 0's geometry, so a LOD 1 whose own count happened to
    ///                   match would still carry deltas quantised against its own reduced vertex
    ///                   ORDER, which is gone.
    ///
    /// The count is the only test there is -- nothing in a mesh file says whether two meshes of
    /// equal size are the same mesh -- which is exactly as safe as the workflow it is for: editing
    /// the vertices of an existing head rather than skinning a new one.
    /// </summary>
    /// <param name="package">
    /// The bytes the LOD's offsets were MEASURED against. Not the buffer being spliced: a caller
    /// works back to front so this LOD has not moved yet, but the original is the one that cannot
    /// go stale whatever order a caller chooses.
    /// </param>
    /// <param name="notes">Told what was kept or lost. Losing facial animation is never silent.</param>
    public static byte[]? MorphFor(byte[] package, Lod lod, int index, int importedVertices,
                                   List<string>? notes = null)
    {
        if (lod.Morph is not { Length: > 0 } m) return null;

        var deltas = m.Batches > 0 && m.DataBytes > 0;
        if (!deltas || (index == 0 && lod.NumVertices == importedVertices))
        {
            if (deltas)
                notes?.Add($"LOD 0 keeps its facial animation -- {m.Targets} morph target(s) in " +
                           $"{m.Batches:N0} batches, and the imported mesh has the same " +
                           $"{importedVertices:N0} vertices, so the deltas still line up");
            return package[m.At..(m.At + m.Length)];
        }

        notes?.Add($"LOD {index} LOSES its facial animation -- {m.Targets} morph target(s) in " +
                   $"{m.Batches:N0} batches dropped, because the imported mesh has " +
                   $"{importedVertices:N0} vertices and this LOD had {lod.NumVertices:N0}, so the " +
                   "deltas address vertices that no longer exist");
        return null;
    }

    /// <summary>
    /// The LOD's CLOTH, kept or dropped, by exactly the rule the morph targets use.
    ///
    /// A garment's simulation is two blocks that have to agree: per-section mapping in the header
    /// (64 bytes per cloth vertex) and a cloth vertex buffer in the streamed geometry. Both are
    /// indexed BY RENDER VERTEX, so they are meaningful only while the vertices are the ones they
    /// were built against -- and writing one without the other produces a package whose own
    /// reader walks off the end, which is what a first attempt did: header round-tripping
    /// perfectly, geometry reading back as zero vertices.
    ///
    /// So the count is the test, and it is the only test available -- nothing in the file can say
    /// whether two meshes of equal size are the same mesh. That makes this exactly as safe as the
    /// author's own workflow, which is to edit the vertices of an existing garment; it is the same
    /// reasoning, and the same caveat, as <see cref="MorphFor"/>.
    ///
    /// Returns null to mean "write no cloth", and the caller must then also empty the SECTIONS'
    /// mapping -- see <see cref="Uncloth"/>. Half of that is worse than neither half.
    /// </summary>
    public static byte[]? ClothFor(byte[] package, Lod lod, int index, int importedVertices,
                                   List<string>? notes = null)
    {
        if (!lod.HasCloth) return null;

        var cloth = lod.Header.Sections.Sum(s => s.ClothVertices);
        if (lod.NumVertices == importedVertices)
        {
            notes?.Add($"LOD {index} keeps its cloth simulation -- {cloth:N0} simulated vertex(es) " +
                       $"across {lod.Header.Sections.Count(s => s.HasCloth)} section(s), and the " +
                       $"imported mesh has the same {importedVertices:N0} vertices, so the mapping " +
                       "still lines up");
            return package[lod.ClothStart..(lod.ClothStart + lod.ClothLength)];
        }

        notes?.Add($"LOD {index} LOSES its cloth simulation -- {cloth:N0} simulated vertex(es) " +
                   $"dropped, because the imported mesh has {importedVertices:N0} vertices and " +
                   $"this LOD had {lod.NumVertices:N0}, so the mapping addresses vertices that no " +
                   "longer exist. The garment will be rigid, and skinned to its bones as normal.");
        return null;
    }

    /// <summary>
    /// A LOD header with every section's cloth mapping emptied, for when the cloth is being
    /// dropped. Four zero bytes is an empty array of arrays, which is what a mesh that was never
    /// simulated writes -- so the result is a header the engine reads as ordinary.
    /// </summary>
    public static LodHeader Uncloth(LodHeader header) => header with
    {
        Sections = header.Sections
            .Select(s => s.HasCloth ? s with { ClothMapping = new byte[4], ClothVertices = 0 } : s)
            .ToArray(),
    };

    // ------------------------------------------------------------------ the whole operation

    /// <param name="Materials">
    /// How many materials the imported file used, which is how many slots the asset needs. Not
    /// the same as how many it HAS -- the difference is what has to be grown.
    /// </param>
    public sealed record Imported(byte[] Package, int Vertices, int Triangles, List<string> Notes,
                                  int Materials = 1, string? MaterialListWhy = null);

    /// <summary>
    /// What the material array needs in order to be GROWN, gathered from the parsed template.
    ///
    /// Passed in rather than read here because this file works on bytes and knows nothing about
    /// USkeletalMesh -- and because the caller has the parsed mesh in hand anyway.
    /// </summary>
    /// <param name="Have">Slots the asset has now.</param>
    /// <param name="BoneCount">FReferenceSkeleton's own count, which ends the array.</param>
    /// <param name="FirstSlotName">The first slot's name, as an index into the package's names.</param>
    /// <param name="ExtraImports">
    /// The FPackageIndex each ADDED slot should name, in order. Shorter than the number of slots
    /// added means the rest clone the first slot's material and wear its paint.
    /// </param>
    /// <param name="Slots">
    /// The FPackageIndex each slot should name, by slot index -- for slots the asset ALREADY has
    /// as well as ones being added. Absent means "leave it as it is", which for a new slot means
    /// the clone's, and for an existing one means the template's own material.
    /// </param>
    /// <param name="SlotNames">
    /// What each slot should be CALLED, by slot index. Absent means the clone's -- which for
    /// every added slot is the first slot's, and that is the bug this exists to fix: a
    /// customization fragment aims at a material slot BY NAME, so three slots called `MI_TORS`
    /// are three slots the game paints with one mask.
    /// </param>
    public sealed record Growth(int Have, int BoneCount, int FirstSlotName,
                                IReadOnlyList<int> ExtraImports,
                                IReadOnlyDictionary<int, int>? Slots = null,
                                IReadOnlyDictionary<int, string>? SlotNames = null);

    /// <summary>
    /// Put an FBX into a copied mesh package: locate the LODs, rebuild each one, splice them back.
    ///
    /// EVERY LOD gets the same geometry. There is no decimation here, so writing the imported mesh
    /// into LOD 0 alone would leave the game's own helmet at LODs 1 to 3 -- and the mod would work
    /// perfectly until the camera pulled back, at which point the character would silently put the
    /// original helmet on. Four copies of a 3,000-vertex mesh is the honest cost of not having a
    /// reducer, and it is said out loud in the notes rather than left to be discovered.
    /// </summary>
    public static Imported? Import(byte[] package,
                                   IReadOnlyList<int> vertexCounts, IReadOnlyList<int> indexCounts,
                                   int requiredBones, bool hasVertexColours,
                                   IReadOnlyList<string> boneNames,
                                   IReadOnlyList<FbxReader.Mesh> fbx,
                                   IReadOnlyList<MeshImport.Basis> bases,
                                   out string? why, Action<string>? log = null,
                                   Growth? growth = null, ushort[]? boneMap = null,
                                   IReadOnlyList<(float X, float Y, float Z, byte R, byte G, byte B, byte A)>? templateColours = null)
    {
        why = null;

        var lods = Read(package, vertexCounts, indexCounts, requiredBones, hasVertexColours, out why);
        if (lods is null) return null;

        // The reader against the writer, on the template's own bytes, BEFORE anything is replaced.
        // If they disagree the geometry is not the problem and rewriting it would only bury the
        // evidence under a mesh that no longer parses.
        foreach (var lod in lods)
        {
            var asRead = package[lod.Start..(lod.StreamedStart - 4)];
            if (!asRead.AsSpan().SequenceEqual(HeaderBytes(lod.Header)))
            { why = "this build reads and writes the LOD header differently; refusing to touch the geometry"; return null; }
        }

        var built = MeshImport.BuildAll(fbx, bases, boneNames, out why, log);
        if (built is null) return null;

        var notes = built.Notes.ToList();

        // A MASK THE FILE LOST, taken back from the mesh being replaced. Per object, gated twice,
        // and silent when it does nothing -- see MeshImport.RestoreColours.
        if (templateColours is { Count: > 0 })
            foreach (var (start, count) in built.Spans ?? [])
            {
                MeshImport.RestoreColours(built.Vertices, start, count, templateColours, out var cnote);
                if (cnote is not null) notes.Add(cnote);
            }

        // HOW MANY SLOTS THE SECTIONS MAY NAME.
        //
        // The template's own sections say how many the asset has today; the file says how many it
        // needs. When the array can be grown the answer is the file's, and the sections are
        // written naming slots that do not exist YET -- they are made below, before anything sees
        // this package.
        //
        // The order matters and getting it wrong is silent. Growing after the sections were
        // clamped leaves a mesh with two sections both pointing at slot zero: it parses, it counts
        // right, and the piece that was supposed to look different wears the body's paint. Writing
        // sections that name a slot the array never gets is worse -- an index past the end of the
        // array is a mesh the game refuses to load -- so the array is CHECKED here, before a
        // single section is written, and only then are the sections allowed to name it.
        var want = built.Groups.Count == 0 ? 1 : built.Groups.Max(g => g.Material) + 1;
        var slots = lods[0].Header.Sections.Select(x => (int) x.MaterialIndex).DefaultIfEmpty(0).Max() + 1;

        var grow = false;
        string? listWhy = null;
        if (growth is { Have: > 0 } g2 && (want > g2.Have || g2.Slots is { Count: > 0 } ||
                                          g2.SlotNames is { Count: > 0 }))
        {
            if (FindMaterials(package, g2.Have, g2.BoneCount, g2.FirstSlotName, out var findWhy) is null)
            {
                // A MATERIAL THE AUTHOR CHOSE FOR A SLOT IS NOT APPLIED when the list cannot be
                // found (under 8 bones, or not where it is expected). That is not a footnote about
                // extra pieces: the choice is dropped, so it is handed back for the caller to say as
                // what it is -- a serious note naming each material. The extras note stays for the
                // case it is true of, a file that really does use more materials than the mesh has.
                if (g2.Slots is { Count: > 0 }) listWhy = findWhy ?? "it was not found";
                if (g2.Slots is not { Count: > 0 } || want > g2.Have)
                    notes.Add($"the file uses {want} materials and the mesh has {g2.Have} slot(s), and " +
                              $"the array could not be found ({findWhy}) -- the extra piece(s) wear " +
                              "the first material's paint");
            }
            else { grow = true; slots = Math.Max(want, g2.Have); }
        }

        // Back to front, so every offset still describes the bytes it was measured against.
        var output = package;
        for (var i = lods.Count - 1; i >= 0; i--)
        {
            var lod = lods[i];
            // The map the vertices were indexed against, into every LOD -- the same vertices go
            // into all of them. The caller's, when it has one: it is the UNION of the template's
            // sections and section zero's alone is not enough.
            var use = boneMap is { Length: > 0 } ? boneMap : lods[0].Header.Sections[0].BoneMap;

            // THE FACIAL ANIMATION, kept only where it still describes these vertices.
            //
            // Sliced out of `package` rather than `output`: the splices run back to front and this
            // LOD's bytes have not moved yet, but `package` is the buffer the offsets were
            // MEASURED against and it is never written to, so it is the one that cannot go stale.
            //
            // The count is the whole test, and it is a test about identity that only a count can
            // be asked. A LOD 1 whose original count happens to equal the import's would pass it
            // and be wrong -- its own deltas were quantised against its own decimated vertex
            // ORDER, which this import replaces with LOD 0's geometry. So the block is only ever
            // taken from a LOD whose vertices are the ones being written, which in practice means
            // LOD 0 and the author's "edit the verts of an existing head".
            var carried = MorphFor(package, lod, i, built.Vertices.Count, notes);

            // THE CLOTH, by the same rule and sliced out of the same buffer for the same reason.
            //
            // The header and the block move TOGETHER: a kept simulation writes the original
            // sections beside the original buffer, and a dropped one writes emptied sections
            // beside nothing. There is no third combination that loads -- the engine reads the
            // buffer only because a section said it was there.
            var simulated = ClothFor(package, lod, i, built.Vertices.Count, notes);
            var header = simulated is null ? Uncloth(lod.Header) : lod.Header;

            var block = Write(header, built, use.Length,
                              hasVertexColours, lod.NumTexCoords, slots, use, carried, simulated);
            output = ZenPackage.SpliceExportData(output, lod.Start, lod.Length, block);
        }

        if (grow)
        {
            output = Grow(output, growth!, Math.Max(want, growth!.Have), notes, out why, log);
            if (output is null) return null;
        }
        if (lods.Count > 1)
            notes.Add($"the same geometry was written into all {lods.Count} LODs -- there is no " +
                      "reducer yet, so the mesh does not simplify with distance");

        log?.Invoke($"      mesh imported into {lods.Count} LOD(s): {package.Length:N0} -> {output.Length:N0} bytes");
        return new Imported(output, built.Vertices.Count, built.Triangles, notes, want, listWhy);
    }

    /// <summary>
    /// Give the mesh as many material slots as the file used, one clone at a time.
    ///
    /// A cooked section names its material by INDEX, and the game's own clothing meshes all carry
    /// exactly one slot -- so a second section has nowhere to point until this runs.
    ///
    /// A slot with no material of its own KEEPS THE FIRST SLOT'S, which is what makes the extra
    /// piece wear the body's paint rather than nothing at all. That is the honest intermediate
    /// state and it is said out loud in the notes.
    ///
    /// Failing here fails the whole import, deliberately. By this point the sections already name
    /// these slots, so handing back a mesh without them would be handing back one that indexes
    /// past the end of its own array -- and that is a mesh the game refuses to load, discovered on
    /// a launch instead of here.
    /// </summary>
    private static byte[]? Grow(byte[] package, Growth growth, int want, List<string> notes,
                                out string? why, Action<string>? log)
    {
        why = null;
        var bytes = package;

        for (var at = growth.Have; at < want; at++)
        {
            // Re-located every time: each splice moves everything after it, so an offset measured
            // before the previous growth describes the wrong bytes.
            var found = FindMaterials(bytes, at, growth.BoneCount, growth.FirstSlotName, out var findWhy);
            if (found is null)
            { why = $"the mesh needs {want} material slots and slot {at + 1} could not be added: {findWhy}"; return null; }

            var k = at - growth.Have;
            var material = k < growth.ExtraImports.Count
                ? growth.ExtraImports[k]
                : BitConverter.ToInt32(bytes, found.EntryAt(0));

            var grown = AddMaterialSlot(bytes, found, material, out var growWhy);
            if (grown is null)
            { why = $"the mesh needs {want} material slots and slot {at + 1} could not be added: {growWhy}"; return null; }
            bytes = grown;
        }

        // NAMED SLOTS ARE PATCHED LAST, once every slot exists and nothing more will move. A
        // patch is four bytes in place and changes no length, so the array is located once here
        // rather than re-found per slot.
        if (growth.Slots is { Count: > 0 } named)
        {
            var found = FindMaterials(bytes, want, growth.BoneCount, growth.FirstSlotName, out var nWhy);
            if (found is null)
            { why = $"the material slots could not be found to point them at their materials: {nWhy}"; return null; }

            foreach (var (slot, material) in named.OrderBy(x => x.Key))
            {
                if (slot < 0 || slot >= want) continue;
                BitConverter.GetBytes(material).CopyTo(bytes, found.EntryAt(slot));
                log?.Invoke($"      slot {slot} wears material {material}");
            }
        }

        // NAMES LAST OF ALL, because appending one to the package's map moves every byte after
        // the batch and the array would have to be found again anyway.
        if (growth.SlotNames is { Count: > 0 })
        {
            var renamed = NameSlots(bytes, growth, want, out var nameWhy, log);
            if (renamed is null)
            { why = $"the material slots could not be named: {nameWhy}"; return null; }
            bytes = renamed;
        }

        log?.Invoke($"      material slots {growth.Have} -> {want} for the file's {want} material(s)" +
                    (growth.ExtraImports.Count > 0
                        ? $", {growth.ExtraImports.Count} of the mod's own" : ""));

        // COUNTED, not subtracted. Arithmetic on the totals forgot the slots that NAME a game
        // material outright -- they arrive through Slots rather than ExtraImports -- so a torso
        // whose every slot had been given a material of its own still reported two of them
        // wearing the body's paint. A note that is wrong is worse than no note.
        var borrowed = 0;
        for (var at = growth.Have; at < want; at++)
        {
            if (at - growth.Have < growth.ExtraImports.Count) continue;
            if (growth.Slots?.ContainsKey(at) == true) continue;
            borrowed++;
        }
        if (borrowed > 0)
            // It says WHERE TO FIX IT now, because until there was somewhere this note only
            // named a fact somebody could do nothing about -- which is how it came back as "you
            // are forced to only the material slots that the game model had".
            notes.Add($"{borrowed} material slot(s) point at the first material because nothing " +
                      "was named or painted for them, so those sections wear the body's paint -- " +
                      "say what each should wear under \"What its materials wear\"");

        return bytes;
    }

    /// <summary>
    /// Give the slots the names the mod asked for, so a fragment can aim at one of them.
    ///
    /// Two steps, in this order and no other:
    ///
    ///   EVERY NAME IS APPENDED FIRST. `AppendName` grows the name batch and moves everything
    ///   after it, so a name added between two patches would leave the second patch writing at an
    ///   offset that now describes different bytes. All of them, then one rewrite, then the
    ///   array is found once in the bytes that came back.
    ///
    ///   THE ARRAY IS FOUND ONCE, WITH SLOT ZERO'S OLD NAME. Patching a name changes no length,
    ///   so one base offset serves every slot -- which matters, because renaming slot zero is
    ///   allowed here and the landmark that locates the array would be gone afterwards.
    ///
    /// The name field is four bytes at entry+4, of which the top two are a flag this preserves.
    /// The FName NUMBER that follows is left alone: every slot name in this game carries zero,
    /// and a nonzero one would suffix the new name rather than corrupt it -- so it is reported
    /// rather than guessed at.
    /// </summary>
    private static byte[]? NameSlots(byte[] package, Growth growth, int want,
                                     out string? why, Action<string>? log)
    {
        why = null;
        var wanted = growth.SlotNames;
        if (wanted is null || wanted.Count == 0) return package;

        var zen = ZenPackage.Read(package);
        var indices = new Dictionary<int, int>();
        foreach (var (slot, name) in wanted)
        {
            if (slot < 0 || slot >= want || string.IsNullOrWhiteSpace(name)) continue;
            try { indices[slot] = zen.AppendName(name); }
            catch (NotSupportedException e) { log?.Invoke($"      slot {slot} keeps its name: {e.Message}"); }
        }
        if (indices.Count == 0) return package;

        var bytes = zen.Write();

        var found = FindMaterials(bytes, want, growth.BoneCount, growth.FirstSlotName, out var nWhy);
        if (found is null) { why = nWhy; return null; }

        foreach (var (slot, index) in indices.OrderBy(x => x.Key))
        {
            var at = found.EntryAt(slot) + 4;
            var flags = BitConverter.ToUInt32(bytes, at) & 0xC0000000;
            BitConverter.GetBytes(flags | (uint) index).CopyTo(bytes, at);
            log?.Invoke($"      slot {slot} is called {wanted[slot]}");
        }

        return bytes;
    }

    // ------------------------------------------------------------------ small binary helpers

    private sealed class Reader(byte[] data, int at)
    {
        public int At = at;

        public byte Byte() => data[At++];
        public short Short() { var v = BitConverter.ToInt16(data, At); At += 2; return v; }
        public int Int() { var v = BitConverter.ToInt32(data, At); At += 4; return v; }
        public uint UInt() { var v = BitConverter.ToUInt32(data, At); At += 4; return v; }
        public void Skip(int n) { At += n; if (At > data.Length || At < 0) throw new IndexOutOfRangeException(); }

        public byte[] Bytes(int n)
        {
            var v = new byte[n];
            Array.Copy(data, At, v, 0, n);
            At += n;
            return v;
        }

        /// <summary>
        /// Everything read since <paramref name="from"/>, for a block that is carried through
        /// whole rather than understood. Cheaper and safer than re-deriving its length from the
        /// fields that were just walked, which is the arithmetic most likely to drift.
        /// </summary>
        public byte[] Since(int from)
        {
            var v = new byte[At - from];
            Array.Copy(data, from, v, 0, v.Length);
            return v;
        }

        public short[] Shorts()
        {
            var n = Int();
            if (n < 0 || At + n * 2 > data.Length) throw new IndexOutOfRangeException();
            var v = new short[n];
            for (var i = 0; i < n; i++) v[i] = Short();
            return v;
        }

        public ushort[] UShorts()
        {
            var n = Int();
            if (n < 0 || At + n * 2 > data.Length) throw new IndexOutOfRangeException();
            var v = new ushort[n];
            for (var i = 0; i < n; i++) { v[i] = BitConverter.ToUInt16(data, At); At += 2; }
            return v;
        }

        public void Ints() { var n = Int(); Skip(n * 4); }

        /// <summary>
        /// A plain TArray of fixed-width elements: a count, then that many elements. Returns the
        /// count, because for morph targets the counts are the check -- four arrays that must all
        /// report the same number of morphs.
        /// </summary>
        public int Items(int stride)
        {
            var n = Int();
            if (n < 0 || At + (long) n * stride > data.Length) throw new IndexOutOfRangeException();
            Skip(n * stride);
            return n;
        }

        /// <summary>Element size, count, then the payload. Returns the count.</summary>
        public int BulkArray()
        {
            var size = Int();
            var count = Int();
            if (size < 0 || count < 0) throw new IndexOutOfRangeException();
            Skip(size * count);
            return count;
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _ms = new();
        private readonly BinaryWriter _w;
        public Writer() => _w = new BinaryWriter(_ms, Encoding.ASCII, leaveOpen: true);

        public void Byte(byte v) => _w.Write(v);
        public void Short(short v) => _w.Write(v);
        public void UShort(ushort v) => _w.Write(v);
        public void Int(int v) => _w.Write(v);
        public void UInt(uint v) => _w.Write(v);
        public void Float(float v) => _w.Write(v);
        public void Raw(byte[] v) => _w.Write(v);

        /// <summary>The first <paramref name="count"/> bytes, padding with zeroes if it is short.</summary>
        public void Raw(byte[] v, int count)
        {
            for (var i = 0; i < count; i++) _w.Write(i < v.Length ? v[i] : (byte) 0);
        }

        /// <summary>
        /// The first <paramref name="count"/> bone indices, one byte each or two.
        ///
        /// The width is the caller's because it is a property of the whole buffer -- one flag in
        /// the header covers every vertex -- and a buffer that changed width partway through would
        /// parse for exactly as long as it took to reach the first wide index.
        /// </summary>
        public void Bones(ushort[] v, int count, bool wide)
        {
            for (var i = 0; i < count; i++)
            {
                var b = i < v.Length ? v[i] : (ushort) 0;
                if (wide) _w.Write(b); else _w.Write((byte) Math.Min(b, byte.MaxValue));
            }
        }

        public void Shorts(short[] v) { Int(v.Length); foreach (var s in v) Short(s); }
        public void UShorts(ushort[] v) { Int(v.Length); foreach (var s in v) UShort(s); }

        public byte[] ToArray() { _w.Flush(); return _ms.ToArray(); }
    }
}
