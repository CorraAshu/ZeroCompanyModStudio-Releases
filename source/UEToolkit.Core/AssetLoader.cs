using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;

namespace UEToolkit.Core;

/// <summary>
/// Loads assets by package path.
///
/// Do NOT build an object path as "&lt;file&gt;.&lt;filename&gt;" — that assumes the primary
/// export is named after the file. When it is not, CUE4Parse resolves the name elsewhere and
/// silently hands back a completely different asset. Load the package and pick the export.
/// </summary>
public static class AssetLoader
{
    /// <summary>
    /// Serialises package loading across threads.
    ///
    /// **CUE4Parse's package cache is not safe to race on**, and the failure is not an exception —
    /// it is an `AccessViolationException` from native code, which .NET Core does not permit
    /// catching. It has cost this project twice already: a 74-minute whole-game export died to it
    /// (fixed for the material walk by `PskUv.ResolveLock`), and the app vanished with no crash
    /// log when a character was opened while the animation library was still
    /// scanning in the background. The breadcrumb trail showed both threads inside CUE4Parse in
    /// the same 80 ms.
    ///
    /// So it is a lock rather than a hope. Everything here is I/O and parsing that already
    /// overlaps with rendering and export work, and the background scans are background precisely
    /// because nobody is waiting on them — serialising them costs latency nobody sees, against a
    /// crash that cannot even be reported.
    ///
    /// **Use <see cref="Read{T}"/> for any provider call that is not already behind this**, which
    /// includes `provider.LoadPackage` called directly.
    /// </summary>
    private static readonly Lock PackageGate = new();

    /// <summary>
    /// The gate itself, for callers <see cref="Read{T}"/> cannot express — an `out` parameter, or a
    /// scope that has to span more than one call.
    ///
    /// **There must be exactly one of these.** `PskUv` guarded the material walk with a lock of its
    /// own and this guarded package loading, and the note above cheerfully described that as "fixed
    /// for the material walk by PskUv.ResolveLock" — but two disjoint critical sections over one
    /// unsafe cache do not exclude each other at all. A material walk on one thread ran flat against
    /// a package load on another for as long as both locks existed, which is exactly the race both
    /// were written to stop.
    /// </summary>
    public static Lock Gate => PackageGate;

    /// <summary>Run a provider read under the package lock. See <see cref="PackageGate"/>.</summary>
    public static T Read<T>(Func<T> read)
    {
        lock (PackageGate) return read();
    }

    /// <summary>
    /// Throw unless the caller already holds <see cref="Gate"/>.
    ///
    /// Every real instance of the export non-determinism has been the same shape: a second copy of
    /// an unsafe operation, written without the lock the first copy documents at length. Two locks
    /// over one cache (`PskUv.ResolveLock` beside this one). Two LOD rules, viewport against export.
    /// Two HLOD tests, component class against owning actor. A comment saying "must be gated" is
    /// what all of those had, and it did not survive the next person adding a caller — which here
    /// is always me, six weeks later.
    ///
    /// So the requirement is checked rather than described. It costs a field read on an operation
    /// that already parses a package, and it converts "a material silently resolves to nothing in
    /// one export out of five" into an exception naming the caller, at the call.
    /// </summary>
    public static void AssertHeld(string operation)
    {
        if (!PackageGate.IsHeldByCurrentThread)
            throw new InvalidOperationException(
                $"{operation} touches CUE4Parse's package cache and must run under " +
                "AssetLoader.Gate. Wrap the call in `lock (AssetLoader.Gate)` or AssetLoader.Read. " +
                "Racing it does not throw where it happens - it makes a material resolve to " +
                "nothing, and the mesh ships with un-remapped UVs.");
    }

    /// <summary>
    /// A mesh's materials, resolved. **The only material walk in the toolkit.**
    ///
    /// There were two identical private copies of this — one in <see cref="PskUv"/> under the gate,
    /// one in <see cref="MeshTextures"/> with no lock at all — and the ungated one runs inside
    /// `--exportall`, which is also what `--regress` shells out to. That is the export
    /// non-determinism's remaining hole: `m.Load&lt;UMaterialInterface&gt;()` walks the import table
    /// and reaches the same cache a background library build is loading packages into, an empty
    /// result gets CACHED, and every later reader of that material sees nothing. It explains the
    /// two things about this bug that never fitted a simple race: why re-reading does not recover
    /// it, and why a different set of meshes is affected each run.
    ///
    /// Returns a LIST, not the lazy `Select`. A lazy sequence handed back from inside a lock is
    /// enumerated by the caller outside it, which moves the race rather than closing it — the same
    /// slip <see cref="LoadPrimary"/> calls out for `GetExports()`.
    /// </summary>
    public static List<UMaterialInterface?> MaterialsOf(UObject? mesh)
    {
        AssertHeld("resolving a mesh's materials");
        return mesh switch
        {
            USkeletalMesh sk => sk.Materials.Select(m => m?.Load<UMaterialInterface>()).ToList(),
            UStaticMesh st => st.Materials.Select(m => m?.Load<UMaterialInterface>()).ToList(),
            _ => [],
        };
    }

