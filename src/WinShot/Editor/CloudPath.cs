using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// The WPF side of Survey's revision cloud: <see cref="RevisionCloud"/> does the geometry,
/// this turns it into a <see cref="PathGeometry"/> and carries the selection-chrome numbers.
///
/// Cloud is a BORDER STYLE, not a stroke decoration — the shape is REPLACED by the engine's
/// crowns. Survey offers it on rectangle / ellipse / polygon / polyline only (never arrow,
/// line, counter, text or callout), because a revision cloud is a region marker.
///
/// One geometry serves both paints, the way <c>cloudSvgPaint.js</c> describes them but using
/// WPF primitives instead of PDF's stroke-to-path workarounds: the body figure fills without
/// being stroked (<c>IsStroked=false</c>), each crown is an open figure that is stroked and
/// still fills (an unclosed figure is implicitly closed for filling), and
/// <see cref="FillRule.Nonzero"/> unions them because the crowns wind the same way the body
/// does. So the fill is the whole scalloped region, humps included — what Drawboard and
/// Bluebeam fill — and the stroke traces only the crowns.
/// </summary>
internal static class CloudPath
{
    /// <summary>Survey's cloud bump range (AppShell.jsx:2657-2731 clamps 1..20).</summary>
    public const double MinIntensity = 1;
    public const double MaxIntensity = 20;
    public const double DefaultIntensity = 2;

    /// <summary>
    /// Selection chrome, measured on a selected cloud in Drawboard PDF
    /// (cloudAnnotationGeometry.js:241-335). The frame pad is measured from the ink's OUTER
    /// edge: sw/2 to reach it from the sampled crown centreline, plus sw of clear gap.
    /// </summary>
    public const double FramePadStrokeRatio = 1.5;
    public const double HoverGlowWidthRatio = 2.85;
    public const double HoverGlowOpacity = 0.666;

    public static double ClampIntensity(double intensity) =>
        Math.Clamp(Math.Round(intensity), MinIntensity, MaxIntensity);

    /// <summary>
    /// Bump -> scallop size: <c>min(80, 14 * bump)</c> (cloudRadiusForIntensity, doubled).
    /// The line width is deliberately NOT part of this — in the studio the scallop size never
    /// depends on it, and the old Acrobat-style <c>max(radius, 2 * strokeWidth)</c> floor made
    /// the same Bump draw different crowns at wide strokes.
    /// </summary>
    public static double ScallopSize(double intensity) =>
        Math.Min(80, 14 * ClampIntensity(intensity));

    /// <param name="strokeWidth">
    /// Half of it is part of the inset — the pen straddles the crown centreline, so the ink
    /// reaches half a stroke further out than the geometry. See <see cref="FitInside"/>.
    /// It is still NOT part of the scallop SIZE, see <see cref="ScallopSize"/>.
    /// </param>
    public static PathGeometry ForRectangle(Rect bounds, double intensity = DefaultIntensity,
        double strokeWidth = 1) =>
        For(CloudKind.Rectangle, Corners(FitInside(CloudKind.Rectangle, bounds, intensity, strokeWidth)), intensity);

    /// <summary>
    /// The engine reads an ellipse cloud straight off the bounding box of the points it is
    /// handed, so it takes the same four corners a rectangle does (ellipseCloudPoints).
    /// </summary>
    public static PathGeometry ForEllipse(Rect bounds, double intensity = DefaultIntensity,
        double strokeWidth = 1) =>
        For(CloudKind.Ellipse, Corners(FitInside(CloudKind.Ellipse, bounds, intensity, strokeWidth)), intensity);

    /// <summary>
    /// <c>bounds</c> is the box the PAINTED cloud has to fit in, so the ring of points the
    /// crowns are built from is pulled in from it — by the crowns' measured reach past that
    /// ring, plus half the pen, which straddles the crown centreline.
    /// <para>Without this the crowns are simply cut off. Every caller hands the cloud to a
    /// <see cref="System.Windows.Shapes.Path"/> that carries an explicit Width/Height
    /// (<c>ResizeBoxShape</c> rebuilds the geometry from those on every resize, so they are
    /// load-bearing, and <c>AnnotationTransform.LocalBounds</c> reads them for the handles).
    /// WPF gives a <see cref="Stretch.None"/> Shape a LAYOUT CLIP at that slot, so anything
    /// drawn outside (0,0,W,H) is chopped — measured, a 320x180 bump-2 cloud painted exactly
    /// its 320x180 slot while its geometry spanned 345.7x205.7, i.e. the crowns rendered as
    /// flat-topped triangles.</para>
    /// <para>Scaling the finished cloud into the slot instead (<see cref="Stretch.Fill"/>) is
    /// the reflex and it is wrong twice over: Survey keeps scale baked into the coordinates
    /// and never applies a scale() transform because that squashes the scallops
    /// (cloudAnnotationGeometry.js:10-18), and a non-uniform one (sx=0.926, sy=0.875 on this
    /// box) would make the same Bump draw a different crown on every aspect ratio, which is
    /// the width-dependent-scallop bug <see cref="ScallopSize"/> exists to prevent.</para>
    /// </summary>
    private static Rect FitInside(CloudKind kind, Rect b, double intensity, double strokeWidth)
    {
        if (b.Width <= 0 || b.Height <= 0) return b;
        // Measure the bulge, don't predict it. How far a crown reaches past the ring it grew
        // from is the engine's business (and half a scallop is only roughly right — at Bump 2
        // it is 12.9, not 14), so build once on the box and read the overhang off the result.
        // The reach is a property of the scallop size, which does not depend on the box, so
        // one pass lands flush; only a box small enough for the engine's own rectangle-fit to
        // shrink the crowns needs the clamp below.
        Rect reach = For(kind, Corners(b), intensity).Bounds;
        double bulge = reach.IsEmpty ? 0 : Math.Max(
            Math.Max(b.Left - reach.Left, reach.Right - b.Right),
            Math.Max(b.Top - reach.Top, reach.Bottom - b.Bottom));
        double inset = Math.Max(0, bulge) + Math.Max(0, strokeWidth) / 2;
        // ponytail: a big Bump on a small box would invert the rect. Clamp to leave a ring at
        // least 1 unit across and let those crowns spill instead — a clipped cloud still reads
        // as a cloud, an inside-out one is nothing at all.
        double max = Math.Min(b.Width, b.Height) / 2 - 0.5;
        inset = Math.Max(0, Math.Min(inset, max));
        return Rect.Inflate(b, -inset, -inset);
    }

