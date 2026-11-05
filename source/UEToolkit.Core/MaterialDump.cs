namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse_Conversion.Materials;
using Newtonsoft.Json;

/// <summary>
/// Every material in the game as the JSON the normal exporter writes -- WITHOUT decoding a single
/// texture.
///
/// A dump of models and textures is missing the piece that says which texture goes in which slot.
/// A .psk names its material sections and nothing else; the binding lives in the material, and
/// the material is where a `Base Mask 1 VT` becomes `T_Rifle_DC_15A_Base_MSK`. Somebody importing
/// the dump into Max or Blender otherwise reconstructs that from filenames and guesses at the
/// masks.
///
/// The Studio's dump dialog never offered materials: its three ticks are Models, Animations and
/// Textures, which map to asset KINDS, and Material is a kind the index tracks (6,906 of them) and
/// the dialog does not list. Mesh exports do not fill the gap either -- batch runs set
/// ExportMaterials = false on purpose, because CUE4Parse re-decodes a shared atlas once per mesh
/// that uses it, measured at 7.2 GB for 250 meshes. So materials fell between the two.
///
/// WHY NOT JUST RUN THE EXPORTER. <see cref="MaterialExporter2"/> builds the whole MaterialData in
/// its CONSTRUCTOR and then, in TryWriteToDir, decodes and writes every referenced texture. That
/// second half is the entire cost: measured at ~1.2 s and ~193 MB of TGA per material, which over
/// the game is hours and hundreds of gigabytes of writes -- for textures a models-and-textures
/// dump already has.
///
/// So this calls the same two lines the constructor does. `GetParams` is CUE4Parse's own parent
/// chain walk and `CMaterialParams2` is its own type, so the JSON is theirs, not a re-implementation
/// that could drift from it.
/// </summary>
public static class MaterialDump
{
    /// <summary>The JSON for one material, or null with a reason. Nothing is decoded.</summary>
    public static string? Read(IFileProvider provider, string packagePath, out string? why)
    {
        why = null;
        try
        {
            var material = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(packagePath))
                        .GetExports().OfType<UMaterialInterface>().FirstOrDefault());
            if (material is null) { why = "no material export in the package"; return null; }

            var data = new MaterialData
            {
                Textures = new Dictionary<string, string>(),
                Parameters = new CMaterialParams2(),
            };
            // AllLayersNoRef is what AssetExporter passes, so a file written here and one written
            // by Export are the same file.
            material.GetParams(data.Parameters, EMaterialFormat.AllLayersNoRef);
            foreach (var (key, value) in data.Parameters.Textures)
                data.Textures[key] = value.GetPathName();

            return JsonConvert.SerializeObject(data, Formatting.Indented);
        }
        catch (Exception e) { why = e.Message; return null; }
    }

    /// <summary>
    /// Write one material's JSON under <paramref name="outDir"/>, mirroring the game's own tree so
    /// it lands beside where the meshes and textures of an existing dump already are.
    /// </summary>
    public static string? Write(IFileProvider provider, string containerPath, string outDir,
                                out string? why)
    {
        var json = Read(provider, containerPath, out why);
        if (json is null) return null;

        var relative = AssetLoader.PackagePath(containerPath) + ".json";
        var file = Path.Combine(outDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json);
        return file;
    }
}