    /// <summary>Strip the extension to get the package path CUE4Parse expects.</summary>
    public static string PackagePath(string filePath)
    {
        var dot = filePath.LastIndexOf('.');
        return dot > 0 ? filePath[..dot] : filePath;
    }

    /// <summary>
    /// The asset a file represents: the first mesh/animation export, falling back to the
    /// export named after the file, then the first export of any kind.
    /// </summary>
    public static UObject? LoadPrimary(IFileProvider provider, string filePath) => Read(() =>
    {
        var pkg = provider.LoadPackage(PackagePath(filePath));
        // GetExports() is lazy, so the enumeration -- not just LoadPackage -- is what touches the
        // cache. Materialise it inside the lock or the race is merely moved.
        var exports = pkg.GetExports().ToArray();
        if (exports.Length == 0) return null;

        var wanted = Path.GetFileNameWithoutExtension(filePath);

        return exports.FirstOrDefault(e => e is USkeletalMesh or UStaticMesh)
               ?? exports.FirstOrDefault(e => e is UAnimSequence or UAnimMontage or UAnimComposite)
               ?? exports.FirstOrDefault(e => e.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
               ?? exports[0];
    });

    /// <summary>
    /// The same load, but from the GAME'S OWN container rather than whatever wins the lookup.
    ///
    /// <see cref="LoadPrimary"/> asks the provider by NAME, and the provider mounts every installed
    /// mod, so a `_P` patch container wins. For anything that merely LOOKS at an asset that is
    /// fine. For the mesh import it was not: it took the template's LOD vertex counts from the
    /// provider and the BYTES it writes into from AssetCopy.PristineRead, so once a mod had
    /// replaced that mesh the counts described the mod's copy and the geometry the game's, and the
    /// import refused itself --
    ///
    ///     SKIPPED .../SK_HAAM_Hum_Trick_Head: LOD 1 parsed 13,671 vertices, the mesh says 32,730
    ///
    /// -- because a head mod writes the author's mesh into all four LODs, so every LOD in its copy
    /// declares LOD 0's count. Reported from the field, and the reporter had done nothing wrong.
    ///
    /// **NOT PristineExports, which cannot do this.** That builds an IoPackage from the raw bytes
    /// with no bulk archives, and a skeletal mesh does not construct at all without them -- MEASURED
    /// on three meshes, all three "NOT a skeletal mesh". Reading the bytes is enough for a part's
    /// property bag and is not enough for geometry, which is the whole difference between the two
    /// jobs. So this goes through the PROVIDER, which resolves payloads properly, and only changes
    /// WHICH FILE it is pointed at.
    ///
    /// Falls back to <see cref="LoadPrimary"/> when no unpatched container holds the path, because
    /// "the game does not ship this" is a real answer for an asset a mod invented, and refusing
    /// there would break authoring against your own copied asset.
    /// </summary>
    public static UObject? LoadPristinePrimary(IFileProvider provider, string filePath) => Read(() =>
    {
        if (provider is not IVfsFileProvider vfs) return null;

        // THE CANONICAL PATH FIRST, because callers hand this both forms: `--meshlods` passes a
        // real file path out of the index and the mesh import passes a package name with no
        // extension. The provider's own indexer resolves either, and the entry it returns -- the
        // mod's, when one is installed -- carries the path that every container keys its files by,
        // which is the same path in the game's container and in the mod's.
        string wanted;
        try { wanted = provider[PackagePath(filePath)].Path; }
        catch (Exception) { return null; }

        foreach (var reader in vfs.MountedVfs)
        {
            if (Path.GetFileNameWithoutExtension(reader.Name)
                    .EndsWith("_P", StringComparison.OrdinalIgnoreCase)) continue;
            if (!reader.Files.TryGetValue(wanted, out var file)) continue;

            var fromGame = provider.LoadPackage(file).GetExports().ToArray();
            if (fromGame.Length == 0) return null;

            var name = Path.GetFileNameWithoutExtension(filePath);
            return fromGame.FirstOrDefault(e => e is USkeletalMesh or UStaticMesh)
                   ?? fromGame.FirstOrDefault(e => e is UAnimSequence or UAnimMontage or UAnimComposite)
                   ?? fromGame.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? fromGame[0];
        }
        return null;
    }) ?? LoadPrimary(provider, filePath);

    public static T? Load<T>(IFileProvider provider, string filePath) where T : UObject => Read(() =>
    {
        var pkg = provider.LoadPackage(PackagePath(filePath));
        return pkg.GetExports().OfType<T>().FirstOrDefault();
    });
}
