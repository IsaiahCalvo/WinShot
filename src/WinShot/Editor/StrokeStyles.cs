using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>How a shape's outline is drawn. Stored in <c>AnnotationData.Dash</c>.</summary>
public enum StrokeDash
{
    Solid,
    Dashed,
    Dotted,
}

/// <summary>
/// Pure geometry for dashed outlines and for simplifying a freehand scribble.
/// No WPF element is touched here, so every rule below can be read and tested without a
/// window.
/// </summary>
internal static class StrokeStyles
{
    // ------------------------------------------------------------------- dashes

    /// <summary>
    /// The dash pattern for a stroke, in multiples of the stroke width — which is what
    /// <c>StrokeDashArray</c> means, so a dashed 2px line and a dashed 20px line read as
    /// the same dash rather than the thick one looking solid.
    /// Solid returns null: an empty array is not "no dashes", it throws.
    /// </summary>
    public static DoubleCollection? DashArray(StrokeDash dash)
    {
        DoubleCollection? c = dash switch
        {
            StrokeDash.Dashed => new DoubleCollection { 4, 2.5 },
            // A dot is a zero-length dash with round caps; giving it a length instead
            // makes a short dash, which is the same thing as Dashed and not a dot.
            StrokeDash.Dotted => new DoubleCollection { 0.001, 2 },
            _ => null,
        };
        c?.Freeze();
        return c;
    }

    /// <summary>Round caps make Dotted read as dots. The other two keep the flat default.</summary>
    public static PenLineCap CapFor(StrokeDash dash) =>
        dash == StrokeDash.Dotted ? PenLineCap.Round : PenLineCap.Flat;

    public static StrokeDash ParseDash(string? name) =>
        Enum.TryParse(name, out StrokeDash d) ? d : StrokeDash.Solid;

    // -------------------------------------------------------------- simplifying

    /// <summary>
    /// Ramer–Douglas–Peucker: drops every point that is within <paramref name="epsilon"/>
    /// of the line its neighbours already describe. Run before the Catmull-Rom pass, it
    /// turns a scribble's several hundred sampled points into the dozen or so that actually
    /// carry its shape — which is what makes the committed stroke a curve rather than a
    /// smoothed recording of the mouse's own jitter.
    ///
    /// Endpoints are always kept, and a run of two points or fewer is returned unchanged.
    /// </summary>
    public static List<Point> Simplify(IReadOnlyList<Point> points, double epsilon)
    {
        if (points.Count <= 2 || epsilon <= 0) return new List<Point>(points);

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        // Iterative rather than recursive: a long stroke is thousands of points and the
        // worst case for RDP is one recursion level per point.
        var stack = new Stack<(int Lo, int Hi)>();
        stack.Push((0, points.Count - 1));

        while (stack.Count > 0)
        {
            var (lo, hi) = stack.Pop();
            if (hi <= lo + 1) continue;

            double worst = -1;
            int worstAt = -1;
            for (int i = lo + 1; i < hi; i++)
            {
                double d = PerpendicularDistance(points[i], points[lo], points[hi]);
                if (d > worst) { worst = d; worstAt = i; }
            }

            if (worst > epsilon && worstAt > 0)
            {
                keep[worstAt] = true;
                stack.Push((lo, worstAt));
                stack.Push((worstAt, hi));
            }
        }

        var result = new List<Point>();
        for (int i = 0; i < points.Count; i++)
            if (keep[i]) result.Add(points[i]);
        return result;
    }

    private static double PerpendicularDistance(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double len = ab.Length;
        // A degenerate span has no line to be perpendicular to; measure to the point.
        if (len < 1e-9) return (p - a).Length;
        return Math.Abs(ab.X * (a.Y - p.Y) - (a.X - p.X) * ab.Y) / len;
    }
}
