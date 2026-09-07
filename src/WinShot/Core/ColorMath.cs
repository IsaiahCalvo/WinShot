using System.Windows.Media;

namespace WinShot.Core;

/// <summary>
/// WCAG 2.x colour arithmetic. Small on purpose: everything here is the sRGB relative
/// luminance formula and the contrast ratio built on it, which is what "is this text
/// readable on that plate?" reduces to everywhere in the app.
/// </summary>
internal static class ColorMath
{
    /// <summary>WCAG 2.x relative luminance, 0 (black) to 1 (white). Alpha is ignored.</summary>
    public static double RelativeLuminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte v)
    {
        double s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary>Contrast ratio between two opaque colours, 1:1 to 21:1.</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Black or white, whichever reads better on <paramref name="background"/>. The worse
    /// of the two is never below 4.58:1, so any plate painted in any colour still carries
    /// legible text — which is the whole reason a label's ink is not the user's own pick.
    /// </summary>
    public static Color TextOn(Color background) =>
        ContrastRatio(Colors.White, background) >= ContrastRatio(Colors.Black, background)
            ? Colors.White
            : Colors.Black;
}
