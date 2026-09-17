using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Survey-compatible line/border dash vocabulary. Shared by line, arrow, rect,
/// ellipse, text box, and callout. Cloud is a rectangle-only border treatment.
/// </summary>
public enum LineBorderStyle
{
    Solid,
    Dashed,
    Dotted,
    Cloud,
}

/// <summary>
/// Survey's six arrowhead looks. Used by the arrow tool and callout leaders.
/// Legacy WinShot Straight/Thin map to <see cref="SolidTriangle"/>; Double maps
/// to a solid triangle on both ends.
/// </summary>
public enum ArrowheadStyle
{
    None,
    SolidTriangle,
    VShape,
    OpenCircle,
    OpenTriangle,
    HorizontalLine,
    // PDF /LE Diamond, Square and Slash. Survey has offered all nine since the line-endings
    // work; WinShot stopped at six because it was ported when Survey only had six.
    Diamond,
    Slash,
    Square,
}

/// <summary>Which color well the compact picker is editing.</summary>
public enum ColorWell
{
    Stroke,
    Fill,
}

/// <summary>Pure helpers for Survey-style stroke/fill, dashes, and arrowheads.</summary>
internal static class AnnotationStyle
{
    public static readonly double[] DashedPattern = { 6, 4 };
    public static readonly double[] DottedPattern = { 2, 4 };
    public static readonly int[] SizePresets = { 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 32, 50 };
    public static readonly int[] EraserSizePresets = { 1, 4, 8, 12, 16, 24, 32, 48, 64, 80, 100 };

    /// <summary>
    /// ANNOTATION_SIZE_PRESETS.counter verbatim (AnnotationSizeControl.jsx:12). The Size
    /// box drives the badge radius directly, over Survey's own COUNTER_SIZE_MIN 4 ..
    /// COUNTER_SIZE_MAX 76 range (utils/annotationSize.js:2-3).
    /// </summary>
    public static readonly int[] CounterSizePresets = { 5, 8, 12, 16, 24, 32, 48, 64 };
    public const int DefaultCounterSize = 14;

    public static int ClampCounterSize(double size) => (int)Math.Clamp(Math.Round(size), 4, 76);
    /// <summary>Survey's FONT_SIZE_PRESETS (AppShell.jsx:1859), verbatim.</summary>
    public static readonly int[] TextSizePresets =
        { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72 };

    /// <summary>
    /// Survey's family list (AppShell.jsx:1833), with the system UI face at the head of it.
    /// Survey has no system font because a PDF has to name a real family; a screenshot
    /// annotation does not, and Segoe UI is the face every other label in this app uses.
    /// </summary>
    public static readonly string[] FontFamilies =
    {
        "Segoe UI", "Arial", "Helvetica", "Times New Roman", "Courier New", "Georgia", "Verdana",
    };

    public const int DefaultPenSize = 3;
    public const int DefaultShapeSize = 2;
    public const int DefaultTextSize = 16;
    public const int HighlighterMin = 8;

    /// <summary>
    /// Survey's highlighter paint is a hard-coded constant, not the colour picker:
    /// highlightColor="rgba(255, 193, 7, 0.3)" (PDFViewer.jsx:31380), selected by
    /// `tool === 'highlighter' ? highlightColor : strokeColor` at commit
    /// (annotationCreationCommit.js:292) and in the live preview.
    /// </summary>
    /// <summary>
    /// The ring every selection affordance is drawn in — handles, the rotate grip, the
    /// hover halo, a callout's glow. Survey strokes these in its own #4a90e2; WinShot has
    /// one selection blue already, the colour Theme.xaml calls InfoColor and ThemePalette
    /// calls AccentRing, and the editor uses that so a handle matches every other focus
    /// ring in the app.
    /// </summary>
    public static readonly Color SelectionRingColor = FromDrawing(WinShot.Core.ThemePalette.AccentRing);

    /// <summary>The text caret, in the one accent. Survey's is its own #007AFF.</summary>
    public static readonly Color CaretColor = FromDrawing(WinShot.Core.ThemePalette.Accent);

    private static Color FromDrawing(System.Drawing.Color c) => Color.FromArgb(0xFF, c.R, c.G, c.B);

