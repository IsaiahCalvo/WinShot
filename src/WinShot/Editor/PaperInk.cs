using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Survey paper-ink: a round-cap swept outline filled even-odd, built from a
/// centerline. Preview stays a polyline; commit/widen is this geometry so an
/// eraser can subtract the same way Survey subtracts from ink.
/// </summary>
internal static class PaperInk
{
    /// <summary>Survey live coalescing distance while the pointer is down.</summary>
    public const double LiveMinDistance = 0.2;

    /// <summary>Survey compact distance applied when the stroke is committed.</summary>
    public const double CompactMinDistance = 0.35;

    public static List<Point> Compact(IEnumerable<Point> raw, double minDistance = CompactMinDistance)
    {
        var pts = new List<Point>();
        foreach (var p in raw)
        {
            if (pts.Count == 0 || (p - pts[^1]).Length >= minDistance)
                pts.Add(p);
        }
        return pts;
    }

    public static PathGeometry Outline(IEnumerable<Point> raw, double width)
    {
        double w = Math.Max(1, width);
        var pts = Compact(raw);
        if (pts.Count == 0)
            return new PathGeometry();
        if (pts.Count == 1)
            return EllipseAt(pts[0], w / 2);

        var centerline = new PathGeometry();
        var fig = new PathFigure { StartPoint = pts[0], IsFilled = false };
        for (int i = 1; i < pts.Count; i++)
            fig.Segments.Add(new LineSegment(pts[i], isStroked: true));
        centerline.Figures.Add(fig);

        var pen = new Pen(Brushes.Black, w)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
            MiterLimit = 8,
        };
        try
        {
            var widened = centerline.GetWidenedPathGeometry(pen);
            widened.FillRule = FillRule.Nonzero;
            return widened;
        }
        catch (InvalidOperationException)
        {
            return EllipseAt(pts[0], w / 2);
        }
    }

    public static PathGeometry EraserStroke(IEnumerable<Point> raw, double diameter) =>
        Outline(raw, Math.Max(1, diameter));

    public static Geometry Subtract(Geometry ink, Geometry cut)
    {
        try
        {
            var result = Geometry.Combine(ink, cut, GeometryCombineMode.Exclude, Transform.Identity);
            return result ?? Geometry.Empty;
        }
        catch (InvalidOperationException)
        {
            return ink;
        }
    }

    public static bool IsEmpty(Geometry geometry)
    {
        if (geometry.IsEmpty()) return true;
        Rect b = geometry.Bounds;
        return b.IsEmpty || b.Width < 0.5 || b.Height < 0.5 || b.Width * b.Height < 1;
    }

    private static PathGeometry EllipseAt(Point center, double radius)
    {
        var g = new PathGeometry();
        g.AddGeometry(new EllipseGeometry(center, radius, radius));
        return g;
    }
}
