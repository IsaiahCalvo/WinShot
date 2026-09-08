using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// How a measurement reads. All arithmetic upstream is in device pixels of the frozen
/// bitmap; this is the only place a number turns into text, and the only place it turns
/// into anything but a device pixel.
/// </summary>
internal static class MeasureFormat
{
    /// <summary>A device-independent pixel is 1/96 inch, so a 150 % monitor's "24 px" is
    /// really 16 dip. Windows makes this matter far more than macOS does.</summary>
    public static double ToDip(int px, int dpi) => dpi <= 0 ? px : px * 96.0 / dpi;

    private static int Dip(int px, int dpi) => (int)Math.Round(ToDip(px, dpi), MidpointRounding.AwayFromZero);

    /// <summary>One axis: a bare integer, no "px" suffix, with the dip value underneath when
    /// the pane's own monitor is not at 96 dpi.</summary>
    public static string SpanLabel(int px, int dpi) =>
        dpi == 96 ? px.ToString() : $"{px}\n{Dip(px, dpi)} dip";

    /// <summary>The live cross: "296 × 118", then "197 × 79 dip" off 96 dpi, then the ratio.
    /// U+00D7, because that is the multiplication sign the rest of the selector already uses.</summary>
    public static string CrossLabel(SD.Size px, int dpi)
    {
        string label = $"{px.Width} × {px.Height}";
        if (dpi != 96)
            label += $"\n{Dip(px.Width, dpi)} × {Dip(px.Height, dpi)} dip";
        string ratio = Ratio(px.Width, px.Height);
        return ratio.Length == 0 ? label : label + "\n" + ratio;
    }

    /// <summary>
    /// The ratios a person says out loud, plus their portrait forms. Deliberately short: add
    /// 16:10 and 1.554 stops reading as "three by two" and starts reading as "sixteen by ten",
    /// which is a worse answer to the same question.
    /// </summary>
    private static readonly (int W, int H)[] CommonRatios =
    [
        (1, 1), (4, 3), (3, 2), (16, 9), (21, 9),
        (3, 4), (2, 3), (9, 16), (9, 21),
    ];

    /// <summary>How far off a named ratio may be and still be called that ratio, with a tilde.</summary>
    private const double RatioSnapBand = 0.05;

    /// <summary>
    /// "3 : 2" when the reduction is exact and small enough to mean something, "~16 : 9" when
    /// it is near a ratio a person actually names, "~1.55 : 1" when it is near nothing. The
    /// tilde is the whole point: printing 1.5541 would be precision about the wrong thing.
    /// </summary>
    public static string Ratio(int w, int h)
    {
        if (w <= 0 || h <= 0)
            return string.Empty;

        int divisor = Gcd(w, h);
        int rw = w / divisor;
        int rh = h / divisor;
        if (rw <= 20 && rh <= 20)
            return $"{rw} : {rh}";

        double target = (double)w / h;
        (int W, int H) best = default;
        double bestError = double.MaxValue;
        foreach (var candidate in CommonRatios)
        {
            double error = Math.Abs(((double)candidate.W / candidate.H) - target) / target;
            if (error < bestError)
            {
                bestError = error;
                best = candidate;
            }
        }

        if (bestError <= RatioSnapBand)
            return $"~{best.W} : {best.H}";
        return target >= 1
            ? $"~{target:0.##} : 1"
            : $"~1 : {1 / target:0.##}";
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
