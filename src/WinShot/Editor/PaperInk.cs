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

    public static PathGeometry Outline(IEnumerable<Point> raw, double width)
    {
        // Survey's radius is Math.max(0.5, width/2) - the same clamp as a diameter floor of 1.
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
            return centerline.GetWidenedPathGeometry(pen);
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

        var flat = geometry.GetFlattenedPathGeometry();
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
        var flat = geometry.GetFlattenedPathGeometry();
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
