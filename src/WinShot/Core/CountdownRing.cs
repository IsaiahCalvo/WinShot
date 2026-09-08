using System.Drawing.Drawing2D;
using System.Drawing.Text;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// The countdown badge drawn by both the recording countdown and the capture self-timer —
/// one ring that depletes as the seconds run out, and one digit that pops as it changes.
/// The two windows had the same static ring and the same 1 Hz digit swap copied between them;
/// this is that drawing, once, plus the pure arithmetic behind it so the sweep and the pop can
/// be pinned by a test instead of watched.
///
/// The clock stays with the caller: a countdown is a real delay, not decoration, so it must
/// still count down when the user has animation turned off system-wide. Only the sweep and the
/// pop ride <see cref="Motion"/>, and with animation off they simply step once per second.
/// </summary>
internal static class CountdownRing
{
    /// <summary>How much of one second the digit's pop occupies.</summary>
    public const double DigitPopFraction = 0.22;

    /// <summary>How much bigger the digit starts each second.</summary>
    public const double DigitPopScale = 1.18;

    /// <summary>Arc still to sweep, in degrees, for a countdown <paramref name="progress"/>
    /// of the way through. Full circle at the start, nothing left at the end.</summary>
    public static float SweepDegrees(double progress) =>
        (float)(360.0 * (1.0 - Math.Clamp(progress, 0d, 1d)));

    /// <summary>Fraction of the whole countdown that has elapsed.</summary>
    public static double Progress(int totalSeconds, int remainingSeconds, double secondFraction)
    {
        if (totalSeconds <= 0)
            return 1d;

        double elapsed = totalSeconds - remainingSeconds + Math.Clamp(secondFraction, 0d, 1d);
        return Math.Clamp(elapsed / totalSeconds, 0d, 1d);
    }

    /// <summary>Size multiplier for the digit this far into its second: it lands from
    /// <see cref="DigitPopScale"/> onto 1 and then holds, so the change reads as a beat
    /// rather than as a swap.</summary>
    public static double DigitScale(double secondFraction)
    {
        double fraction = Math.Clamp(secondFraction, 0d, 1d);
        if (fraction >= DigitPopFraction)
            return 1d;

        double landed = Motion.EaseOutCubic(fraction / DigitPopFraction);
        return DigitPopScale + ((1d - DigitPopScale) * landed);
    }

    /// <summary>
    /// Paints the badge: a full-circle track, the accent arc that remains, and the digit
    /// centred in <paramref name="digitBox"/> at its current pop scale. The caller owns the
    /// window's background and anything else it wants below the badge.
    /// </summary>
    public static void Draw(
        SD.Graphics g,
        SD.Size clientSize,
        int ringThickness,
        double progress,
        string digits,
        SD.Font digitFont,
        SD.Color digitColor,
        SD.Color trackColor,
        SD.Color arcColor,
        SD.Rectangle digitBox,
        double digitScale)
    {
        SmoothingMode previousSmoothing = g.SmoothingMode;
        TextRenderingHint previousText = g.TextRenderingHint;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        int inset = Math.Max(1, ringThickness);
        var ring = new SD.Rectangle(
            inset,
            inset,
            Math.Max(1, clientSize.Width - (inset * 2) - 1),
            Math.Max(1, clientSize.Height - (inset * 2) - 1));

        using (var track = new SD.Pen(trackColor, ringThickness))
            g.DrawEllipse(track, ring);

        float sweep = SweepDegrees(progress);
        if (sweep > 0.05f)
        {
            using var arc = new SD.Pen(arcColor, ringThickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, ring, -90f, sweep); // twelve o'clock, clockwise
        }

        // DrawString rather than TextRenderer: GDI ignores the Graphics transform, and the
        // pop is a transform. At this size the two are indistinguishable anyway.
        using var format = new SD.StringFormat
        {
            Alignment = SD.StringAlignment.Center,
            LineAlignment = SD.StringAlignment.Center,
            FormatFlags = SD.StringFormatFlags.NoWrap,
        };
        using var brush = new SD.SolidBrush(digitColor);
        GraphicsState state = g.Save();
        float centreX = digitBox.Left + (digitBox.Width / 2f);
        float centreY = digitBox.Top + (digitBox.Height / 2f);
        g.TranslateTransform(centreX, centreY);
        g.ScaleTransform((float)digitScale, (float)digitScale);
        g.TranslateTransform(-centreX, -centreY);
        g.DrawString(digits, digitFont, brush, digitBox, format);
        g.Restore(state);

        g.TextRenderingHint = previousText;
        g.SmoothingMode = previousSmoothing;
    }
}
