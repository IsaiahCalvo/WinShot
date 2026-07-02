using SD = System.Drawing;

namespace WinShot.Capture;

public static class AllInOneDragLayout
{
    public static SD.Rectangle CreatePixelRectangle(SD.Point start, SD.Point current, double? aspectRatio)
    {
        var adjusted = AdjustCurrent(start.X, start.Y, current.X, current.Y, aspectRatio);
        int currentX = (int)Math.Round(adjusted.X);
        int currentY = (int)Math.Round(adjusted.Y);
        int x = Math.Min(start.X, currentX);
        int y = Math.Min(start.Y, currentY);
        return new SD.Rectangle(x, y, Math.Abs(start.X - currentX), Math.Abs(start.Y - currentY));
    }

    private static (double X, double Y) AdjustCurrent(
        double startX,
        double startY,
        double currentX,
        double currentY,
        double? aspectRatio)
    {
        if (aspectRatio is not double ratio || ratio <= 0 || double.IsNaN(ratio) || double.IsInfinity(ratio))
            return (currentX, currentY);

        double dx = currentX - startX;
        double dy = currentY - startY;
        double width = Math.Max(Math.Abs(dx), Math.Abs(dy) * ratio);
        double height = width / ratio;
        return (
            startX + (dx < 0 ? -width : width),
            startY + (dy < 0 ? -height : height));
    }
}
