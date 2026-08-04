using System.Drawing.Drawing2D;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Pin;

/// <summary>Pixel geometry shared by the pinned window's clip and painted edge.</summary>
public static class PinWindowGeometry
{
    public const int CornerRadiusLogical = 10;

    public static int CornerRadius(int dpi) =>
        PinInteraction.ScaleLogical(CornerRadiusLogical, dpi);

    public static GraphicsPath RoundedClipPath(SD.Size size, int dpi) =>
        GdiPaths.RoundedRect(
            new SD.Rectangle(SD.Point.Empty, size),
            CornerRadius(dpi));

    public static GraphicsPath RoundedBorderPath(SD.Size size, int dpi)
    {
        int strokeWidth = PinInteraction.ScaleLogical(1, dpi);
        int requestedInset = Math.Max(1, (strokeWidth + 1) / 2);
        int inset = Math.Min(
            requestedInset,
            Math.Max(0, (Math.Min(size.Width, size.Height) - 1) / 2));
        var bounds = new SD.Rectangle(
            inset,
            inset,
            Math.Max(0, size.Width - 1 - inset * 2),
            Math.Max(0, size.Height - 1 - inset * 2));
        return GdiPaths.RoundedRect(bounds, Math.Max(1, CornerRadius(dpi) - inset));
    }

    public static void DrawBorder(
        SD.Graphics graphics,
        SD.Pen pen,
        SD.Size size,
        int dpi,
        bool roundedCorners)
    {
        if (size.Width <= 0 || size.Height <= 0)
            return;

        if (!roundedCorners)
        {
            graphics.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
            return;
        }

        SmoothingMode previous = graphics.SmoothingMode;
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedBorderPath(size, dpi);
            graphics.DrawPath(pen, path);
        }
        finally
        {
            graphics.SmoothingMode = previous;
        }
    }
}
