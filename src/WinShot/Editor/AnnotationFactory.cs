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
