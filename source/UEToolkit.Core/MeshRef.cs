namespace UEToolkit.Core;

public sealed record MaterialOverride(string SlotName, string? MaterialPath);

/// <summary>
/// Where a static part hangs off the rig. This is the game's own answer to how a helmet or a
/// tank hatch is attached — a bone name and a relative transform — rather than anything
/// inferred from sockets or from how a render looks.
/// </summary>
public sealed record PartTransform(
    float QX, float QY, float QZ, float QW,
    float PX, float PY, float PZ,
    float SX, float SY, float SZ);

/// <summary>One mesh the game loads for a region+permutation.</summary>
public sealed record MeshRef(
    string AssetPath,
    bool IsSkeletal,
    IReadOnlyList<string> ComponentTags,
    IReadOnlyList<MaterialOverride> MaterialOverrides,
    string? ParentBoneName,
    PartTransform? Transform,
    string? ComponentClass = null)
{
    public string DisplayName => Path.GetFileNameWithoutExtension(AssetPath);

    /// <summary>
    /// Shield and overlay meshes, which the game draws see-through.
    ///
    /// The COMPONENT CLASS is the reliable signal — the game picks a translucent variant of its
    /// skeletal-mesh component class rather than the plain one — with the component tag as a
    /// secondary. Drawing these opaque puts a featureless shell over the real geometry: one
    /// weapon's translucent overlay mesh is loaded first and hid a fully textured weapon behind
    /// flat grey.
    /// </summary>
    public bool IsTranslucent =>
        (ComponentClass?.Contains("Translucent", StringComparison.OrdinalIgnoreCase) ?? false) ||
        ComponentTags.Any(t => t.Contains("translucent", StringComparison.OrdinalIgnoreCase));
}
