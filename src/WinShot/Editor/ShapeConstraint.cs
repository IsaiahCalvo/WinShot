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
    /// The coordinates <see cref="EditorWindow.NewBoundsForHandle"/> holds fixed while
    /// <paramref name="handle"/> is dragged — Survey's <c>anchorMap</c> (useSVGInteraction.js:4118-4127).
    /// A null component means that axis is not driven by this handle, so its anchor is the
    /// box's own mid-line. Handles: 0=TL 1=TR 2=BR 3=BL 4=T 5=R 6=B 7=L.
    /// </summary>
    public static (double? X, double? Y) AxisAnchors(Rect b, int handle) =>
    (
        handle switch { 0 or 3 or 7 => b.Right, 1 or 2 or 5 => b.Left, _ => (double?)null },
        handle switch { 0 or 1 or 4 => b.Bottom, 2 or 3 or 6 => b.Top, _ => (double?)null }
    );

    /// <summary>The full anchor POINT for a handle: Survey's <c>anchorX/anchorY</c>.</summary>
    public static Point AnchorPoint(Rect b, int handle)
    {
        var (ax, ay) = AxisAnchors(b, handle);
        return new Point(ax ?? b.Left + b.Width / 2, ay ?? b.Top + b.Height / 2);
    }

    /// <summary>
    /// Survey Shift+resize (useSVGInteraction.js:2167-2171): ONE magnitude is written back to
    /// both axes. On a corner that is the average of |sx| and |sy|; on an edge pill the
    /// untouched axis still carries scale 1, so the average is <c>(moved + 1) / 2</c> — the
    /// shape grows at HALF the drag, not the full drag against the original aspect.
    /// <paramref name="average"/> false is the counter rule (useSVGInteraction.js:2174-2183):
    /// <c>max(|sx|,|sy|)</c> on a corner, the moved axis alone on an edge, no averaging.
    /// <paramref name="mirror"/> false goes with it: <c>safeUniformScale</c> is unconditionally
    /// positive, so a counter's offset-from-anchor is never negated (:2232-2246) and the badge
    /// stays on the dragged handle's own side of the anchor however far past it you pull.
    /// </summary>
    public static Rect UniformResize(Rect original, int handle, Rect unconstrained,
        bool average = true, bool mirror = true)
    {
        if (original.Width < 0.5 || original.Height < 0.5)
            return unconstrained;

        double sx = Math.Abs(unconstrained.Width / original.Width);
        double sy = Math.Abs(unconstrained.Height / original.Height);
        double s;
        if (handle is >= 0 and <= 3)
            s = average ? (sx + sy) / 2 : Math.Max(sx, sy);
        else if (handle is 4 or 5 or 6 or 7)
        {
            double moved = handle is 4 or 6 ? sy : sx;
            s = average ? (moved + 1) / 2 : moved;
        }
        else return unconstrained;

        return Place(original, handle, original.Width * s, original.Height * s, unconstrained, mirror);
    }

    /// <summary>Survey rotation snap: pull to the nearest 45° when within <paramref name="thresholdDegrees"/>.</summary>
    public static double SnapAngleToNearest45(double degrees, double thresholdDegrees = 3)
    {
        double snapped = Math.Round(degrees / LineSnapDegrees) * LineSnapDegrees;
        return Math.Abs(degrees - snapped) <= thresholdDegrees ? snapped : degrees;
    }

    /// <summary>
    /// Puts a w×h box back on the handle's anchor, on the SIDE of it the free drag is already
    /// on. Survey keeps the sign of each axis through the Shift lock (useSVGInteraction.js:2167-2171),
    /// so a Shift+corner drag past the anchor still mirrors instead of bouncing back.
    /// An axis this handle does not drive stays centred on the original box.
    /// With <paramref name="mirror"/> false the side comes from the handle's own growth
    /// direction instead, which is what Survey's always-positive counter scale means.
    /// </summary>
    private static Rect Place(Rect original, int handle, double w, double h, Rect unconstrained,
        bool mirror = true)
    {
        var (ax, ay) = AxisAnchors(original, handle);
        double x = ax is double axv
            ? ((mirror ? unconstrained.X >= axv - 1e-6 : handle is not (0 or 3 or 7)) ? axv : axv - w)
            : original.Left + (original.Width - w) / 2;
        double y = ay is double ayv
            ? ((mirror ? unconstrained.Y >= ayv - 1e-6 : handle is not (0 or 1 or 4)) ? ayv : ayv - h)
            : original.Top + (original.Height - h) / 2;
        return new Rect(x, y, w, h);
    }
}
