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
    /// Accepted for call-site compatibility and ignored, see <see cref="ScallopSize"/>.
    /// </param>
    public static PathGeometry ForRectangle(Rect bounds, double intensity = DefaultIntensity,
        double strokeWidth = 1) =>
        For(CloudKind.Rectangle, Corners(bounds), intensity);

    /// <summary>
    /// The engine reads an ellipse cloud straight off the bounding box of the points it is
    /// handed, so it takes the same four corners a rectangle does (ellipseCloudPoints).
    /// </summary>
    public static PathGeometry ForEllipse(Rect bounds, double intensity = DefaultIntensity,
        double strokeWidth = 1) =>
        For(CloudKind.Ellipse, Corners(bounds), intensity);

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
