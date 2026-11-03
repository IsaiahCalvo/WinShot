using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// Lining a mark up with the marks already on the canvas. Pure geometry: given where a
/// mark would land and where everything else is, it returns the nudge that puts an edge or
/// a centre exactly on a neighbour's.
///
/// Each axis snaps independently — a mark can align its left edge with one neighbour and
/// its middle with another — and the SMALLEST available correction wins on each axis, so
/// the mark goes to the nearest guide rather than the first one considered.
/// </summary>
internal static class ObjectSnap
{
    /// <summary>How close an edge has to get, in content pixels, before it sticks.</summary>
    public const double Tolerance = 6;

    /// <summary>Survey SNAP_ALIGN_EPS: how close two features must sit to count as lined up.</summary>
    public const double AlignEps = 0.01;

    /// <summary>
    /// Survey findSnapOffset (snapGuides.js): edges meet only edges (left or right of a
    /// neighbour) and centres meet only centres, as in Drawboard.
    /// </summary>
    public static Vector Adjust(Rect moving, IEnumerable<Rect> others, double tolerance = Tolerance)
    {
        if (moving.IsEmpty || tolerance <= 0) return default;

        double bestX = double.NaN, bestY = double.NaN;
        double[] movingX = { moving.Left, moving.Right };
        double[] movingY = { moving.Top, moving.Bottom };
        double mcx = moving.Left + moving.Width / 2, mcy = moving.Top + moving.Height / 2;

        foreach (Rect other in others)
        {
            if (other.IsEmpty) continue;
            double[] otherX = { other.Left, other.Right };
            double[] otherY = { other.Top, other.Bottom };

            foreach (double from in movingX)
                foreach (double to in otherX)
                    Consider(from, to, tolerance, ref bestX);
            Consider(mcx, other.Left + other.Width / 2, tolerance, ref bestX);

            foreach (double from in movingY)
                foreach (double to in otherY)
                    Consider(from, to, tolerance, ref bestY);
            Consider(mcy, other.Top + other.Height / 2, tolerance, ref bestY);
        }

        return new Vector(
            double.IsNaN(bestX) ? 0 : bestX,
            double.IsNaN(bestY) ? 0 : bestY);
    }

    /// <summary>
    /// Survey findSnapOffset with one edge candidate (the group-resize pull): the nudge that
    /// puts a dragged edge at <paramref name="value"/> exactly on a neighbour's edge along the
    /// same axis, or 0 when none is within <paramref name="tolerance"/>.
    /// </summary>
    public static double EdgeOffset(double value, IEnumerable<Rect> others, bool axisX, double tolerance = Tolerance)
    {
        if (tolerance <= 0 || double.IsNaN(value)) return 0;
        double best = double.NaN;
        foreach (Rect other in others)
        {
            if (other.IsEmpty) continue;
            Consider(value, axisX ? other.Left : other.Top, tolerance, ref best);
            Consider(value, axisX ? other.Right : other.Bottom, tolerance, ref best);
        }
        return double.IsNaN(best) ? 0 : best;
    }

    /// <summary>One dashed guide: a vertical line at x = Value (axis X) or a horizontal one
    /// at y = Value (axis Y), running from Min to Max along the other axis.</summary>
    public readonly record struct Guide(bool AxisX, double Value, double Min, double Max);

    /// <summary>
    /// Survey buildSnapGuides: for the box as it is really shown, one line for every edge or
    /// centre that sits exactly on a neighbour's matching feature. An edge line spans both
    /// boxes; a centre line runs centre to centre; lines on the same spot merge. Points are
    /// the lined-up corners and centres, which get small X marks.
    /// </summary>
    public static (List<Guide> Lines, List<Point> Points) Guides(
        Rect moving, IEnumerable<Rect> others, double eps = AlignEps)
    {
        var lines = new List<Guide>();
        var points = new List<Point>();
        if (moving.IsEmpty) return (lines, points);
        Rect[] targets = others.Where(r => !r.IsEmpty && double.IsFinite(r.X) && double.IsFinite(r.Y)).ToArray();

        void AddPoint(double x, double y)
        {
            if (!points.Any(p => Math.Abs(p.X - x) < eps && Math.Abs(p.Y - y) < eps)) points.Add(new Point(x, y));
        }

        foreach (bool axisX in new[] { true, false })
        {
            (double Value, bool Centre)[] features = axisX
                ? new[] { (moving.Left, false), (moving.Right, false), (moving.Left + moving.Width / 2, true) }
                : new[] { (moving.Top, false), (moving.Bottom, false), (moving.Top + moving.Height / 2, true) };
            foreach (var (value, centre) in features)
            {
                IEnumerable<double> Values(Rect r) => (axisX, centre) switch
                {
                    (true, true) => new[] { r.Left + r.Width / 2 },
                    (true, false) => new[] { r.Left, r.Right },
                    (false, true) => new[] { r.Top + r.Height / 2 },
                    _ => new[] { r.Top, r.Bottom },
                };
                (double, double) Span(Rect r)
                {
                    if (centre)
                    {
                        double c = axisX ? r.Top + r.Height / 2 : r.Left + r.Width / 2;
                        return (c, c);
                    }
                    return axisX ? (r.Top, r.Bottom) : (r.Left, r.Right);
                }
                Rect[] hits = targets.Where(t => Values(t).Any(v => Math.Abs(v - value) < eps)).ToArray();
                if (hits.Length == 0) continue;
                var (min, max) = Span(moving);
                foreach (Rect h in hits)
                {
                    var (a, b) = Span(h);
                    min = Math.Min(min, a);
                    max = Math.Max(max, b);
                }
                int same = lines.FindIndex(l => l.AxisX == axisX && Math.Abs(l.Value - value) < eps);
                if (same >= 0)
                    lines[same] = lines[same] with { Min = Math.Min(lines[same].Min, min), Max = Math.Max(lines[same].Max, max) };
                else
                    lines.Add(new Guide(axisX, value, min, max));
                foreach (Rect r in hits.Prepend(moving))
                {
                    var (a, b) = Span(r);
                    foreach (double c in a == b ? new[] { a } : new[] { a, b })
                    {
                        if (axisX) AddPoint(value, c); else AddPoint(c, value);
                    }
                }
            }
        }
        return (lines, points);
    }

    private static void Consider(double from, double to, double tolerance, ref double best)
    {
        double delta = to - from;
        if (Math.Abs(delta) > tolerance) return;
        if (double.IsNaN(best) || Math.Abs(delta) < Math.Abs(best))
            best = delta;
    }
}
