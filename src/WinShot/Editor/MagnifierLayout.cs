using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// Geometry for the magnifier callout: a bubble on the canvas showing a piece of the
/// picture blown up. Pure — the bubble's own rectangle and the region of the source it
/// shows are two different rectangles, and keeping the relationship here is what lets the
/// bubble be dragged away from what it is pointing at without the maths moving with it.
/// </summary>
internal static class MagnifierLayout
{
    public const double MinScale = 1.5;
    public const double MaxScale = 8;
    public const double DefaultScale = 2;

    public static double ClampScale(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, MinScale, MaxScale) : DefaultScale;

    /// <summary>
    /// The region of the source the bubble shows: <paramref name="bounds"/> shrunk by the
    /// magnification and re-centred on <paramref name="focus"/>. Bigger bubble shows more,
    /// higher magnification shows less — which is what makes resizing the bubble and
    /// turning the dial two different controls rather than the same one twice.
    /// </summary>
    public static Rect SourceRegion(Rect bounds, Point focus, double scale)
    {
        double s = ClampScale(scale);
        double w = Math.Max(1, bounds.Width) / s;
        double h = Math.Max(1, bounds.Height) / s;
        return new Rect(focus.X - w / 2, focus.Y - h / 2, w, h);
    }

    /// <summary>
    /// Where a new callout looks when it is first dragged out: at itself. The focus starts
    /// at the bubble's own centre, so the first thing you see is the pixels the bubble is
    /// sitting on, blown up — then you drag the bubble off them.
    /// </summary>
    public static Point DefaultFocus(Rect bounds) =>
        new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    /// <summary>
    /// The focus kept inside the picture. A bubble whose focus wandered off the canvas
    /// would show a band of black and read as broken rather than as empty.
    /// </summary>
    public static Point ClampFocus(Point focus, Size source) => new(
        Inside(focus.X, source.Width),
        Inside(focus.Y, source.Height));

    /// <summary>Math.Clamp returns NaN unchanged, so the method named Clamp would not
    /// clamp the one value that matters. This one does.</summary>
    private static double Inside(double value, double extent)
    {
        double max = double.IsFinite(extent) ? Math.Max(0, extent) : 0;
        return double.IsFinite(value) ? Math.Clamp(value, 0, max) : 0;
    }
}
