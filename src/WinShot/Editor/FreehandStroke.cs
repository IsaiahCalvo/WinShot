using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Port of perfect-freehand (steveruizok, MIT): turns raw pointer samples into a
/// pressure-shaped outline polygon. Pressure is simulated from speed, so fast strokes
/// thin out and slow strokes thicken, with round caps and streamlined (lerped) input.
/// Options are the library defaults: thinning .5, smoothing .5, streamline .5, no taper.
/// </summary>
internal static class FreehandStroke
{
    private const double RateOfPressureChange = 0.275;
    private const double FixedPi = Math.PI + 0.0001;
    private const int StartCapSegments = 13, EndCapSegments = 29, CornerCapSegments = 13;
    private const double EndNoiseThreshold = 3, MinStreamlineT = 0.15, StreamlineTRange = 0.85;
    private const double MinRadius = 0.01, DefaultFirstPressure = 0.25, DefaultPressure = 0.5;

    public const double Thinning = 0.5, Smoothing = 0.5, Streamline = 0.5;

    private readonly record struct StrokePoint(Point P, double Pressure, Vector V, double Distance, double Running);

    /// <summary>Filled outline for <paramref name="input"/> at base diameter <paramref name="size"/>.</summary>
    public static PathGeometry Outline(IList<Point> input, double size, bool complete)
    {
        var outline = OutlinePoints(StrokePoints(input, size, complete), size);
        var g = new PathGeometry { FillRule = FillRule.Nonzero };
        if (outline.Count < 2) return g;
        var fig = new PathFigure { StartPoint = outline[0], IsClosed = true, IsFilled = true };
        fig.Segments.Add(new PolyLineSegment(outline.Skip(1), false));
        g.Figures.Add(fig);
        return g;
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

    private static List<Point> OutlinePoints(List<StrokePoint> points, double size)
    {
        var res = new List<Point>();
        if (points.Count == 0 || size <= 0) return res;
        double totalLength = points[^1].Running;
        double minDistance = Math.Pow(size * Smoothing, 2);
        var left = new List<Point>();
        var right = new List<Point>();
        double prevPressure = InitialPressure(points, size);
        double radius = StrokeRadius(size, points[^1].Pressure);
        double? firstRadius = null;
        Vector prevVector = points[0].V;
        Point prevLeft = points[0].P, prevRight = prevLeft;
        Point tl = prevLeft, tr = prevRight;
        bool prevSharp = false;

        for (int i = 0; i < points.Count; i++)
        {
            var sp = points[i];
            bool isLast = i == points.Count - 1;
            if (!isLast && totalLength - sp.Running < EndNoiseThreshold) continue;

            double pressure = SimulatePressure(prevPressure, sp.Distance, size);
            radius = Math.Max(MinRadius, StrokeRadius(size, pressure));
            firstRadius ??= radius;

            Vector nextVector = (!isLast ? points[i + 1] : points[i]).V;
            double nextDpr = !isLast ? Dot(sp.V, nextVector) : 1.0;
            double prevDpr = Dot(sp.V, prevVector);
            bool sharp = prevDpr < 0 && !prevSharp;
            bool nextSharp = nextDpr < 0;

            if (sharp || nextSharp)
            {
                // Sharp corner: a rounded cap at this point on both sides.
                Vector offset = Per(prevVector) * radius;
                double step = 1.0 / CornerCapSegments;
                for (double u = 0; u <= 1; u += step)
                {
                    tl = Rot(sp.P - offset, sp.P, FixedPi * u); left.Add(tl);
                    tr = Rot(sp.P + offset, sp.P, FixedPi * -u); right.Add(tr);
                }
                prevLeft = tl; prevRight = tr;
                if (nextSharp) prevSharp = true;
                continue;
            }
            prevSharp = false;

            if (isLast)
            {
                Vector off = Per(sp.V) * radius;
                left.Add(sp.P - off);
                right.Add(sp.P + off);
                continue;
            }

            Vector o = Per(Lerp(nextVector, sp.V, nextDpr)) * radius;
            tl = sp.P - o;
            if (i <= 1 || Dist2(prevLeft, tl) > minDistance) { left.Add(tl); prevLeft = tl; }
            tr = sp.P + o;
            if (i <= 1 || Dist2(prevRight, tr) > minDistance) { right.Add(tr); prevRight = tr; }

            prevPressure = pressure;
            prevVector = sp.V;
        }

        Point first = points[0].P;
        if (points.Count == 1 || right.Count == 0 || left.Count == 0)
            return Dot(first, firstRadius ?? radius);
        Point last = points[^1].P;

        var startCap = new List<Point>();
        double capStep = 1.0 / StartCapSegments;
        for (double u = capStep; u <= 1; u += capStep) startCap.Add(Rot(right[0], first, FixedPi * u));

        var endCap = new List<Point>();
        Vector dir = Per(-points[^1].V);
        Point endStart = last + dir * radius;
        double endStep = 1.0 / EndCapSegments;
        for (double u = endStep; u < 1; u += endStep) endCap.Add(Rot(endStart, last, FixedPi * 3 * u));

        right.Reverse();
        res.AddRange(left); res.AddRange(endCap); res.AddRange(right); res.AddRange(startCap);
        return res;
    }

    private static List<Point> Dot(Point center, double radius)
    {
        Point offsetPoint = center + new Vector(1, 1);
        Vector n = Per(center - offsetPoint);
        n.Normalize();
        Point start = center + n * -radius;
        var pts = new List<Point>();
        double step = 1.0 / StartCapSegments;
        for (double u = step; u <= 1; u += step) pts.Add(Rot(start, center, FixedPi * 2 * u));
        return pts;
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
    private static Vector Lerp(Vector a, Vector b, double t) => a + (b - a) * t;
    private static Vector Per(Vector a) => new(a.Y, -a.X);
    private static double Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y;
    private static double Dist2(Point a, Point b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    private static Point Rot(Point a, Point c, double r)
    {
        double s = Math.Sin(r), k = Math.Cos(r);
        double px = a.X - c.X, py = a.Y - c.Y;
        return new Point(px * k - py * s + c.X, px * s + py * k + c.Y);
    }
}
