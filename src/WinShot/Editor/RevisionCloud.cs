using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;

namespace WinShot.Editor;

/// <summary>Shape families the crown engine fits a revision cloud to.</summary>
internal enum CloudKind
{
    /// <summary>Axis-aligned box: the four corners, with Survey's rectangleFit crown width.</summary>
    Rectangle,

    /// <summary>Ellipse or circle: the bounding box, walked as an arc-length table.</summary>
    Ellipse,

    /// <summary>Closed vertex chain.</summary>
    Polygon,

    /// <summary>Open vertex chain (Survey's 'polyline' and 'freehand' are the same run to the engine).</summary>
    Polyline,
}

/// <summary>
/// One crown. The half-ellipse is two quarter-ellipse cubics (start-&gt;apex, apex-&gt;end);
/// the third cubic is the separator hook that carries on 0.28 rad past the baseline and
/// curves back inward. <see cref="Controls"/> is always a multiple of three points.
/// </summary>
internal sealed class CloudLobe
{
    public Point Start;
    public Point End;

    /// <summary>Where the half-ellipse itself ends, before the separator hook.</summary>
    public Point? BaseEnd;

    public Point Apex;
    public double Width;
    public double Depth;
    public Point[] Controls = [];

    public CloudLobe Copy() => (CloudLobe)MemberwiseClone();
}

/// <summary>An absolute <c>M</c> (one point) or <c>C</c> (two controls plus an endpoint).</summary>
internal readonly record struct CloudCommand(char Verb, Point P1, Point P2, Point P3)
{
    public static CloudCommand Move(Point p) => new('M', p, default, default);

    public static CloudCommand Curve(Point c1, Point c2, Point end) => new('C', c1, c2, end);
}

/// <summary>One painted subpath group: a corner's crowns, an edge's row, or a whole ellipse.</summary>
internal sealed class CloudRun
{
    public string Id = string.Empty;
    public List<CloudCommand> Commands = [];
    public List<CloudLobe> Lobes = [];
}

/// <summary>The vertices plus the per-vertex measurements the engine reads.</summary>
internal sealed class CloudShape
{
    public CloudKind Kind;
    public Point[] Points = [];
    public double[] Angles = [];
    public double[] Bends = [];
    public double[] Clearance = [];
    public double Size = 28;
    public double Depth = 12;
    public int Side = 1;

    public bool Closed => Kind != CloudKind.Polyline;

    public CloudShape Copy() => (CloudShape)MemberwiseClone();
}

/// <summary>
/// Survey's revision-cloud engine, ported from the approved reference
/// <c>tests/fixtures/reference/cloud-v17.ts</c> (studio <c>lib/cloud.ts</c> @ d1abe78,
/// tag <c>cloud-final-short-gap</c>) — the same file Survey's own
/// <c>src/utils/revisionCloudGeometry.js</c> is a port of.
///
/// Cloud is a BORDER STYLE, not a stroke decoration: the shape is REPLACED by crowns
/// built from its vertices. Everything emitted is cubic — one <c>M</c> per crown, so each
/// crown is its own open subpath — with coordinates rounded to 5 decimals exactly as the
/// reference does, because that rounding is part of the approved output.
///
/// Pure geometry: no WPF types beyond <see cref="Point"/>, no Application, no clock. The
/// reference's <c>stepCount</c>/<c>states</c>/<c>now</c>/<c>animate</c> are vestigial —
/// crown counts are a pure function of the vertices — and are not ported.
/// Screen coordinates, y down; <see cref="CloudShape.Side"/> comes from the shoelace area.
/// </summary>
internal static class RevisionCloud
{
    /// <summary>Circle-to-cubic constant: a quarter ellipse's control offset.</summary>
    internal const double K = 0.5522847498307936;

    /// <summary>Studio default scallop size (Bump 2).</summary>
    public const double DefaultSize = 28;

    /// <summary>
    /// Roundness on the engine's legacy 2..40 scale. The app exposes no roundness control
    /// and the approved studio build shipped 12, so every cloud passes exactly this;
    /// pre-scaling it was one of the three v17 bugs.
    /// </summary>
    public const double ApprovedDepth = 12;

    // arc()'s separator hook runs 0.28 rad past the baseline. cos/sin/tan of a literal are
    // the only transcendentals evaluated per crown, and V8 and the CRT need not agree on
    // their last bit, so the reference's own values are pinned here.
    private const double HookAngleCos = 0.9610554383107709;   // Math.cos(0.28)
    private const double HookAngleSin = 0.27635564856411376;  // Math.sin(0.28)
    private const double HookK = 0.09348607716267028;         // (4 / 3) * Math.tan(0.28 / 4)

    // ---------------------------------------------------------------------
    // Scalars
    // ---------------------------------------------------------------------

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    /// <summary>Cubic smoothstep, the reference's <c>smooth()</c>.</summary>
    private static double Smooth(double v)
    {
        double t = Clamp(v, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// V8's <c>Math.hypot</c> (compensated summation over the largest term), not
    /// <c>sqrt(x*x + y*y)</c>: a last-bit difference in a chord length can move a crown
    /// count across <c>Math.floor(span / pitch + 1e-9)</c>.
    /// </summary>
    internal static double Hypot(double x, double y)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y);
        if (double.IsInfinity(ax) || double.IsInfinity(ay)) return double.PositiveInfinity;
        if (double.IsNaN(ax) || double.IsNaN(ay)) return double.NaN;
        double max = Math.Max(ax, ay);
        if (max == 0) max = 1;

        double n = ax / max;
        double summand = n * n;
        double sum = summand;
        double compensation = (sum - 0) - summand;

        n = ay / max;
        summand = n * n - compensation;
        double preliminary = sum + summand;
        compensation = (preliminary - sum) - summand;
        sum = preliminary;

        return Math.Sqrt(sum) * max;
    }

    // fdlibm's __kernel_sin / __kernel_cos, the polynomials V8 evaluates for |x| <= pi/4
    // (src/base/ieee754.cc). The Windows CRT's Math.Sin is one ulp from V8's at the
    // 45-degree corner bisector, and the reference decides whether a crown grows its
    // separator hook by testing d / w against 0.88 when d / w IS 0.88 — so one ulp there
    // changes what gets painted. Verified bit-identical to V8 over 200,000 samples each;
    // outside the kernel range the CRT already agrees, so it is used unchanged.
    private const double S1 = -1.66666666666666324348e-01;
    private const double S2 = 8.33333333332248946124e-03;
    private const double S3 = -1.98412698298579493134e-04;
    private const double S4 = 2.75573137070700676789e-06;
    private const double S5 = -2.50507602534068634195e-08;
    private const double S6 = 1.58969099521155010221e-10;
    private const double C1 = 4.16666666666666019037e-02;
    private const double C2 = -1.38888888888741095749e-03;
    private const double C3 = 2.48015872894767294178e-05;
    private const double C4 = -2.75573143513906633035e-07;
    private const double C5 = 2.08757232129817482790e-09;
    private const double C6 = -1.13596475577881948265e-11;

    /// <summary>fdlibm's own test: the high word of |x| at or below pi/4's.</summary>
    private static bool InKernelRange(double x, out uint high)
    {
        high = (uint)((BitConverter.DoubleToInt64Bits(x) >> 32) & 0x7fffffff);
        return high <= 0x3fe921fb;
    }

