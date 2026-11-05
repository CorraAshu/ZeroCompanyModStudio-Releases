namespace UEToolkit.Core;

/// <summary>
/// The PUBLIC EXPORT HASH a cooked package stores for each export -- the number another
/// package's import matches on. Every export in this game carries one, subobjects included
/// (measured 2026-09-23 on CPD_H_Outfit_Clo002_HELM: all eight, fragments too).
///
/// A NEW export needs its own. Copying its donor's -- what LevelAdd does -- gives two exports one
/// identity. UE computes it in FPackageStoreOptimizer::GetPublicExportHash: CityHash64 over the
/// UTF-16 bytes of the export's path inside its package, lowercased, '/'-joined, without the
/// leading slash. Trusted because --pubhash reproduces the game's stored hashes with it.
/// </summary>
public static class ExportHash
{
    public static ulong Public(string packageRelativePath) =>
        CUE4Parse.Utils.CityHash.CityHash64(
            System.Text.Encoding.Unicode.GetBytes(packageRelativePath.ToLowerInvariant()));

    /// <summary>
    /// Export <paramref name="exportIndex"/>'s path inside its package: its outers' names and its
    /// own, '/'-joined -- "CPD_H_Outfit_Clo002_HELM/CustomizationFragmentFoley_0".
    /// </summary>
    public static string PathOf(ZenPackage pkg, int exportIndex)
    {
        var names = pkg.ExportNames;
        var outers = pkg.ExportOuters;
        var parts = new List<string>();
        for (var i = exportIndex; i >= 0 && parts.Count <= names.Length; i = outers[i])
            parts.Insert(0, names[i]);
        return string.Join('/', parts);
    }
}
