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

    /// <summary>The band the whole scrim occupies: the lead-in gradient plus the strip.</summary>
    internal static SD.Rectangle GradientBounds(SD.Rectangle card, int dpi)
    {
        SD.Rectangle strip = Bounds(card, dpi);
        return new SD.Rectangle(
            strip.Left,
            strip.Top - LeadHeight(card, dpi),
            strip.Width,
            strip.Height + LeadHeight(card, dpi));
    }

    /// <summary>
    /// The ramp above the strip, so the darkening fades in rather than banding. It stops at
    /// the strip's top edge: the text lives in the UPPER half of the strip, so running one
    /// ramp all the way through the strip left the digits sitting at roughly 60 % of the
    /// terminal alpha — 3.1:1 over a white capture, where WCAG AA wants 4.5:1. The strip
    /// itself takes a flat scrim instead, which reaches the floor everywhere the text is.
    /// </summary>
    internal static int LeadHeight(SD.Rectangle card, int dpi)
        => Math.Min(card.Height - Bounds(card, dpi).Height, Scale(GradientLogical, dpi));

    internal static int Padding(int dpi) => Scale(PadLogical, dpi);

    /// <summary>
    /// Where the strip's text may start and end. The hover buttons are painted over the
    /// strip — the bottom action row overlaps its top 8 logical px at every card size — so
    /// the duration and the file size have to sit clear of the columns those buttons
    /// occupy, or hovering hides both. Returns an empty run when nothing fits.
    /// </summary>
    internal static (int Left, int Right) TextRun(
        SD.Rectangle strip,
        IEnumerable<SD.Rectangle> obstacles,
        int padding)
    {
        int left = strip.Left + padding;
        int right = strip.Right - padding;
        int middle = strip.Left + strip.Width / 2;
        foreach (SD.Rectangle obstacle in obstacles)
        {
            if (!obstacle.IntersectsWith(strip))
                continue;
            if (obstacle.Left + obstacle.Width / 2 < middle)
                left = Math.Max(left, obstacle.Right + padding);
            else
                right = Math.Min(right, obstacle.Left - padding);
        }
        return right > left ? (left, right) : (0, 0);
    }

    /// <summary>
    /// "3s" and "53s" under a minute, "1:23" over it, "1:02:03" over an hour. Short enough
    /// to leave the file size room on a 160 px card.
    /// </summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        // Round to whole seconds FIRST: picking the branch on the unrounded value and then
        // rounding printed "60s" for anything from 59.5 s up instead of rolling over to 1:00.
        var whole = TimeSpan.FromSeconds(Math.Round(duration.TotalSeconds));
        if (whole.TotalSeconds < 60)
            return $"{(int)whole.TotalSeconds}s";
        if (whole.TotalHours < 1)
            return $"{whole.Minutes}:{whole.Seconds:00}";
        return $"{(int)whole.TotalHours}:{whole.Minutes:00}:{whole.Seconds:00}";
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
            // Round FIRST, then decide whether the unit still holds — the same fix the
            // duration above needed. Choosing on the raw value and rounding afterwards
            // printed "1024 KB" for anything in the last half-kilobyte below a megabyte.
            bool oneDecimal = value < 10d && unit != "KB";
            double rounded = oneDecimal ? Math.Round(value, 1) : Math.Round(value);
            if (rounded < 1024d || unit == "GB")
                return $"{rounded.ToString(oneDecimal ? "0.0" : "0", CultureInfo.InvariantCulture)} {unit}";
            value /= 1024d;
        }
        return $"{bytes} B";
    }
}
