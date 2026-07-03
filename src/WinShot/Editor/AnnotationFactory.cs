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
    /// Straight arrow in one of the <see cref="ArrowStyle"/> variants:
    /// Straight (one filled head), Double (filled head at both ends), or Thin (a slimmer,
    /// tapered head). All heads scale with stroke thickness so they read well at any size.
    /// </summary>
    public static Geometry ArrowGeometry(Point from, Point to, double thickness, ArrowStyle style)
    {
        var v = to - from;
        if (v.Length < 0.5) v = new Vector(0.5, 0);
        var dir = v;
        dir.Normalize();

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

        var shaft = new PathFigure { StartPoint = startBase, IsFilled = false };
        shaft.Segments.Add(new LineSegment(endBase, isStroked: true));
        geometry.Figures.Add(shaft);

        geometry.Figures.Add(HeadFigure(to, endBase, perp));
        if (style == ArrowStyle.Double)
            geometry.Figures.Add(HeadFigure(from, startBase, perp));

        return geometry;
    }

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

    public static double FontSizeFor(double thickness) => 10 + thickness * 4;

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
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x4D, 0xA3, 0xFF)),
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
        };

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
                    Child = CreateTextLabel(text, foreground, fontSize),
                };

            default: // Plain; Huge is Plain at a size the caller already doubled
                return CreateTextLabel(text, foreground, fontSize);
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
}
