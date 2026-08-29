using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

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
}

/// <summary>
/// Intensity preset for the blur / pixelate tools. Maps to a blur radius or a
/// pixelate block size so the user can dial the effect instead of a single hardcoded value.
/// </summary>
internal enum EffectStrength
{
    Light,
    Medium,
    Strong,
}

/// <summary>Builds the WPF elements used as canvas annotations.</summary>
internal static class AnnotationFactory
{
    /// <summary>Box-blur radius (px) for an <see cref="EffectStrength"/>. Larger = smoother/heavier blur.</summary>
    public static int BlurRadiusFor(EffectStrength strength) => strength switch
    {
        EffectStrength.Light => 3,
        EffectStrength.Strong => 12,
        _ => 6, // Medium — matches the previous hardcoded default
    };

    /// <summary>Pixelate block size (px) for an <see cref="EffectStrength"/>. Larger = coarser mosaic.</summary>
    public static int PixelateCellFor(EffectStrength strength) => strength switch
    {
        EffectStrength.Light => 7,
        EffectStrength.Strong => 22,
        _ => 12, // Medium — matches the previous hardcoded default
    };

    /// <summary>Maps a stored <c>AnnotationData.Style</c> string to an <see cref="ArrowStyle"/> (default Straight).</summary>
    public static ArrowStyle ParseArrowStyle(string? name) =>
        Enum.TryParse(name, out ArrowStyle s) ? s : ArrowStyle.Straight;

    /// <summary>
    /// Legacy CleanShot-style arrows (Straight / Double / Thin) mapped onto Survey's
    /// six-head vocabulary so old .winshot files keep their look.
    /// </summary>
    public static Geometry ArrowGeometry(Point from, Point to, double thickness, ArrowStyle style)
    {
        var end = AnnotationStyle.ParseArrowhead(style.ToString(), out var start);
        return ArrowGeometry(from, to, thickness, end, start, thin: style == ArrowStyle.Thin);
    }

    /// <summary>
    /// Survey arrow: a stroked shaft plus optional start/end heads. Head size is
    /// <c>max(8, thickness * 3)</c> (Survey Phase 15). Solid triangles are filled;
    /// the other five heads are stroked.
    /// </summary>
    public static Geometry ArrowGeometry(
        Point from, Point to, double thickness, ArrowheadStyle end,
        ArrowheadStyle start = ArrowheadStyle.None, bool thin = false, Point? mid = null)
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

