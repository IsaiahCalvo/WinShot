using System.Drawing.Imaging;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// Turns the configured image format into the one this particular capture is saved as.
/// Everything except <see cref="Auto"/> passes straight through; "Automatic" looks at the
/// pixels and picks PNG for the flat, few-colour images screenshots usually are and JPEG for
/// the photographic ones, where PNG costs several times the bytes for no visible gain.
/// </summary>
public static class ImageFormatChooser
{
    public const string Auto = "auto";

    /// <summary>Above this share of distinct colours the image is photographic enough that
    /// PNG stops paying for itself. Screenshots of text and UI land an order of magnitude
    /// below it; a photo or a gradient-heavy render lands well above.</summary>
    private const double PhotographicColourShare = 0.18;

    /// <summary>Enough samples to characterise an image, few enough to be unnoticeable
    /// beside the encode that follows.</summary>
    private const int MaxSamples = 10_000;

    public static string Resolve(string? configured, SD.Bitmap bmp)
    {
        if (!IsAuto(configured))
            return string.IsNullOrWhiteSpace(configured) ? "png" : configured;

        try
        {
            return Choose(bmp);
        }
        catch (Exception ex)
        {
            // A format choice must never be the thing that loses a capture.
            Log.Error("Automatic image format choice failed; saving as PNG", ex);
            return "png";
        }
    }

    public static bool IsAuto(string? configured) =>
        string.Equals(configured, Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>PNG or JPEG for one bitmap, on its content alone.</summary>
    public static string Choose(SD.Bitmap bmp)
    {
        if (bmp.Width <= 0 || bmp.Height <= 0)
            return "png";

        // JPEG has no alpha channel, so a capture with real transparency — a window grab
        // with rounded corners, an editor export over "None" — is never a candidate.
        if (HasTransparency(bmp) || DistinctColourShare(bmp) < PhotographicColourShare)
            return "png";

        return "jpg";
    }

    /// <summary>
    /// Distinct colours as a share of the pixels sampled, quantised to 5 bits per channel so
    /// that JPEG-ish noise and anti-aliasing on text do not read as a photograph.
    /// </summary>
    private static double DistinctColourShare(SD.Bitmap bmp)
    {
        var seen = new HashSet<int>();
        int sampled = 0;
        foreach ((int x, int y) in SamplePoints(bmp))
        {
            SD.Color c = bmp.GetPixel(x, y);
            seen.Add(((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3));
            sampled++;
        }
        return sampled == 0 ? 0 : (double)seen.Count / sampled;
    }

    private static bool HasTransparency(SD.Bitmap bmp)
    {
        if (!SD.Image.IsAlphaPixelFormat(bmp.PixelFormat))
            return false;

        foreach ((int x, int y) in SamplePoints(bmp))
        {
            if (bmp.GetPixel(x, y).A < 255)
                return true;
        }
        return false;
    }

    /// <summary>An even grid over the whole image — a corner crop would call a screenshot of
    /// a photo viewer flat because its chrome is.</summary>
    private static IEnumerable<(int X, int Y)> SamplePoints(SD.Bitmap bmp)
    {
        long pixels = (long)bmp.Width * bmp.Height;
        int step = pixels <= MaxSamples ? 1 : (int)Math.Ceiling(Math.Sqrt((double)pixels / MaxSamples));
        for (int y = 0; y < bmp.Height; y += step)
        {
            for (int x = 0; x < bmp.Width; x += step)
                yield return (x, y);
        }
    }
}
