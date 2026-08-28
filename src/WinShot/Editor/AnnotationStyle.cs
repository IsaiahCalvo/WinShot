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

    public static Color FillColorFrom(AnnotationData a, Color stroke)
    {
        if (a.FillColor is string hex && TryParseHex(hex, out var parsed))
            return parsed;
        return ShapeFillBrush.CreateFromName(a.Fill, stroke) is SolidColorBrush brush
            ? brush.Color
            : Color.FromArgb(0, stroke.R, stroke.G, stroke.B);
    }
}
