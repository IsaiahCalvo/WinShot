using System.Globalization;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>The notations a picked colour can be written in.</summary>
public enum ColorFormat
{
    /// <summary>#RRGGBB — what every colour in this app is stored and shown as.</summary>
    Hex,

    /// <summary>RRGGBB, for fields that supply their own '#'.</summary>
    HexBare,

    /// <summary>#AARRGGBB — the .NET/WPF order, which is what this app's own hex boxes take.</summary>
    HexArgb,

    /// <summary>rgb(255, 59, 48)</summary>
    Rgb,

    /// <summary>hsl(3, 100%, 59%)</summary>
    Hsl,

    /// <summary>oklch(62.8% 0.258 29.23) — perceptually uniform, and what CSS wants now.</summary>
    Oklch,
}

/// <summary>
/// Writes and reads the colour notations a picked colour can take. Pure: no UI type, no
/// clipboard, so the arithmetic can be checked against known values.
/// </summary>
public static class ColorFormatter
{
    public static string Format(SD.Color color, ColorFormat format) => format switch
    {
        ColorFormat.HexBare => $"{color.R:X2}{color.G:X2}{color.B:X2}",
        ColorFormat.HexArgb => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}",
        ColorFormat.Rgb => $"rgb({color.R}, {color.G}, {color.B})",
        ColorFormat.Hsl => FormatHsl(color),
        ColorFormat.Oklch => FormatOklch(color),
        _ => $"#{color.R:X2}{color.G:X2}{color.B:X2}",
    };

    /// <summary>
    /// Reads #RGB, #RRGGBB, #AARRGGBB or any of those without the '#'. Deliberately tolerant
    /// about the hash and about case: this parses what a person typed or what the settings
    /// file happens to hold, not a wire format.
    /// </summary>
    public static bool TryParse(string? text, out SD.Color color)
    {
        color = SD.Color.Empty;
        string value = (text ?? string.Empty).Trim().TrimStart('#');
        if (value.Length is not (3 or 6 or 8))
            return false;
        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint raw))
            return false;

        switch (value.Length)
        {
            case 3:
                // #RGB expands each nibble to a byte, so #F30 is #FF3300 and not #0F0300.
                int r3 = (int)((raw >> 8) & 0xF), g3 = (int)((raw >> 4) & 0xF), b3 = (int)(raw & 0xF);
                color = SD.Color.FromArgb(255, (r3 << 4) | r3, (g3 << 4) | g3, (b3 << 4) | b3);
                return true;
            case 6:
                color = SD.Color.FromArgb(255, (int)((raw >> 16) & 0xFF), (int)((raw >> 8) & 0xFF), (int)(raw & 0xFF));
                return true;
            default:
                color = SD.Color.FromArgb(
                    (int)((raw >> 24) & 0xFF), (int)((raw >> 16) & 0xFF),
                    (int)((raw >> 8) & 0xFF), (int)(raw & 0xFF));
                return true;
        }
    }

    private static string FormatHsl(SD.Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2d;
        double delta = max - min;

        double hue = 0d;
        double saturation = 0d;
        if (delta > 0d)
        {
            saturation = lightness > 0.5d ? delta / (2d - max - min) : delta / (max + min);
            if (max == r) hue = ((g - b) / delta) + (g < b ? 6d : 0d);
            else if (max == g) hue = ((b - r) / delta) + 2d;
            else hue = ((r - g) / delta) + 4d;
            hue *= 60d;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"hsl({Math.Round(hue):0}, {Math.Round(saturation * 100d):0}%, {Math.Round(lightness * 100d):0}%)");
    }

    /// <summary>
    /// sRGB to OKLCH, via Björn Ottosson's OKLab matrices. Perceptually uniform, so two
    /// colours with the same L really do look equally bright -- which is why CSS moved to it
    /// and why Shottr offers it.
    /// </summary>
    private static string FormatOklch(SD.Color color)
    {
        double r = ToLinear(color.R / 255d);
        double g = ToLinear(color.G / 255d);
        double b = ToLinear(color.B / 255d);

        double l = Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        double m = Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        double s = Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));

        double lightness = (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s);
        double a1 = (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s);
        double b1 = (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s);

        double chroma = Math.Sqrt((a1 * a1) + (b1 * b1));
        double hue = chroma < 1e-6 ? 0d : ((Math.Atan2(b1, a1) * 180d / Math.PI) + 360d) % 360d;

        return string.Create(CultureInfo.InvariantCulture,
            $"oklch({lightness * 100d:0.#}% {chroma:0.###} {hue:0.##})");
    }

    private static double ToLinear(double channel) =>
        channel <= 0.04045d ? channel / 12.92d : Math.Pow((channel + 0.055d) / 1.055d, 2.4d);

    private static double Cbrt(double value) =>
        value < 0d ? -Math.Pow(-value, 1d / 3d) : Math.Pow(value, 1d / 3d);
}
