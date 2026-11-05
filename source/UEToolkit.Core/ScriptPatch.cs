using System.Globalization;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToolkit.Core;

/// <summary>
/// A constant inside a Blueprint function's bytecode, changed in place.
///
/// A cooked Blueprint carries no graph -- only the bytecode Unreal's compiler turned the graph
/// into, stored in each function export as a run of expressions. A literal typed into a node is
/// one of them: an opcode and the value's bytes (a double is 0x37 and eight bytes). Swapping the
/// value for another of the SAME TYPE changes nothing's length, so no jump target, export offset
/// or size anywhere has to move -- which is why this is the first write to Blueprint logic and
/// not a later one.
/// </summary>
public static class ScriptPatch
{
    /// <summary>
    /// What each constant looks like: its opcode, the width of its value, and the name CUE4Parse
    /// decodes it as.
    /// </summary>
    private static readonly Dictionary<string, (byte Op, int Width, string Token)> Shapes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["double"] = (0x37, 8, "EX_DoubleConst"),
            ["float"] = (0x1E, 4, "EX_FloatConst"),
            ["int"] = (0x1D, 4, "EX_IntConst"),
        };

    public static byte[] SetConstant(IFileProvider provider, byte[] raw, string packageName, string function,
                                     string type, string was, string now, out string? why)
    {
        why = null;
        if (!Shapes.TryGetValue(type, out var shape))
        { why = $"'{type}' is not a constant this can change -- double, float or int"; return raw; }
        if (Encode(type, was) is not { } old) { why = $"'{was}' is not a {type}"; return raw; }
        if (Encode(type, now) is not { } neu) { why = $"'{now}' is not a {type}"; return raw; }

        // The function's OWN bytes: a constant with the same value elsewhere in the package -- a
        // default on the class, another function's literal -- is not this edit's business.
        var named = ZenPackage.Read(raw).ExportNames;
        var index = Array.FindIndex(named, n => string.Equals(n, function, StringComparison.Ordinal));
        if (index < 0) { why = $"the package has no export called {function}"; return raw; }
        var (start, length) = ZenPackage.ExportDataRange(raw, index);

        var hits = new List<int>();
        for (var at = start; at + 1 + shape.Width <= start + length; at++)
            if (raw[at] == shape.Op && raw.AsSpan(at + 1, shape.Width).SequenceEqual(old)) hits.Add(at + 1);

        // EXACTLY ONE, or refuse. The radial menu's event graph uses 180 four times -- 180 - offset,
        // 180 + offset and twice more for the dial -- and a request to change "the 180" cannot say
        // which it means. Changing the first one found would be a guess that builds cleanly and
        // does the wrong thing.
        if (hits.Count != 1)
        {
            why = hits.Count == 0
                ? $"{function} holds no {type} constant {was}"
                : $"{function} holds {hits.Count} {type} constants {was}, so which one is meant is not clear";
            return raw;
        }

        var written = (byte[]) raw.Clone();
        neu.CopyTo(written, hits[0]);

        // READ BACK, OR REFUSE. The bytes matched an opcode and a value, but a match can sit
        // inside some other expression's operand, and a write that lands wrong still hands back a
        // package of the right size. What the game runs is the DECODED function, so that is what
        // is compared: the expressions before, with that one constant changed by hand, must be
        // exactly the expressions after.
        return ReadsBack(provider, raw, written, packageName, function, shape.Token, type, was, now, out why)
            ? written : raw;
    }

    private static bool ReadsBack(IFileProvider provider, byte[] before, byte[] after, string packageName,
                                  string function, string token, string type, string was, string now,
                                  out string? why)
    {
        why = null;
        var expected = Script(provider, before, packageName, function);
        var got = Script(provider, after, packageName, function);
        if (expected is null || got is null)
        {
            why = $"{function} does not decode as a Blueprint function {(expected is null ? "before" : "after")} the write";
            return false;
        }

        var old = Json(type, was);
        var match = expected.DescendantsAndSelf().OfType<JObject>()
            .Where(o => (string?) o["Token"] == token && JToken.DeepEquals(o["Value"], old)).ToList();
        if (match.Count != 1)
        {
            why = $"{function} decodes with {match.Count} {token} {was}, not the one its bytes hold";
            return false;
        }

        match[0]["Value"] = Json(type, now);
        if (!JToken.DeepEquals(expected, got))
        {
            why = $"{function} does not read back as that one constant changed -- the write landed somewhere else";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Every constant of one type in a function's code, in the order the decode lists them, or
    /// null when the export is not a Blueprint function. For checking a build after the fact:
    /// what a built package's function holds, read the way the game will read it.
    /// </summary>
    public static List<double>? Constants(IFileProvider provider, byte[] raw, string packageName,
                                          string function, string type)
    {
        if (!Shapes.TryGetValue(type, out var shape)) return null;
        return Script(provider, raw, packageName, function)?.DescendantsAndSelf().OfType<JObject>()
            .Where(o => (string?) o["Token"] == shape.Token && o["Value"] is JValue)
            .Select(o => o["Value"]!.Value<double>())
            .ToList();
    }

    /// <summary>A function's bytecode as CUE4Parse decodes it, or null when it is not a function.</summary>
    private static JArray? Script(IFileProvider provider, byte[] bytes, string packageName, string function)
    {
        // The provider skips every script unless told otherwise, because parsing all of them costs
        // every load; switched on for this one decode and put back as it was found.
        var wasReading = provider.ReadScriptData;
        provider.ReadScriptData = true;
        try
        {
            var f = PropertyAdd.PristineExports(provider, bytes, packageName).OfType<UFunction>()
                .FirstOrDefault(x => string.Equals(x.Name, function, StringComparison.Ordinal));
            return f?.ScriptBytecode is { Length: > 0 } code ? JArray.Parse(JsonConvert.SerializeObject(code)) : null;
        }
        finally { provider.ReadScriptData = wasReading; }
    }

    /// <summary>
    /// A value as the decode spells it. Through JSON text on purpose: a float read back is a
    /// double parsed from "0.1", and comparing it with the float 0.1f would call the same number
    /// different.
    /// </summary>
    private static JToken Json(string type, string text) => JToken.Parse(type.ToLowerInvariant() switch
    {
        "double" => JsonConvert.SerializeObject(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "float" => JsonConvert.SerializeObject(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
        _ => JsonConvert.SerializeObject(int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)),
    });

    private static byte[]? Encode(string type, string text) => type.ToLowerInvariant() switch
    {
        "double" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => BitConverter.GetBytes(d),
        "float" when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) => BitConverter.GetBytes(f),
        "int" when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => BitConverter.GetBytes(i),
        _ => null,
    };
}