    internal static double Sin(double x)
    {
        if (!InKernelRange(x, out _)) return Math.Sin(x);
        double z = x * x, v = z * x;
        double r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));
        return x + v * (S1 + z * r);
    }

    internal static double Cos(double x)
    {
        if (!InKernelRange(x, out uint ix)) return Math.Cos(x);
        double z = x * x;
        double r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));
        if (ix < 0x3FD33333) return 1 - (0.5 * z - z * r);
        double qx = ix > 0x3fe90000
            ? 0.28125
            : BitConverter.Int64BitsToDouble((long)(ix - 0x00200000) << 32);
        return (1 - qx) - (0.5 * z - qx - z * r);
    }

    private static double Dist(Point a, Point b) => Hypot(a.X - b.X, a.Y - b.Y);

    private static Point Add(Point a, Point b, double k = 1) => new(a.X + b.X * k, a.Y + b.Y * k);

    private static Point Mix(Point a, Point b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static Point Sub(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);

    private static double Cross(Point a, Point b) => a.X * b.Y - a.Y * b.X;

    /// <summary>
    /// The reference's <c>num = v =&gt; Number(v.toFixed(5))</c>. JS toFixed rounds half
    /// away from zero on the exact value, and prints -0 as 0.
    /// </summary>
    internal static double Round5(double v)
    {
        double r = Math.Round(v, 5, MidpointRounding.AwayFromZero);
        return r == 0 ? 0 : r;
    }

    private static Point Round5(Point p) => new(Round5(p.X), Round5(p.Y));

    // ---------------------------------------------------------------------
    // Shape measurement
    // ---------------------------------------------------------------------

    /// <summary>Shoelace signed area; its sign picks which way the crowns bulge.</summary>
    public static double Area(IReadOnlyList<Point> points)
    {
        double sum = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Point p = points[i], q = points[(i + 1) % points.Count];
            sum += p.X * q.Y - q.X * p.Y;
        }
        return sum / 2;
    }

    /// <summary>Bisector heading at vertex <paramref name="i"/>; the corner crown's chord direction.</summary>
    public static double Heading(IReadOnlyList<Point> points, int i, bool closed)
    {
        Point p = points[i];
        Point prev = points[i != 0 ? i - 1 : closed ? points.Count - 1 : 0];
        Point next = points[i + 1 < points.Count ? i + 1 : closed ? 0 : i];
        double a = Math.Atan2(p.Y - prev.Y, p.X - prev.X);
        double b = Math.Atan2(next.Y - p.Y, next.X - p.X);
        double ax = Cos(a) + Cos(b);
        double ay = Sin(a) + Sin(b);
        if (!closed && i == 0)
        {
            ax = Cos(b);
            ay = Sin(b);
        }
        if (!closed && i == points.Count - 1)
        {
            ax = Cos(a);
            ay = Sin(a);
        }
        if (Hypot(ax, ay) < 1e-6)
        {
            ax = Cos(b);
            ay = Sin(b);
        }
        return Math.Atan2(ay, ax);
    }

    /// <summary>Signed turn at a vertex; negative is re-entrant once multiplied by side.</summary>
    private static double BendAt(IReadOnlyList<Point> points, int i)
    {
        Point p = points[i];
        Point a = points[(i + points.Count - 1) % points.Count];
        Point b = points[(i + 1) % points.Count];
        double d = Math.Atan2(b.Y - p.Y, b.X - p.X) - Math.Atan2(p.Y - a.Y, p.X - a.X);
        return Math.Atan2(Sin(d), Cos(d));
    }

    /// <summary>Distance to the nearer neighbour; shrinks a crown that would not fit.</summary>
    private static double ClearanceAt(IReadOnlyList<Point> points, int i, bool closed)
    {
        double prev = i > 0 ? Dist(points[i], points[i - 1])
            : closed ? Dist(points[i], points[^1]) : double.PositiveInfinity;
        double next = i < points.Count - 1 ? Dist(points[i], points[i + 1])
            : closed ? Dist(points[i], points[0]) : double.PositiveInfinity;
        return Math.Min(prev, next);
    }

    private static Rect BoundsOf(IReadOnlyList<Point> points)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (Point p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    public static CloudShape MakeShape(
        CloudKind kind, IReadOnlyList<Point> points, double size = DefaultSize, double depth = ApprovedDepth)
    {
        bool closed = kind != CloudKind.Polyline;
        int side = closed && Area(points) < 0 ? -1 : 1;
        var copy = new Point[points.Count];
        var angles = new double[points.Count];
        var bends = new double[points.Count];
        var clearance = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            copy[i] = points[i];
            angles[i] = Heading(points, i, closed);
            bends[i] = BendAt(points, i);
            clearance[i] = ClearanceAt(points, i, closed);
        }
        return new CloudShape
        {
            Kind = kind,
            Points = copy,
            Angles = angles,
            Bends = bends,
            Clearance = clearance,
            Size = size,
            Depth = depth,
            Side = side,
        };
    }

    // ---------------------------------------------------------------------
    // Crowns
    // ---------------------------------------------------------------------

    /// <summary>
    /// One crown over the chord start-&gt;end: a half ellipse with semi-axes width/2 along
    /// the chord and <paramref name="depth"/> along the outward normal, as three cubics.
    /// The third runs 0.28 rad past the baseline and hooks back inward — that is the
    /// visible separator between crowns, not a circular arc.
    /// </summary>
    public static CloudLobe Arc(Point start, Point end, double depth, int side = 1)
    {
        double width = Dist(start, end);
        Point u = width > 1e-8
            ? new Point((end.X - start.X) / width, (end.Y - start.Y) / width)
            : new Point(1, 0);
        Point n = new(u.Y * side, -u.X * side);
        Point mid = Mix(start, end, 0.5);
        Point apex = Add(mid, n, depth);
        double r = width / 2;
        Point tail = Add(Add(mid, u, r * HookAngleCos), n, -depth * HookAngleSin);
        return new CloudLobe
        {
            Start = start,
            BaseEnd = end,
            End = tail,
            Apex = apex,
            Width = width,
            Depth = depth,
            Controls =
            [
                Add(start, n, depth * K),
                Add(apex, u, -r * K),
                apex,
                Add(apex, u, r * K),
                Add(end, n, depth * K),
                end,
                Add(end, n, -depth * HookK),
                Add(Add(tail, u, r * HookAngleSin * HookK), n, depth * HookAngleCos * HookK),
                tail,
            ],
        };
    }

    /// <summary>
    /// The crown at a vertex. Its chord centre is pulled inward by depth so the apex lands
    /// exactly on the vertex, along the bisector heading.
    /// </summary>
    public static CloudLobe Corner(CloudShape shape, int i)
    {
        Point p = shape.Points[i];
        double angle = shape.Angles[i];
        Point u = new(Cos(angle), Sin(angle));
        Point n = new(u.Y * shape.Side, -u.X * shape.Side);
        Point center = Add(p, n, -shape.Depth);
        return Arc(Add(center, u, -shape.Size / 2), Add(center, u, shape.Size / 2), shape.Depth, shape.Side);
    }

    /// <summary>Independent strokes preserve the rounded end of each full-size scallop.</summary>
    private static List<CloudCommand> Path(IReadOnlyList<CloudLobe> lobes)
    {
        var commands = new List<CloudCommand>();
        foreach (CloudLobe l in lobes)
        {
            commands.Add(CloudCommand.Move(Round5(l.Start)));
            for (int i = 0; i < l.Controls.Length; i += 3)
            {
                commands.Add(CloudCommand.Curve(
                    Round5(l.Controls[i]), Round5(l.Controls[i + 1]), Round5(l.Controls[i + 2])));
            }
        }
        return commands;
    }

    // ---------------------------------------------------------------------
    // Cubic plumbing
    // ---------------------------------------------------------------------

    private static (Point[] Left, Point[] Right) Split(Point[] c, double t)
    {
        Point a = Mix(c[0], c[1], t), b = Mix(c[1], c[2], t), d = Mix(c[2], c[3], t);
        Point e = Mix(a, b, t), f = Mix(b, d, t), g = Mix(e, f, t);
        return ([c[0], a, e, g], [g, f, d, c[3]]);
    }

    private static List<Point[]> Cubics(CloudLobe l)
    {
        var result = new List<Point[]>();
        Point p = l.Start;
        for (int i = 0; i < l.Controls.Length; i += 3)
        {
            Point[] c = [p, l.Controls[i], l.Controls[i + 1], l.Controls[i + 2]];
            result.Add(c);
            p = c[3];
        }
        return result;
    }

    /// <summary>de Casteljau trim of a lobe between two fractional cubic indices.</summary>
    private static CloudLobe CutLobe(CloudLobe l, double from, double to)
    {
        var controls = new List<Point>();
        Point start = l.Start, end = l.End;
        for (int i = 0; i < l.Controls.Length / 3; i++)
        {
            double lo = Clamp(from - i, 0, 1), hi = Clamp(to - i, 0, 1);
            if (hi - lo < 1e-9) continue;
            Point[] c = [i != 0 ? l.Controls[3 * i - 1] : l.Start, l.Controls[3 * i], l.Controls[3 * i + 1], l.Controls[3 * i + 2]];
            if (hi < 1) c = Split(c, hi).Left;
            if (lo > 0) c = Split(c, lo / hi).Right;
            if (controls.Count == 0) start = c[0];
            controls.Add(c[1]);
            controls.Add(c[2]);
            controls.Add(c[3]);
            end = c[3];
        }
        CloudLobe cut = l.Copy();
        cut.Start = start;
        cut.End = end;
        cut.Controls = [.. controls];
        return cut;
    }

    private static double CubicCoordinate(double a, double b, double c, double d, double t)
    {
        double ab = a + (b - a) * t, bc = b + (c - b) * t, cd = c + (d - c) * t;
        double abc = ab + (bc - ab) * t, bcd = bc + (cd - bc) * t;
        return abc + (bcd - abc) * t;
    }

    private static Point CubicAt(Point[] c, double t) => new(
        CubicCoordinate(c[0].X, c[1].X, c[2].X, c[3].X, t),
        CubicCoordinate(c[0].Y, c[1].Y, c[2].Y, c[3].Y, t));

    private static double CubicDerivative(double a, double b, double c, double d, double t)
    {
        double ab = b - a, bc = c - b, cd = d - c;
        double abc = ab + (bc - ab) * t, bcd = bc + (cd - bc) * t;
        return (abc + (bcd - abc) * t) * 3;
    }

    private static Point CubicTangent(Point[] c, double t) => new(
        CubicDerivative(c[0].X, c[1].X, c[2].X, c[3].X, t),
        CubicDerivative(c[0].Y, c[1].Y, c[2].Y, c[3].Y, t));

    /// <summary>Curve-curve crossing by flattened first hit then Newton refinement.</summary>
    private static (double T, double U)? Intersect(Point[] a, Point[] b)
    {
        Rect ab = BoundsOf(a), bb = BoundsOf(b);
        if (ab.X > bb.X + bb.Width || bb.X > ab.X + ab.Width ||
            ab.Y > bb.Y + bb.Height || bb.Y > ab.Y + ab.Height) return null;

        const int N = 16;
        var ap = new Point[N + 1];
        var bp = new Point[N + 1];
        for (int i = 0; i <= N; i++)
        {
            ap[i] = CubicAt(a, (double)i / N);
            bp[i] = CubicAt(b, (double)i / N);
        }
        for (int i = 0; i < N; i++)
        {
            Point p = ap[i];
            double rx = ap[i + 1].X - p.X, ry = ap[i + 1].Y - p.Y;
            for (int j = 0; j < N; j++)
            {
                Point q = bp[j];
                double vx = bp[j + 1].X - q.X, vy = bp[j + 1].Y - q.Y;
                double den = rx * vy - ry * vx;
                if (Math.Abs(den) < 1e-10) continue;
                double dx = q.X - p.X, dy = q.Y - p.Y;
                double x = (dx * vy - dy * vx) / den, y = (dx * ry - dy * rx) / den;
                if (x < 0 || x > 1 || y < 0 || y > 1) continue;
                double t = (i + x) / N, u = (j + y) / N;
                for (int k = 0; k < 10; k++)
                {
                    Point delta = Sub(CubicAt(b, u), CubicAt(a, t));
                    Point da = CubicTangent(a, t), db = CubicTangent(b, u);
                    double det = Cross(da, db);
                    if (Math.Abs(det) < 1e-12) break;
                    t += Cross(delta, db) / det;
                    u += Cross(delta, da) / det;
                }
                if (t > 1e-7 && t < 1 - 1e-7 && u > 1e-7 && u < 1 - 1e-7 &&
                    Dist(CubicAt(a, t), CubicAt(b, u)) < 1e-5)
                    return (t, u);
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // Overlap trimming
    // ---------------------------------------------------------------------

    /// <summary>
    /// Restore a visible divider without drawing the hidden native loop. Its position
    /// follows the existing crossing; crown positions and counts never change.
    /// </summary>
    private static Point[]? ReplacementTail(CloudLobe prev, double crossing, double d, Point u, Point? inside = null)
    {
        double w = prev.Width, r = d / w;
        if (r <= 1e-8 || r >= 0.88 || prev.Depth <= 1e-8) return null;
        CloudLobe native = CutLobe(prev, crossing, 3);
        List<Point[]> nc = Cubics(native);
        if (nc.Count != 2) return null;
        double alpha = Smooth((r - 0.65) / 0.23);
        double length = 0.75 * prev.Depth * Smooth((r - 0.12) / 0.2);
        double slope = Math.Sqrt(Math.Max(0, 1 - r * r)) / (2 * (prev.Depth / w) * Math.Max(r, 1e-9));
        double bend = Math.Min(Math.Min(0.4 * length, 0.25 * slope * length), Math.Min(0.08 * w, d * 0.2));
        Point center = Mix(prev.Start, prev.BaseEnd!.Value, 0.5);
        Point inward = inside ?? new Point(
            (center.X - prev.Apex.X) / prev.Depth,
            (center.Y - prev.Apex.Y) / prev.Depth);
        Point p = native.Start;
        Point[] synthetic =
        [
            p,
            Add(Add(p, u, 0.3 * bend), inward, 0.2 * length),
            Add(Add(p, u, bend), inward, 0.6 * length),
            Add(Add(p, u, bend), inward, length),
        ];
        (Point[] left, Point[] right) = Split(synthetic, 0.5);
        var tail = new Point[6];
        for (int k = 0; k < 3; k++) tail[k] = Mix(left[k + 1], nc[0][k + 1], alpha);
        for (int k = 0; k < 3; k++) tail[k + 3] = Mix(right[k + 1], nc[1][k + 1], alpha);
        return tail;
    }

    /// <summary>
    /// Trim a straight row of equal crowns. Equal-radius ellipses cross half way between
    /// their centres, so the crossing is solved analytically on the normalised
    /// quarter-ellipse cubic with 7 Newton steps rather than by curve intersection.
    /// </summary>
    private static List<CloudLobe> OverlapAligned(List<CloudLobe> lobes, Point u)
    {
        var from = new double[lobes.Count];
        var to = new double[lobes.Count];
        Point[]?[] continuations = new Point[lobes.Count][];
        for (int i = 0; i < lobes.Count; i++) to[i] = lobes[i].Controls.Length / 3.0;

        for (int i = 1; i < lobes.Count; i++)
        {
            CloudLobe prev = lobes[i - 1], l = lobes[i];
            double w = l.Width;
            double d = (l.Start.X - prev.Start.X) * u.X + (l.Start.Y - prev.Start.Y) * u.Y;
            double target = Clamp(1 - d / w, 0, 1);
            double t = Math.Sqrt(target);
            for (int step = 0; step < 7 && target > 1e-12; step++)
            {
                double value = 3 * (1 - K) * t * t + (3 * K - 2) * t * t * t;
                double derivative = 6 * (1 - K) * t + 3 * (3 * K - 2) * t * t;
                t = Clamp(t - (value - target) / Math.Max(derivative, 1e-12), 0, 1);
            }
            from[i] = t;
            double crossing = 2 - t;
            to[i - 1] = crossing + (to[i - 1] - crossing) * Smooth((d / w - 0.65) / 0.23);
            Point[]? tail = ReplacementTail(prev, crossing, d, u);
            if (tail is not null)
            {
                continuations[i - 1] = tail;
                to[i - 1] = crossing;
            }
        }
        return ApplyCuts(lobes, from, to, continuations);
    }

    private static List<CloudLobe> ApplyCuts(
        List<CloudLobe> lobes, double[] from, double[] to, Point[]?[] continuations)
    {
        var result = new List<CloudLobe>(lobes.Count);
        for (int i = 0; i < lobes.Count; i++)
        {
            CloudLobe cut = CutLobe(lobes[i], from[i], to[i]);
            Point[]? tail = continuations[i];
            if (tail is not null)
            {
                cut.End = tail[^1];
                cut.Controls = [.. cut.Controls, .. tail];
            }
            result.Add(cut);
        }
        return result;
    }

    private static (double T, double U)? EllipseIntersection(Point[] a, Point[] b, Point axis, bool canonical)
    {
        double ax0 = double.PositiveInfinity, ax1 = double.NegativeInfinity;
        double ay0 = double.PositiveInfinity, ay1 = double.NegativeInfinity;
        double bx0 = double.PositiveInfinity, bx1 = double.NegativeInfinity;
        double by0 = double.PositiveInfinity, by1 = double.NegativeInfinity;
        Point origin = a[0];
        for (int i = 0; i < 4; i++)
        {
            double dx = a[i].X - origin.X, dy = a[i].Y - origin.Y;
            double x = dx * axis.X + dy * axis.Y, y = dx * axis.Y - dy * axis.X;
            ax0 = Math.Min(ax0, x); ax1 = Math.Max(ax1, x);
            ay0 = Math.Min(ay0, y); ay1 = Math.Max(ay1, y);
            double ex = b[i].X - origin.X, ey = b[i].Y - origin.Y;
            double uu = ex * axis.X + ey * axis.Y, vv = ex * axis.Y - ey * axis.X;
            bx0 = Math.Min(bx0, uu); bx1 = Math.Max(bx1, uu);
            by0 = Math.Min(by0, vv); by1 = Math.Max(by1, vv);
        }
        const double eps = 1e-9;
        if (ax0 > bx1 + eps || bx0 > ax1 + eps || ay0 > by1 + eps || by0 > ay1 + eps) return null;
        return canonical ? AdjacentIntersection(a, b, axis) : Intersect(a, b);
    }

    /// <summary>
    /// The crossing between two adjacent ellipse crowns, when both halves are monotone
    /// along the chord axis: seeded at (0.75, 0.25) and Newton-refined, falling back to the
    /// general intersection the moment any of that stops holding.
    /// </summary>
    private static (double T, double U)? AdjacentIntersection(Point[] a, Point[] b, Point axis)
    {
        double Along(Point p) => p.X * axis.X + p.Y * axis.Y;

        static bool Forward(Point[] c, Point axis)
        {
            for (int i = 1; i < 4; i++)
                if ((c[i].X - c[i - 1].X) * axis.X + (c[i].Y - c[i - 1].Y) * axis.Y < -1e-12) return false;
            return true;
        }

        if (!Forward(a, axis) || !Forward(b, axis)) return Intersect(a, b);

        (Point[] ah0, Point[] ah1) = Split(a, 0.5);
        (Point[] bh0, _) = Split(b, 0.5);

        static bool Transverse(Point[] c, double sign, Point axis)
        {
            for (int i = 1; i < 4; i++)
                if (sign * ((c[i].X - c[i - 1].X) * axis.Y - (c[i].Y - c[i - 1].Y) * axis.X) < -1e-12) return false;
            return true;
        }

        // Shared axis range must be confined to these opposing monotone half-curves.
        if (Along(ah0[3]) >= Along(b[0]) || Along(bh0[3]) <= Along(a[3]) ||
            !Transverse(ah1, -1, axis) || !Transverse(bh0, 1, axis))
            return Intersect(a, b);

        double t = 0.75, u = 0.25;
        for (int k = 0; k < 5; k++)
        {
            Point delta = Sub(CubicAt(b, u), CubicAt(a, t));
            Point da = CubicTangent(a, t), db = CubicTangent(b, u);
            double det = Cross(da, db);
            if (Math.Abs(det) < 1e-12) return Intersect(a, b);
            t += Cross(delta, db) / det;
            u += Cross(delta, da) / det;
            if (t < -0.5 || t > 1.5 || u < -0.5 || u > 1.5) return Intersect(a, b);
        }
        if (t > 1e-7 && t < 1 - 1e-7 && u > 1e-7 && u < 1 - 1e-7 &&
            Dist(CubicAt(a, t), CubicAt(b, u)) < 1e-8) return (t, u);
        return Intersect(a, b);
    }

    /// <summary>
    /// Hide only the leading branch below a crossing. Keep the complete crown and the
    /// preceding arc's round trailing end; never clip both crowns.
    /// </summary>
    private static List<CloudLobe> OverlapTails(List<CloudLobe> lobes, bool closed)
    {
        var from = new double[lobes.Count];
        var to = new double[lobes.Count];
        Point[]?[] continuations = new Point[lobes.Count][];
        for (int i = 0; i < lobes.Count; i++) to[i] = lobes[i].Controls.Length / 3.0;

        for (int i = closed ? 0 : 1; i < lobes.Count; i++)
        {
            int j = (i + lobes.Count - 1) % lobes.Count;
            CloudLobe prev = lobes[j], l = lobes[i];
            List<Point[]> ac = Cubics(prev), bc = Cubics(l);
            bool found = false;
            for (int a = ac.Count - 1; a >= 0 && !found; a--)
            {
                for (int b = 0; b < bc.Count; b++)
                {
                    Point chord = new(
                        (prev.BaseEnd!.Value.X - prev.Start.X) / prev.Width,
                        (prev.BaseEnd!.Value.Y - prev.Start.Y) / prev.Width);
                    (double T, double U)? hit = closed
                        ? EllipseIntersection(ac[a], bc[b], chord, a == 1 && b == 0)
                        : Intersect(ac[a], bc[b]);
                    if (hit is null) continue;
                    from[i] = b + hit.Value.U;
                    double d = Dist(
                        Mix(prev.Start, prev.BaseEnd!.Value, 0.5),
                        Mix(l.Start, l.BaseEnd!.Value, 0.5));
                    double crossing = a + hit.Value.T;
                    to[j] = crossing + (to[j] - crossing) * Smooth((d / l.Width - 0.65) / 0.23);
                    Point pc = Mix(prev.Start, prev.BaseEnd!.Value, 0.5);
                    Point lc = Mix(l.Start, l.BaseEnd!.Value, 0.5);
                    double nx = (pc.X - prev.Apex.X) / prev.Depth + (lc.X - l.Apex.X) / l.Depth;
                    double ny = (pc.Y - prev.Apex.Y) / prev.Depth + (lc.Y - l.Apex.Y) / l.Depth;
                    double normalLength = Hypot(nx, ny);
                    if (a == 1 && b == 0 && normalLength > 1)
                    {
                        Point[]? tail = ReplacementTail(prev, crossing, d, chord,
                            new Point(nx / normalLength, ny / normalLength));
                        if (tail is not null)
                        {
                            CloudLobe tailCurve = prev.Copy();
                            tailCurve.Start = CutLobe(prev, 0, crossing).End;
                            tailCurve.End = tail[^1];
                            tailCurve.Controls = tail;
                            CloudLobe nextCurve = CutLobe(l, from[i], 3);
                            // On tight turns, reject a separator that would cross the next crown.
                            List<Point[]> nextCubics = Cubics(nextCurve);
                            bool loops = false;
                            foreach (Point[] ta in Cubics(tailCurve))
                            {
                                foreach (Point[] tb in nextCubics)
                                {
                                    if ((closed ? EllipseIntersection(ta, tb, chord, false) : Intersect(ta, tb)) is not null)
                                    {
                                        loops = true;
                                        break;
                                    }
                                }
                                if (loops) break;
                            }
                            if (!loops)
                            {
                                continuations[j] = tail;
                                to[j] = crossing;
                            }
                        }
                    }
                    found = true;
                    break;
                }
            }
        }
        return ApplyCuts(lobes, from, to, continuations);
    }

    /// <summary>
    /// Outward arcs overlap at a re-entrant vertex. Keep their outer pieces and discard the
    /// hidden loop; never reverse a scallop to manufacture a corner.
    /// </summary>
    private static void JoinConcave(CloudRun prev, CloudRun next)
    {
        for (int i = Math.Max(0, prev.Lobes.Count - 2); i < prev.Lobes.Count; i++)
        {
            List<Point[]> ac = Cubics(prev.Lobes[i]);
            for (int ai = 0; ai < ac.Count; ai++)
            {
                for (int j = Math.Min(1, next.Lobes.Count - 1); j >= 0; j--)
                {
                    List<Point[]> bc = Cubics(next.Lobes[j]);
                    for (int bi = bc.Count - 1; bi >= 0; bi--)
                    {
                        (double T, double U)? hit = Intersect(ac[ai], bc[bi]);
                        if (hit is null) continue;
                        Point[] left = Split(ac[ai], hit.Value.T).Left;
                        Point[] right = Split(bc[bi], hit.Value.U).Right;
                        right[0] = left[3];

                        CloudLobe a = prev.Lobes[i].Copy();
                        a.End = left[3];
                        var aControls = new List<Point>();
                        for (int k = 0; k < ai; k++) aControls.AddRange(ac[k][1..]);
                        aControls.AddRange(left[1..]);
                        a.Controls = [.. aControls];
                        prev.Lobes = [.. prev.Lobes.Take(i), a];

                        CloudLobe b = next.Lobes[j].Copy();
                        b.Start = left[3];
                        var bControls = new List<Point>(right[1..]);
                        for (int k = bi + 1; k < bc.Count; k++) bControls.AddRange(bc[k][1..]);
                        b.Controls = [.. bControls];
                        next.Lobes = [b, .. next.Lobes.Skip(j + 1)];

                        prev.Commands = Path(prev.Lobes);
                        next.Commands = Path(next.Lobes);
                        return;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // Rows
    // ---------------------------------------------------------------------

    /// <summary>
    /// One row of crowns along a straight span. Every row uses the same continuous entry at
    /// every length: an integer-count fit on longer rows would skip the separation between
    /// crowns. Pitch is 0.88w (12% overlap); the leftover is absorbed by a quintic
    /// smoothstep correction over the last three crowns plus a bounded relaxation.
    /// </summary>
    private static CloudRun RunBetween(
        string id, Point start, Point end, CloudShape shape, double shortCap = 0)
    {
        double length = Dist(start, end);
        double target = Math.Max(0.5, shape.Size);
        if (length < 1e-8) return new CloudRun { Id = id };
        Point u = new((end.X - start.X) / length, (end.Y - start.Y) / length);

        if (shortCap > 0 && length < target)
        {
            CloudLobe full = Arc(
                Add(Mix(start, end, 0.5), u, -target / 2),
                Add(Mix(start, end, 0.5), u, target / 2),
                shape.Depth, shape.Side);
            // Select the central part of the original ellipse instead of shrinking it.
            double lo = 0, hi = 1;
            for (int k = 0; k < 40; k++)
            {
                double t = (lo + hi) / 2, a = 1 - t;
                double x = -a * a * a - 3 * a * a * t - 3 * K * a * t * t;
                if (x < -length / target) lo = t; else hi = t;
            }
            double fromT = (lo + hi) / 2;
            // Keep both quarters even at a nearly empty span. Generic trimming drops tiny
            // pieces; their removal would change this cap's control layout.
            Point[] left = Split([full.Start, full.Controls[0], full.Controls[1], full.Controls[2]], fromT).Right;
            Point[] right = Split([full.Controls[2], full.Controls[3], full.Controls[4], full.Controls[5]], 1 - fromT).Left;
            Point capStart = left[0];
            var capControls = new List<Point>(left[1..]);
            capControls.AddRange(right[1..]);

            Point delta = new(start.X - capStart.X, start.Y - capStart.Y);
            Point Shift(Point p) => new(p.X + delta.X, p.Y + delta.Y);
            double tailWeight = Smooth((length / target - 0.75) / 0.25);
            Point nativeEnd = full.BaseEnd!.Value;
            var controls = new List<Point>();
            foreach (Point p in capControls) controls.Add(Shift(p));
            controls[5] = end;
            for (int k = 6; k < full.Controls.Length; k++)
            {
                Point p = full.Controls[k];
                controls.Add(new Point(
                    end.X + (p.X - nativeEnd.X) * tailWeight,
                    end.Y + (p.Y - nativeEnd.Y) * tailWeight));
            }
            // Fade to the ordinary arc before its vertical endpoint: directly clipping near
            // that endpoint has unbounded speed with respect to span length.
            double weight = shortCap * Smooth((1 - length / target) / 0.2);
            CloudLobe native = Arc(start, end, shape.Depth * length / target, shape.Side);
            var blended = new Point[controls.Count];
            for (int k = 0; k < controls.Count; k++) blended[k] = Mix(native.Controls[k], controls[k], weight);
            Point apex = Mix(native.Apex, Shift(full.Apex), weight);
            CloudLobe lobe = native.Copy();
            lobe.Controls = blended;
            lobe.Apex = apex;
            lobe.End = blended[8];
            lobe.Depth = Dist(apex, Mix(start, end, 0.5));
            return new CloudRun { Id = id, Commands = Path([lobe]), Lobes = [lobe] };
        }

        // Keep the row phase fixed. Its last three gaps share a small correction while a
        // full-size crown separates at the end, behind the changing tail.
        double w = Math.Max(Math.Min(target, length), length / (1 + 2047 * 0.88));
        double pitch = 0.88 * w;
        double span = Math.Max(0, length - w);
        int count = (int)Math.Floor(span / pitch + 1e-9) + 1;
        double fraction = Math.Max(0, span / pitch - (count - 1));
        double eased = Math.Pow(fraction, 3) * (10 + fraction * (-15 + 6 * fraction));
        double rawCorrection = (fraction - eased) * pitch;
        // With only one full rectangle gap, the shared fit can flatten its valley. Ease its
        // correction toward an eight-percent bound so the entering pair separates sooner.
        double correction = shape.Kind == CloudKind.Rectangle && count == 2
            ? rawCorrection / Hypot(1, rawCorrection / (0.08 * w))
            : rawCorrection;
        int group = Math.Min(3, count - 1);
        var offsets = new List<double>(count + 1);
        for (int i = 0; i < count; i++)
        {
            offsets.Add(i * pitch +
                correction * (group != 0 ? Math.Max(0, (i - (count - 1 - group)) / (double)group) : 0));
        }
        if (span - offsets[^1] > 1e-7) offsets.Add(span);
        // Give the entering pair room by sharing a small shift across nearby gaps. The shift
        // fades to zero at count boundaries; widths and anchors stay fixed.
        double phase = Math.Pow(Sin(Math.PI * fraction), 2);
        if (offsets.Count > 2)
        {
            int neighbors = Math.Min(5, offsets.Count - 2);
            int first = offsets.Count - 2 - neighbors;
            double step = Math.Min(
                0.04 * w * phase,
                Math.Max(0, pitch - (offsets[^1] - offsets[^2])) / neighbors);
            for (int i = first + 1; i < offsets.Count - 1; i++)
                step = Math.Min(step, Math.Max(0, offsets[i] - offsets[i - 1] - 0.831 * w));
            for (int i = first + 1; i < offsets.Count - 1; i++)
                offsets[i] -= step * (i - first);
        }
        var lobes = new List<CloudLobe>(offsets.Count);
        foreach (double x in offsets)
            lobes.Add(Arc(Add(start, u, x), Add(start, u, x + w), shape.Depth * w / target, shape.Side));
        return new CloudRun { Id = id, Commands = Path(OverlapAligned(lobes, u)), Lobes = lobes };
    }

    private static double RectangleFitWidth(CloudShape shape)
    {
        Rect b = BoundsOf(shape.Points);
        double q = shape.Depth / shape.Size;
        return Math.Min(shape.Size, Math.Min(b.Width, b.Height) / (Math.Sqrt(2) * (0.5 + q) + 1));
    }

    // ---------------------------------------------------------------------
    // Whole shapes
    // ---------------------------------------------------------------------

    /// <summary>
    /// The painted runs for a shape: one per corner and one per edge for a vertex chain,
    /// a single run for an ellipse. The order is the paint order.
    /// </summary>
    public static List<CloudRun> CloudRuns(CloudShape source)
    {
        if (source.Points.Length < 2) return [];
        if (source.Kind == CloudKind.Rectangle)
        {
            Rect b = BoundsOf(source.Points);
            if (Math.Min(b.Width, b.Height) < 0.001) return [];
        }
        // Depth is stored on the legacy 2-40 scale; the UI presents roundness. Keep a usable
        // curve aspect ratio even at either control extreme.
        CloudShape shape = source.Copy();
        shape.Depth = shape.Size * (0.24 + 0.4 * Clamp(source.Depth / 40, 0, 1));

        if (shape.Kind == CloudKind.Ellipse) return EllipseRuns(shape);

        bool closed = shape.Closed;
        int n = shape.Points.Length;
        var styles = new CloudShape[n];
        double? rectFit = shape.Kind == CloudKind.Rectangle ? RectangleFitWidth(shape) : null;
        for (int i = 0; i < n; i++)
        {
            double scale = Math.Min(1, shape.Clearance[i] / (3 * (shape.Size / 2 + shape.Depth)));
            CloudShape style = shape.Copy();
            style.Size = rectFit ?? shape.Size * scale;
            style.Depth = rectFit is double fit ? shape.Depth * fit / shape.Size : shape.Depth * scale;
            styles[i] = style;
        }

        var cornerRuns = new CloudRun[n];
        for (int i = 0; i < n; i++) cornerRuns[i] = CornerRun(shape, styles[i], i);

        if (shape.Kind == CloudKind.Polygon || !closed) DockCloseCorners(shape, cornerRuns, closed);

        // A sharply folded tip can loop or form a bulbous return. Blend it into one full
        // crown at the same visible anchor, then share the docking change only with nearby
        // crowns. Keep the original row fit so distant paint stays fixed.
        var oldDocks = new (Point Start, Point End)[n];
        for (int i = 0; i < n; i++)
        {
            CloudLobe last = cornerRuns[i].Lobes[^1];
            oldDocks[i] = (cornerRuns[i].Lobes[0].Start, last.BaseEnd ?? last.End);
        }
        var shifts = new (Point Start, Point End)[n];
        if (!closed) BlendFoldedTips(shape, styles, cornerRuns, oldDocks, shifts);

        var result = new List<CloudRun>();
        for (int i = 0; i < n; i++)
        {
            bool hasCorner = closed || (i > 0 && i < n - 1);
            if (hasCorner) result.Add(cornerRuns[i]);
            if (!closed && i == n - 1) break;
            int j = (i + 1) % n;
            Point start = !closed && i == 0 ? shape.Points[i] : oldDocks[i].End;
            Point end = !closed && j == n - 1 ? shape.Points[j] : oldDocks[j].Start;
            string id = $"cloud:edge:{i}";
            // A short neighbouring edge may need a smaller corner, but it must not shrink
            // this whole row when this edge has room for full-size crowns.
            double scale = rectFit is double fitWidth
                ? fitWidth / shape.Size
                : Math.Max(
                    Math.Sqrt(styles[i].Size * styles[j].Size) / shape.Size,
                    Math.Min(1, Dist(shape.Points[i], shape.Points[j]) / (3 * (shape.Size / 2 + shape.Depth))));
            double shortCap = 0;
            if (shape.Kind == CloudKind.Polygon && Dist(start, end) < shape.Size * scale)
            {
                double actualI = shape.Bends[i] * shape.Side;
                double actualJ = shape.Bends[j] * shape.Side;
                shortCap =
                    Smooth((Math.Min(shape.Bends[i] * shape.Side, actualI) - 0.24) / 0.24) *
                    Smooth((Math.Min(shape.Bends[j] * shape.Side, actualJ) - 0.24) / 0.24) *
                    Smooth((Math.PI - Math.Abs(actualI)) / 0.4) *
                    Smooth((Math.PI - Math.Abs(actualJ)) / 0.4);
            }
            CloudShape rowStyle = shape.Copy();
            rowStyle.Size = shape.Size * scale;
            rowStyle.Depth = shape.Depth * scale;
            CloudRun run = RunBetween(id, start, end, rowStyle, shortCap);

            if (shortCap > 0 && run.Lobes.Count == 1)
                BridgeSwallowedRow(shape, cornerRuns, i, j, start, end, scale, shortCap, run);

            if (!closed && j == n - 1 && run.Lobes.Count > 0)
            {
                CloudLobe l = run.Lobes[^1].Copy();
                l.End = l.BaseEnd!.Value;
                l.Controls = l.Controls[..6];
                run.Lobes[^1] = l;
                double rowLength = Dist(start, end);
                Point u = new((end.X - start.X) / rowLength, (end.Y - start.Y) / rowLength);
                run.Commands = Path(OverlapAligned(run.Lobes, u));
            }

            // Shift the existing clipped paint over at most three crown widths. Moving
            // controls as well keeps each join attached throughout the blend.
            if (!closed && run.Lobes.Count > 0)
                WarpRunEnds(shape, run, start, end, i == 0 ? default : shifts[i].End,
                    j == n - 1 ? default : shifts[j].Start);

            result.Add(run);
        }

        if (!closed) AlignConcaveBranches(shape, result, shifts);
        return result;
    }

    /// <summary>The crowns at one vertex: a full crown when convex, two branches when re-entrant.</summary>
    private static CloudRun CornerRun(CloudShape shape, CloudShape style, int i)
    {
        Point p = shape.Points[i];
        string id = $"cloud:corner:{i}";
        CloudLobe convex = Corner(style, i);
        double signedBend = shape.Bends[i] * shape.Side;
        if (signedBend >= 0.24) return new CloudRun { Id = id, Commands = Path([convex]), Lobes = [convex] };

        double bend = shape.Bends[i];
        double a = shape.Angles[i] - bend / 2, b = shape.Angles[i] + bend / 2;
        CloudLobe incoming = Arc(
            Add(p, new Point(Cos(a), Sin(a)), -style.Size), p, style.Depth, shape.Side);
        // The inward branch ends at the valley; a tail here would cross the opposite branch.
        // The outgoing lobe owns the corner's trailing end.
        incoming.End = incoming.BaseEnd!.Value;
        incoming.Controls = incoming.Controls[..6];
        var prev = new CloudRun { Id = id, Lobes = [incoming] };
        var next = new CloudRun
        {
            Id = id,
            Lobes = [Arc(p, Add(p, new Point(Cos(b), Sin(b)), style.Size), style.Depth, shape.Side)],
        };
        JoinConcave(prev, next);

        Point join = prev.Lobes[^1].End;
        Point shift = new(p.X - join.X, p.Y - join.Y);
        Point Move(Point v) => Add(v, shift);
        var lobes = new List<CloudLobe>();
        foreach (CloudLobe source in prev.Lobes.Concat(next.Lobes))
        {
            CloudLobe l = source.Copy();
            l.Start = Move(source.Start);
            l.End = Move(source.End);
            l.Apex = Move(source.Apex);
            l.BaseEnd = source.BaseEnd is Point be ? Move(be) : null;
            l.Controls = [.. source.Controls.Select(Move)];
            lobes.Add(l);
        }
        if (signedBend <= -0.48) return new CloudRun { Id = id, Commands = Path(lobes), Lobes = lobes };

        // Keep every outward corner a full crown. Begin the continuous change to a valley
        // only after the vertex turns inward; both patches keep its anchor.
        List<Point[]> cc = Cubics(convex);
        (Point[] f0, Point[] f1) = Split(cc[0], 0.5);
        (Point[] f2, Point[] f3) = Split(cc[1], 0.5);
        Point[][] fromCubics = [f0, f1, f2, f3, cc[2]];
        List<Point[]> to = lobes.SelectMany(Cubics).ToList();
        if (to.Count == 5)
        {
            double f = Clamp(-signedBend / 0.48, 0, 1);
            double t = f * f * (3 - 2 * f);
            var controls = new List<Point>();
            for (int j = 0; j < fromCubics.Length; j++)
                for (int k = 0; k < 3; k++) controls.Add(Mix(fromCubics[j][k + 1], to[j][k + 1], t));
            CloudLobe l = convex.Copy();
            l.Start = Mix(convex.Start, lobes[0].Start, t);
            l.End = controls[^1];
            l.BaseEnd = Mix(convex.BaseEnd!.Value, lobes[^1].BaseEnd!.Value, t);
            l.Controls = [.. controls];
            return new CloudRun { Id = id, Commands = Path([l]), Lobes = [l] };
        }
        return new CloudRun { Id = id, Commands = Path(lobes), Lobes = lobes };
    }

    /// <summary>
    /// Before close corners overlap, let their facing halves share one arch. The opposite
    /// halves and both visible vertices stay fixed. Open paths dock only internal corners
    /// and follow their signed bend.
    /// </summary>
    private static void DockCloseCorners(CloudShape shape, CloudRun[] cornerRuns, bool closed)
    {
        int n = shape.Points.Length;
        for (int i = closed ? 0 : 1; i < n - (closed ? 0 : 2); i++)
        {
            int j = (i + 1) % n;
            double L = Dist(shape.Points[i], shape.Points[j]);
            double f = Clamp(((closed ? 2.4 : 2) * shape.Size - L) / ((closed ? 1.2 : 0.8) * shape.Size), 0, 1);
            double t = f * f * (3 - 2 * f);
            if (t == 0) continue;
            CloudRun prev = cornerRuns[i], next = cornerRuns[j];

            static CloudLobe Flatten(CloudRun run)
            {
                CloudLobe flat = run.Lobes[0].Copy();
                flat.End = run.Lobes[^1].End;
                flat.BaseEnd = run.Lobes[^1].BaseEnd;
                flat.Controls = [.. run.Lobes.SelectMany(l => l.Controls)];
                return flat;
            }

            CloudLobe a = Flatten(prev), b = Flatten(next);
            Point A = shape.Points[i], B = shape.Points[j];
            // Collapsed curves can have several coincident control points. The corner layout,
            // not the first matching point, identifies its anchor.
            int aIndex = a.Controls.Length == 9 ? 2 : a.Controls.Length == 15 ? 5 : -1;
            int anchorIndex = b.Controls.Length == 9 ? 2 : b.Controls.Length == 15 ? 5 : -1;
            if (aIndex < 0 || anchorIndex < 0 ||
                Dist(a.Controls[aIndex], A) > 1e-6 || Dist(b.Controls[anchorIndex], B) > 1e-6) continue;

            Point u = new((B.X - A.X) / (L != 0 ? L : 1), (B.Y - A.Y) / (L != 0 ? L : 1));
            Point nrm = new(u.Y * shape.Side, -u.X * shape.Side);
            double Alignment(double dx, double dy) => (dx * u.X + dy * u.Y) / Math.Max(1e-9, Hypot(dx, dy));
            double facing = Math.Min(
                Alignment(a.Controls[aIndex + 1].X - A.X, a.Controls[aIndex + 1].Y - A.Y),
                Alignment(B.X - b.Controls[anchorIndex - 1].X, B.Y - b.Controls[anchorIndex - 1].Y));
            // When the span folds back, a deep arch can brush the preceding curve. Reduce
            // only its bow; keep docking active through coincident vertices.
            double g = Clamp(facing / 0.5, 0, 1);
            double bowScale = 0.15 + 0.85 * g * g * (3 - 2 * g);
            double Tangent(double dx, double dy)
            {
                double longitudinal = dx * u.X + dy * u.Y;
                double transverse = dx * nrm.X + dy * nrm.Y;
                return Clamp(transverse / Math.Max(Math.Max(1e-9, 0.2 * Hypot(dx, dy)), longitudinal), -1, 1);
            }
            double ka = Tangent(a.Controls[aIndex + 1].X - A.X, a.Controls[aIndex + 1].Y - A.Y);
            double kb = Tangent(B.X - b.Controls[anchorIndex - 1].X, B.Y - b.Controls[anchorIndex - 1].Y);
            // Endpoint tangents alone can cancel the arch and leave a flat join. Keep an
            // outward bow, limited by the space between the two vertices.
            double bow = Math.Min(0.35 * L, 1.35 * shape.Depth);
            // Reversing an open line reverses its bend, but not its chosen cloud side.
            double archSign = closed
                ? 1
                : Math.Tanh(3 * (shape.Bends[i] + shape.Bends[j]) * shape.Side);
            Point[] curve =
            [
                A,
                Add(Add(A, u, L / 3), nrm, archSign * bowScale * Math.Max(bow, ka * L / 3)),
                Add(Add(B, u, -L / 3), nrm, archSign * bowScale * Math.Max(bow, -kb * L / 3)),
                B,
            ];
            (Point[] left, Point[] right) = Split(curve, 0.5);
            Point dock = left[3];
            int leftCount = (a.Controls.Length - aIndex - 1) / 3 - 1;
            var leftControls = new List<Point>();
            if (leftCount == 2)
            {
                (Point[] l0, Point[] l1) = Split(left, 0.5);
                leftControls.AddRange(l0[1..]);
                leftControls.AddRange(l1[1..]);
            }
            else leftControls.AddRange(left[1..]);

            CloudLobe aa = a.Copy();
            aa.BaseEnd = Mix(a.BaseEnd!.Value, dock, t);
            aa.End = Mix(a.End, dock, t);
            var aaControls = new Point[a.Controls.Length];
            for (int k = 0; k < a.Controls.Length; k++)
            {
                aaControls[k] = k <= aIndex
                    ? a.Controls[k]
                    : Mix(a.Controls[k], k < a.Controls.Length - 3 ? leftControls[k - aIndex - 1] : dock, t);
            }
            aa.Controls = aaControls;

            var prefix = new List<Point>();
            if (anchorIndex == 5)
            {
                (Point[] r0, Point[] r1) = Split(right, 0.5);
                prefix.AddRange(r0[1..]);
                prefix.AddRange(r1[1..]);
            }
            else prefix.AddRange(right[1..]);

            CloudLobe bb = b.Copy();
            bb.Start = Mix(b.Start, dock, t);
            var bbControls = new Point[b.Controls.Length];
            for (int k = 0; k < b.Controls.Length; k++)
                bbControls[k] = k <= anchorIndex ? Mix(b.Controls[k], prefix[k], t) : b.Controls[k];
            bb.Controls = bbControls;

            prev.Lobes = Repack(prev.Lobes, aa);
            prev.Commands = Path(prev.Lobes);
            next.Lobes = Repack(next.Lobes, bb);
            next.Commands = Path(next.Lobes);
        }
    }

    private static List<CloudLobe> Repack(List<CloudLobe> original, CloudLobe flat)
    {
        int offset = 0;
        Point start = flat.Start;
        var result = new List<CloudLobe>(original.Count);
        for (int k = 0; k < original.Count; k++)
        {
            CloudLobe old = original[k];
            Point[] controls = flat.Controls[offset..(offset + old.Controls.Length)];
            offset += old.Controls.Length;
            CloudLobe l = old.Copy();
            l.Start = start;
            l.Controls = controls;
            l.End = controls[^1];
            l.BaseEnd = k == original.Count - 1 ? flat.BaseEnd : old.BaseEnd;
            start = l.End;
            result.Add(l);
        }
        return result;
    }

    private static void BlendFoldedTips(
        CloudShape shape, CloudShape[] styles, CloudRun[] cornerRuns,
        (Point Start, Point End)[] oldDocks, (Point Start, Point End)[] shifts)
    {
        int n = shape.Points.Length;
        for (int i = 1; i < n - 1; i++)
        {
            Point p = shape.Points[i], A = shape.Points[i - 1], B = shape.Points[i + 1];
            double la = Dist(A, p), lb = Dist(p, B);
            Point ua = new((p.X - A.X) / Math.Max(la, 1e-9), (p.Y - A.Y) / Math.Max(la, 1e-9));
            Point ub = new((B.X - p.X) / Math.Max(lb, 1e-9), (B.Y - p.Y) / Math.Max(lb, 1e-9));
            double fold = Math.Acos(Clamp(ua.X * ub.X + ua.Y * ub.Y, -1, 1));
            double bendValue = shape.Bends[i];
            int turnSign = bendValue > 0 ? 1 : bendValue < 0 ? -1 : shape.Side;
            double turnWeight = Clamp(Math.Abs(bendValue) / 0.48, 0, 1);
            double room = Math.Min(
                Dist(i == 1 ? shape.Points[i - 1] : oldDocks[i - 1].End, oldDocks[i].Start),
                Dist(oldDocks[i].End, i == n - 2 ? shape.Points[i + 1] : oldDocks[i + 1].Start));
            double fit = Clamp(room / (0.5 * shape.Size), 0, 1);
            double f = Clamp((fold - 2) / 0.6, 0, 1);
            double t = fit * fit * (3 - 2 * fit) * f * f * (3 - 2 * f) *
                turnWeight * turnWeight * (3 - 2 * turnWeight) *
                Clamp(Math.Min(la, lb) / (0.1 * shape.Size), 0, 1);
            if (t == 0) continue;

            double nx = ua.X - ub.X, ny = ua.Y - ub.Y;
            double orientation = Math.Atan2(nx * turnSign, -ny * turnSign);
            CloudShape targetShape = styles[i].Copy();
            targetShape.Side = turnSign;
            targetShape.Angles = [.. shape.Angles];
            targetShape.Angles[i] = orientation;
            CloudLobe target = Corner(targetShape, i);

            CloudRun run = cornerRuns[i];
            List<Point[]> old = run.Lobes.SelectMany(Cubics).ToList();
            List<Point[]> tc = Cubics(target);
            List<Point[]> targetCubics;
            if (old.Count == 5)
            {
                (Point[] t0, Point[] t1) = Split(tc[0], 0.5);
                (Point[] t2, Point[] t3) = Split(tc[1], 0.5);
                targetCubics = [t0, t1, t2, t3, tc[2]];
            }
            else targetCubics = tc;
            if (old.Count != targetCubics.Count) continue;

            var controls = new List<Point>();
            for (int j = 0; j < old.Count; j++)
                for (int k = 0; k < 3; k++) controls.Add(Mix(old[j][k + 1], targetCubics[j][k + 1], t));
            CloudLobe blended = target.Copy();
            blended.Start = Mix(old[0][0], target.Start, t);
            blended.End = Mix(old[^1][3], target.End, t);
            blended.BaseEnd = Mix(oldDocks[i].End, target.BaseEnd!.Value, t);
            blended.Controls = [.. controls];
            run.Lobes = [blended];
            run.Commands = Path([blended]);
            shifts[i] = (
                new Point(blended.Start.X - oldDocks[i].Start.X, blended.Start.Y - oldDocks[i].Start.Y),
                new Point(blended.BaseEnd!.Value.X - oldDocks[i].End.X, blended.BaseEnd!.Value.Y - oldDocks[i].End.Y));
        }
    }

    /// <summary>
    /// A row too short to hold a crown becomes a bridge between the two corner crowns. This
    /// divider belongs to the swallowed row, so only the preceding crown's trailing
    /// extension retracts while the two crowns share their bridge.
    /// </summary>
    private static void BridgeSwallowedRow(
        CloudShape shape, CloudRun[] cornerRuns, int i, int j,
        Point start, Point end, double scale, double shortCap, CloudRun run)
    {
        CloudLobe l = run.Lobes[0];
        CloudLobe a = cornerRuns[i].Lobes[^1];
        CloudLobe b = cornerRuns[j].Lobes[0];
        int baseIndex = -1;
        if (a.BaseEnd is Point baseEnd)
        {
            for (int k = 0; k < a.Controls.Length; k++)
            {
                if (k % 3 == 2 && Dist(a.Controls[k], baseEnd) < 1e-8) { baseIndex = k; break; }
            }
        }
        if (l.Controls.Length != 9 || baseIndex < 2 || b.Controls.Length < 3) return;

        double L = Dist(start, end);
        Point u = new((end.X - start.X) / L, (end.Y - start.Y) / L);
        Point ta = Sub(a.BaseEnd!.Value, a.Controls[baseIndex - 1]);
        Point tb = Sub(b.Controls[0], b.Start);
        double da = ta.X * u.X + ta.Y * u.Y, db = tb.X * u.X + tb.Y * u.Y;
        double forward = Math.Min(
            da / Math.Max(1e-9, Hypot(ta.X, ta.Y)),
            db / Math.Max(1e-9, Hypot(tb.X, tb.Y)));
        double weight = shortCap *
            Smooth((0.8 - L / Math.Max(0.5, shape.Size * scale)) / 0.3) *
            Smooth((forward - 0.6) / 0.2);
        if (weight <= 0) return;

        Point[] bridge = [start, Add(start, ta, L / (3 * da)), Add(end, tb, -L / (3 * db)), end];
        (Point[] h0, Point[] h1) = Split(bridge, 0.5);
        Point[] bridgeControls = [h0[1], h0[2], h0[3], h1[1], h1[2], h1[3], end, end, end];
        var controls = new Point[l.Controls.Length];
        for (int k = 0; k < l.Controls.Length; k++) controls[k] = Mix(l.Controls[k], bridgeControls[k], weight);
        CloudLobe next = l.Copy();
        next.Controls = controls;
        next.Apex = controls[2];
        next.End = controls[8];
        next.Depth = Dist(controls[2], Mix(start, end, 0.5));
        run.Lobes = [next];
        run.Commands = Path([next]);

        CloudRun prev = cornerRuns[i];
        CloudLobe tail = a.Copy();
        var tailControls = new Point[a.Controls.Length];
        for (int k = 0; k < a.Controls.Length; k++)
            tailControls[k] = k <= baseIndex ? a.Controls[k] : Mix(a.Controls[k], a.BaseEnd!.Value, weight);
        tail.Controls = tailControls;
        tail.End = tailControls[^1];
        prev.Lobes = [.. prev.Lobes.Take(prev.Lobes.Count - 1), tail];
        prev.Commands = Path(prev.Lobes);
    }

    private static void WarpRunEnds(CloudShape shape, CloudRun run, Point start, Point end, Point ds, Point de)
    {
        if (Hypot(ds.X, ds.Y) + Hypot(de.X, de.Y) <= 1e-9) return;
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double L = Hypot(dx, dy);
        double range = Math.Min(3 * shape.Size, L);
        Point Warp(Point p)
        {
            double q = ((p.X - start.X) * dx + (p.Y - start.Y) * dy) / (L != 0 ? L : 1);
            double a = Smooth(1 - q / range), b = Smooth(1 - (L - q) / range);
            return new Point(p.X + a * ds.X + b * de.X, p.Y + a * ds.Y + b * de.Y);
        }
        // The reference warps the RENDERED path data, which is already 5-decimal rounded,
        // then re-rounds it; the lobes warp from their full-precision values. Both matter:
        // `d` is what is painted, the lobes are what the next pass reads.
        for (int k = 0; k < run.Commands.Count; k++)
        {
            CloudCommand c = run.Commands[k];
            run.Commands[k] = c.Verb == 'M'
                ? CloudCommand.Move(Round5(Warp(c.P1)))
                : CloudCommand.Curve(Round5(Warp(c.P1)), Round5(Warp(c.P2)), Round5(Warp(c.P3)));
        }
        for (int k = 0; k < run.Lobes.Count; k++)
        {
            CloudLobe source = run.Lobes[k];
            CloudLobe l = source.Copy();
            l.Start = Warp(source.Start);
            l.End = Warp(source.End);
            l.BaseEnd = source.BaseEnd is Point be ? Warp(be) : null;
            l.Apex = Warp(source.Apex);
            l.Controls = [.. source.Controls.Select(Warp)];
            run.Lobes[k] = l;
        }
    }

    /// <summary>Align near-parallel branches at a concave join without moving its anchor.</summary>
    private static void AlignConcaveBranches(CloudShape shape, List<CloudRun> result, (Point Start, Point End)[] shifts)
    {
        for (int i = 1; i < result.Count; i++)
        {
            CloudRun run = result[i], prev = result[i - 1];
            if (!run.Id.Contains(":corner:") || !prev.Id.Contains(":edge:") ||
                run.Lobes.Count != 2 || prev.Lobes.Count == 0) continue;
            int index = int.Parse(run.Id[(run.Id.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
            if (index <= 0 || index >= shape.Points.Length - 1) continue;
            Point shift = shifts[index - 1].End;
            double scope = Smooth(Hypot(shift.X, shift.Y) / (0.1 * shape.Size));
            if (scope == 0) continue;
            CloudLobe incoming = prev.Lobes[^1], next = run.Lobes[0];
            if (incoming.Controls.Length != 9 || incoming.BaseEnd is null ||
                Dist(incoming.BaseEnd.Value, next.Start) > 1e-5) continue;
            Point a = Sub(incoming.Controls[4], incoming.BaseEnd.Value);
            Point b = Sub(next.Controls[0], next.Start);
            double la = Hypot(a.X, a.Y), lb = Hypot(b.X, b.Y);
            if (Math.Min(la, lb) < 1e-8) continue;
            double dot = (a.X * b.X + a.Y * b.Y) / (la * lb);
            Point aPoint = shape.Points[index - 1], p = shape.Points[index], bPoint = shape.Points[index + 1];
            double incomingLength = Dist(aPoint, p), outgoingLength = Dist(p, bPoint);
            double fold = Math.Acos(Clamp(
                ((p.X - aPoint.X) * (bPoint.X - p.X) + (p.Y - aPoint.Y) * (bPoint.Y - p.Y)) /
                Math.Max(1e-12, incomingLength * outgoingLength), -1, 1));
            double t = scope *
                Smooth((dot - 0.85) / 0.1) *
                Smooth((-shape.Bends[index] * shape.Side - 0.48) / 0.24) *
                Smooth((2 - fold) / 0.3) *
                Smooth(Math.Min(incomingLength, outgoingLength) / shape.Size);
            if (t == 0) continue;
            Point target = Add(next.Start, a, lb / la);
            CloudLobe aligned = next.Copy();
            aligned.Controls = [Mix(next.Controls[0], target, t), .. next.Controls[1..]];
            run.Lobes[0] = aligned;
            run.Commands = Path(run.Lobes);
        }
    }

    /// <summary>
    /// Ellipses take a completely different path: sample a rounded-offset ellipse into a
    /// 2048-segment arc-length table (refining the corner radius over four passes), then
    /// place crowns at even arc-length pitch so spacing stays even on a long, narrow one.
    /// </summary>
    private static List<CloudRun> EllipseRuns(CloudShape shape)
    {
        Rect b = BoundsOf(shape.Points);
        double rx = Math.Max(0.01, b.Width / 2), ry = Math.Max(0.01, b.Height / 2);
        double cx = b.X + rx, cy = b.Y + ry;
        double opticalTarget = shape.Size *
            Math.Min(1, Math.Min(b.Width, b.Height) / (3 * (shape.Size / 2 + shape.Depth)));
        double target = Math.Max(0.5, opticalTarget);
        double radius = Math.Max(0,
            opticalTarget * 0.5 - Math.Pow(Math.Min(rx, ry), 2) / Math.Max(rx, ry));

        const int Samples = 2048;
        var samples = new Point[Samples + 1];
        var lengths = new double[Samples + 1];
        double length = 0;
        void SampleOutline()
        {
            double ex = Math.Max(0.0001, rx - radius), ey = Math.Max(0.0001, ry - radius);
            length = 0;
            lengths[0] = 0;
            for (int i = 0; i <= Samples; i++)
            {
                double t = (double)i / Samples * Math.PI * 2;
                double nx = Cos(t) / ex, ny = Sin(t) / ey;
                double norm = Hypot(nx, ny);
                Point p = new(cx + ex * Cos(t) + radius * nx / norm, cy + ey * Sin(t) + radius * ny / norm);
                samples[i] = p;
                if (i != 0)
                {
                    length += Dist(samples[i - 1], p);
                    lengths[i] = length;
                }
            }
        }
        SampleOutline();
        // Round tips for the crowns actually painted after minimum width/count limits.
        // Recompute the distance table when that width changes the rounded outline.
        for (int pass = 0; pass < 4; pass++)
        {
            double fittedWidth = Math.Max(length / Samples, Math.Min(target * 0.88, length / 4)) / 0.88;
            double nextRadius = Math.Max(0, fittedWidth * 0.5 - Math.Pow(Math.Min(rx, ry), 2) / Math.Max(rx, ry));
            if (Math.Abs(nextRadius - radius) < 1e-7) break;
            radius = nextRadius;
            SampleOutline();
        }

        Point At(double f)
        {
            double want = f * length;
            int lo = 0, hi = Samples;
            while (lo + 1 < hi)
            {
                int mid = (lo + hi) >> 1;
                if (lengths[mid] < want) lo = mid; else hi = mid;
            }
            double denom = lengths[hi] - lengths[lo];
            return Mix(samples[lo], samples[hi], (want - lengths[lo]) / (denom != 0 ? denom : 1));
        }

        double pitch = Math.Max(length / Samples, Math.Min(target * 0.88, length / 4));
        double w = pitch / 0.88;
        int count = Math.Max(4, (int)Math.Floor(length / pitch + 1e-9));
        double fraction = Math.Max(0, length / pitch - count);
        double eased = Math.Pow(fraction, 3) * (10 + fraction * (-15 + 6 * fraction));
        double correction = (fraction - eased) * pitch;
        var offsets = new List<double>(count + 1);
        for (int i = 0; i < count; i++)
            offsets.Add(i * pitch + correction * Math.Max(0, (i - (count - 3)) / 3.0));
        if (fraction > 1e-8) offsets.Add(count * pitch + correction);
        double phase = Math.Pow(Sin(Math.PI * fraction), 2);
        int neighbors = Math.Min(5, offsets.Count - 1);
        int first = offsets.Count - 1 - neighbors;
        double step = Math.Min(0.04 * w * phase, Math.Max(0, pitch - (length - offsets[^1])) / neighbors);
        for (int i = first + 1; i < offsets.Count; i++)
            step = Math.Min(step, Math.Max(0, offsets[i] - offsets[i - 1] - 0.831 * w));
        for (int i = first + 1; i < offsets.Count; i++)
            offsets[i] -= step * (i - first);

        var lobes = new List<CloudLobe>(offsets.Count);
        foreach (double offset in offsets)
        {
            double f = offset / length;
            Point p = At((f - pitch / (2 * length) + 1) % 1);
            Point q = At((f + pitch / (2 * length)) % 1);
            Point center = Mix(p, q, 0.5);
            double d = Dist(p, q);
            Point u = new((q.X - p.X) / Math.Max(d, 1e-10), (q.Y - p.Y) / Math.Max(d, 1e-10));
            lobes.Add(Arc(Add(center, u, -w / 2), Add(center, u, w / 2), shape.Depth * w / shape.Size));
        }
        return [new CloudRun { Id = "cloud:curve", Commands = Path(OverlapTails(lobes, true)), Lobes = lobes }];
    }

    // ---------------------------------------------------------------------
    // Output
    // ---------------------------------------------------------------------

    /// <summary>Every run's commands, concatenated in paint order.</summary>
    public static List<CloudCommand> Outline(CloudKind kind, IReadOnlyList<Point> points,
        double size = DefaultSize, double depth = ApprovedDepth)
    {
        var commands = new List<CloudCommand>();
        foreach (CloudRun run in CloudRuns(MakeShape(kind, points, size, depth)))
            commands.AddRange(run.Commands);
        return commands;
    }

    /// <summary>SVG path data, for diffing against the reference and for debugging.</summary>
    public static string ToPathData(IEnumerable<CloudCommand> commands)
    {
        var text = new StringBuilder();
        foreach (CloudCommand c in commands)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(c.Verb);
            if (c.Verb == 'M') Append(text, c.P1);
            else
            {
                Append(text, c.P1);
                Append(text, c.P2);
                Append(text, c.P3);
            }
        }
        return text.ToString();

        static void Append(StringBuilder text, Point p)
        {
            text.Append(' ').Append(p.X.ToString("R", CultureInfo.InvariantCulture));
            text.Append(' ').Append(p.Y.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
