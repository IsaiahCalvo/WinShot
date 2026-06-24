using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinShot.Editor;

/// <summary>Builds the WPF elements used as canvas annotations.</summary>
internal static class AnnotationFactory
{
    /// <summary>Shaft plus a filled triangular head; the head scales with stroke thickness.</summary>
    public static Geometry ArrowGeometry(Point from, Point to, double thickness)
    {
        var v = to - from;
        if (v.Length < 0.5) v = new Vector(0.5, 0);
        var dir = v;
        dir.Normalize();

        double head = Math.Clamp(thickness * 3.5, 10, 30);
        Point headBase = to - dir * head;
        var perp = new Vector(-dir.Y, dir.X) * head * 0.45;

        var geometry = new PathGeometry();

        var shaft = new PathFigure { StartPoint = from, IsFilled = false };
        shaft.Segments.Add(new LineSegment(headBase, isStroked: true));
        geometry.Figures.Add(shaft);

        var headFigure = new PathFigure { StartPoint = to, IsClosed = true, IsFilled = true };
        headFigure.Segments.Add(new LineSegment(headBase + perp, isStroked: true));
        headFigure.Segments.Add(new LineSegment(headBase - perp, isStroked: true));
        geometry.Figures.Add(headFigure);

        return geometry;
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

    /// <summary>Numbered circle badge; ring and digit color flip to black on light fills.</summary>
    public static Grid CreateStepBadge(int number, Color color, double thickness)
    {
        double diameter = 22 + thickness * 3;
        bool lightFill = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 160;
        Color contrast = lightFill ? Colors.Black : Colors.White;

        var badge = new Grid { Width = diameter, Height = diameter };
        badge.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(color),
            Stroke = new SolidColorBrush(contrast) { Opacity = 0.85 },
            StrokeThickness = 2,
        });
        badge.Children.Add(new TextBlock
        {
            Text = number.ToString(),
            Foreground = new SolidColorBrush(contrast),
            FontWeight = FontWeights.Bold,
            FontSize = diameter * (number < 10 ? 0.5 : 0.4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return badge;
    }
}
