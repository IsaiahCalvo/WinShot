using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// The arithmetic behind the editor's colour picker: HSV both ways, and the mapping
/// between a point on the saturation/value square and the colour it stands for. Pure, so
/// the picker's behaviour can be checked without opening a window.
///
/// Hue is 0-360 (360 folds back to 0), saturation and value are 0-1. Alpha rides along
/// unchanged — it is a separate slider, not part of the square.
/// </summary>
internal static class ColorPickerMath
{
    public static (double Hue, double Saturation, double Value) ToHsv(Color c)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue;
        if (delta < 1e-9) hue = 0;
        else if (max == r) hue = 60 * (((g - b) / delta) % 6);
        else if (max == g) hue = 60 * (((b - r) / delta) + 2);
        else hue = 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;

        double saturation = max < 1e-9 ? 0 : delta / max;
        return (hue, saturation, max);
    }

    public static Color FromHsv(double hue, double saturation, double value, byte alpha = 255)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        double c = value * saturation;
        double x = c * (1 - Math.Abs(((hue / 60) % 2) - 1));
        double m = value - c;

        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromArgb(
            alpha,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Where the thumb sits on the SV square for a colour: saturation across, value up.</summary>
    public static Point PositionFor(double saturation, double value, Size canvas) => new(
        Math.Clamp(saturation, 0, 1) * Math.Max(0, canvas.Width),
        (1 - Math.Clamp(value, 0, 1)) * Math.Max(0, canvas.Height));

    /// <summary>The inverse: a click anywhere on (or off) the square, clamped into it.</summary>
    public static (double Saturation, double Value) FromPosition(Point p, Size canvas)
    {
        double saturation = canvas.Width <= 0 ? 0 : Math.Clamp(p.X / canvas.Width, 0, 1);
        double value = canvas.Height <= 0 ? 1 : 1 - Math.Clamp(p.Y / canvas.Height, 0, 1);
        return (saturation, value);
    }

    /// <summary>"#RRGGBB", or "#AARRGGBB" once the colour is no longer opaque.</summary>
    public static string ToHex(Color c) => c.A == 255
        ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
        : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
