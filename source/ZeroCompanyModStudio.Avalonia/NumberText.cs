using System.Globalization;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// A NUMBER AS TEXT, THE SAME ON EVERY PC.
///
/// A number box in this Studio is read and printed with ONE convention, whatever the machine's
/// regional settings say. The default is the machine's, and the machine's is the problem:
/// <c>double.TryParse("0.5")</c> is 5 on a German Windows (a point is a thousands separator
/// there) and is refused on a French one, while "2,5" is 25 on an English one. Nothing in the
/// program sets a culture, so every box that took the default turned "0.5" into five for a
/// German author and said so only in a log line -- found in review.
///
/// THE POINT IS THE DECIMAL SEPARATOR, because it is what the handbook, the game's own data and
/// every forum post write. A single COMMA is accepted as one as well, since a person on a
/// comma-decimal PC is shown nothing else on their own keyboard and "2,5" can only mean two and
/// a half to them. What is NOT guessed at is a comma that could be a thousands separator
/// ("1,500"): that is refused, with both readings named, because guessing wrong turns 1500 into
/// 1.5 and nothing downstream can tell.
/// </summary>
public static class NumberText
{
    /// <summary>Up to six decimals, no more: see StudioVm.Tables.</summary>
    public const string Wide = "0.######";

    /// <summary>A number as the box shows it: invariant, so it reads back as the same number.</summary>
    public static string Show(double value, string format = Wide)
        => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// What a person typed, as a number -- or <paramref name="why"/>, in words fit for the screen.
    /// Never throws, and never returns infinity or NaN.
    /// </summary>
    public static bool TryParse(string? text, out double value, out string why)
    {
        value = 0;
        why = "";

        var typed = (text ?? "").Trim();
        if (typed.Length == 0) { why = "nothing was typed"; return false; }

        var comma = typed.IndexOf(',');
        var forParsing = typed;
        if (comma >= 0)
        {
            if (typed.Contains('.') || typed.IndexOf(',', comma + 1) >= 0)
            {
                why = $"'{typed}' mixes commas and points, or has more than one comma -- type " +
                      "it as 2.5, with a point and no thousands separator";
                return false;
            }

            // ONE COMMA FOLLOWED BY EXACTLY THREE DIGITS, after one to three digits that do not
            // start with a zero, is what a thousands separator looks like. "2,500" is two and a
            // half to a German and twenty-five hundred to an American, so it is not guessed.
            var whole = typed[..comma].TrimStart('+', '-');
            var fraction = typed[(comma + 1)..];
            if (fraction.Length == 3 && fraction.All(char.IsAsciiDigit) &&
                whole.Length is >= 1 and <= 3 && whole.All(char.IsAsciiDigit) && whole[0] != '0')
            {
                var asDecimal = Show(double.Parse($"{whole}.{fraction}", CultureInfo.InvariantCulture));
                why = $"'{typed}' could mean {asDecimal} or {whole}{fraction} -- type " +
                      $"{asDecimal} for the first, or {whole}{fraction} with no comma for the second";
                return false;
            }

            forParsing = typed.Replace(',', '.');
        }

        if (!double.TryParse(forParsing, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            !double.IsFinite(value))
        {
            value = 0;
            why = $"'{typed}' is not a number";
            return false;
        }

        return true;
    }
}
