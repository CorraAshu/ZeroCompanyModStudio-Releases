namespace UEToolkit.Core;

/// <summary>
/// The bytes one cooked property VALUE is written as, for the types a slot and its traits use.
/// Each is checked against the game's own bytes by --blockcheck. Every name a value refers to must
/// already be in the package's name map -- see <see cref="SlotAdd.EnsureNames"/>.
/// </summary>
public static class ValueBytes
{
    /// <summary>An FName: its stem's index in the name map and UE's number (a "_N" suffix is
    /// stored as the stem and N + 1; "_04" is not split, a leading zero keeps it whole).</summary>
    public static byte[] Name(ZenPackage pkg, string name)
    {
        var (stem, number) = ZenPackage.ParseNumber(name);
        var index = pkg.Names.IndexOf(stem ?? name);
        if (index < 0) throw new InvalidOperationException($"'{stem ?? name}' is not in the package's name map");
        var b = new byte[8];
        BitConverter.GetBytes((uint) index).CopyTo(b, 0);
        BitConverter.GetBytes(stem is null ? 0u : number).CopyTo(b, 4);
        return b;
    }

    /// <summary>A GameplayTag struct: its one member TagName, present and last (`00 03`), then the name.</summary>
    public static byte[] Tag(ZenPackage pkg, string tag) =>
        [.. PropertyAdd.BuildHeader([(0, false)], 1), .. Name(pkg, tag)];

    /// <summary>A GameplayTagContainer: the count, then each tag's name.</summary>
    public static byte[] TagContainer(ZenPackage pkg, IReadOnlyList<string> tags)
    {
        var b = new List<byte>(BitConverter.GetBytes(tags.Count));
        foreach (var t in tags) b.AddRange(Name(pkg, t));
        return b.ToArray();
    }

    /// <summary>A soft object path: package name, asset name, empty sub-path -- 20 bytes.</summary>
    public static byte[] SoftObject(ZenPackage pkg, string path)
    {
        var (package, asset) = PropertyEdit.SplitRef(path);
        if (package is null || asset is null)
            throw new ArgumentException($"'{path}' is not an asset path like /Game/Folder/Thing.Thing");
        return [.. Name(pkg, package), .. Name(pkg, asset), .. BitConverter.GetBytes(0)];
    }

    public static byte[] Str(string value) => PropertyAdd.FStringBytes(value);

    public static byte[] StrArray(IReadOnlyList<string> values)
    {
        var b = new List<byte>(BitConverter.GetBytes(values.Count));
        foreach (var v in values) b.AddRange(Str(v));
        return b.ToArray();
    }

    /// <summary>A LITERAL FText, keyed the way <see cref="PropertyAdd.SetText"/> keys the texts it writes.</summary>
    public static byte[] Text(string packageName, string export, string property, string text) =>
        PropertyAdd.TextBytes(packageName, export, property, text);

    /// <summary>An array of FInstancedStruct: the count, then per entry the struct type (an
    /// FPackageIndex into the import map), the body's size, the body -- the shape measured in this
    /// game's TagDataBank and in CUE4Parse's reader.</summary>
    public static byte[] InstancedStructs(IReadOnlyList<(int StructType, byte[] Body)> items)
    {
        var b = new List<byte>(BitConverter.GetBytes(items.Count));
        foreach (var (type, body) in items)
        {
            b.AddRange(BitConverter.GetBytes(type));
            b.AddRange(BitConverter.GetBytes(body.Length));
            b.AddRange(body);
        }
        return b.ToArray();
    }

    /// <summary>An enum member's byte, from the enum's own table in the mappings; null when the
    /// enum or the member is not there (never a guess).</summary>
    public static byte? EnumByte(CUE4Parse.FileProvider.IFileProvider provider, string enumName, string member)
    {
        if (provider.MappingsContainer?.MappingsForGame?.Enums is not { } enums ||
            !enums.TryGetValue(enumName, out var listed)) return null;
        var want = member.Contains("::", StringComparison.Ordinal) ? member[(member.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : member;
        foreach (var (value, name) in listed)
            if (string.Equals(name, want, StringComparison.Ordinal) || string.Equals(name, member, StringComparison.Ordinal))
                return (byte) value;
        return null;
    }
}
