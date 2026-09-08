using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// What Shift does to a drag. Pure geometry, kept out of the mouse handlers so the
/// rules can be read — and tested — without a window.
///
/// Every rule is applied to the drag's TOTAL, never to the per-frame delta: locking each
/// frame's delta independently lets a diagonal drag creep in both axes a pixel at a time,
/// which is exactly the wobble the lock is supposed to remove.
/// </summary>
internal static class DragConstraints
{
    /// <summary>
    /// Axis-lock: keep the dominant component of a move and zero the other, so a mark
    /// slides along one axis until Shift is let go.
    /// </summary>
    public static Vector LockAxis(Vector total) =>
        Math.Abs(total.X) >= Math.Abs(total.Y)
            ? new Vector(total.X, 0)
            : new Vector(0, total.Y);

    /// <summary>
    /// Squares a dragged rectangle about its anchor: the shorter side grows to the longer
    /// one, keeping the direction the pointer went in each axis.
    /// </summary>
    public static Point Square(Point anchor, Point current)
    {
        double dx = current.X - anchor.X;
        double dy = current.Y - anchor.Y;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point(
            anchor.X + (dx < 0 ? -side : side),
            anchor.Y + (dy < 0 ? -side : side));
    }

    /// <summary>
    /// Quantises a line or arrow to the nearest 45°, keeping the drag's length along that
    /// bearing — so a Shift-drag gives a true horizontal, vertical or diagonal rather than
    /// a nearly-straight one. Reuses the rotation tool's own 45° snap so "Shift means 45°"
    /// is one rule in one place.
    /// </summary>
    public static Point QuantizeBearing(Point anchor, Point current)
    {
        var v = current - anchor;
        double length = v.Length;
        if (length < 0.001) return current;

        double snapped = AnnotationRotation.QuantizeTo45(
            AnnotationRotation.AngleFromPointer(anchor, current));
        // AngleFromPointer measures 0° as straight UP and clockwise positive; convert back.
        double radians = snapped * Math.PI / 180;
        return new Point(
            anchor.X + Math.Sin(radians) * length,
            anchor.Y - Math.Cos(radians) * length);
    }
}
