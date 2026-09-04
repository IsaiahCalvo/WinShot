using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Survey paper-ink: a CONSTANT-radius swept disk built from a centerline with
/// round caps/joins (paperAnnotationGeometry.js:1537-1556 createInkAnnotation ->
/// sweptDiskPolygon(points, Math.max(0.5, width/2))). There is no pressure, no
/// thinning and no smoothing: the drawn line passes through the pointer samples.
/// Survey unions the swept disks into non-overlapping rings before painting them
/// evenodd, so a self-crossing stroke is SOLID; WPF's widen output is consistently
/// wound, so Nonzero is that union and forcing EvenOdd would punch holes at every
/// self-crossing instead.
/// </summary>
internal static class PaperInk
{
    /// <summary>Survey live coalescing distance while the pointer is down.</summary>
    public const double LiveMinDistance = 0.2;

    /// <summary>Survey compact distance applied when the stroke is committed.</summary>
    public const double CompactMinDistance = 0.35;

    /// <summary>
    /// Survey compactPoints (paperAnnotationGeometry.js:74-95): drop samples closer
    /// than <paramref name="minDistance"/> to the last kept one, then force-append the
    /// TRUE final sample so the stroke always terminates where the user released.
    /// </summary>
    public static List<Point> Compact(IEnumerable<Point> raw, double minDistance = CompactMinDistance)
    {
        var src = raw as IList<Point> ?? raw.ToList();
        var pts = new List<Point>();
        foreach (var p in src)
        {
            if (pts.Count == 0 || (p - pts[^1]).Length >= minDistance)
                pts.Add(p);
        }
        if (pts.Count > 0 && src.Count > 1 && src[^1] != pts[^1])
            pts.Add(src[^1]);
        return pts;
    }

    /// <summary>
    /// Flattening / boolean tolerance, in ABSOLUTE canvas units. Survey's round caps and joins
    /// are ALWAYS an 18-facet semicircle at every stroke width - sweptDiskSemicircleSteps falls
    /// through to DEFAULT_ARC_STEPS = 18 unless the caller passes arcSteps/curveTolerance, and
    /// neither createInkAnnotation nor eraseAnnotations does (paperAnnotationGeometry.js:5,
    /// :468-487, :1537-1555, :2367-2380), so Survey's chord error is r*(1-cos(pi/36)) and its
    /// facet count never depends on the width. WPF instead takes a max chord error, and its
    /// default (Geometry.StandardFlatteningTolerance = 0.25, also absolute) makes the facet
    /// count COLLAPSE as the stroke thins: a 1px capsule flattened to 7 vertices - a triangular
    /// "round" cap losing 16% of its area - while a 30px one kept 35. That bit every path that
    /// turns ink into a fixed polygon: erasing, moving, culling and the overlap tests.
    /// One absolute value covers every width here: ~0.0076 canvas units is under 1/100 of the
    /// THINNEST stroke WinShot draws, so the chord error is sub-pixel at any width and any zoom,
    /// where WPF's 0.25 was a quarter of a whole 1px stroke. Width-scaling it to Survey's exact
    /// r*(1-cos(pi/36)) would buy no visible accuracy and would mean choosing between the ink's
    /// width and the eraser's on every boolean.
    /// </summary>
    public static readonly double Tolerance = 0.5 * (1 - Math.Cos(Math.PI / 18));

