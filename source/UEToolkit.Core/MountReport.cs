using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace UEToolkit.Core;

public sealed record LockedContainer(string Name, string KeyGuid)
{
    /// <summary>A zero GUID means the container wants the game's main key.</summary>
    public bool WantsMainKey => KeyGuid == Guid.Empty.ToString("N").ToUpperInvariant()
                                || KeyGuid.All(c => c is '0');
}

public sealed record MountReport(
    int MountedContainers,
    int TotalFiles,
    bool HasMappings,
    IReadOnlyList<LockedContainer> Locked,
    bool MappingsRequired = true)
{
    public bool NeedsKeys => Locked.Count > 0;

    public IEnumerable<string> ToLines()
    {
        yield return $"mounted containers : {MountedContainers}";
        yield return $"files              : {TotalFiles:N0}";
        // "will parse as garbage" is true for UE5, where unversioned properties are the default
        // and a .usmap is the only way to name a field. UE4 titles generally ship VERSIONED
        // properties and parse perfectly without one -- some read their level transforms
        // correctly with no mappings at all -- so stating it unconditionally is a false alarm on
        // half the games this tool exists to open, and a false alarm is how a real one gets
        // ignored.
        yield return "mappings           : " + (HasMappings ? "loaded"
            : MappingsRequired ? "*** NONE - assets will parse as garbage ***"
            : "none (this engine version normally ships versioned properties, so probably fine)");
        if (Locked.Count == 0) yield break;

        yield return $"LOCKED containers  : {Locked.Count} - an AES key is required";
        foreach (var c in Locked)
            yield return $"  !! {c.Name}  key guid {c.KeyGuid}";
    }
}

public static class MountReporting
{
    /// <summary>
    /// <paramref name="engine"/> decides whether missing mappings are fatal or merely worth
    /// mentioning: UE5 defaults to unversioned properties and cannot be read without a .usmap,
    /// while UE4 titles generally ship versioned ones and parse fine.
    /// </summary>
    public static MountReport Report(IVfsFileProvider provider, EGame? engine = null)
    {
        // global.utoc holds script objects and is loaded separately, so it always shows up
        // as unloaded. Reporting it would be a false alarm.
        var locked = provider.UnloadedVfs
            .Where(v => !v.Name.StartsWith("global", StringComparison.OrdinalIgnoreCase))
            .Select(v => new LockedContainer(v.Name, v.EncryptionKeyGuid.ToString().ToUpperInvariant()))
            .ToList();

        return new MountReport(
            provider.MountedVfs.Count,
            provider.Files.Count,
            provider.MappingsContainer is not null,
            locked,
            MappingsRequired: engine is null || engine >= EGame.GAME_UE5_0);
    }

    /// <summary>
    /// Distinct key GUIDs the game is asking for. Handy for the UI: one row per key
    /// needed, rather than one per container.
    /// </summary>
    public static IReadOnlyList<string> RequiredKeyGuids(MountReport report) =>
        report.Locked.Select(l => l.KeyGuid).Distinct().ToList();

    /// <summary>
    /// Submit a key to an already-mounted provider and report how many containers it
    /// unlocked, so the UI can say "that key was wrong" instead of failing silently.
    /// </summary>
    public static async Task<int> TrySubmitKeyAsync(IVfsFileProvider provider, string keyGuid, string aesKey)
    {
        if (!IsValidKey(aesKey)) throw new ArgumentException(
            "AES key must be 64 hex characters, optionally prefixed with 0x.");

        var guid = string.IsNullOrWhiteSpace(keyGuid) ? new FGuid() : new FGuid(keyGuid);
        var before = provider.MountedVfs.Count;
        await provider.SubmitKeyAsync(guid, new FAesKey(Normalise(aesKey)));
        return provider.MountedVfs.Count - before;
    }

    public static string Normalise(string key)
    {
        key = key.Trim();
        return key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key : "0x" + key;
    }

    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim();
        if (k.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) k = k[2..];
        return k.Length == 64 && k.All(Uri.IsHexDigit);
    }
}
