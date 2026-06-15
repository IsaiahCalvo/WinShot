using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor.Background;

/// <summary>
/// Built-in backdrop brushes for <see cref="BackgroundComposerWindow"/>.
/// Every brush is frozen so the same instance can back both the picker
/// swatch and the compose surface.
/// </summary>
public static class BackgroundPresets
{
    public static IReadOnlyList<(string Name, Brush Brush)> All { get; } = new List<(string, Brush)>
    {
        ("Azure", Linear("#2D7DFF", "#6DD5ED")),
        ("Deep Ocean", Linear("#0F2027", "#203A43", "#2C5364")),
        ("Purple Haze", Linear("#CC2B5E", "#753A88")),
        ("Lavender", Linear("#9796F0", "#FBC7D4")),
        ("Sunset", Linear("#FF512F", "#F09819")),
        ("Dusk", Linear("#355C7D", "#6C5B7B", "#C06C84")),
        ("Emerald", Linear("#11998E", "#38EF7D")),
        ("Aurora", Linear("#43CEA2", "#185A9D")),
        ("Graphite", Linear("#232526", "#414345")),
        ("Mesh", Mesh()),
    };

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Diagonal gradient with evenly spaced stops.</summary>
    private static Brush Linear(params string[] hexStops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (int i = 0; i < hexStops.Length; i++)
        {
            double offset = hexStops.Length == 1 ? 0 : i / (double)(hexStops.Length - 1);
            brush.GradientStops.Add(new GradientStop(C(hexStops[i]), offset));
        }
        brush.Freeze();
        return brush;
    }

    /// <summary>Mesh-ish multi-stop: blue-violet base with soft pink and amber radial glows.</summary>
    private static Brush Mesh()
    {
        var bounds = new Rect(0, 0, 100, 100);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(C("#4158D0")), null, new RectangleGeometry(bounds)));
        group.Children.Add(new GeometryDrawing(Glow("#C850C0", 0.85, 0.10, 1.0), null, new RectangleGeometry(bounds)));
        group.Children.Add(new GeometryDrawing(Glow("#FFCC70", 0.10, 0.95, 0.9), null, new RectangleGeometry(bounds)));
        var brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Radial glow fading to the same color at alpha 0 (not Colors.Transparent,
    /// which would interpolate through white and leave a pale fringe).
    /// </summary>
    private static RadialGradientBrush Glow(string hex, double cx, double cy, double radius)
    {
        var color = C(hex);
        var brush = new RadialGradientBrush
        {
            Center = new Point(cx, cy),
            GradientOrigin = new Point(cx, cy),
            RadiusX = radius,
            RadiusY = radius,
        };
        brush.GradientStops.Add(new GradientStop(color, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        return brush;
    }
}
