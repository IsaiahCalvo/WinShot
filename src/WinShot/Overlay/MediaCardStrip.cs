using System.Globalization;
using SD = System.Drawing;

namespace WinShot.Overlay;

/// <summary>
/// The strip along the bottom of a recording's card: camcorder glyph and duration on the
/// left, file size on the right. A finished recording and a screenshot used to look
/// identical sitting in the corner, and the poster frame of a screen recording is often a
/// still-looking screen, so there was nothing to tell them apart.
///
/// Geometry and text are pure so they can be asserted without a window.
/// </summary>
internal static class MediaCardStrip
{
    /// <summary>Strip height in logical pixels — two lines of the card's smallest face.</summary>
    private const int HeightLogical = 17;

    /// <summary>How far above the strip the darkening starts, so it fades in rather than banding.</summary>
    private const int GradientLogical = 13;

    /// <summary>Inset of the glyph and the size text from the card's edges.</summary>
    private const int PadLogical = 6;

    internal static int Scale(int logical, int dpi) =>
        Math.Max(1, (int)Math.Round(logical * Math.Clamp(dpi, 96, 480) / 96d));

    /// <summary>The strip itself, flush with the bottom of the card.</summary>
    internal static SD.Rectangle Bounds(SD.Rectangle card, int dpi)
    {
        int height = Math.Min(card.Height, Scale(HeightLogical, dpi));
        return new SD.Rectangle(card.Left, card.Bottom - height, card.Width, height);
    }

    /// <summary>The band the dark gradient occupies, which starts above the strip.</summary>
    internal static SD.Rectangle GradientBounds(SD.Rectangle card, int dpi)
    {
        SD.Rectangle strip = Bounds(card, dpi);
        int extra = Math.Min(card.Height - strip.Height, Scale(GradientLogical, dpi));
        return new SD.Rectangle(strip.Left, strip.Top - extra, strip.Width, strip.Height + extra);
    }

    internal static int Padding(int dpi) => Scale(PadLogical, dpi);

    /// <summary>
    /// "3s" and "53s" under a minute, "1:23" over it, "1:02:03" over an hour. Short enough
    /// to leave the file size room on a 160 px card.
    /// </summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        if (duration.TotalSeconds < 60)
            return $"{(int)Math.Round(duration.TotalSeconds)}s";
        if (duration.TotalHours < 1)
            return $"{duration.Minutes}:{duration.Seconds:00}";
        return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    /// <summary>
    /// "812 B", "84 KB", "6.8 MB", "1.2 GB". One decimal only where it carries information —
    /// "6.8 MB" tells you something "6.83 MB" does not.
    /// </summary>
    internal static string FormatSize(long bytes)
    {
        if (bytes < 0)
            bytes = 0;
        if (bytes < 1024)
            return $"{bytes} B";

        double value = bytes / 1024d;
        foreach (string unit in new[] { "KB", "MB", "GB" })
        {
            if (value < 1024d || unit == "GB")
            {
                string text = value < 10d && unit != "KB"
                    ? value.ToString("0.0", CultureInfo.InvariantCulture)
                    : Math.Round(value).ToString(CultureInfo.InvariantCulture);
                return $"{text} {unit}";
            }
            value /= 1024d;
        }
        return $"{bytes} B";
    }
}
