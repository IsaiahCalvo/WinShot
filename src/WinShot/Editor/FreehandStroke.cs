using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Port of perfect-freehand's stroke model (steveruizok, MIT): raw pointer samples are
/// streamlined (lerped toward the previous point) and given a simulated pressure from
/// speed, so fast parts thin out and slow parts thicken. Options are the library
/// defaults: thinning .5, smoothing .5, streamline .5, round caps, no taper.
///
/// Geometry: instead of the library's single outline polygon (which self-intersects on
/// loops and sharp turns and leaves winding-zero slivers under any fill rule), the stroke
/// is the union of a disc polygon at every point plus a tangent quad between neighbours
/// — a variable-radius swept disc. One GeometryGroup, Nonzero, every piece wound the
/// same way, so there are no seams and no holes.
/// </summary>
internal static class FreehandStroke
{
    private const double RateOfPressureChange = 0.275;
    private const double MinStreamlineT = 0.15, StreamlineTRange = 0.85;
    private const double MinRadius = 0.25, DefaultFirstPressure = 0.25, DefaultPressure = 0.5;
    private const double EndNoiseThreshold = 3;

    public const double Thinning = 0.5, Smoothing = 0.5, Streamline = 0.5;

    private readonly record struct StrokePoint(Point P, double Pressure, Vector V, double Distance, double Running);
    private readonly record struct Disc(Point P, double R);

    /// <summary>Filled stroke for <paramref name="input"/> at base diameter <paramref name="size"/>.</summary>
    public static Geometry Outline(IList<Point> input, double size, bool complete)
    {
        var discs = Discs(StrokePoints(input, size, complete), size);
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        if (discs.Count == 0) return group;
        for (int i = 0; i < discs.Count; i++)
        {
            AddPolygon(group, DiscPolygon(discs[i]));
            if (i == 0) continue;
            var a = discs[i - 1];
            var b = discs[i];
            Vector d = b.P - a.P;
            if (d.Length < 0.01) continue;
            d.Normalize();
            var n = new Vector(-d.Y, d.X);
            AddPolygon(group, new[] { a.P + n * a.R, b.P + n * b.R, b.P - n * b.R, a.P - n * a.R });
        }
        group.Freeze();
        return group;
    }

    /// <summary>
    /// Every piece is added with the same winding (positive signed area), so Nonzero
    /// unions them: overlaps never cancel, joins never show slits.
    /// </summary>
    private static void AddPolygon(GeometryGroup group, IList<Point> pts)
    {
        double area = 0;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            area += pts[j].X * pts[i].Y - pts[i].X * pts[j].Y;
        if (area < 0) pts = pts.Reverse().ToList();
        var fig = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
        fig.Segments.Add(new PolyLineSegment(pts.Skip(1), false));
        var g = new PathGeometry { FillRule = FillRule.Nonzero };
        g.Figures.Add(fig);
        group.Children.Add(g);
    }

    private static Point[] DiscPolygon(Disc d)
    {
        int n = d.R < 2 ? 8 : d.R < 6 ? 16 : 24;
        var pts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double t = i * Math.PI * 2 / n;
            pts[i] = new Point(d.P.X + d.R * Math.Cos(t), d.P.Y + d.R * Math.Sin(t));
        }
        return pts;
    }

    private static List<StrokePoint> StrokePoints(IList<Point> points, double size, bool complete)
    {
        var res = new List<StrokePoint>();
        if (points.Count == 0) return res;
        double t = MinStreamlineT + (1 - Streamline) * StreamlineTRange;
        var pts = new List<Point>(points);
        if (pts.Count == 2)
        {
            Point last = pts[1];
            pts.RemoveAt(1);
            for (int i = 1; i < 5; i++) pts.Add(Lerp(pts[0], last, i / 4.0));
        }
        if (pts.Count == 1) pts.Add(pts[0] + new Vector(1, 1));

        res.Add(new StrokePoint(pts[0], DefaultFirstPressure, new Vector(1, 1), 0, 0));
        bool reachedMin = false;
        double running = 0;
        var prev = res[0];
        int max = pts.Count - 1;
        for (int i = 1; i < pts.Count; i++)
        {
            Point point = complete && i == max ? pts[i] : Lerp(prev.P, pts[i], t);
            if (point == prev.P) continue;
            double distance = (point - prev.P).Length;
            running += distance;
            if (i < max && !reachedMin)
            {
                if (running < size) continue;
                reachedMin = true;
            }
            Vector v = prev.P - point;
            v.Normalize();
            prev = new StrokePoint(point, DefaultPressure, v, distance, running);
            res.Add(prev);
        }
        res[0] = res[0] with { V = res.Count > 1 ? res[1].V : new Vector(0, 0) };
        return res;
    }

    /// <summary>Per-point radius from simulated pressure (perfect-freehand's getStrokeRadius).</summary>
    private static List<Disc> Discs(List<StrokePoint> points, double size)
    {
        var res = new List<Disc>();
        if (points.Count == 0 || size <= 0) return res;
        double totalLength = points[^1].Running;
        double prevPressure = InitialPressure(points, size);
        for (int i = 0; i < points.Count; i++)
        {
            var sp = points[i];
            bool isLast = i == points.Count - 1;
            if (!isLast && totalLength - sp.Running < EndNoiseThreshold) continue;
            double pressure = SimulatePressure(prevPressure, sp.Distance, size);
            double radius = Math.Max(MinRadius, StrokeRadius(size, pressure));
            res.Add(new Disc(sp.P, radius));
            prevPressure = pressure;
        }
        return res;
    }

    /// <summary>Averages the first few points so slow starts don't produce fat blobs.</summary>
    private static double InitialPressure(List<StrokePoint> points, double size)
    {
        double acc = points[0].Pressure;
        foreach (var p in points.Take(10))
            acc = (acc + SimulatePressure(acc, p.Distance, size)) / 2;
        return acc;
    }

    private static double SimulatePressure(double prevPressure, double distance, double size)
    {
        double sp = Math.Min(1, distance / size);
        double rp = Math.Min(1, 1 - sp);
        return Math.Min(1, prevPressure + (rp - prevPressure) * (sp * RateOfPressureChange));
    }

    private static double StrokeRadius(double size, double pressure) =>
        size * (0.5 - Thinning * (0.5 - pressure));

    private static Point Lerp(Point a, Point b, double t) => a + (b - a) * t;
}
