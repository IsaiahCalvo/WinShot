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
