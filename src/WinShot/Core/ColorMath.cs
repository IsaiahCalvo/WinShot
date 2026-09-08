using SD = System.Drawing;
using Media = System.Windows.Media;

namespace WinShot.Core;

/// <summary>
/// Contrast maths for the measurement HUD's readout above the marquee: WCAG 2.x, which is
/// what an accessibility audit asks for, and APCA, which is what actually predicts
/// readability on a screen. Both are pure functions of two colours.
/// </summary>
public static class ColorMath
{
    // ---------------------------------------------------------------- WCAG 2.x

    /// <summary>WCAG 2.x relative luminance (sRGB, gamma-expanded).</summary>
    public static double RelativeLuminance(SD.Color c) =>
        (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    /// <summary>WCAG contrast ratio, 1.0 (identical) to 21.0 (black on white). Order-free.</summary>
    public static double ContrastRatio(SD.Color a, SD.Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        double lighter = Math.Max(la, lb);
        double darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    // ---------------------------------------------------------------- APCA

    // APCA-W3 0.1.9 constants. Unlike WCAG this is polarity-aware — dark-on-light and
    // light-on-dark are genuinely different problems — so the sign of Lc is information,
    // and text/background are not interchangeable.
    private const double MainTrc = 2.4;
    private const double NormBg = 0.56, NormTxt = 0.57, RevTxt = 0.62, RevBg = 0.65;
    private const double BlackThreshold = 0.022, BlackClamp = 1.414;
    private const double ScaleBoW = 1.14, ScaleWoB = 1.14;
    private const double LoBoWOffset = 0.027, LoWoBOffset = 0.027;
    private const double DeltaYMin = 0.0005, LoClip = 0.1;

    private static double ApcaY(SD.Color c)
    {
        double y = (0.2126729 * Math.Pow(c.R / 255.0, MainTrc)) +
                   (0.7151522 * Math.Pow(c.G / 255.0, MainTrc)) +
                   (0.0721750 * Math.Pow(c.B / 255.0, MainTrc));
        // Soft-clamp the very dark end: below this, screen black is not linear enough to
        // trust and the raw value would overstate contrast.
        return y < BlackThreshold ? y + Math.Pow(BlackThreshold - y, BlackClamp) : y;
    }

    /// <summary>
    /// APCA lightness contrast (Lc), roughly -108..+106. Positive is dark text on a light
    /// background, negative is light text on a dark one; 0 means "too close to score".
    /// </summary>
    public static double ApcaLc(SD.Color text, SD.Color background)
    {
        double yTxt = ApcaY(text);
        double yBg = ApcaY(background);
        if (Math.Abs(yBg - yTxt) < DeltaYMin)
            return 0;

        double sapc;
        double output;
        if (yBg > yTxt)
        {
            sapc = (Math.Pow(yBg, NormBg) - Math.Pow(yTxt, NormTxt)) * ScaleBoW;
            output = sapc < LoClip ? 0 : sapc - LoBoWOffset;
        }
        else
        {
            sapc = (Math.Pow(yBg, RevBg) - Math.Pow(yTxt, RevTxt)) * ScaleWoB;
            output = sapc > -LoClip ? 0 : sapc + LoWoBOffset;
        }

        return output * 100;
    }

    /// <summary>The readout itself. WCAG reads as a ratio, APCA as a signed Lc number —
    /// they are different scales and must never be mistaken for each other.</summary>
    public static string ContrastLabel(SD.Color foreground, SD.Color background, bool apca) =>
        apca
            ? $"APCA Lc {ApcaLc(foreground, background):0.#}"
            : $"WCAG {ContrastRatio(foreground, background):0.0} : 1";

    // ---------------------------------------------------------------- dominant pair

    /// <summary>
    /// The two colours a region is really made of: the one covering most of it, and the one
    /// with the most contrast against that among everything else present. Sampling is a
    /// bounded grid — a 4K region must not cost a full-resolution histogram on every
    /// mouse-move — and colours are bucketed to 5 bits per channel so anti-aliasing and JPEG
    /// noise land in the same bin as the colour they came from.
    /// </summary>
    public static (SD.Color Background, SD.Color Foreground) DominantPair(
        Func<int, int, SD.Color> sample, SD.Rectangle region, int maxSamplesPerAxis = 48)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (region.Width < 1 || region.Height < 1)
            return (SD.Color.Black, SD.Color.White);

        int stepX = Math.Max(1, region.Width / Math.Max(1, maxSamplesPerAxis));
        int stepY = Math.Max(1, region.Height / Math.Max(1, maxSamplesPerAxis));

        var buckets = new Dictionary<int, (int Count, long R, long G, long B)>();
        int total = 0;
        for (int y = region.Top; y < region.Bottom; y += stepY)
        {
            for (int x = region.Left; x < region.Right; x += stepX)
            {
                SD.Color c = sample(x, y);
                int key = ((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3);
                buckets.TryGetValue(key, out var bucket);
                buckets[key] = (bucket.Count + 1, bucket.R + c.R, bucket.G + c.G, bucket.B + c.B);
                total++;
            }
        }

        if (total == 0 || buckets.Count == 0)
            return (SD.Color.Black, SD.Color.White);

        var ordered = buckets.Values.OrderByDescending(b => b.Count).ToList();
        SD.Color background = Average(ordered[0]);
        if (ordered.Count == 1)
            return (background, background);

        // Ignore specks: a single stray pixel must not become "the text colour".
        int floor = Math.Max(1, total / 100);
        SD.Color foreground = background;
        double best = -1;
        foreach (var bucket in ordered.Skip(1))
        {
            if (bucket.Count < floor && best >= 0)
                break;
            SD.Color candidate = Average(bucket);
            double contrast = ContrastRatio(candidate, background);
            if (contrast > best)
            {
                best = contrast;
                foreground = candidate;
            }
        }
        return (background, foreground);
    }

    private static SD.Color Average((int Count, long R, long G, long B) bucket) =>
        SD.Color.FromArgb(
            (int)(bucket.R / bucket.Count),
            (int)(bucket.G / bucket.Count),
            (int)(bucket.B / bucket.Count));

    // ------------------------------------------------------- the WPF side of the app

    // The editor's label plates and the colour picker's marker are System.Windows.Media
    // colours, and there is exactly one relative-luminance formula in this app. These are
    // overloads, not a second copy: a WPF Color converts to a GDI one and the arithmetic
    // above answers. (0.04045 is the sRGB threshold WCAG 2.1 corrected 2.0's 0.03928 to.)
    private static SD.Color Gdi(Media.Color c) => SD.Color.FromArgb(c.R, c.G, c.B);

    /// <summary>WCAG 2.x relative luminance of a WPF colour. Alpha is ignored.</summary>
    public static double RelativeLuminance(Media.Color c) => RelativeLuminance(Gdi(c));

    /// <summary>WCAG contrast ratio between two opaque WPF colours.</summary>
    public static double ContrastRatio(Media.Color a, Media.Color b) => ContrastRatio(Gdi(a), Gdi(b));

    /// <summary>
    /// Black or white, whichever reads better on <paramref name="background"/>. The worse
    /// of the two is never below 4.58:1, so any plate painted in any colour still carries
    /// legible text — which is the whole reason a label's ink is not the user's own pick.
    /// </summary>
    public static Media.Color TextOn(Media.Color background) =>
        ContrastRatio(Media.Colors.White, background) >= ContrastRatio(Media.Colors.Black, background)
            ? Media.Colors.White
            : Media.Colors.Black;
}
