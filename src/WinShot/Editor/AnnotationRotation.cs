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
    /// <summary>Degrees within which a free drag latches onto a snap increment. Survey's default.</summary>
    public const double SnapThresholdDegrees = 3;

    /// <summary>
    /// Snap increment. Survey snaps at 45°; Isaiah wants finer control, so 15° — every
    /// 45° is still a snap point, there are just two more stops in between.
    /// </summary>
    public const double SnapIncrementDegrees = 15;

    /// <summary>
    /// Floor on the distance from the top edge to the rotation handle's centre. Survey:
    /// <c>Math.max(36, rotationR + topHandleHalfHeight + minGap)</c>
    /// (selectionHandleVisibility.js:70-71) — the 36 floor is what is in effect at rest.
    /// </summary>
    public const double HandleOffsetScreenPx = 36;

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
    /// Soft snap: inside the threshold the angle latches onto the nearest increment,
    /// outside it the angle passes through untouched. Clean angles without having to
    /// fight the drag for them.
    /// </summary>
    public static double SnapToNearest(double degrees, double threshold = SnapThresholdDegrees)
    {
        double a = Normalize(degrees);
        double nearest = Math.Round(a / SnapIncrementDegrees) * SnapIncrementDegrees;
        return Math.Abs(a - nearest) <= threshold ? Normalize(nearest) : a;
    }

    /// <summary>
    /// Hard snap: always the nearest increment. Survey only ever snaps SOFTLY, so this is
    /// reserved for the counter's bbox-edit rotation grip, whose Survey twin snaps in the
    /// <c>mtr</c> branch before the counter special-case (useSVGInteraction.js:2357-2380).
    /// </summary>
    public static double Quantize(double degrees) =>
        Normalize(Math.Round(Normalize(degrees) / SnapIncrementDegrees) * SnapIncrementDegrees);

    /// <summary>
    /// Survey's counter convention: the pin's pointer angle is the plain bearing from the
    /// badge centre to the cursor, 0° = east, clockwise positive (Math.atan2 in degrees).
    /// Survey's tip handle applies NO snapping and reads no modifier
    /// (SVGAnnotationLayer.jsx:5183) — the nub follows the cursor continuously.
    /// </summary>
    public static double PointerAngleFromCursor(Point centre, Point cursor) =>
        Normalize(Math.Atan2(cursor.Y - centre.Y, cursor.X - centre.X) * 180 / Math.PI);

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
    /// Full drag resolution. Survey rotates to the ABSOLUTE bearing from the shape centre to
    /// the pointer with no grab offset (useSVGInteraction.js:2350-2352), so the mark's top
    /// faces the cursor from the first pointermove. Survey applies
    /// <c>snapAngleToNearest45(newAngle, 3)</c> ONLY while Shift is held (:2357-2360) and
    /// leaves an unmodified drag completely free — so a deliberate 14° tilt stays reachable.
    /// WinShot keeps its 15° increment rather than Survey's 45°.
    /// </summary>
    public static double ResolveDrag(double pointerAngle, bool shift)
    {
        double raw = Normalize(pointerAngle);
        return shift ? SnapToNearest(raw) : raw;
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
