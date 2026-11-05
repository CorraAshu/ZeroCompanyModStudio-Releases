using System.Text.RegularExpressions;

namespace UEToolkit.Core;

/// <summary>
/// Static checks a generated MaxScript must pass before it is written.
///
/// **A `.ms` has no compiler on this side of the fence.** Everything else this toolkit emits is
/// verified against something — PSKs are re-read off disk, PSAs are forward-kinematicked back,
/// level transforms are rebuilt by an independent script — but a MaxScript is text until 3ds Max
/// parses it, and by then the user has spent an import.
///
/// That is not hypothetical. `fn p ax ay az bx by bz ...` failed to parse for the user AFTER a
/// four-million-polygon import, because **`by` is a MAXScript reserved word** — the step in
/// `for i = 1 to 10 by 2`. A reserved identifier is a syntax error at the DECLARATION, so the
/// whole file fails and nothing in it runs; the 59 correct placements after it never had a chance.
///
/// These checks cannot prove a script runs, and nothing here should pretend otherwise. They
/// encode the three ways this project has actually broken one: keyword collisions, `format`
/// arity, and constructs MAXScript does not have. <c>tools/msvalidate.py</c> runs the same rules
/// over a file on disk.
/// </summary>
public static class MaxScriptCheck
{
    /// <summary>
    /// MAXScript keywords. Naming a parameter or local after one does not shadow it — it fails to
    /// parse.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "about", "and", "animate", "as", "at", "by", "case", "catch", "collect", "continue",
        "coordsys", "do", "else", "exit", "fn", "for", "from", "function", "global", "if", "in",
        "local", "macroscript", "mapped", "max", "not", "of", "off", "on", "or", "parameters",
        "persistent", "plugin", "private", "public", "rcmenu", "return", "rollout", "set",
        "struct", "then", "to", "tool", "try", "undo", "utility", "when", "where", "while", "with",
    };

    private static readonly Regex Declaration =
        new(@"^\s*(?:fn|function)\s+\w+((?:\s+\w+)*)\s*=|^\s*local\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex StringLiteral = new(@"""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);
    private static readonly Regex Format =
        new(@"^\s*format\s+(""(?:[^""\\]|\\.)*"")\s*(.*)$", RegexOptions.Compiled);

    /// <summary>Every problem found, empty when the script is clean.</summary>
    public static List<string> Problems(string script)
    {
        var problems = new List<string>();
        var lines = script.Replace("\r\n", "\n").Split('\n');
        var depth = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            // Comments and string bodies are not code: a `--` inside a literal does not start a
            // comment, and a brace inside one does not count toward nesting.
            var code = StringLiteral.Replace(lines[i], "\"\"");
            var cut = code.IndexOf("--", StringComparison.Ordinal);
            if (cut >= 0) code = code[..cut];

            depth += code.Count(c => c == '(') - code.Count(c => c == ')');
            if (depth < 0)
            {
                problems.Add($"line {i + 1}: unbalanced ')' — {lines[i].Trim()}");
                depth = 0;
            }

            if (code.Contains("continue", StringComparison.Ordinal))
                problems.Add($"line {i + 1}: MAXScript has no `continue` — {lines[i].Trim()}");

            if (Declaration.Match(code) is { Success: true } d)
                foreach (var name in (d.Groups[1].Value + " " + d.Groups[2].Value)
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (Reserved.Contains(name))
                        problems.Add($"line {i + 1}: '{name}' is a MAXScript reserved word — {lines[i].Trim()}");

            // `format` takes a BARE % only. No %s, no widths, no precision: '%-14s' prints
            // literally AND eats its argument, so every later placeholder is off by one.
            if (Format.Match(lines[i]) is { Success: true } f)
            {
                var holes = f.Groups[1].Value.Count(c => c == '%');
                var args = f.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (holes != args)
                    problems.Add($"line {i + 1}: {holes} '%' but {args} argument(s) — {lines[i].Trim()}");
            }
        }

        if (depth != 0) problems.Add($"unbalanced parentheses: {depth:+#;-#;0} at end of file");
        return problems;
    }

    /// <summary>Throws rather than let a script that cannot parse reach disk.</summary>
    public static void Assert(string script, string what)
    {
        var problems = Problems(script);
        if (problems.Count == 0) return;
        throw new InvalidOperationException(
            $"{what} would not parse in 3ds Max:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", problems.Take(10)));
    }
}
