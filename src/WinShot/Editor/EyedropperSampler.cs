using System.Windows;
using System.Windows.Media;
using SD = System.Drawing;

namespace WinShot.Editor;

public readonly record struct EyedropperPreview(
    bool Visible,
    Color Color,
    string Hex,
    double Scale,
    double Left,
    double Top);

public static class EyedropperSampler
{
    private const double Offset = 16;
    private const double PreviewWidth = 96;
    private const double PreviewHeight = 28;

    public static Color SampleClamped(SD.Bitmap source, Point point)
    {
        int x = Math.Clamp((int)Math.Floor(point.X), 0, source.Width - 1);
        int y = Math.Clamp((int)Math.Floor(point.Y), 0, source.Height - 1);
        SD.Color pixel = source.GetPixel(x, y);
        return Color.FromRgb(pixel.R, pixel.G, pixel.B);
    }

    /// <summary>The pixel at a bitmap offset, clamped inside it. GDI-native, for the capture
    /// selector, which works in System.Drawing colours rather than WPF ones.</summary>
    public static SD.Color SampleAt(SD.Bitmap source, SD.Point point)
    {
        ArgumentNullException.ThrowIfNull(source);
        int x = Math.Clamp(point.X, 0, source.Width - 1);
        int y = Math.Clamp(point.Y, 0, source.Height - 1);
        return source.GetPixel(x, y);
    }

    /// <summary>
    /// The mean colour of a region — what a designer wants off a gradient, a photo or an
    /// antialiased logo, where any single pixel is the wrong answer. Averaged in linear steps
    /// over the raw channel values, which is what a colour picker reports.
    /// </summary>
    public static SD.Color Average(SD.Bitmap source, SD.Rectangle region)
    {
        ArgumentNullException.ThrowIfNull(source);
        SD.Rectangle clipped = SD.Rectangle.Intersect(region, new SD.Rectangle(0, 0, source.Width, source.Height));
        if (clipped.Width < 1 || clipped.Height < 1)
            return SampleAt(source, new SD.Point(region.X, region.Y));

        // Sample on a grid rather than every pixel: a full-screen region is millions of
        // GetPixel calls, and the mean of a 64x64 grid is the same colour to the eye.
        const int MaxSamples = 64;
        int stepX = Math.Max(1, clipped.Width / MaxSamples);
        int stepY = Math.Max(1, clipped.Height / MaxSamples);
        long r = 0, g = 0, b = 0, count = 0;
        for (int y = clipped.Top; y < clipped.Bottom; y += stepY)
        {
            for (int x = clipped.Left; x < clipped.Right; x += stepX)
            {
                SD.Color pixel = source.GetPixel(x, y);
                r += pixel.R;
                g += pixel.G;
                b += pixel.B;
                count++;
            }
        }

        if (count == 0)
            return SampleAt(source, new SD.Point(clipped.X, clipped.Y));
        return SD.Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count));
    }

    /// <summary>
    /// The darkest pixel in a box around a point — the text colour of a line of type, without
    /// having to land the pointer on a glyph stroke. Darkness is measured by luminance, not by
    /// channel sum, so a saturated blue does not beat a mid grey.
    /// </summary>
    public static SD.Color Darkest(SD.Bitmap source, SD.Point centre, int box)
    {
        ArgumentNullException.ThrowIfNull(source);
        int half = Math.Max(1, box) / 2;
        SD.Rectangle region = SD.Rectangle.Intersect(
            new SD.Rectangle(centre.X - half, centre.Y - half, Math.Max(1, box), Math.Max(1, box)),
            new SD.Rectangle(0, 0, source.Width, source.Height));
        if (region.Width < 1 || region.Height < 1)
            return SampleAt(source, centre);

        SD.Color darkest = source.GetPixel(region.Left, region.Top);
        double best = Luminance(darkest);
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                SD.Color pixel = source.GetPixel(x, y);
                double luminance = Luminance(pixel);
                if (luminance < best)
                {
                    best = luminance;
                    darkest = pixel;
                }
            }
        }
        return darkest;
    }

    private static double Luminance(SD.Color color) =>
        (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);

    public static EyedropperPreview Preview(SD.Bitmap source, Point point, double zoom)
    {
        int x = (int)Math.Floor(point.X);
        int y = (int)Math.Floor(point.Y);
        double scale = PreviewScale(zoom);

        if (x < 0 || y < 0 || x >= source.Width || y >= source.Height)
            return new EyedropperPreview(false, default, string.Empty, scale, 0, 0);

        Color color = SampleClamped(source, point);
        double width = PreviewWidth * scale;
        double height = PreviewHeight * scale;
        double offset = Offset * scale;
        double left = point.X + offset;
        double top = point.Y + offset;

        if (left + width > source.Width)
        {
            double flippedLeft = point.X - offset - width;
            left = flippedLeft < 0 ? source.Width - width : flippedLeft;
        }
        if (top + height > source.Height)
        {
            double flippedTop = point.Y - offset - height;
            top = flippedTop < 0 ? source.Height - height : flippedTop;
        }

        left = Math.Clamp(left, 0, Math.Max(0, source.Width - width));
        top = Math.Clamp(top, 0, Math.Max(0, source.Height - height));

        return new EyedropperPreview(true, color, ToHex(color), scale, left, top);
    }

    private static double PreviewScale(double zoom) =>
        double.IsFinite(zoom) && zoom > 0 ? 1 / zoom : 1;

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
