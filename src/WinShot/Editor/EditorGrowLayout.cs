using System.Windows;
using SD = System.Drawing;

namespace WinShot.Editor;

/// <summary>
/// Where the editor window lands, and what it grows out of. Two jobs that are really
/// one sum: the window opens on the display the capture came from (Shottr's "editor
/// opens on the capture's display") and tweens out of the card's rect to get there,
/// so the target rect must be computed once and shared by both.
///
/// Everything here is in DEVICE PIXELS, and the caller passes the TARGET monitor's DPI
/// scale explicitly. DIPs are the wrong unit for this: a window's DIP space belongs to
/// whichever monitor it currently sits on, and the whole point of the move is that the
/// destination is a different monitor with a different scale. The caller drives the tween
/// with SetWindowPos, which is what History already does for the same reason.
/// </summary>
internal static class EditorGrowLayout
{
    /// <summary>
    /// The most of a monitor's work area the editor will take. A window that fills its
    /// display edge to edge reads as maximised, and the grow tween has nowhere to land.
    /// </summary>
    public const double MaxWorkAreaFraction = 0.9;

    /// <summary>
    /// The editor's resting rect in device pixels: its desired DIP size scaled by the
    /// TARGET monitor's DPI, capped at 90 % of that monitor's work area on each axis, and
    /// centred in it. So a shot taken on a 100 % monitor above a 150 % laptop (negative Y)
    /// opens up there at the size it asked for, not at two thirds of it on the primary.
    ///
    /// <paramref name="minimumDip"/> is the window's own MinWidth/MinHeight. Windows
    /// enforces those through WM_GETMINMAXINFO, so a target below them is not a size the
    /// window can hold: it would be resized back up on the next layout pass, which reads as
    /// a snap at the end of the grow. The floor therefore wins over the 90 % cap — but not
    /// over the work area itself, because a minimum wider than the screen is worse than a
    /// window that fills it.
    /// </summary>
    public static SD.Rectangle Target(
        SD.Rectangle workAreaPx, Size desiredDip, double scale, Size minimumDip = default)
    {
        double s = double.IsFinite(scale) && scale > 0 ? scale : 1;
        double wantWidth = double.IsFinite(desiredDip.Width) ? desiredDip.Width * s : workAreaPx.Width;
        double wantHeight = double.IsFinite(desiredDip.Height) ? desiredDip.Height * s : workAreaPx.Height;

        int width = Axis(wantWidth, minimumDip.Width * s, workAreaPx.Width);
        int height = Axis(wantHeight, minimumDip.Height * s, workAreaPx.Height);
        return new SD.Rectangle(
            workAreaPx.X + (workAreaPx.Width - width) / 2,
            workAreaPx.Y + (workAreaPx.Height - height) / 2,
            width,
            height);
    }

    private static int Axis(double want, double minimumPx, double workExtent)
    {
        double value = Math.Min(want, workExtent * MaxWorkAreaFraction);
        if (double.IsFinite(minimumPx) && minimumPx > value)
            value = Math.Min(minimumPx, workExtent);
        return Math.Max(1, (int)Math.Round(value));
    }

    /// <summary>
    /// The rect the tween starts from. A degenerate origin is floored to 1x1 rather than
    /// rejected: the size is written every frame and a zero-width window is not a window,
    /// so a card that reported a collapsed rect must still produce a runnable tween.
    /// </summary>
    public static SD.Rectangle Origin(SD.Rectangle originPx) => new(
        originPx.X,
        originPx.Y,
        Math.Max(1, originPx.Width),
        Math.Max(1, originPx.Height));

    /// <summary>One frame of the grow: the whole rect interpolated, position and size together.</summary>
    public static SD.Rectangle Lerp(SD.Rectangle from, SD.Rectangle to, double progress)
    {
        double t = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 1;
        static int At(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);
        return new SD.Rectangle(
            At(from.X, to.X, t),
            At(from.Y, to.Y, t),
            Math.Max(1, At(from.Width, to.Width, t)),
            Math.Max(1, At(from.Height, to.Height, t)));
    }
}
