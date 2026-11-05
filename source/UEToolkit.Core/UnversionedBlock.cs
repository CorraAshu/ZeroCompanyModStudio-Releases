using CUE4Parse.MappingsProvider;

namespace UEToolkit.Core;

/// <summary>
/// A struct's or an object's properties, serialised UNVERSIONED, from nothing.
///
/// A cooked property block is a header saying which schema slots are present -- runs of skips and
/// values, and a zero mask for values that are present-and-zero -- followed by each present
/// value's bytes in schema order. <see cref="PropertyAdd.BuildHeader"/> already writes that header
/// the way UE does; this lines the values up behind it. Built whole rather than spliced property by
/// property, because a slot has up to eleven fields and every splice would re-plan the header.
///
/// Checked by --blockcheck against the game's own blocks: rebuilt from their decoded values, they
/// come out byte for byte.
/// </summary>
public static class UnversionedBlock
{
    /// <summary>One property: its schema name and its value's bytes -- or null for a ZERO value
    /// (a false bool), which the header records and no bytes follow.</summary>
    public sealed record Value(string Name, byte[]? Bytes);

    public static byte[] Build(Struct schema, IReadOnlyList<Value> values, out string? why)
    {
        why = null;
        var total = schema.CountProperties(true);
        var present = new List<(int Index, byte[]? Bytes)>();
        foreach (var v in values)
        {
            var index = IndexOf(schema, total, v.Name);
            if (index < 0) { why = $"'{v.Name}' is not a property of {schema.Name}"; return []; }
            if (present.Any(p => p.Index == index)) { why = $"'{v.Name}' is given twice"; return []; }
            present.Add((index, v.Bytes));
        }
        present.Sort((a, b) => a.Index.CompareTo(b.Index));

        var bytes = new List<byte>(PropertyAdd.BuildHeader(
            present.Select(p => (p.Index, p.Bytes is null)).ToList(), total));
        foreach (var p in present)
            if (p.Bytes is not null) bytes.AddRange(p.Bytes);
        return bytes.ToArray();
    }

    /// <summary>A whole OBJECT's data: its property block, then the four-byte "no GUID" flag
    /// UObject::Serialize ends with.</summary>
    public static byte[] Object(Struct schema, IReadOnlyList<Value> values, out string? why)
    {
        var block = Build(schema, values, out why);
        return why is null ? [.. block, 0, 0, 0, 0] : [];
    }

    /// <summary>A property's schema index, walking the super chain the way the mappings number it.</summary>
    public static int IndexOf(Struct schema, int total, string name)
    {
        for (var i = 0; i < total; i++)
            if (schema.TryGetValue(i, out var info) && string.Equals(info.Name, name, StringComparison.Ordinal))
                return i;
        return -1;
    }
}