    public static readonly Color HighlighterColor = Color.FromArgb(0x4D, 0xFF, 0xC1, 0x07);

    /// <summary>
    /// Survey has no highlighter width of its own: it reuses the shared strokeWidth
    /// and draws at Math.max(strokeWidth, 8) (annotationCreationCommit.js:293).
    /// </summary>
    public static int HighlighterWidth(double size) =>
        Math.Max(HighlighterMin, ClampSize(size));

    public static readonly Color[] PickerPresets =
    {
        Colors.Transparent,
        Color.FromRgb(0xFF, 0x00, 0x00),
        Color.FromRgb(0xFF, 0x00, 0x80),
        Color.FromRgb(0xFF, 0x00, 0xFF),
        Color.FromRgb(0x80, 0x00, 0xFF),
        Color.FromRgb(0x00, 0x00, 0xFF),
        Color.FromRgb(0x00, 0x80, 0xFF),
        Color.FromRgb(0x00, 0xFF, 0xFF),
        Color.FromRgb(0x00, 0xFF, 0x80),
        Color.FromRgb(0x00, 0xFF, 0x00),
        Color.FromRgb(0x80, 0xFF, 0x00),
        Color.FromRgb(0xFF, 0xFF, 0x00),
        Color.FromRgb(0xFF, 0x80, 0x00),
        Colors.White,
        Color.FromRgb(0x80, 0x80, 0x80),
        Colors.Black,
    };

    public static DoubleCollection? DashArray(LineBorderStyle style) => style switch
    {
        LineBorderStyle.Dashed => new DoubleCollection(DashedPattern),
        LineBorderStyle.Dotted => new DoubleCollection(DottedPattern),
        _ => null,
    };

    public static LineBorderStyle ParseLineStyle(string? name)
    {
        if (string.Equals(name, "dashed", StringComparison.OrdinalIgnoreCase))
            return LineBorderStyle.Dashed;
        if (string.Equals(name, "dotted", StringComparison.OrdinalIgnoreCase))
            return LineBorderStyle.Dotted;
        if (string.Equals(name, "cloud", StringComparison.OrdinalIgnoreCase))
            return LineBorderStyle.Cloud;
        if (Enum.TryParse(name, ignoreCase: true, out LineBorderStyle parsed))
            return parsed;
        return LineBorderStyle.Solid;
    }

    public static ArrowheadStyle ParseArrowhead(string? name, out ArrowheadStyle startHead)
    {
        startHead = ArrowheadStyle.None;
        if (string.IsNullOrWhiteSpace(name))
            return ArrowheadStyle.SolidTriangle;

        if (Enum.TryParse(name, ignoreCase: true, out ArrowStyle legacy))
        {
            if (legacy == ArrowStyle.Double)
            {
                startHead = ArrowheadStyle.SolidTriangle;
                return ArrowheadStyle.SolidTriangle;
            }
            return ArrowheadStyle.SolidTriangle;
        }

        return name.Replace("-", "", StringComparison.Ordinal) switch
        {
            "none" => ArrowheadStyle.None,
            "solidTriangle" or "solidtriangle" => ArrowheadStyle.SolidTriangle,
            "vShape" or "vshape" => ArrowheadStyle.VShape,
            "openCircle" or "opencircle" => ArrowheadStyle.OpenCircle,
            "openTriangle" or "opentriangle" => ArrowheadStyle.OpenTriangle,
            "horizontalLine" or "horizontalline" => ArrowheadStyle.HorizontalLine,
            "diamond" => ArrowheadStyle.Diamond,
            "slash" => ArrowheadStyle.Slash,
            "square" => ArrowheadStyle.Square,
            _ => Enum.TryParse(name, ignoreCase: true, out ArrowheadStyle parsed)
                ? parsed
                : ArrowheadStyle.SolidTriangle,
        };
    }

    public static string ToStorageName(ArrowheadStyle style) => style switch
    {
        ArrowheadStyle.None => "none",
        ArrowheadStyle.VShape => "vShape",
        ArrowheadStyle.OpenCircle => "openCircle",
        ArrowheadStyle.OpenTriangle => "openTriangle",
        ArrowheadStyle.HorizontalLine => "horizontalLine",
        ArrowheadStyle.Diamond => "diamond",
        ArrowheadStyle.Slash => "slash",
        ArrowheadStyle.Square => "square",
        _ => "solidTriangle",
    };

