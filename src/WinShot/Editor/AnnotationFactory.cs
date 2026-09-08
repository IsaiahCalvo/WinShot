using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Editor;

/// <summary>
/// Selectable straight-arrow variants. CleanShot's editor offers four arrow looks;
/// combined with the curved-arrow tool these cover the parity set. Persisted in
/// <c>AnnotationData.Style</c> (as the enum name) so a restyle/resize rebuilds the
/// right geometry and an in-session round-trip keeps the chosen look.
/// </summary>
internal enum ArrowStyle
{
    /// <summary>Single filled head, thickness-scaled — the original WinShot arrow.</summary>
    Straight,

    /// <summary>Filled heads at BOTH ends (a double-headed / two-way arrow).</summary>
    Double,

    /// <summary>A slim tapered arrow: thinner head proportions and a hairline shaft accent.</summary>
    Thin,

    /// <summary>
    /// CleanShot's "fancy" arrow: a solid shaft that swells from a point at the tail into
    /// the head, so the whole mark is one filled silhouette rather than a line plus a
    /// triangle. Drawn as a closed figure, so it wants Fill and needs no stroke width.
    /// </summary>
    Fancy,

    /// <summary>
    /// Not a straight-arrow geometry at all: picking it arms the curved-arrow tool, and
    /// restyling a selected straight arrow to it converts the annotation to a curved one.
    /// It lives in this enum because it is a row in the same style menu.
    /// </summary>
    Curved,
}


/// <summary>Builds the WPF elements used as canvas annotations.</summary>
internal static class AnnotationFactory
{

