using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Rotation maths for annotations, ported from Survey's svgTransformMath
/// (snapAngleToNearest45) and its rotation-handle drag.
///
/// Angles are degrees, clockwise positive, normalised to (-180, 180].
/// </summary>
internal static class AnnotationRotation
{
    /// <summary>Degrees within which a free drag snaps onto a 45° multiple. Survey's default.</summary>
    public const double SnapThresholdDegrees = 3;

    /// <summary>Distance (screen px) from the top edge to the rotation handle's centre.</summary>
    public const double HandleOffsetScreenPx = 22;

    /// <summary>Wraps any angle into (-180, 180].</summary>
    public static double Normalize(double degrees)
    {
        if (!double.IsFinite(degrees)) return 0;
        double a = degrees % 360;
        if (a > 180) a -= 360;
        if (a <= -180) a += 360;
        // -0 is a legal double but reads badly in the angle field.
        return a == 0 ? 0 : a;
    }

    /// <summary>
    /// Soft snap: inside the threshold the angle latches onto the nearest 45° multiple,
    /// outside it the angle passes through untouched. Clean angles without having to
    /// fight the drag for them.
    /// </summary>
    public static double SnapToNearest45(double degrees, double threshold = SnapThresholdDegrees)
    {
        double a = Normalize(degrees);
        double nearest = Math.Round(a / 45) * 45;
        return Math.Abs(a - nearest) <= threshold ? Normalize(nearest) : a;
    }

    /// <summary>Hard snap for Shift-drag: always the nearest 45° multiple.</summary>
    public static double QuantizeTo45(double degrees) => Normalize(Math.Round(Normalize(degrees) / 45) * 45);

    /// <summary>
    /// The angle a rotation-handle drag implies: the bearing from the pivot to the pointer,
    /// measured so that "pointer directly above the pivot" is 0°.
    /// </summary>
    public static double AngleFromPointer(Point pivot, Point pointer)
    {
        double dx = pointer.X - pivot.X;
        double dy = pointer.Y - pivot.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return 0;
        // atan2(dx, -dy): straight up is 0, clockwise positive.
        return Normalize(Math.Atan2(dx, -dy) * 180 / Math.PI);
    }

    /// <summary>Rotates <paramref name="p"/> about <paramref name="centre"/> by <paramref name="degrees"/> (clockwise).</summary>
    public static Point RotatePoint(Point p, Point centre, double degrees)
    {
        if (degrees == 0) return p;
        double rad = degrees * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double dx = p.X - centre.X, dy = p.Y - centre.Y;
        return new Point(
            centre.X + dx * cos - dy * sin,
            centre.Y + dx * sin + dy * cos);
    }

    /// <summary>
    /// Full drag resolution: bearing, then Shift = hard 45° steps, otherwise the soft snap.
    /// <paramref name="startAngle"/> is the annotation's angle when the drag began and
    /// <paramref name="grabOffset"/> the bearing difference at grab time, so the mark does
    /// not jump when the handle is picked up slightly off-centre.
    /// </summary>
    public static double ResolveDrag(double startAngle, double grabOffset, double pointerAngle, bool shift)
    {
        double raw = Normalize(startAngle + (pointerAngle - grabOffset));
        return shift ? QuantizeTo45(raw) : SnapToNearest45(raw);
    }
}

/// <summary>
/// Reads and writes the render transform WinShot puts on an annotation.
///
/// Annotations carry a move offset and, since the rotation work, an angle. Both live in
/// one <see cref="TransformGroup"/> — rotate first (about the element's own centre), then
/// translate — so callers never have to care whether a given element happens to be rotated.
/// An unrotated annotation still gets a plain <see cref="TranslateTransform"/>, which keeps
/// the on-disk shape and every existing code path unchanged.
/// </summary>
internal static class AnnotationTransform
{
    /// <summary>The accumulated move offset (moves + crop shifts), or zero.</summary>
    public static Vector OffsetOf(UIElement element) => element.RenderTransform switch
    {
        TranslateTransform t => new Vector(t.X, t.Y),
        TransformGroup g => g.Children.OfType<TranslateTransform>().Select(t => new Vector(t.X, t.Y))
            .FirstOrDefault(),
        _ => default,
    };

    /// <summary>The annotation's rotation in degrees, or 0 when it is not rotated.</summary>
    public static double AngleOf(UIElement element) => element.RenderTransform switch
    {
        RotateTransform r => r.Angle,
        TransformGroup g => g.Children.OfType<RotateTransform>().Select(r => r.Angle).FirstOrDefault(),
        _ => 0,
    };

    /// <summary>The rotation pivot in the element's own coordinates, or its centre.</summary>
    public static Point PivotOf(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup g)
        {
            foreach (var r in g.Children.OfType<RotateTransform>())
                return new Point(r.CenterX, r.CenterY);
        }
        if (element.RenderTransform is RotateTransform only)
            return new Point(only.CenterX, only.CenterY);
        return LocalCentre(element);
    }

    /// <summary>Centre of the element's own (unrotated) bounds.</summary>
    public static Point LocalCentre(FrameworkElement element)
    {
        Rect b = LocalBounds(element);
        return new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
    }

    /// <summary>The element's bounds in its OWN coordinates — before any render transform.</summary>
    public static Rect LocalBounds(FrameworkElement element)
    {
        Rect b = VisualTreeHelper.GetDescendantBounds(element);
        if (b.IsEmpty) b = new Rect(element.RenderSize);
        return b;
    }

    /// <summary>
    /// Writes offset + angle back onto the element. An angle of zero collapses to a plain
    /// TranslateTransform so unrotated annotations keep exactly the shape they always had.
    /// </summary>
    public static void Apply(FrameworkElement element, Vector offset, double angle, Point? pivot = null)
    {
        double a = AnnotationRotation.Normalize(angle);
        if (Math.Abs(a) < 0.01)
        {
            element.RenderTransform = offset == default
                ? Transform.Identity
                : new TranslateTransform(offset.X, offset.Y);
            return;
        }

        Point p = pivot ?? LocalCentre(element);
        var group = new TransformGroup();
        group.Children.Add(new RotateTransform(a, p.X, p.Y));
        group.Children.Add(new TranslateTransform(offset.X, offset.Y));
        element.RenderTransform = group;
    }

    /// <summary>Replaces just the angle, keeping the current offset. Re-centres the pivot.</summary>
    public static void SetAngle(FrameworkElement element, double angle) =>
        Apply(element, OffsetOf(element), angle, LocalCentre(element));

    /// <summary>Replaces just the offset, keeping the current angle and pivot.</summary>
    public static void SetOffset(FrameworkElement element, Vector offset)
    {
        double angle = AngleOf(element);
        Point pivot = angle == 0 ? default : PivotOf(element);
        Apply(element, offset, angle, angle == 0 ? null : pivot);
    }
}
