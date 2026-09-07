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
/// The line quality of a mark: the ruled default, or a hand-drawn look that perturbs the
/// same geometry. Stored in <c>AnnotationData.Sketch</c> together with the seed, so a
/// sketchy mark redraws identically after a move, an undo or a project reload instead of
/// re-scribbling itself every frame.
/// </summary>
public enum StrokeRender
{
    Clean,
    Sketchy,
}

/// <summary>
/// Pure geometry for the two stroke qualities and for simplifying a freehand scribble.
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

    public static StrokeRender ParseRender(string? name) =>
        Enum.TryParse(name, out StrokeRender r) ? r : StrokeRender.Clean;

    // ------------------------------------------------------------------ sketchy

    /// <summary>
    /// How far, in pixels, a sketchy line may wander off true at its loosest point. Small
    /// enough that a 3px rectangle still reads as a rectangle; large enough to be visible
    /// at 100 %.
    /// </summary>
    public const double SketchAmplitude = 2.4;

    /// <summary>
    /// One hand-drawn pass over a straight run: the segment resampled into a few points,
    /// each pushed sideways by a seeded amount that starts and ends at zero — so the ends
    /// still meet exactly where they were asked to, and only the middle wobbles. A shape
    /// whose corners drifted would stop being a shape.
    /// </summary>
    public static List<Point> SketchSegment(Point from, Point to, int seed, double amplitude = SketchAmplitude)
    {
        var points = new List<Point> { from };
        Vector along = to - from;
        double length = along.Length;
        if (length < 1e-6) { points.Add(to); return points; }

        // One waypoint per ~24px, two minimum, capped so a very long line does not become
        // a polyline of hundreds of points.
        int steps = Math.Clamp((int)Math.Round(length / 24), 2, 12);
        var normal = new Vector(-along.Y / length, along.X / length);
        var rng = new Random(seed);

        for (int i = 1; i < steps; i++)
        {
            double t = i / (double)steps;
            // sin(pi*t) is 0 at both ends and 1 in the middle: the wobble tapers into the
            // endpoints instead of being clipped there.
            double taper = Math.Sin(Math.PI * t);
            double offset = (rng.NextDouble() * 2 - 1) * amplitude * taper;
            points.Add(from + along * t + normal * offset);
        }
        points.Add(to);
        return points;
    }

    /// <summary>
    /// A sketchy version of a closed polygon (a rectangle's four corners, an ellipse's
    /// sampled outline). Each edge gets its own seed derived from the shape's, so editing
    /// one edge's length does not reshuffle the others.
    /// </summary>
    public static List<Point> SketchPolygon(IReadOnlyList<Point> corners, int seed)
    {
        var result = new List<Point>();
        if (corners.Count < 2) return new List<Point>(corners);

        for (int i = 0; i < corners.Count; i++)
        {
            Point a = corners[i];
            Point b = corners[(i + 1) % corners.Count];
            var run = SketchSegment(a, b, unchecked(seed * 397 + i));
            // The run repeats the previous edge's end point; drop it. The LAST edge also
            // ends back on the start point, and the figure closes itself — repeating it
            // would be a zero-length segment at the seam.
            int skipHead = i == 0 ? 0 : 1;
            int dropTail = i == corners.Count - 1 ? 1 : 0;
            result.AddRange(run.Skip(skipHead).Take(run.Count - skipHead - dropTail));
        }
        return result;
    }

    /// <summary>The four corners of a rect, clockwise from the top-left.</summary>
    public static Point[] RectCorners(Rect r) =>
    [
        r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft,
    ];

    /// <summary>An ellipse sampled as a closed polygon, fine enough to still read as a curve.</summary>
    public static Point[] EllipsePoints(Rect r, int samples = 40)
    {
        samples = Math.Max(8, samples);
        double cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        double rx = r.Width / 2, ry = r.Height / 2;
        var pts = new Point[samples];
        for (int i = 0; i < samples; i++)
        {
            double a = 2 * Math.PI * i / samples;
            pts[i] = new Point(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a));
        }
        return pts;
    }

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