    /// <summary>
    /// Fills a redaction rectangle with the effected pixels it covers, sized 1:1 so the
    /// flattened export is pixel-exact. Anything that cannot be rendered — no source, a
    /// degenerate rect, Black Out — paints flat black, which is the only safe failure for
    /// a mark whose job is to hide something.
    /// </summary>
    public static void PaintRedaction(
        Rectangle target, SD.Bitmap? source, Rect bounds,
        RedactStyle style, int strength, int seed)
    {
        target.Width = Math.Max(1, bounds.Width);
        target.Height = Math.Max(1, bounds.Height);

        SD.Bitmap? effected = null;
        if (source is not null && style != RedactStyle.BlackOut)
        {
            var region = new SD.Rectangle(
                (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
                (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
            try
            {
                effected = Redaction.Render(source, region, style, strength, seed);
            }
            catch (Exception ex)
            {
                Log.Error("Redaction render failed; falling back to black", ex);
            }
        }

        if (effected is null)
        {
            target.Fill = Brushes.Black;
            return;
        }

        using (effected)
        {
            var image = CaptureService.ToBitmapSource(effected);
            var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            if (brush.CanFreeze) brush.Freeze();
            target.Fill = brush;
        }
    }

    /// <summary>
    /// Fills a magnifier callout with the piece of the picture it is looking at, blown up.
    /// Nearest-neighbour scaling on purpose: the point of a callout is usually to show
    /// exactly which pixels are there, and a smoothed blow-up hides that.
    ///
    /// A callout whose focus has been dragged off the picture, or that cannot be rendered,
    /// paints nothing at all and keeps its border — an empty bubble reads as "there is
    /// nothing here", which is the truth, where a black one would read as broken.
    /// </summary>
    public static void PaintMagnifier(
        Rectangle target, SD.Bitmap? source, Rect bounds, Point focus, double scale)
    {
        target.Width = Math.Max(1, bounds.Width);
        target.Height = Math.Max(1, bounds.Height);

        if (source is null) { target.Fill = null; return; }

        Rect region = MagnifierLayout.SourceRegion(bounds, focus, scale);
        var crop = SD.Rectangle.Intersect(
            new SD.Rectangle(
                (int)Math.Floor(region.X), (int)Math.Floor(region.Y),
                (int)Math.Ceiling(region.Width), (int)Math.Ceiling(region.Height)),
            new SD.Rectangle(0, 0, source.Width, source.Height));
        if (crop.Width < 1 || crop.Height < 1) { target.Fill = null; return; }

        try
        {
            using SD.Bitmap piece = CaptureService.Crop(source, crop);
            var image = CaptureService.ToBitmapSource(piece);
            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            if (brush.CanFreeze) brush.Freeze();
            target.Fill = brush;
        }
        catch (Exception ex)
        {
            Log.Error("Magnifier callout render failed", ex);
            target.Fill = null;
        }
    }

    /// <summary>The rounded bubble of a magnifier callout, already filled from the source.</summary>
    public static Rectangle CreateMagnifier(
        Rect bounds, Point focus, double scale, Color border, double thickness, SD.Bitmap? source)
    {
        var bubble = new Rectangle
        {
            RadiusX = 6,
            RadiusY = 6,
            Stroke = new SolidColorBrush(border),
            StrokeThickness = thickness,
        };
        PaintMagnifier(bubble, source, bounds, focus, MagnifierLayout.ClampScale(scale));
        Canvas.SetLeft(bubble, bounds.X);
        Canvas.SetTop(bubble, bounds.Y);
        return bubble;
    }

    /// <summary>
    /// Which end of a drag the head lands on. Two inputs, both of which flip it: the
    /// "Inverse arrow direction" preference, and the Alt key held for one arrow. Holding
    /// Alt with the preference already on puts the head back where the drag ended, which
    /// is the point of a per-drag override.
    /// </summary>
    public static (Point Tail, Point Head) ArrowEnds(Point from, Point to, bool inverted, bool altHeld) =>
        inverted ^ altHeld ? (to, from) : (from, to);

    /// <summary>
    /// The next style in the double-click cycle. Curved is in it because it is a row in
    /// the same style menu, and the cycle wraps so repeated double-clicks never dead-end.
    /// </summary>
    public static ArrowStyle NextArrowStyle(ArrowStyle current)
    {
        var all = Enum.GetValues<ArrowStyle>();
        int i = Array.IndexOf(all, current);
        return all[(i < 0 ? 0 : i + 1) % all.Length];
    }

    /// <summary>
    /// The box a sketchy outline is generated in. 100 units keeps
    /// <see cref="StrokeStyles.SketchAmplitude"/> reading as a percentage of the mark,
    /// which is what makes the wobble look the same at every size.
    /// </summary>
    private const double SketchUnitBox = 100;

    /// <summary>
    /// One figure from a run of points, sketched or ruled — the shared spelling of "draw
    /// these points in order".
    /// </summary>
    private static PathFigure PolyFigure(IReadOnlyList<Point> points, bool closed, bool filled)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = closed, IsFilled = filled };
        for (int i = 1; i < points.Count; i++)
            figure.Segments.Add(new LineSegment(points[i], isStroked: true));
        return figure;
    }

    /// <summary>
    /// One closed silhouette: a shaft that starts at a point and swells to the head's
    /// shoulders, then the head itself. Because it is filled rather than stroked, the
    /// mark keeps a crisp tail at any thickness instead of a round line cap.
    /// </summary>
    private static Geometry FancyArrowGeometry(
        Point from, Point to, Vector dir, double thickness, bool sketchy, int seed)
    {
        double head = Math.Clamp(thickness * 3.5, 10, 30);
        var perp = new Vector(-dir.Y, dir.X);
        Point headBase = to - dir * head;
        // The shaft never gets wider than the head's shoulders, or the silhouette pinches.
        double shaftHalf = Math.Min(thickness * 0.62, head * 0.45 * 0.8);

        Point[] silhouette =
        {
            from,
            headBase + perp * shaftHalf,
            headBase + perp * (head * 0.45),
            to,
            headBase - perp * (head * 0.45),
            headBase - perp * shaftHalf,
        };

        var geometry = new PathGeometry();
        geometry.Figures.Add(PolyFigure(
            sketchy ? StrokeStyles.SketchPath(silhouette, seed, closed: true) : silhouette,
            closed: true, filled: true));
        return geometry;
    }

    /// <summary>
    /// A line or arrow SHAFT, straight or curved, in whichever line quality is asked for.
    /// Clean hands straight back to <see cref="LineCurve.Stroke"/>; Sketchy flattens the
    /// curve first so the wobble follows it instead of cutting the corner.
    /// </summary>
    public static PathGeometry LineGeometry(
        Point from, Point to, Point? mid, StrokeRender render = StrokeRender.Clean, int seed = 0)
    {
        if (render != StrokeRender.Sketchy) return LineCurve.Stroke(from, to, mid);

        IReadOnlyList<Point> run = LineCurve.IsCurved(mid, from, to)
            ? StrokeStyles.FlattenQuadratic(from, LineCurve.ControlThroughMid(from, to, mid!.Value), to)
            : new[] { from, to };
        var geometry = new PathGeometry();
        geometry.Figures.Add(PolyFigure(
            StrokeStyles.SketchPath(run, seed, closed: false), closed: false, filled: false));
        return geometry;
    }

    /// <summary>Maps a stored <c>AnnotationData.Style</c> string to an <see cref="ArrowStyle"/> (default Straight).</summary>
    public static ArrowStyle ParseArrowStyle(string? name) =>
        Enum.TryParse(name, out ArrowStyle s) ? s : ArrowStyle.Straight;

    /// <summary>
    /// Legacy CleanShot-style arrows (Straight / Double / Thin) mapped onto Survey's
    /// six-head vocabulary so old .winshot files keep their look.
    /// </summary>
    public static Geometry ArrowGeometry(
        Point from, Point to, double thickness, ArrowStyle style,
        StrokeRender render = StrokeRender.Clean, int seed = 0)
    {
        // Fancy is not a head choice at all: it is one closed silhouette, so it never
        // reaches the head vocabulary below.
        if (style == ArrowStyle.Fancy)
        {
            var v = to - from;
            if (v.Length < 0.5) v = new Vector(0.5, 0);
            var dir = v;
            dir.Normalize();
            return FancyArrowGeometry(from, to, dir, thickness, render == StrokeRender.Sketchy, seed);
        }

        var end = AnnotationStyle.ParseArrowhead(style.ToString(), out var start);
        return ArrowGeometry(from, to, thickness, end, start, thin: style == ArrowStyle.Thin,
            mid: null, render: render, seed: seed);
    }

    /// <summary>
    /// Survey arrow: a stroked shaft plus optional start/end heads. Head size is
    /// <c>max(8, thickness * 3)</c> (Survey Phase 15). Solid triangles are filled;
    /// the other five heads are stroked.
    /// </summary>
    public static Geometry ArrowGeometry(
        Point from, Point to, double thickness, ArrowheadStyle end,
        ArrowheadStyle start = ArrowheadStyle.None, bool thin = false, Point? mid = null,
        StrokeRender render = StrokeRender.Clean, int seed = 0)
    {
        double headSize = Math.Max(8, thickness * (thin ? 2.6 : 3));
        bool curved = LineCurve.IsCurved(mid, from, to);
        double endAngle = curved
            ? LineCurve.EndAngleRadians(from, to, mid!.Value)
            : Math.Atan2(to.Y - from.Y, to.X - from.X);
        double startAngle = curved
            ? Math.Atan2(
                LineCurve.ControlThroughMid(from, to, mid!.Value).Y - from.Y,
                LineCurve.ControlThroughMid(from, to, mid!.Value).X - from.X)
            : endAngle;

        var endDir = new Vector(Math.Cos(endAngle), Math.Sin(endAngle));
        var startDir = new Vector(Math.Cos(startAngle), Math.Sin(startAngle));
        Point endBase = end == ArrowheadStyle.None || end == ArrowheadStyle.HorizontalLine
            ? to
            : to - endDir * ShaftTrim(end, headSize);
        Point startBase = start == ArrowheadStyle.None || start == ArrowheadStyle.HorizontalLine
            ? from
            : from + startDir * ShaftTrim(start, headSize);

        var geometry = LineGeometry(startBase, endBase, curved ? mid : null, render, seed);
        AppendHead(geometry, to, endAngle, headSize, thickness, end);
        if (start != ArrowheadStyle.None)
            AppendHead(geometry, from, startAngle + Math.PI, headSize, thickness, start);
        return geometry;
    }

    /// <summary>
    /// Survey shortens the shaft for the two triangle heads ONLY, by headSize/3
    /// (lineRenderHelpers.js:261-268). The open circle is centred on the tip, so the shaft
    /// runs into its centre rather than stopping at the rim like a lollipop stick.
    /// </summary>
    private static double ShaftTrim(ArrowheadStyle style, double headSize) => style switch
    {
        ArrowheadStyle.SolidTriangle or ArrowheadStyle.OpenTriangle => headSize / 3,
        _ => 0,
    };

    /// <summary>
    /// Survey strokes the five non-solid-triangle heads at <c>max(2, strokeWidth)</c> so they
    /// stay visible on a 1px line, with round joins/caps and no dash
    /// (lineRenderHelpers.js:110-176). WPF gives one Path a single StrokeThickness and a
    /// single dash array, so the head cannot simply share the shaft's pen. Instead the head
    /// outline is WIDENED by that pen and appended as a FILLED figure: it paints at the 2px
    /// floor, keeps the round join/cap shape, and — being a fill — is immune to the shaft's
    /// dash pattern, all without a second Path threaded through nine call sites.
    /// </summary>
    private static void AppendHead(
        PathGeometry geometry, Point tip, double angle, double headSize, double thickness, ArrowheadStyle style)
    {
        if (style == ArrowheadStyle.None) return;
        if (style == ArrowheadStyle.SolidTriangle)
        {
            double back = headSize / 3;
            double half = headSize / 2;
            Point left = tip + Polar(angle + Math.PI, back) + Polar(angle + Math.PI / 2, half);
            Point right = tip + Polar(angle + Math.PI, back) + Polar(angle - Math.PI / 2, half);
            var solid = new PathFigure { StartPoint = left, IsClosed = true, IsFilled = true };
            solid.Segments.Add(new LineSegment(tip, isStroked: false));
            solid.Segments.Add(new LineSegment(right, isStroked: false));
            geometry.Figures.Add(solid);
            return;
        }

        var outline = new PathGeometry();
        switch (style)
        {
            case ArrowheadStyle.OpenTriangle:
            {
                double back = headSize / 3;
                double half = headSize / 2;
                Point left = tip + Polar(angle + Math.PI, back) + Polar(angle + Math.PI / 2, half);
                Point right = tip + Polar(angle + Math.PI, back) + Polar(angle - Math.PI / 2, half);
                var fig = new PathFigure { StartPoint = left, IsClosed = true, IsFilled = false };
                fig.Segments.Add(new LineSegment(tip, isStroked: true));
                fig.Segments.Add(new LineSegment(right, isStroked: true));
                outline.Figures.Add(fig);
                break;
            }
            case ArrowheadStyle.VShape:
            {
                const double spread = Math.PI / 6;
                Point arm1 = tip - new Vector(Math.Cos(angle - spread), Math.Sin(angle - spread)) * headSize;
                Point arm2 = tip - new Vector(Math.Cos(angle + spread), Math.Sin(angle + spread)) * headSize;
                var fig = new PathFigure { StartPoint = arm1, IsClosed = false, IsFilled = false };
                fig.Segments.Add(new LineSegment(tip, isStroked: true));
                fig.Segments.Add(new LineSegment(arm2, isStroked: true));
                outline.Figures.Add(fig);
                break;
            }
            case ArrowheadStyle.OpenCircle:
            {
                double r = headSize / 2;
                Point start = tip + new Vector(r, 0);
                var fig = new PathFigure { StartPoint = start, IsClosed = true, IsFilled = false };
                fig.Segments.Add(new ArcSegment(
                    tip + new Vector(-r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                fig.Segments.Add(new ArcSegment(
                    start, new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                outline.Figures.Add(fig);
                break;
            }
            case ArrowheadStyle.HorizontalLine:
            {
                double half = headSize / 2;
                var perp = new Vector(-Math.Sin(angle), Math.Cos(angle)) * half;
                var fig = new PathFigure { StartPoint = tip + perp, IsClosed = false, IsFilled = false };
                fig.Segments.Add(new LineSegment(tip - perp, isStroked: true));
                outline.Figures.Add(fig);
                break;
            }
            default:
                return;
        }

        var pen = new Pen(Brushes.Black, Math.Max(2, thickness))
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        var widened = outline.GetWidenedPathGeometry(pen);
        foreach (var fig in widened.Figures)
        {
            var copy = fig.Clone();
            copy.IsFilled = true;
            // The head shares the arrow's Path, whose Stroke paints the SHAFT. Leaving the
            // outline stroked as well would re-widen it by the shaft pen and fill the open
            // circle's / open triangle's hole back in, so the outline is fill-only.
            foreach (var seg in copy.Segments) seg.IsStroked = false;
            geometry.Figures.Add(copy);
        }
    }

    private static Vector Polar(double angle, double length) =>
        new(Math.Cos(angle) * length, Math.Sin(angle) * length);

    /// <summary>One filled triangular arrowhead: tip at <paramref name="tip"/>, base centered at <paramref name="baseCenter"/>.</summary>
    private static PathFigure HeadFigure(Point tip, Point baseCenter, Vector perp)
    {
        var head = new PathFigure { StartPoint = tip, IsClosed = true, IsFilled = true };
        head.Segments.Add(new LineSegment(baseCenter + perp, isStroked: true));
        head.Segments.Add(new LineSegment(baseCenter - perp, isStroked: true));
        return head;
    }

    /// <summary>
    /// Quadratic Bézier shaft (from → control → to) with a filled triangular head
    /// oriented along the curve's end tangent. Same head sizing as the straight arrow.
    /// </summary>
    public static Geometry CurvedArrowGeometry(
        Point from, Point control, Point to, double thickness,
        StrokeRender render = StrokeRender.Clean, int seed = 0)
    {
        var tangent = to - control;
        if (tangent.Length < 0.5) tangent = to - from;
        if (tangent.Length < 0.5) tangent = new Vector(0.5, 0);
        var dir = tangent;
        dir.Normalize();

        double head = Math.Clamp(thickness * 3.5, 10, 30);
        Point headBase = to - dir * head;
        var perp = new Vector(-dir.Y, dir.X) * head * 0.45;

        var geometry = new PathGeometry();

        if (render == StrokeRender.Sketchy)
        {
            geometry.Figures.Add(PolyFigure(
                StrokeStyles.SketchPath(
                    StrokeStyles.FlattenQuadratic(from, control, headBase), seed, closed: false),
                closed: false, filled: false));
        }
        else
        {
            var shaft = new PathFigure { StartPoint = from, IsFilled = false };
            shaft.Segments.Add(new QuadraticBezierSegment(control, headBase, isStroked: true));
            geometry.Figures.Add(shaft);
        }

        Point[] triangle = { to, headBase + perp, headBase - perp };
        geometry.Figures.Add(PolyFigure(
            render == StrokeRender.Sketchy
                ? StrokeStyles.SketchPath(triangle, unchecked(seed + 1), closed: true)
                : triangle,
            closed: true, filled: true));

        return geometry;
    }

    /// <summary>Default Bézier control point: the midpoint offset perpendicular by 20% of the length.</summary>
    public static Point DefaultCurveControl(Point from, Point to)
    {
        var v = to - from;
        Point mid = from + v / 2;
        if (v.Length < 0.5) return mid;
        var perp = new Vector(-v.Y, v.X);
        perp.Normalize();
        return mid + perp * v.Length * 0.2;
    }

    /// <summary>
    /// Smooths a raw freehand point stream into a polished pen stroke. The points are
    /// first lightly de-noised with a small moving average, then a Catmull-Rom spline is
    /// resampled into a denser, rounded point list so the committed Polyline reads like
    /// CleanShot's Pencil instead of a jagged trace. Storing points (not a path geometry)
    /// keeps the freehand annotation identical to what the project serializer rebuilds.
    /// Fewer than 3 points are returned as-is so dots/short flicks still draw.
    /// </summary>
    public static PointCollection SmoothFreehandPoints(IList<Point> raw)
    {
        var pts = MovingAverage(raw, window: 2);
        var result = new PointCollection();
        if (pts.Count < 3)
        {
            foreach (var p in pts) result.Add(p);
            return result;
        }

        // Sample each Catmull-Rom segment a few times for a smooth, continuous curve.
        const int steps = 6;
        result.Add(pts[0]);
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Point p0 = pts[i == 0 ? 0 : i - 1];
            Point p1 = pts[i];
            Point p2 = pts[i + 1];
            Point p3 = pts[i + 2 < pts.Count ? i + 2 : pts.Count - 1];

            for (int s = 1; s <= steps; s++)
            {
                double tt = (double)s / steps;
                result.Add(CatmullRom(p0, p1, p2, p3, tt));
            }
        }
        return result;
    }

    /// <summary>Catmull-Rom interpolation (tension 0.5) between p1 and p2 at parameter t∈[0,1].</summary>
    private static Point CatmullRom(Point p0, Point p1, Point p2, Point p3, double t)
    {
        double t2 = t * t, t3 = t2 * t;
        double x = 0.5 * ((2 * p1.X) + (-p0.X + p2.X) * t +
                          (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 +
                          (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
        double y = 0.5 * ((2 * p1.Y) + (-p0.Y + p2.Y) * t +
                          (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                          (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
        return new Point(x, y);
    }

    /// <summary>Collapses near-duplicate points and applies a tiny centered moving average.</summary>
    private static List<Point> MovingAverage(IList<Point> raw, int window)
    {
        var dedup = new List<Point>();
        foreach (var p in raw)
            if (dedup.Count == 0 || (p - dedup[^1]).Length > 0.5)
                dedup.Add(p);
        if (dedup.Count <= 2) return dedup.Count == 0 ? new List<Point> { new(0, 0) } : dedup;

        var smoothed = new List<Point>(dedup.Count);
        for (int i = 0; i < dedup.Count; i++)
        {
            int lo = Math.Max(0, i - window), hi = Math.Min(dedup.Count - 1, i + window);
            double sx = 0, sy = 0;
            for (int j = lo; j <= hi; j++) { sx += dedup[j].X; sy += dedup[j].Y; }
            int n = hi - lo + 1;
            smoothed.Add(new Point(sx / n, sy / n));
        }
        // Preserve the true endpoints so the stroke still starts/ends where drawn.
        smoothed[0] = dedup[0];
        smoothed[^1] = dedup[^1];
        return smoothed;
    }

    /// <summary>
    /// Formats a 1-based step index as a spreadsheet-style letter sequence
    /// (1→A, 26→Z, 27→AA, …). Values &lt; 1 fall back to "A".
    /// </summary>
    public static string StepLetterLabel(int number)
    {
        if (number < 1) return "A";
        var sb = new System.Text.StringBuilder();
        int n = number;
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('A' + n % 26));
            n /= 26;
        }
        return sb.ToString();
    }

    /// <summary>
    /// A selectable vector spotlight: a path covering the whole image with an
    /// even-odd hole at <paramref name="hole"/>, filled #99000000. Lives on the
    /// annotation canvas, so Select can move/delete it like any other annotation.
    /// </summary>
    public static Path CreateSpotlight(Size imageSize, Rect hole)
    {
        var layout = SpotlightLayout.Calculate(imageSize, hole);
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, layout.Outer.Width, layout.Outer.Height)));
        group.Children.Add(new RectangleGeometry(layout.Hole));
        return new Path
        {
            Data = group,
            Fill = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
        };
    }

    public static double FontSizeFor(double thickness) => 11 + thickness * 4;

    /// <summary>Takes the foreground brush and font size directly so re-editing a committed label can reproduce its look.</summary>
    public static TextBox CreateTextEditor(Brush foreground, double fontSize) =>
        new TextBox
        {
            // Survey's floor for the editable box is 8 + 2 * TEXT_PADDING
            // (TextEditOverlay.jsx:164); anything wider would render past the box it commits.
            MinWidth = 8 + 2 * TextPadding,
            FontSize = fontSize,
            FontWeight = FontWeights.Normal,
            Foreground = foreground,
            // Survey's caret is a fixed iOS blue, independent of the text colour
            // (TextEditOverlay.jsx:591 caretColor '#007AFF').
            CaretBrush = new SolidColorBrush(AnnotationStyle.CaretColor),
            Background = Brushes.Transparent, // Survey edits in place over the page
            // Chromeless: the box chrome comes from the live preview painted with the
            // COMMITTED look, so nothing changes appearance on commit
            // (TextEditOverlay.jsx:563-564).
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(TextPadding),
        }.WithLineHeight(fontSize * TextLineHeightFactor);

    /// <summary>Transparent background keeps the whole text box clickable for the Select tool without rendering anything.</summary>
    public static TextBlock CreateTextLabel(string text, Brush foreground, double fontSize) =>
        CreateTextLabel(text, foreground, fontSize, DefaultTextFontFamily, bold: false, italic: false,
            underline: false, strike: false, TextAlignment.Left);

    public static TextBlock CreateTextLabel(
        string text, Brush foreground, double fontSize, string fontFamily,
        bool bold, bool italic, bool underline, bool strike, TextAlignment align)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontFamily = new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? DefaultTextFontFamily : fontFamily),
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = foreground,
            Background = Brushes.Transparent,
            TextAlignment = align,
            TextWrapping = TextWrapping.Wrap,
            // Same line-height contract the editor uses, so nothing reflows on commit.
            LineHeight = fontSize * TextLineHeightFactor,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        if (underline || strike)
        {
            tb.TextDecorations = new TextDecorationCollection();
            if (underline) tb.TextDecorations.Add(TextDecorations.Underline);
            if (strike) tb.TextDecorations.Add(TextDecorations.Strikethrough);
        }
        return tb;
    }

    public static TextAlignment ParseAlign(string? name) => name switch
    {
        "center" => TextAlignment.Center,
        "right" => TextAlignment.Right,
        _ => TextAlignment.Left,
    };

    public static string AlignName(TextAlignment align) => align switch
    {
        TextAlignment.Center => "center",
        TextAlignment.Right => "right",
        _ => "left",
    };

    /// <summary>
    /// Builds the committed annotation element for a text style. Plain/Bold/Huge return
    /// a TextBlock (so Select double-click re-edit keeps working); Outline returns a
    /// glyph Path with a white stroke; Pill wraps the text in a rounded dark Border.
    /// Huge's enlarged font size is applied by the caller before committing.
    /// </summary>
    public static FrameworkElement CreateStyledTextLabel(string text, Brush foreground, double fontSize, TextStyle style) =>
        CreateStyledTextLabel(text, foreground, fontSize, style, DefaultTextFontFamily,
            bold: style == TextStyle.Bold, italic: false, underline: false, strike: false, TextAlignment.Left);

    public static FrameworkElement CreateStyledTextLabel(
        string text, Brush foreground, double fontSize, TextStyle style,
        string fontFamily, bool bold, bool italic, bool underline, bool strike, TextAlignment align) =>
        CreateStyledTextLabel(text, foreground, fontSize, style, fontFamily, bold, italic, underline, strike, align,
            verticalAlign: "top", boxWidth: null, boxHeight: null);

    public static FrameworkElement CreateStyledTextLabel(
        string text, Brush foreground, double fontSize, TextStyle style,
        string fontFamily, bool bold, bool italic, bool underline, bool strike, TextAlignment align,
        string? verticalAlign, double? boxWidth, double? boxHeight) =>
        CreateStyledTextLabel(text, foreground, fontSize, style, fontFamily, bold, italic, underline, strike, align,
            verticalAlign, boxWidth, boxHeight, fill: Colors.Transparent, strokeThickness: 0, LineBorderStyle.Solid);

    public const double DefaultTextBoxWidth = 160;

    public static FrameworkElement CreateStyledTextLabel(
        string text, Brush foreground, double fontSize, TextStyle style,
        string fontFamily, bool bold, bool italic, bool underline, bool strike, TextAlignment align,
        string? verticalAlign, double? boxWidth, double? boxHeight,
        Color fill, double strokeThickness, LineBorderStyle lineStyle, Brush? chromeStroke = null)
    {
        // Survey has no `style` pseudo-property on a Textbox and nothing ORs boldness in from
        // one: bold is exactly fontWeight === 'bold' (textEditCommit.js:26-62, :91-104).
        bool useBold = bold;
        FrameworkElement inner = style switch
        {
            TextStyle.Outline => CreateOutlineGlyph(text, foreground, fontSize, fontFamily, useBold, italic),
            TextStyle.Pill => CreatePillLabel(text, foreground, fontSize),
            _ => CreateTextLabel(text, foreground, fontSize, fontFamily, useBold, italic, underline, strike, align),
        };

        if (style is TextStyle.Outline or TextStyle.Pill)
            return inner;

        double width = boxWidth is > 1 ? boxWidth.Value : DefaultTextBoxWidth;
        double height = boxHeight is > 1 ? boxHeight.Value : SingleLineBoxHeight(fontSize, fontFamily);
        if (inner is TextBlock tb)
        {
            tb.Width = Math.Max(1, width - 2 * TextPadding);
            tb.TextWrapping = TextWrapping.Wrap;
            tb.VerticalAlignment = ParseVerticalAlign(verticalAlign);
        }

        var box = new Grid { Width = width, Height = height };
        var chrome = new Rectangle
        {
            Fill = fill.A == 0 ? Brushes.Transparent : new SolidColorBrush(fill),
            // Survey's textbox border is a plain <rect> with no rx/ry, stroked in the
            // stored `stroke` — hardcoded '#000000' for user-created text, never the
            // picker colour (svgAnnotationRenderers.jsx:1231-1246, TextEditOverlay.jsx:140-142).
            // Survey's textbox border rect is stroked in the stored `stroke`, a field of its
            // own — '#000000' for user-created text, repainted only by the top-bar stroke
            // swatch. The GLYPHS are `fill` (svgAnnotationRenderers.jsx:1236-1246, :1276).
            Stroke = strokeThickness > 0 ? (chromeStroke ?? Brushes.Black) : Brushes.Transparent,
            StrokeThickness = Math.Max(0, strokeThickness),
        };
        ApplyDash(chrome, lineStyle == LineBorderStyle.Cloud ? LineBorderStyle.Solid : lineStyle);
        box.Children.Add(chrome);
        // Survey gives the content area fontSize * 0.35 of extra bottom room for descenders
        // (svgAnnotationRenderers.jsx:1191) rather than growing the box itself.
        inner.Margin = new Thickness(
            TextPadding, TextPadding, TextPadding, TextPadding - fontSize * TextDescenderRatio);
        box.Children.Add(inner);
        return box;
    }

    private static Path CreateOutlineGlyph(
        string text, Brush foreground, double fontSize, string fontFamily, bool bold, bool italic)
    {
        var typeface = new Typeface(new FontFamily(fontFamily),
            italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, fontSize, foreground, pixelsPerDip: 1.0);
        return new Path
        {
            Data = formatted.BuildGeometry(new Point(0, 0)),
            Fill = foreground,
            Stroke = Brushes.White,
            StrokeThickness = Math.Max(1.2, fontSize / 14),
            StrokeLineJoin = PenLineJoin.Round,
        };
    }

    /// </summary>
    private static (Brush Plate, Brush Ink) PillBrushes(Brush foreground)
    {
        Color pick = foreground is SolidColorBrush { Color: var c } ? c : Colors.Black;
        // The ALPHA IS DROPPED, deliberately: the ink is picked by contrast against the
        // plate, so the plate has to be the colour that ink was measured against. Keeping
        // the pick's alpha let a 25 %-opaque black plate composite to ~#BFBFBF over a light
        // screenshot and put white ink on it at 1.8:1.
        Color plate = Color.FromRgb(pick.R, pick.G, pick.B);
        return (new SolidColorBrush(plate), new SolidColorBrush(ColorMath.TextOn(plate)));
    }

    /// <summary>
    /// A pill label and its pointer tail. The tail sticks out past the label, so the two
    /// live in a Grid whose size is the LABEL's — the tail is drawn outside those bounds
    /// on purpose, which is what keeps the label measuring, resizing and re-editing exactly
    /// as it did before the tail existed.
    ///
    /// The geometry cannot be built here: the pill's size comes from measuring the text, so
    /// the tail is redrawn on the label's own SizeChanged and whenever the tip moves.
    /// </summary>
    public static Grid CreatePillLabel(string text, Brush foreground, double fontSize)
    {
        (Brush plate, Brush ink) = PillBrushes(foreground);
        var pill = new Border
        {
            Background = plate,
            CornerRadius = new CornerRadius(fontSize * 0.55),
            Padding = new Thickness(fontSize * 0.5, fontSize * 0.2, fontSize * 0.5, fontSize * 0.2),
            Child = CreateTextLabel(text, ink, fontSize),
        };
        var tail = new Path
        {
            Name = PillTailName,
            Fill = plate,
            IsHitTestVisible = false,
        };
        // The tail is drawn OUTSIDE the label's bounds, and a Path asked for its desired
        // size reports the box its geometry needs — which would grow the pill to make room
        // for its own tail. A zero-sized Canvas reports nothing, clips nothing, and puts
        // its child's origin exactly on the label's top-left, which is the coordinate
        // space the tail geometry is computed in.
        var well = new Canvas { Width = 0, Height = 0, IsHitTestVisible = false };
        well.Children.Add(tail);

        var host = new Grid();
        host.Children.Add(well);   // behind the pill, so the join is never a visible seam
        host.Children.Add(pill);
        host.SizeChanged += (_, _) => RefreshPillTail(host);
        return host;
    }

    /// <summary>The tail Path inside a pill label, by name — one lookup rule, one place.</summary>
    public const string PillTailName = "PillTail";

    /// <summary>Where a pill label's tail currently points, in the label's own coordinates.</summary>
    public static Point? PillTailTip(FrameworkElement label) =>
        label is Grid host && FindTail(host) is Path { Tag: Point tip } ? tip : null;

    private static Path? FindTail(Grid host) =>
        host.Children.OfType<Canvas>()
            .SelectMany(c => c.Children.OfType<Path>())
            .FirstOrDefault(p => p.Name == PillTailName);

    /// <summary>
    /// Points a pill label's tail at <paramref name="tip"/> (label coordinates). Passing
    /// null gives the label a tail in its default place, which is what a new pill gets.
    /// </summary>
    public static void SetPillTail(FrameworkElement label, Point? tip)
    {
        if (label is not Grid host || FindTail(host) is not Path tail) return;
        tail.Tag = tip ?? TextTail.DefaultTip(new Size(host.ActualWidth, host.ActualHeight));
        RefreshPillTail(host);
    }

    /// <summary>Rebuilds a pill's tail from its current size and tip.</summary>
    public static void RefreshPillTail(FrameworkElement label)
    {
        if (label is not Grid host || FindTail(host) is not Path tail) return;

        var pill = new Size(host.ActualWidth, host.ActualHeight);
        if (pill.Width < 1 || pill.Height < 1) { tail.Data = null; return; }

        // A label that has never been given a tip gets the default one the moment it has a
        // size to compute it from — which is this, the first layout pass.
        if (tail.Tag is not Point tip)
        {
            tip = TextTail.DefaultTip(pill);
            tail.Tag = tip;
        }

        Point[]? triangle = TextTail.Triangle(pill, tip);
        if (triangle is null) { tail.Data = null; return; }

        var figure = new PathFigure { StartPoint = triangle[0], IsClosed = true, IsFilled = true };
        for (int i = 1; i < triangle.Length; i++)
            figure.Segments.Add(new LineSegment(triangle[i], isStroked: false));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        tail.Data = geometry;
    }

    /// <summary>32px emoji dropped as a text annotation; transparent background keeps it fully clickable.</summary>
    public static TextBlock CreateEmojiLabel(string emoji) =>
        new()
        {
            Text = emoji,
            FontSize = 32,
            FontFamily = new FontFamily("Segoe UI Emoji"),
            Background = Brushes.Transparent,
        };

    /// <summary>
    /// Survey teardrop pin: circle + nub at 225° (southwest). Caption is the
    /// number (1, 2, …) or a letter sequence when <paramref name="letters"/> is set.
    /// </summary>
    /// <summary>
    /// Turns a counter's pointer to <paramref name="pointerAngle"/> (0 = east, clockwise) in
    /// place: only the pin path is regenerated, so the number stays upright — Survey's
    /// counter rotates the tip, never the label.
    /// </summary>
    public static void SetStepPointerAngle(Grid badge, double pointerAngle)
    {
        if (badge.Children.OfType<Path>().FirstOrDefault() is not { } pin) return;
        double tipDist = badge.Width / 2;
        double radius = tipDist / 1.5; // CounterGeometry.TipDistance is radius * 1.5
        pin.Data = CounterGeometry.Pin(new Point(tipDist, tipDist), radius, pointerAngle);
    }

    public static Grid CreateStepBadge(int number, Color color, double thickness, bool letters,
        double pointerAngle = CounterGeometry.DefaultPointerAngle)
    {
        double radius = CounterGeometry.RadiusFor(thickness);
        double tipDist = CounterGeometry.TipDistance(radius);
        double size = tipDist * 2;
        Point body = new(tipDist, tipDist);
        string caption = letters ? StepLetterLabel(number) : number.ToString();

        double font = CounterGeometry.FontSize(radius, caption);
        var badge = new Grid { Width = size, Height = size };
        // Survey renderCounter draws the pin as ONE filled path with stroke="none"
        // (svgAnnotationRenderers.jsx:1725) — no ring, ever.
        badge.Children.Add(new Path
        {
            Data = CounterGeometry.Pin(body, radius, pointerAngle),
            Fill = new SolidColorBrush(color),
        });
        // Survey's label colour is data.numberColor with a '#ffffff' fallback — per-series and
        // never derived from the fill's luminance. The badge Grid's exact centre IS the pin
        // body, so cell-centring lands the glyphs without a per-glyph width guess.
        badge.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = new SolidColorBrush(StepNumberColor),
            FontWeight = FontWeights.Bold,
            FontSize = font,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return badge;
    }

    /// <summary>Survey's counter label default: '#ffffff' (PDFViewer.jsx:4250, 25614).</summary>
    public static readonly Color StepNumberColor = Colors.White;

    /// <summary>Rewrites a badge's caption in place after a renumber, refitting the font.</summary>
    public static void SetStepCaption(Grid badge, int number, bool letters)
    {
        if (badge.Children.OfType<TextBlock>().FirstOrDefault() is not { } label) return;
        string caption = letters ? StepLetterLabel(number) : number.ToString();
        double radius = badge.Width / 2 / 1.5; // CounterGeometry.TipDistance is radius * 1.5
        label.Text = caption;
        label.FontSize = CounterGeometry.FontSize(radius, caption);
    }

    /// <summary>
    /// Applies Survey's line-height contract to an in-place editor. TextBox has no LineHeight
    /// of its own; Block.LineHeight is an inherited attached property its inner paragraph
    /// picks up, which is how a TextBox is given a fixed line box in WPF.
    /// </summary>
    public static TextBox WithLineHeight(this TextBox tb, double lineHeight)
    {
        tb.SetValue(System.Windows.Documents.Block.LineHeightProperty, lineHeight);
        tb.SetValue(System.Windows.Documents.Block.LineStackingStrategyProperty,
            LineStackingStrategy.BlockLineHeight);
        return tb;
    }

    public const double TextPadding = 6;

    /// <summary>
    /// The face a new text label is drawn in. Survey pins its own DEFAULT_FONT_FAMILY
    /// 'Helvetica' in both the editor and the measurement context so the committed label and
    /// its editor can never fall back to different faces (TextEditOverlay.jsx:47,
    /// textEditCommit.js:154) — that ONE-CONST rule is the mechanic worth keeping. The face
    /// itself is not: Helvetica is not installed on Windows and resolves to whatever the
    /// font fallback picks, so the default here is the system UI face, and Helvetica stays
    /// in the family list for anyone who wants it.
    /// </summary>
    public const string DefaultTextFontFamily = "Segoe UI";

    /// <summary>
    /// Survey's line-height contract: <c>(lineHeight || 1.16) * 1.13</c>
    /// (svgAnnotationRenderers.jsx:1086) — the same 1.3108 factor the editor and the
    /// committed SVG both use.
    /// </summary>
    public const double TextLineHeightFactor = 1.16 * 1.13;

    /// <summary>
    /// Survey's descender buffer: the text CONTENT area is <c>fontSize * 0.35</c> taller
    /// than the box's inner height so j/p/g/q/y are not cut off
    /// (svgAnnotationRenderers.jsx:1181, TextEditOverlay.jsx:470).
    /// </summary>
    public const double TextDescenderRatio = 0.35;

    /// <summary>
    /// Height of a one-line text box, matching Survey's new-text mount geometry
    /// (TextEditOverlay.jsx:165): <c>fontSize * 1.16 * 1.13 + 2 * TEXT_PADDING</c>.
    /// The font family no longer participates — Survey's factor is font-independent.
    /// </summary>
    public static double SingleLineBoxHeight(double fontSize, string? fontFamily = null) =>
        fontSize * TextLineHeightFactor + 2 * TextPadding;

    public static VerticalAlignment ParseVerticalAlign(string? name) => name switch
    {
        "middle" => VerticalAlignment.Center,
        "bottom" => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Top,
    };

    public static string VerticalAlignName(VerticalAlignment align) => align switch
    {
        VerticalAlignment.Center => "middle",
        VerticalAlignment.Bottom => "bottom",
        _ => "top",
    };

    public static Shape CreateBoxShape(
        string type, Rect bounds, Color stroke, Color fill, double thickness, LineBorderStyle lineStyle,
        double cloudIntensity = CloudPath.DefaultIntensity,
        StrokeRender render = StrokeRender.Clean, int seed = 0)
    {
        Shape shape;
        if (type == AnnotationData.TypeRectangle && lineStyle == LineBorderStyle.Cloud)
        {
            shape = new Path
            {
                Data = CloudPath.ForRectangle(
                    new Rect(0, 0, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)),
                    cloudIntensity, thickness),
            };
        }
        else if (render == StrokeRender.Sketchy)
        {
            // Jitter is generated in a fixed unit box and stretched to the mark's real size:
            // sizing the wobble with the mark would make a big rectangle wildly shaky and a
            // small one perfectly straight. Stretch.Fill keeps every caller that sets
            // Width/Height working, and leaves the stroke at the asked-for thickness.
            var box = new Rect(0, 0, SketchUnitBox, SketchUnitBox);
            var outline = type == AnnotationData.TypeEllipse
                ? StrokeStyles.SketchPolygon(StrokeStyles.EllipsePoints(box), seed)
                : StrokeStyles.SketchPolygon(StrokeStyles.RectCorners(box), seed);
            var sketch = new PathGeometry();
            sketch.Figures.Add(PolyFigure(outline, closed: true, filled: true));
            sketch.Freeze();
            shape = new Path { Data = sketch, Stretch = Stretch.Fill };
        }
        else
        {
            // Survey rects are square-cornered (annotationCreationCommit.js:180-181 emits
            // rx:0/ry:0; renderRect writes no rx at all).
            shape = type == AnnotationData.TypeEllipse ? new Ellipse() : new Rectangle();
        }

        shape.Stroke = new SolidColorBrush(stroke);
        shape.StrokeThickness = thickness;
        shape.Fill = fill.A == 0 ? Brushes.Transparent : new SolidColorBrush(fill);
        shape.Width = Math.Max(1, bounds.Width);
        shape.Height = Math.Max(1, bounds.Height);
        if (lineStyle != LineBorderStyle.Cloud)
            ApplyDash(shape, lineStyle);
        else
            shape.StrokeDashArray = null;
        Canvas.SetLeft(shape, bounds.X);
        Canvas.SetTop(shape, bounds.Y);
        return shape;
    }

    public static Path CreateInk(IList<Point> points, Color color, double width, bool highlighter,
        double penAspect = 1)
    {
        var path = new Path
        {
            // Survey has ONE ink geometry for both tools: a constant-width swept disk
            // (paperAnnotationGeometry.js:1542). No pressure, no taper, either tool - until a
            // non-uniform resize stretches the pen with the geometry (AnnotationData.PenAspect).
            Data = PaperInk.Outline(points, width, aspect: penAspect),
            Fill = new SolidColorBrush(color),
            Stroke = Brushes.Transparent,
            StrokeThickness = 0,
        };
        // Live canvas is alpha-fill; Flatten multiplies highlighter ink against the screenshot.
        if (highlighter)
            path.Opacity = 1;
        return path;
    }

    /// <summary>
    /// Survey's dash pattern is in ABSOLUTE user units — dashed [6,4], dotted [2,4] straight
    /// onto SVG stroke-dasharray, unaffected by stroke width (annotationCreationCommit.js:92-107).
    /// WPF multiplies StrokeDashArray by StrokeThickness, so divide it back out here.
    /// Must run AFTER StrokeThickness is assigned, and again whenever it changes.
    /// </summary>
    /// <remarks>
    /// Cap follows the shape: lines, arrow shafts and callout leaders carry Survey's
    /// strokeLinecap:'round'; closed-shape borders (rect/ellipse/text-box chrome) use SVG's
    /// default butt cap, so a dotted [2,4] border reads as square beads, not round ones.
    /// </remarks>
    public static void ApplyDash(Shape shape, LineBorderStyle style)
    {
        double[]? pattern = style switch
        {
            LineBorderStyle.Dashed => AnnotationStyle.DashedPattern,
            LineBorderStyle.Dotted => AnnotationStyle.DottedPattern,
            _ => null,
        };
        double t = shape.StrokeThickness > 0 ? shape.StrokeThickness : 1;
        shape.StrokeDashArray = pattern is null
            ? null
            : new DoubleCollection(pattern.Select(v => v / t));
        shape.StrokeDashCap = shape is Rectangle or Ellipse ? PenLineCap.Flat : PenLineCap.Round;
    }

    public static CalloutAnnotation CreateCallout(
        CalloutLayout layout, string text, Color stroke, Color fill, double thickness,
        ArrowheadStyle head, LineBorderStyle lineStyle, double fontSize, string? fontFamily = null,
        Color? fontColor = null)
    {
        var visual = new CalloutAnnotation();
        visual.Apply(layout, text, stroke, fill, thickness, head, lineStyle, fontSize, fontFamily, fontColor);
        return visual;
    }
}

