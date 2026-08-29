using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Survey line/arrow midpoint curve. The handle is a point ON the stroke;
/// the quadratic control is derived so the curve passes through it at t=0.5.
/// Snap-back to linear uses Survey's 10px threshold; render hysteresis is 1px.
/// </summary>
internal static class LineCurve
{
    public const double SnapToLinearPx = 10;
    public const double RenderHysteresisPx = 1;

    public static Point Midpoint(Point start, Point end) =>
        new((start.X + end.X) / 2, (start.Y + end.Y) / 2);

    /// <summary>Control point C such that Q(start, C, end) passes through <paramref name="mid"/> at t=0.5.</summary>
    public static Point ControlThroughMid(Point start, Point end, Point mid) =>
        new(2 * mid.X - 0.5 * start.X - 0.5 * end.X,
            2 * mid.Y - 0.5 * start.Y - 0.5 * end.Y);

    public static double DistanceToSegment(Point point, Point start, Point end)
    {
        var v = end - start;
        double length = v.Length;
        if (length < 1e-9) return (point - start).Length;
        return Math.Abs(v.Y * point.X - v.X * point.Y + end.X * start.Y - end.Y * start.X) / length;
    }

    public static bool ShouldSnapToLinear(Point mid, Point start, Point end, double threshold = SnapToLinearPx) =>
        DistanceToSegment(mid, start, end) <= threshold;

    public static bool IsCurved(Point? mid, Point start, Point end) =>
        mid is Point m && DistanceToSegment(m, start, end) > RenderHysteresisPx;

    public static double EndAngleRadians(Point start, Point end, Point mid)
    {
        Point c = ControlThroughMid(start, end, mid);
        return Math.Atan2(end.Y - c.Y, end.X - c.X);
    }

    public static PathGeometry Stroke(Point start, Point end, Point? mid)
    {
        var g = new PathGeometry();
        var fig = new PathFigure { StartPoint = start, IsFilled = false };
        if (IsCurved(mid, start, end))
        {
            Point c = ControlThroughMid(start, end, mid!.Value);
            fig.Segments.Add(new QuadraticBezierSegment(c, end, isStroked: true));
        }
        else
        {
            fig.Segments.Add(new LineSegment(end, isStroked: true));
        }
        g.Figures.Add(fig);
        return g;
    }

    public static Point? ParseMid(double[]? mid)
    {
        if (mid is not { Length: >= 2 }) return null;
        if (double.IsNaN(mid[0]) || double.IsNaN(mid[1])) return null;
        return new Point(mid[0], mid[1]);
    }

    public static double[] ToArray(Point mid) => new[] { mid.X, mid.Y };
}