    private static Point[] Corners(Rect b) => [b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft];

    /// <summary>
    /// The crowns for any cloud-capable shape. The body is filled but never stroked, so the
    /// fill is the scalloped region and the ink is only the crowns; an open shape has no
    /// interior, so it gets no body figure.
    /// </summary>
    public static PathGeometry For(CloudKind kind, IReadOnlyList<Point> points,
        double intensity = DefaultIntensity)
    {
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        List<CloudCommand> outline = RevisionCloud.Outline(kind, points, ScallopSize(intensity));
        if (outline.Count == 0) return geometry;

        if (kind != CloudKind.Polyline)
        {
            var body = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
            for (int i = 1; i < points.Count; i++)
                body.Segments.Add(new LineSegment(points[i], isStroked: false));
            geometry.Figures.Add(body);
        }

        PathFigure? crown = null;
        foreach (CloudCommand command in outline)
        {
            if (command.Verb == 'M')
            {
                // One M per crown: each is its own open subpath, which is what keeps the
                // rounded end of every full-size scallop.
                crown = new PathFigure { StartPoint = command.P1, IsClosed = false, IsFilled = true };
                geometry.Figures.Add(crown);
            }
            else
            {
                crown!.Segments.Add(new BezierSegment(command.P1, command.P2, command.P3, isStroked: true));
            }
        }
        return geometry;
    }

    /// <summary>
    /// The dashed selection frame: the outer hull of the crowns padded by
    /// <see cref="FramePadStrokeRatio"/> stroke widths, never the inner box the cloud was
    /// built from.
    /// </summary>
    public static Rect SelectionFrame(Geometry cloud, double strokeWidth)
    {
        Rect hull = cloud.Bounds;
        if (hull.IsEmpty) return Rect.Empty;
        double pad = Math.Max(0, strokeWidth) * FramePadStrokeRatio;
        hull.Inflate(pad, pad);
        return hull;
    }

    /// <summary>
    /// The eight resize grabbers, ON the frame: four corners and four edge midpoints, in
    /// Survey's order (tl, mt, tr, mr, br, mb, bl, ml). Always all eight — cusp-snapped
    /// handles were tried (9d3c964) and reverted (2e0d871) because they put a rectangle's
    /// corner handles inside the frame and doubled grabbers up on a shared crown.
    /// </summary>
    public static Point[] SelectionAnchors(Rect frame)
    {
        double midX = frame.Left + frame.Width / 2, midY = frame.Top + frame.Height / 2;
        return
        [
            frame.TopLeft, new Point(midX, frame.Top), frame.TopRight,
            new Point(frame.Right, midY), frame.BottomRight, new Point(midX, frame.Bottom),
            frame.BottomLeft, new Point(frame.Left, midY),
        ];
    }

    public static double HoverGlowWidth(double strokeWidth) =>
        Math.Max(0, strokeWidth) * HoverGlowWidthRatio;

    /// <summary>
    /// The hover / selection glow is a RING around the ink, never a bed under it: the crowns
    /// widened to <see cref="HoverGlowWidthRatio"/> with the ink band itself knocked out, so
    /// a translucent cloud keeps its own colour instead of reading purple through the blue.
    /// Survey deviates from Drawboard here on purpose (cloudSvgPaint.js, 2026-09-10).
    /// Paint it UNDER the ink at <see cref="HoverGlowOpacity"/> in
    /// <see cref="AnnotationStyle.SelectionRingColor"/>; there is no blur.
    /// </summary>
    public static Geometry HoverGlowRing(PathGeometry cloud, double strokeWidth)
    {
        // The body figure fills but is never stroked, so it must not glow either; only the
        // open crown figures carry ink.
        var crowns = new PathGeometry();
        foreach (PathFigure figure in cloud.Figures)
            if (!figure.IsClosed) crowns.Figures.Add(figure);

        double ink = strokeWidth > 0 ? strokeWidth : 1;
        return new CombinedGeometry(
            GeometryCombineMode.Exclude,
            crowns.GetWidenedPathGeometry(RoundPen(HoverGlowWidth(ink))),
            crowns.GetWidenedPathGeometry(RoundPen(ink)));
    }

    private static Pen RoundPen(double width) => new()
    {
        Thickness = width,
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
}