        var geometry = LineCurve.Stroke(startBase, endBase, curved ? mid : null);
        AppendHead(geometry, to, endAngle, headSize, thickness, end);
        if (start != ArrowheadStyle.None)
            AppendHead(geometry, from, startAngle + Math.PI, headSize, thickness, start);
        return geometry;
    }

    private static double ShaftTrim(ArrowheadStyle style, double headSize) => style switch
    {
        ArrowheadStyle.SolidTriangle or ArrowheadStyle.OpenTriangle => headSize / 3,
        ArrowheadStyle.OpenCircle => headSize / 2,
        ArrowheadStyle.VShape => 0,
        _ => 0,
    };

    private static void AppendHead(
        PathGeometry geometry, Point tip, double angle, double headSize, double thickness, ArrowheadStyle style)
    {
        switch (style)
        {
            case ArrowheadStyle.None:
                return;
            case ArrowheadStyle.SolidTriangle:
            case ArrowheadStyle.OpenTriangle:
            {
                bool filled = style == ArrowheadStyle.SolidTriangle;
                double back = headSize / 3;
                double half = headSize / 2;
                Point left = tip + Polar(angle + Math.PI, back) + Polar(angle + Math.PI / 2, half);
                Point right = tip + Polar(angle + Math.PI, back) + Polar(angle - Math.PI / 2, half);
                Point nose = tip;
                var fig = new PathFigure { StartPoint = left, IsClosed = true, IsFilled = filled };
                fig.Segments.Add(new LineSegment(nose, isStroked: !filled));
                fig.Segments.Add(new LineSegment(right, isStroked: !filled));
                geometry.Figures.Add(fig);
                return;
            }
            case ArrowheadStyle.VShape:
            {
                const double spread = Math.PI / 6;
                Point arm1 = tip - new Vector(Math.Cos(angle - spread), Math.Sin(angle - spread)) * headSize;
                Point arm2 = tip - new Vector(Math.Cos(angle + spread), Math.Sin(angle + spread)) * headSize;
                var fig = new PathFigure { StartPoint = arm1, IsClosed = false, IsFilled = false };
                fig.Segments.Add(new LineSegment(tip, isStroked: true));
                fig.Segments.Add(new LineSegment(arm2, isStroked: true));
                geometry.Figures.Add(fig);
                return;
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
                geometry.Figures.Add(fig);
                return;
            }
            case ArrowheadStyle.HorizontalLine:
            {
                double half = headSize / 2;
                var perp = new Vector(-Math.Sin(angle), Math.Cos(angle)) * half;
                var fig = new PathFigure { StartPoint = tip + perp, IsClosed = false, IsFilled = false };
                fig.Segments.Add(new LineSegment(tip - perp, isStroked: true));
                geometry.Figures.Add(fig);
                return;
            }
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
    public static Geometry CurvedArrowGeometry(Point from, Point control, Point to, double thickness)
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

        var shaft = new PathFigure { StartPoint = from, IsFilled = false };
        shaft.Segments.Add(new QuadraticBezierSegment(control, headBase, isStroked: true));
        geometry.Figures.Add(shaft);

        var headFigure = new PathFigure { StartPoint = to, IsClosed = true, IsFilled = true };
        headFigure.Segments.Add(new LineSegment(headBase + perp, isStroked: true));
        headFigure.Segments.Add(new LineSegment(headBase - perp, isStroked: true));
        geometry.Figures.Add(headFigure);

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
    private static string StepLetterLabel(int number)
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
        CreateTextLabel(text, foreground, fontSize, "Segoe UI", bold: false, italic: false,
            underline: false, strike: false, TextAlignment.Left);

    public static TextBlock CreateTextLabel(
        string text, Brush foreground, double fontSize, string fontFamily,
        bool bold, bool italic, bool underline, bool strike, TextAlignment align)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontFamily = new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily),
            FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = foreground,
            Background = Brushes.Transparent,
            TextAlignment = align,
            TextWrapping = TextWrapping.Wrap,
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
        CreateStyledTextLabel(text, foreground, fontSize, style, "Segoe UI",
            bold: style == TextStyle.Bold, italic: false, underline: false, strike: false, TextAlignment.Left);

    public static FrameworkElement CreateStyledTextLabel(
        string text, Brush foreground, double fontSize, TextStyle style,
        string fontFamily, bool bold, bool italic, bool underline, bool strike, TextAlignment align)
    {
        bool useBold = bold || style == TextStyle.Bold;
        switch (style)
        {
            case TextStyle.Outline:
                var typeface = new Typeface(new FontFamily(fontFamily),
                    italic ? FontStyles.Italic : FontStyles.Normal,
                    useBold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal);
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

            case TextStyle.Pill:
                return new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x1E, 0x1E, 0x1E)),
                    CornerRadius = new CornerRadius(fontSize * 0.55),
                    Padding = new Thickness(fontSize * 0.5, fontSize * 0.2, fontSize * 0.5, fontSize * 0.2),
                    Child = CreateTextLabel(text, foreground, fontSize, fontFamily, useBold, italic, underline, strike, align),
                };

            default: // Plain / Bold / Huge
                return CreateTextLabel(text, foreground, fontSize, fontFamily, useBold, italic, underline, strike, align);
        }
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

    public static Path CreateInk(IList<Point> points, Color color, double width, bool highlighter)
    {
        var path = new Path
        {
            Data = PaperInk.Outline(points, width),
            Fill = new SolidColorBrush(color),
            Stroke = Brushes.Transparent,
            StrokeThickness = 0,
        };
        if (highlighter)
            path.Opacity = 1; // alpha is in the fill; multiply is approximate via that alpha
        return path;
    }

    public static void ApplyDash(Shape shape, LineBorderStyle style)
    {
        shape.StrokeDashArray = AnnotationStyle.DashArray(style);
        shape.StrokeDashCap = PenLineCap.Round;
    }

    public static CalloutAnnotation CreateCallout(
        CalloutLayout layout, string text, Color stroke, Color fill, double thickness,
        ArrowheadStyle head, LineBorderStyle lineStyle, double fontSize)
    {
        var visual = new CalloutAnnotation();
        visual.Apply(layout, text, stroke, fill, thickness, head, lineStyle, fontSize);
        return visual;
    }
}