    public static PathGeometry Outline(IEnumerable<Point> raw, double width, double? minDistance = null)
    {
        // Survey's radius is Math.max(0.5, width/2) - the same clamp as a diameter floor of 1.
        double w = Math.Max(1, width);
        var pts = Compact(raw, minDistance ?? CompactMinDistance);
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
            // No tolerance argument: WPF widens round caps/joins into PolyBezierSegments, not
            // facets, and passing a tolerance here provably changes nothing (identical segment
            // types, vertex counts and areas at w = 1/3/12/30). The outline is exact; only the
            // consumers that FLATTEN it need Tolerance.
            return centerline.GetWidenedPathGeometry(pen);
        }
        catch (InvalidOperationException)
        {
            return EllipseAt(pts[0], w / 2);
        }
    }

    /// <summary>
    /// Survey eraseAnnotations (paperAnnotationGeometry.js:2367-2375) gives the eraser polyline
    /// its OWN radius-relative sample distance - max(MIN_VALUE, min(0.1, radius*0.1)) - and
    /// passes it to sweptDiskPolygonRecords AND to the centerline compaction. 0.35 is the INK
    /// commit distance (createInkAnnotation :1539) and never applies to an eraser gesture;
    /// reusing it here thinned the eraser's samples ~2:1 against the live 0.2 sampler, so the
    /// same pointer path produced a different swept polygon than Survey's.
    /// </summary>
    public static PathGeometry EraserStroke(IEnumerable<Point> raw, double diameter)
    {
        double d = Math.Max(1, diameter);
        return Outline(raw, d, Math.Max(double.Epsilon, Math.Min(0.1, d / 2 * 0.1)));
    }

    /// <summary>
    /// One classified cut. Survey's subtractPolygonPart returns
    /// <c>{status:'failed', stage:'intersection'|'subtraction'|'containment'}</c> and never a
    /// silent result (paperAnnotationGeometry.js:2241-2296), so this returns NULL for "the
    /// clipper failed, the answer is unknown" and a (possibly empty) geometry otherwise.
    /// The old code mapped both failure modes onto a value: a throw returned the UNCUT ink
    /// (so the eraser left the stroke whole where it passed) and a null Combine returned
    /// Geometry.Empty, which callers read as "fully erased" and used to DELETE the stroke.
    /// </summary>
    public static Geometry? Subtract(Geometry ink, Geometry cut)
    {
        Geometry? result;
        try
        {
            result = Geometry.Combine(
                ink, cut, GeometryCombineMode.Exclude, Transform.Identity, Tolerance, ToleranceType.Absolute);
        }
        catch (Exception)
        {
            // Survey's bare `catch` around diff(): any clipper blow-up is a failed cut, not a
            // crash. A narrower catch here escaped into the pointer-move handler.
            return null;
        }
        if (result is null) return null;

        // Survey's subtractionStayedInsideSubject postcondition (:1627-1720): a difference that
        // leaves the subject is a clipper artefact, so reject it rather than paint it. Bounds is
        // the half that catches the eraser-shaped streak; Survey's extra edge-interval audit
        // exists for martinez needles, which WPF's widen path does not emit.
        if (!result.IsEmpty())
        {
            Rect r = result.Bounds, s = ink.Bounds;
            double eps = Math.Max(1e-9, s.Width + s.Height) * 1e-9 + Tolerance;
            if (r.Left < s.Left - eps || r.Top < s.Top - eps ||
                r.Right > s.Right + eps || r.Bottom > s.Bottom + eps)
                return null;
        }
        return result;
    }

    /// <summary>
    /// Cut one whole eraser gesture out of <paramref name="ink"/>, retrying failures through
    /// smaller pieces the way Survey's subtractPolygonRecord re-attempts a rejected merged
    /// branch through the record's children (paperAnnotationGeometry.js:2298-2333, :536-580).
    /// The set identity that makes this legal is Survey's own (:525-535): for C = C1 u C2,
    /// <c>S \ C == (S \ C1) \ C2</c>. WinShot's record is the eraser polyline, so "children"
    /// is that polyline split in half. A piece that still fails at the recursion floor is
    /// Survey's failed leaf: leave the ink UNCHANGED there - never delete on a failed cut.
    /// </summary>
    public static Geometry SubtractStroke(Geometry ink, IReadOnlyList<Point> gesture, double diameter, int depth = 4)
    {
        if (ink is null || ink.IsEmpty() || gesture.Count == 0) return ink ?? Geometry.Empty;
        var cut = EraserStroke(gesture, diameter);
        var direct = Subtract(ink, cut);
        if (direct is not null) return direct;
        if (depth <= 0) return ink;

        if (gesture.Count > 2)
        {
            int mid = gesture.Count / 2; // the split point belongs to BOTH halves, so C1 u C2 == C
            return SubtractStroke(
                SubtractStroke(ink, gesture.Take(mid + 1).ToList(), diameter, depth - 1),
                gesture.Skip(mid).ToList(), diameter, depth - 1);
        }
        if (gesture.Count < 2 || (gesture[1] - gesture[0]).Length < 1) return ink; // failed leaf
        var m = new Point((gesture[0].X + gesture[1].X) / 2, (gesture[0].Y + gesture[1].Y) / 2);
        return SubtractStroke(
            SubtractStroke(ink, new[] { gesture[0], m }, diameter, depth - 1),
            new[] { m, gesture[1] }, diameter, depth - 1);
    }

    /// <summary>
    /// Survey cullInkSliverPolygons (paperAnnotationGeometry.js:1586-1615). Boolean
    /// subtraction legitimately emits arbitrarily thin crescents that persist as
    /// hairline streaks of ink colour where the user just erased. Drop surviving
    /// components whose area OR mean thickness (2*area/perimeter) is far below what
    /// ink drawn at <paramref name="width"/> could visibly be; holes only have to
    /// clear the numeric floor. A full pen dot (area ~ 0.785*w^2) survives both.
    /// </summary>
    public static Geometry CullSlivers(Geometry geometry, double width)
    {
        if (geometry is null || geometry.IsEmpty()) return Geometry.Empty;
        double w = double.IsFinite(width) && width > 0 ? width : 1;
        double numericAreaFloor = w * w * 1e-12;
        double minArea = Math.Max(numericAreaFloor, Math.Min(0.4 * w, 0.35 * w * w));
        double meanWidthCullFloor = 0.15 * w * 0.94;

        var flat = geometry.GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute);
        int n = flat.Figures.Count;
        if (n == 0) return Geometry.Empty;

        var area = new double[n];
        var perimeter = new double[n];
        var single = new PathGeometry[n];
        for (int i = 0; i < n; i++)
        {
            (area[i], perimeter[i]) = FigureMetrics(flat.Figures[i]);
            single[i] = new PathGeometry(new[] { flat.Figures[i].Clone() });
        }

        // ponytail: O(n^2) containment to tell outer rings from holes - ink geometry
        // has a handful of figures. Swap for a sweep only if that stops being true.
        var owner = new int[n];
        for (int i = 0; i < n; i++)
        {
            owner[i] = -1;
            var probe = flat.Figures[i].StartPoint;
            for (int j = 0; j < n; j++)
            {
                if (i == j || area[j] <= area[i]) continue;
                if (single[j].FillContains(probe) && (owner[i] < 0 || area[j] < area[owner[i]]))
                    owner[i] = j;
            }
        }

        var keep = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (owner[i] >= 0)
                continue; // holes are judged below, once their outer ring is known
            keep[i] = area[i] >= minArea
                && perimeter[i] > w * 1e-12
                && (2 * area[i]) / perimeter[i] >= meanWidthCullFloor;
        }
        for (int i = 0; i < n; i++)
        {
            if (owner[i] < 0) continue;
            keep[i] = keep[owner[i]] && area[i] > numericAreaFloor;
        }

        var kept = new PathGeometry { FillRule = flat.FillRule };
        for (int i = 0; i < n; i++)
            if (keep[i]) kept.Figures.Add(flat.Figures[i].Clone());
        return kept.Figures.Count == 0 ? Geometry.Empty : kept;
    }

    /// <summary>
    /// Nothing left to draw. Survey decides this from the surviving polygon set, so
    /// judge the figures - a bounding box is neither an upper nor a lower bound on
    /// the ink that is actually there.
    /// </summary>
    public static bool IsEmpty(Geometry geometry)
    {
        if (geometry is null || geometry.IsEmpty()) return true;
        var flat = geometry.GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute);
        foreach (var fig in flat.Figures)
        {
            (double a, _) = FigureMetrics(fig);
            if (a > 0) return false;
        }
        return true;
    }

    /// <summary>Absolute area and perimeter of one flattened (polyline) figure.</summary>
    private static (double Area, double Perimeter) FigureMetrics(PathFigure figure)
    {
        var pts = new List<Point> { figure.StartPoint };
        foreach (var seg in figure.Segments)
        {
            switch (seg)
            {
                case LineSegment ls: pts.Add(ls.Point); break;
                case PolyLineSegment pls: pts.AddRange(pls.Points); break;
            }
        }
        if (pts.Count < 3) return (0, 0);
        double area = 0, perim = 0;
        for (int i = 0, prev = pts.Count - 1; i < pts.Count; prev = i, i++)
        {
            area += pts[prev].X * pts[i].Y - pts[i].X * pts[prev].Y;
            perim += (pts[i] - pts[prev]).Length;
        }
        return (Math.Abs(area) / 2, perim);
    }

    private static PathGeometry EllipseAt(Point center, double radius)
    {
        var g = new PathGeometry();
        g.AddGeometry(new EllipseGeometry(center, radius, radius));
        return g;
    }
}
