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
        // The app's accent and its documented gradient partner, read from the palette:
        // two hex literals for one colour is how a palette drifts.
        ("Azure", Linear(WinShot.Core.ThemePalette.Accent, WinShot.Core.ThemePalette.AccentGradientEnd)),
        ("Deep Ocean", Linear("#0F2027", "#203A43", "#2C5364")),
        ("Purple Haze", Linear("#CC2B5E", "#753A88")),
        ("Lavender", Linear("#9796F0", "#FBC7D4")),
        ("Sunset", Linear("#FF512F", "#F09819")),
        ("Dusk", Linear("#355C7D", "#6C5B7B", "#C06C84")),
        ("Emerald", Linear("#11998E", "#38EF7D")),
        ("Aurora", Linear("#43CEA2", "#185A9D")),
        ("Graphite", Linear("#232526", "#414345")),
        ("Mesh", Mesh()),
        ("Peach", Linear("#FFD3A5", "#FD6585")),
        ("Mint", Linear("#A8FF78", "#78FFD6")),
        ("Cobalt", Linear("#141E30", "#243B55")),
        ("Blush", Linear("#F6D5F7", "#FBE9D7")),
        ("Ember", Linear("#870000", "#190A05")),
        ("Citrus", Linear("#FDC830", "#F37335")),
        ("Slate", Linear("#3E5151", "#DECBA4")),
        ("Ultraviolet", Linear("#654EA3", "#EAAFC8")),
        ("Ice", Linear("#E0EAFC", "#CFDEF3")),
        ("Nebula", Mesh("#2B1055", "#7597DE", "#FF61A6")),
    };

    /// <summary>
    /// The plain-colour swatches, light to dark then hue-ordered. Eighteen is what fits two
    /// rows of nine in the 200-pt panel without the tiles shrinking below a comfortable hit
    /// target.
    /// </summary>
    public static IReadOnlyList<string> SolidHexes { get; } = new[]
    {
        "#FFFFFF", "#F2F2F7", "#D1D1D6", "#8E8E93", "#3A3A3C", "#1E1E1E",
        "#000000", "#0A84FF", "#5AC8FA", "#34C759", "#30D158", "#FFD60A",
        "#FF9F0A", "#FF453A", "#FF375F", "#AF52DE", "#5E5CE6", "#64D2FF",
    };

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color C(System.Drawing.Color color) => Color.FromRgb(color.R, color.G, color.B);

    /// <summary>Diagonal gradient from palette colours, so the accent has one definition.</summary>
    private static Brush Linear(params System.Drawing.Color[] stops)
        => Linear(stops.Select(C).ToArray());

    private static Brush Linear(params Color[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (int i = 0; i < stops.Length; i++)
        {
            double offset = stops.Length == 1 ? 0 : i / (double)(stops.Length - 1);
            brush.GradientStops.Add(new GradientStop(stops[i], offset));
        }
        brush.Freeze();
        return brush;
    }

    /// <summary>Diagonal gradient with evenly spaced stops.</summary>
    private static Brush Linear(params string[] hexStops)
        => Linear(hexStops.Select(C).ToArray());

    /// <summary>Mesh-ish multi-stop: a flat base with two soft radial glows over it.</summary>
    private static Brush Mesh(string baseHex = "#4158D0", string glowA = "#C850C0", string glowB = "#FFCC70")
    {
        var bounds = new Rect(0, 0, 100, 100);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(C(baseHex)), null, new RectangleGeometry(bounds)));
        group.Children.Add(new GeometryDrawing(Glow(glowA, 0.85, 0.10, 1.0), null, new RectangleGeometry(bounds)));
        group.Children.Add(new GeometryDrawing(Glow(glowB, 0.10, 0.95, 0.9), null, new RectangleGeometry(bounds)));
        var brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The checkerboard that stands for "no backdrop — export the padding transparent".
    /// Drawn, not an image, so it scales with the tile and the canvas alike.
    /// </summary>
    public static Brush Checkerboard { get; } = BuildCheckerboard();

    private static Brush BuildCheckerboard()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(C("#FFFFFF")), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var dark = new SolidColorBrush(C("#D9D9DE"));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
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
