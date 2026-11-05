namespace UEToolkit.Core;

/// <summary>
/// The hash a Zen package uses to reference an engine class.
///
/// Packages inside a .ucas do not name their imports. A part definition does not say
/// "/Script/BitReactorCore.CustomizationPartDefinition"; it stores a 62-bit hash of that path, and
/// the runtime looks it up in the global script object table. So writing a Zen package is
/// impossible without this, and it was the last hash that could have been an unknown.
///
/// It is not: it is the same CityHash64 the name batch uses, over different input. UE's
/// GenerateImportHashFromObjectPath lowercases the path, turns '.' and ':' into '/', hashes the
/// UTF-16 bytes, and clears the top two bits (which is what leaves 62).
///
/// Two differences from <see cref="NameHash"/> that matter, and both change the answer completely:
/// the bytes are UTF-16 here and UTF-8 there, and the separators are flattened first. Verified by
/// `--scripthash` against the game's own global script object table, which is keyed by these
/// hashes and so answers for itself.
/// </summary>
public static class ScriptObjectHash
{
    /// <summary>The top two bits are the FPackageObjectIndex type field, so the hash never uses them.</summary>
    private const ulong IndexMask = (1UL << 62) - 1UL;

    public static ulong Of(string objectPath)
    {
        var chars = objectPath.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            // Flattened, not stripped: "/Script/Engine.Foo" and "/Script/Engine:Foo" hash alike,
            // which is why the caller need not know which separator a given depth uses.
            if (c is '.' or ':') chars[i] = '/';
            else if (c is >= 'A' and <= 'Z') chars[i] = (char) (c + 32);
        }

        // UTF-16 little-endian, because UE hashes the TCHAR buffer directly. Hashing UTF-8 here
        // gives a wrong answer for every path, including the pure-ASCII ones.
        var bytes = System.Text.Encoding.Unicode.GetBytes(chars);
        return NameHash.CityHash64(bytes) & IndexMask;
    }
}
