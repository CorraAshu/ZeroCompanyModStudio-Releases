namespace UEToolkit.Core;

/// <summary>
/// Give a duplicated asset an identity of its own.
///
/// Some assets carry an FGuid that IS their identity rather than a description of it -- a
/// CustomizationDefinition's <c>ID</c> is the example that forced this. Copy one and two assets
/// claim the same id, which is the same class of fault as two parts sharing a PrimaryAssetName:
/// everything builds, everything installs, and one of them silently loses.
///
/// Found by CONTENT, like every other edit here. The asset registry prints the guid, so the sixteen
/// bytes to look for are known before the search starts -- and requiring exactly one occurrence is
/// what separates "I found the id" from "I found sixteen bytes that happen to match".
/// </summary>
public static class GuidEdit
{
    /// <summary>
    /// The bytes an FGuid occupies, from the hex the registry prints.
    ///
    /// UE writes a guid as four uint32s and prints them as four eight-digit hex groups in order,
    /// so the string maps to the words directly -- and each word is stored little-endian, which is
    /// the step that is easy to miss and produces a pattern that never matches.
    /// </summary>
    public static byte[]? Bytes(string hex)
    {
        var clean = hex.Replace("-", "").Trim();
        if (clean.Length != 32) return null;

        var bytes = new byte[16];
        for (var word = 0; word < 4; word++)
        {
            if (!uint.TryParse(clean.AsSpan(word * 8, 8), System.Globalization.NumberStyles.HexNumber,
                               null, out var value)) return null;
            BitConverter.GetBytes(value).CopyTo(bytes, word * 4);
        }
        return bytes;
    }

    /// <summary>The hex form the registry stores, from the bytes.</summary>
    public static string Hex(byte[] guid)
    {
        var text = new System.Text.StringBuilder(32);
        for (var word = 0; word < 4; word++)
            text.Append(BitConverter.ToUInt32(guid, word * 4).ToString("X8"));
        return text.ToString();
    }

    /// <summary>
    /// A guid derived from a name rather than a random one.
    ///
    /// Deterministic on purpose: a mod rebuilt twice should produce the same asset, and a random
    /// id would mean every rebuild is a different thing to anything that remembered the old one.
    /// Derived from the package name, which is already required to be unique across installed
    /// mods -- so two mods cannot collide here without colliding somewhere louder first.
    /// </summary>
    public static byte[] From(string packageName)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(packageName));
        return hash[..16];
    }

    /// <summary>
    /// Replace one guid with another. Sixteen bytes for sixteen, so nothing moves.
    ///
    /// Refuses on anything but exactly one occurrence: none means the guid is not stored the way
    /// this expects, and more than one means the caller cannot say which is the identity.
    /// </summary>
    public static byte[] Set(byte[] raw, byte[] was, byte[] now, out int at, out string? why)
    {
        at = -1;
        why = null;
        if (was.Length != 16 || now.Length != 16) { why = "a guid is sixteen bytes"; return raw; }

        var found = new List<int>();
        var headerSize = (int) BitConverter.ToUInt32(raw, 4);
        for (var i = headerSize; i + 16 <= raw.Length; i++)
            if (raw.AsSpan(i, 16).SequenceEqual(was)) found.Add(i);

        if (found.Count == 0) { why = $"{Hex(was)} does not appear in this package's export data"; return raw; }
        if (found.Count > 1)
        { why = $"{Hex(was)} appears {found.Count} times; refusing to guess which is the identity"; return raw; }

        var edited = (byte[]) raw.Clone();
        now.CopyTo(edited, found[0]);
        at = found[0];
        return edited;
    }
}
