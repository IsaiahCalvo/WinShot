using System.Windows;
using System.Windows.Input;

namespace WinShot.Editor;

/// <summary>
/// Survey draw/resize modifiers: Shift squares a box, snaps a line/arrow to
/// 45° increments, and locks a box resize to uniform aspect.
/// </summary>
internal static class ShapeConstraint
{
    public const double LineSnapDegrees = 45;

    public static bool ShiftHeld => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    /// <summary>
    /// Axis-aligned rect from <paramref name="start"/> to <paramref name="current"/>.
    /// With <paramref name="square"/>, the longer axis wins and the sign of each
    /// axis keeps the drag quadrant (Survey / CleanShot square lock).
    /// </summary>
    public static Rect BoxFromDrag(Point start, Point current, bool square)
    {
        double dx = current.X - start.X;
        double dy = current.Y - start.Y;
        if (!square)
        {
            return new Rect(
                Math.Min(start.X, current.X),
                Math.Min(start.Y, current.Y),
                Math.Max(1, Math.Abs(dx)),
                Math.Max(1, Math.Abs(dy)));
        }

        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (side < 1) side = 1;
        double x = dx >= 0 ? start.X : start.X - side;
        double y = dy >= 0 ? start.Y : start.Y - side;
        return new Rect(x, y, side, side);
    }

    /// <summary>
    /// Snap <paramref name="current"/> onto the nearest 45° ray out of
    /// <paramref name="start"/>. Length is preserved.
    /// </summary>
    public static Point SnapLineEnd(Point start, Point current, bool snap)
    {
        if (!snap) return current;
        double dx = current.X - start.X;
        double dy = current.Y - start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return current;
        double angle = Math.Atan2(dy, dx);
        double snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        return new Point(start.X + Math.Cos(snapped) * len, start.Y + Math.Sin(snapped) * len);
    }

    /// <summary>
    /// Survey Shift+resize: corner handles average |sx|/|sy|; edge handles
    /// preserve the original aspect, driving from the moved axis.
    /// <paramref name="unconstrained"/> is the rect after the handle moved freely.
    /// </summary>
    public static Rect UniformResize(Rect original, int handle, Rect unconstrained)
    {
        if (original.Width < 0.5 || original.Height < 0.5)
            return unconstrained;

        double w = Math.Max(1, unconstrained.Width);
        double h = Math.Max(1, unconstrained.Height);
        bool corner = handle is >= 0 and <= 3;
        if (corner)
        {
            double sx = w / original.Width;
            double sy = h / original.Height;
            double avg = (Math.Abs(sx) + Math.Abs(sy)) / 2;
            w = Math.Max(1, original.Width * avg);
            h = Math.Max(1, original.Height * avg);
            return AnchorOpposite(original, handle, w, h);
        }

        double aspect = original.Width / original.Height;
        switch (handle)
        {
            case 4: // T
            case 6: // B
                w = Math.Max(1, h * aspect);
                return handle == 4
                    ? new Rect(original.Left + (original.Width - w) / 2, unconstrained.Y, w, h)
                    : new Rect(original.Left + (original.Width - w) / 2, original.Top, w, h);
            case 5: // R
            case 7: // L
                h = Math.Max(1, w / aspect);
                return handle == 5
                    ? new Rect(original.Left, original.Top + (original.Height - h) / 2, w, h)
                    : new Rect(unconstrained.X, original.Top + (original.Height - h) / 2, w, h);
            default:
                return unconstrained;
        }
    }

    /// <summary>Survey rotation snap: pull to the nearest 45° when within <paramref name="thresholdDegrees"/>.</summary>
    public static double SnapAngleToNearest45(double degrees, double thresholdDegrees = 3)
    {
        double snapped = Math.Round(degrees / LineSnapDegrees) * LineSnapDegrees;
        return Math.Abs(degrees - snapped) <= thresholdDegrees ? snapped : degrees;
    }

    private static Rect AnchorOpposite(Rect original, int handle, double w, double h) => handle switch
    {
        0 => new Rect(original.Right - w, original.Bottom - h, w, h), // TL, anchor BR
        1 => new Rect(original.Left, original.Bottom - h, w, h),      // TR, anchor BL
        2 => new Rect(original.Left, original.Top, w, h),             // BR, anchor TL
        3 => new Rect(original.Right - w, original.Top, w, h),        // BL, anchor TR
        _ => new Rect(original.X, original.Y, w, h),
    };
}