    public static string ToStorageName(LineBorderStyle style) => style switch
    {
        LineBorderStyle.Dashed => "dashed",
        LineBorderStyle.Dotted => "dotted",
        LineBorderStyle.Cloud => "cloud",
        _ => "solid",
    };

    /// <summary>
    /// Survey's one-visible rule: a shape cannot have both fill and stroke fully
    /// transparent. If both would vanish, keep the stroke.
    /// </summary>
    public static (Color stroke, Color fill) EnforceOneVisible(Color stroke, Color fill)
    {
        if (stroke.A == 0 && fill.A == 0)
            return (Color.FromArgb(0xFF, stroke.R, stroke.G, stroke.B), fill);
        return (stroke, fill);
    }

    public static Color Compose(Color rgb, double opacity)
    {
        byte a = (byte)Math.Clamp(Math.Round(Math.Clamp(opacity, 0, 1) * 255), 0, 255);
        return Color.FromArgb(a, rgb.R, rgb.G, rgb.B);
    }

    public static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    public static double OpacityOf(Color color) => color.A / 255.0;

    public static Color Opaque(Color color) => Color.FromRgb(color.R, color.G, color.B);

    public static bool IsTransparent(Color color) => color.A == 0;

    public static int ClampSize(double size) =>
        (int)Math.Clamp(Math.Round(size), 1, 50);

    /// <summary>Survey clamps font size to 6..200 (TextEditOverlay.jsx:297, textEditCommit.js:93).</summary>
    public static int ClampTextSize(double size) =>
        (int)Math.Clamp(Math.Round(size), 6, 200);

    public static int ClampEraserSize(double size) =>
        (int)Math.Clamp(Math.Round(size), 1, 100);

    public static (double h, double s, double v) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double f(double n)
        {
            double k = (n + h / 60) % 6;
            return v - v * s * Math.Max(Math.Min(k, Math.Min(4 - k, 1)), 0);
        }
        return Color.FromRgb(
            (byte)Math.Clamp(Math.Round(f(5) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(f(3) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(f(1) * 255), 0, 255));
    }

    public static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        try
        {
            if (ColorConverter.ConvertFromString(s) is Color c)
            {
                color = c;
                return true;
            }
        }
        catch
        {
            // invalid hex
        }
        return false;
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    public static string ToRgbHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static (ArrowheadStyle end, ArrowheadStyle start) HeadsFrom(AnnotationData a)
    {
        if (!string.IsNullOrWhiteSpace(a.Head))
        {
            var end = ParseArrowhead(a.Head, out _);
            var start = string.IsNullOrWhiteSpace(a.StartHead)
                ? ArrowheadStyle.None
                : ParseArrowhead(a.StartHead, out _);
            return (end, start);
        }
        var parsed = ParseArrowhead(a.Style, out var startLegacy);
        return (parsed, startLegacy);
    }

    public static LineBorderStyle LineStyleFrom(AnnotationData a) => ParseLineStyle(a.LineStyle);

    /// <summary>
    /// A textbox's GLYPH colour. Survey keeps three independent colour fields on a Textbox —
    /// `stroke` (border rect), `backgroundColor` (box fill) and `fill` (the letters) — and
    /// only the rich-text font-colour swatch writes `fill`
    /// (svgAnnotationRenderers.jsx:1276, TextEditOverlay.jsx:302). WinShot stores `fill` as
    /// FontColor; a row that was never recoloured falls back to the border colour.
    /// </summary>
    public static Color TextGlyphColor(AnnotationData a, Color stroke) =>
        a.FontColor is string hex && TryParseHex(hex, out var glyph) ? glyph : stroke;

    public static Color FillColorFrom(AnnotationData a, Color stroke)
    {
        if (a.FillColor is string hex && TryParseHex(hex, out var parsed))
            return parsed;
        return ShapeFillBrush.CreateFromName(a.Fill, stroke) is SolidColorBrush brush
            ? brush.Color
            : Color.FromArgb(0, stroke.R, stroke.G, stroke.B);
    }
}
