using System.Numerics;

namespace UEToolkit.App.Rendering;

/// <summary>
/// COLOUR TEMPERATURE TO LINEAR sRGB, in one place because two unrelated things need it.
///
/// The light rig needed it first: 93 of the hub's lights set `bUseTemperature` and a Kelvin value,
/// and a warm-to-cold ramp invented by hand would have thrown away numbers a lighting artist chose
/// deliberately. Then `MM_Emissive` turned out to want the same curve for a different reason --
/// 17 of its 205 instances set `UseColorTemperature` to 1 and leave their colour parameter at
/// white, so the fixture's colour IS its temperature. `MI_CRT_LensSquare_Red_A_Blink` is white at
/// 1100 K; read the colour and ignore the temperature and a red bulb renders white.
///
/// Lives here rather than beside either caller because this repo's recurring bug is two code paths
/// disagreeing about one asset, and a duplicated colour curve is exactly that waiting to happen.
/// `UEToolkit.App/Rendering/*.cs` is compiled into the level editor as well as the CLI, so one
/// copy reaches both.
/// </summary>
internal static class Kelvin
{
    /// <summary>
    /// Kelvin to linear sRGB along the Planckian locus: Kim et al.'s cubic fit for the CIE 1931
    /// chromaticity, then xyY to XYZ to sRGB. The curve UE's own
    /// `FLinearColor::MakeFromColorTemperature` walks.
    ///
    /// NORMALISED so the brightest channel is 1. The temperature decides hue only -- brightness is
    /// the light's intensity or the material's own emissive strength, and folding the two together
    /// makes a 1100 K bulb dim as well as orange, which is not what either parameter says.
    /// </summary>
    public static Vector3 ToRgb(float kelvin)
    {
        double t = Math.Clamp(kelvin, 1000f, 15000f);
        double x = t <= 4000
            ? -0.2661239e9 / (t * t * t) - 0.2343589e6 / (t * t) + 0.8776956e3 / t + 0.179910
            : -3.0258469e9 / (t * t * t) + 2.1070379e6 / (t * t) + 0.2226347e3 / t + 0.240390;
        double y = t <= 2222
            ? -1.1063814 * x * x * x - 1.34811020 * x * x + 2.18555832 * x - 0.20219683
            : t <= 4000
                ? -0.9549476 * x * x * x - 1.37418593 * x * x + 2.09137015 * x - 0.16748867
                : 3.0817580 * x * x * x - 5.87338670 * x * x + 3.75112997 * x - 0.37001483;
        if (y <= 1e-6) return Vector3.One;

        double bigY = 1.0, bigX = x / y * bigY, bigZ = (1.0 - x - y) / y * bigY;
        double r = 3.2404542 * bigX - 1.5371385 * bigY - 0.4985314 * bigZ;
        double g = -0.9692660 * bigX + 1.8760108 * bigY + 0.0415560 * bigZ;
        double b = 0.0556434 * bigX - 0.2040259 * bigY + 1.0572252 * bigZ;
        r = Math.Max(r, 0); g = Math.Max(g, 0); b = Math.Max(b, 0);
        var m = Math.Max(r, Math.Max(g, b));
        return m <= 1e-6 ? Vector3.One : new Vector3((float) (r / m), (float) (g / m), (float) (b / m));
    }
}
