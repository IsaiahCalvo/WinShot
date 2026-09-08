using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Core;
using System.Windows.Shapes;
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
    /// The element for a rectangle or ellipse annotation, in whichever line quality is
    /// asked for. Clean is the plain WPF shape it has always been; Sketchy is a
    /// <see cref="Path"/> whose data is the same outline drawn by a shaky hand.
    ///
    /// The Path is <see cref="Stretch.Fill"/>ed into its Width/Height, so it sizes and
    /// resizes exactly the way the plain shapes do — every caller that sets
    /// Canvas.Left/Top and Width/Height keeps working, and the stroke stays the asked-for
    /// thickness rather than being scaled by the fit. The seed lives on the annotation, so
    /// a sketchy mark redraws identically after a move, an undo or a project reload.
    /// </summary>
    public static Shape CreateShape(bool ellipse, StrokeRender render, int seed)
    {
        if (render != StrokeRender.Sketchy)
            return ellipse ? new Ellipse() : new Rectangle { RadiusX = 2, RadiusY = 2 };

        // Jitter is generated in a fixed unit box and stretched to the mark's real size:
        // sizing the wobble with the mark would make a big rectangle wildly shaky and a
        // small one perfectly straight.
        var box = new Rect(0, 0, SketchUnitBox, SketchUnitBox);
        var outline = ellipse
            ? StrokeStyles.SketchPolygon(StrokeStyles.EllipsePoints(box), seed)
            : StrokeStyles.SketchPolygon(StrokeStyles.RectCorners(box), seed);

        var figure = new PathFigure { StartPoint = outline[0], IsClosed = true, IsFilled = true };
        for (int i = 1; i < outline.Count; i++)
            figure.Segments.Add(new LineSegment(outline[i], isStroked: true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        return new Path { Data = geometry, Stretch = Stretch.Fill };
    }

    /// <summary>
    /// The box a sketchy outline is generated in. 100 units keeps
    /// <see cref="StrokeStyles.SketchAmplitude"/> reading as a percentage of the mark,
    /// which is what makes the wobble look the same at every size.
    /// </summary>
    private const double SketchUnitBox = 100;

    /// <summary>
    /// Applies a dash pattern to a shape's outline. Called on create and on restyle, so a
    /// mark drawn solid can become dotted without being redrawn.
    /// </summary>
    public static void ApplyDash(Shape shape, StrokeDash dash)
    {
        shape.StrokeDashArray = StrokeStyles.DashArray(dash);
        var cap = StrokeStyles.CapFor(dash);
        shape.StrokeDashCap = cap;
        // A dotted OUTLINE needs round ends on every dash; a solid one keeps whatever the
        // caller set, because a line and an arrow already choose their own caps.
        if (dash != StrokeDash.Solid)
        {
            shape.StrokeStartLineCap = cap;
            shape.StrokeEndLineCap = cap;
        }
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

    /// <summary>Maps a stored <c>AnnotationData.Style</c> string to an <see cref="ArrowStyle"/> (default Straight).</summary>
    public static ArrowStyle ParseArrowStyle(string? name) =>
        Enum.TryParse(name, out ArrowStyle s) ? s : ArrowStyle.Straight;

    /// <summary>
    /// Straight arrow in one of the <see cref="ArrowStyle"/> variants:
    /// Straight (one filled head), Double (filled head at both ends), or Thin (a slimmer,
    /// tapered head). All heads scale with stroke thickness so they read well at any size.
    ///
    /// <paramref name="render"/> is the same line quality the shapes carry: Sketchy runs the
    /// shaft and every head through <see cref="StrokeStyles.SketchPath"/>, so a hand-drawn
    /// rectangle and a hand-drawn arrow look like they were drawn by the same hand. The seed
    /// lives on the annotation, so the wobble is identical after a move, an undo or a reload.
    /// </summary>
    public static Geometry ArrowGeometry(
        Point from, Point to, double thickness, ArrowStyle style,
        StrokeRender render = StrokeRender.Clean, int seed = 0)
    {
        var v = to - from;
        if (v.Length < 0.5) v = new Vector(0.5, 0);
        var dir = v;
        dir.Normalize();

        bool sketchy = render == StrokeRender.Sketchy;
        if (style == ArrowStyle.Fancy)
            return FancyArrowGeometry(from, to, dir, thickness, sketchy, seed);

        // Thin arrows use a smaller, narrower head so the whole mark looks slimmer.
        double headScale = style == ArrowStyle.Thin ? 2.6 : 3.5;
        double headWidth = style == ArrowStyle.Thin ? 0.34 : 0.45;
        double head = Math.Clamp(thickness * headScale, style == ArrowStyle.Thin ? 8 : 10, 30);
        var perp = new Vector(-dir.Y, dir.X) * head * headWidth;

        // The shaft stops short of each head so the stroked line never pokes through the
        // filled triangle. A double-headed arrow trims both ends.
        Point endBase = to - dir * head;
        Point startBase = style == ArrowStyle.Double ? from + dir * head : from;

        var geometry = new PathGeometry();

        Point[] shaftRun = { startBase, endBase };
        geometry.Figures.Add(PolyFigure(
            sketchy ? StrokeStyles.SketchPath(shaftRun, seed, closed: false) : shaftRun,
            closed: false, filled: false));

        geometry.Figures.Add(HeadFigure(to, endBase, perp, sketchy, unchecked(seed + 1)));
        if (style == ArrowStyle.Double)
            geometry.Figures.Add(HeadFigure(from, startBase, perp, sketchy, unchecked(seed + 2)));

        return geometry;
    }

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

    /// <summary>One filled triangular arrowhead: tip at <paramref name="tip"/>, base centered at <paramref name="baseCenter"/>.</summary>
    private static PathFigure HeadFigure(
        Point tip, Point baseCenter, Vector perp, bool sketchy = false, int seed = 0)
    {
        Point[] triangle = { tip, baseCenter + perp, baseCenter - perp };
        return PolyFigure(
            sketchy ? StrokeStyles.SketchPath(triangle, seed, closed: true) : triangle,
            closed: true, filled: true);
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

        bool sketchy = render == StrokeRender.Sketchy;
        var geometry = new PathGeometry();

        if (sketchy)
        {
            // A Bezier cannot be perturbed in place, so the shaft is flattened first and the
            // wobble applied to the run — the same treatment the shapes get.
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

        geometry.Figures.Add(HeadFigure(to, headBase, perp, sketchy, unchecked(seed + 1)));
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
        // Simplify first: RDP throws away the points the pointer contributed and the hand
        // did not, so the Catmull-Rom below interpolates the stroke's actual shape rather
        // than smoothing a recording of the mouse's own jitter.
        var pts = MovingAverage(StrokeStyles.Simplify(AsList(raw), FreehandSimplifyEpsilon), window: 2);
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

    /// <summary>
    /// How far a sampled point may sit off the line its neighbours describe before it is
    /// worth keeping. 1.2 px removes the pointer's own noise while leaving every
    /// deliberate change of direction — a scribble that reads as drawn, not as recorded.
    /// </summary>
    private const double FreehandSimplifyEpsilon = 1.2;

    private static IReadOnlyList<Point> AsList(IList<Point> raw) =>
        raw as IReadOnlyList<Point> ?? raw.ToList();

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
        new()
        {
            MinWidth = 48,
            // A caption that runs long used to march off the edge of the picture with no
            // way to break it. Enter still commits (that is the muscle memory); Shift+Enter
            // is the newline, and the box wraps once it reaches its own width.
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            CaretBrush = foreground,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x0A, 0x84, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
        };

    /// <summary>Transparent background keeps the whole text box clickable for the Select tool without rendering anything.</summary>
    public static TextBlock CreateTextLabel(string text, Brush foreground, double fontSize) =>
        new()
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            Background = Brushes.Transparent,
            // A one-line caption reads the same centred or left; a paragraph does not, and
            // ragged-centre body text is the tell that a label became a note.
            TextAlignment = LineCount(text) >= MultiLineThreshold
                ? TextAlignment.Left
                : TextAlignment.Center,
        };

    /// <summary>Lines above which a text annotation stops being a caption and starts being a note.</summary>
    private const int MultiLineThreshold = 3;

    private const char LineFeed = (char)10;
    private const char CarriageReturn = (char)13;

    private static int LineCount(string text)
    {
        int lines = 1;
        foreach (char c in text)
            if (c == LineFeed) lines++;
        return lines;
    }

    /// <summary>
    /// Builds the committed annotation element for a text style. Plain/Bold/Huge return
    /// a TextBlock (so Select double-click re-edit keeps working); Outline returns a
    /// glyph Path with a white stroke; Pill wraps the text in a rounded dark Border.
    /// Huge's enlarged font size is applied by the caller before committing.
    /// </summary>
    public static FrameworkElement CreateStyledTextLabel(string text, Brush foreground, double fontSize, TextStyle style)
    {
        switch (style)
        {
            case TextStyle.Bold:
                var bold = CreateTextLabel(text, foreground, fontSize);
                bold.FontWeight = FontWeights.Bold;
                return bold;

            case TextStyle.Outline:
                var typeface = new Typeface(new FontFamily("Segoe UI"),
                    FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                // FormattedText draws a carriage return as a glyph rather than a break,
                // so a multi-line outline label would otherwise come out with boxes in it.
                var formatted = new FormattedText(
                    text.Replace(CarriageReturn.ToString() + LineFeed, LineFeed.ToString())
                        .Replace(CarriageReturn, LineFeed),
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, fontSize, foreground, pixelsPerDip: 1.0);
                return new Path
                {
                    Data = formatted.BuildGeometry(new Point(0, 0)),
                    Fill = foreground,
                    Stroke = Brushes.White,
                    StrokeThickness = Math.Max(1.2, fontSize / 14),
                    StrokeLineJoin = PenLineJoin.Round,
                };

            case TextStyle.Pill:
                return CreatePillLabel(text, foreground, fontSize);

            default: // Plain; Huge is Plain at a size the caller already doubled
                return CreateTextLabel(text, foreground, fontSize);
        }
    }

    /// <summary>
    /// A pill's plate colour and the ink that goes on it. The plate is the TOOL's colour —
    /// the colour control has to mean something on a pill, and a fixed dark plate made it
    /// mean nothing — and the ink is black or white by luminance, never the raw pick. The
    /// plate is fully opaque: at 85 % the effective backdrop moved with the screenshot
    /// underneath, so the same label passed 4.5:1 over a dark region and failed over a
    /// light one. Opaque + <see cref="ColorMath.TextOn"/> is >= 4.58:1 for every colour in
    /// the picker, black and #3A3A3C included.
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
    /// Circle badge whose caption is either the number (1, 2, …) or a letter sequence
    /// (A, B, …, Z, AA) when <paramref name="letters"/> is set. Ring and caption color
    /// flip to black on light fills; the font shrinks for longer captions.
    /// </summary>
    public static Grid CreateStepBadge(int number, Color color, double thickness, bool letters)
    {
        double diameter = 22 + thickness * 3;
        bool lightFill = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 160;
        Color contrast = lightFill ? Colors.Black : Colors.White;

        string caption = letters ? StepLetterLabel(number) : number.ToString();

        var badge = new Grid { Width = diameter, Height = diameter };
        badge.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(color),
            Stroke = new SolidColorBrush(contrast) { Opacity = 0.85 },
            StrokeThickness = 2,
        });
        badge.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = new SolidColorBrush(contrast),
            FontWeight = FontWeights.Bold,
            FontSize = diameter * (caption.Length < 2 ? 0.5 : 0.4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return badge;
    }
}
