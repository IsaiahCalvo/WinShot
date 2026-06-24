using System.Windows;

namespace WinShot.Editor;

public static class CropSelectionLayout
{
    public static Rect Calculate(
        Size imageSize,
        Point start,
        Point current,
        double? aspectRatio,
        double snapDistance)
    {
        double width = Math.Max(1, imageSize.Width);
        double height = Math.Max(1, imageSize.Height);
        double snap = Math.Max(0, snapDistance);
        Point anchor = Clamp(start, width, height);
        Point point = Clamp(current, width, height);

        if (aspectRatio is not double ratio || ratio <= 0 || double.IsNaN(ratio) || double.IsInfinity(ratio))
            return SnapRectEdges(new Rect(anchor, point), width, height, snap);

        double dx = point.X - anchor.X;
        double dy = point.Y - anchor.Y;
        double sx = dx < 0 ? -1 : 1;
        double sy = dy < 0 ? -1 : 1;
        double w = Math.Abs(dx);
        double h = Math.Abs(dy);

        if (w < h * ratio)
            w = h * ratio;
        else
            h = w / ratio;

        double maxW = sx > 0 ? width - anchor.X : anchor.X;
        double maxH = sy > 0 ? height - anchor.Y : anchor.Y;
        if (w > maxW)
        {
            w = maxW;
            h = w / ratio;
        }
        if (h > maxH)
        {
            h = maxH;
            w = h * ratio;
        }

        var rect = new Rect(anchor, new Point(anchor.X + sx * w, anchor.Y + sy * h));
        return SnapRectTranslate(rect, width, height, snap);
    }

    private static Point Clamp(Point point, double width, double height) =>
        new(Math.Clamp(point.X, 0, width), Math.Clamp(point.Y, 0, height));

    private static Rect SnapRectEdges(Rect rect, double imageWidth, double imageHeight, double snapDistance)
    {
        double left = rect.Left;
        double top = rect.Top;
        double right = rect.Right;
        double bottom = rect.Bottom;

        if (Math.Abs(left) <= snapDistance)
            left = 0;
        if (Math.Abs(top) <= snapDistance)
            top = 0;
        if (Math.Abs(right - imageWidth) <= snapDistance)
            right = imageWidth;
        if (Math.Abs(bottom - imageHeight) <= snapDistance)
            bottom = imageHeight;

        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private static Rect SnapRectTranslate(Rect rect, double imageWidth, double imageHeight, double snapDistance)
    {
        double x = rect.X;
        double y = rect.Y;

        if (Math.Abs(rect.Left) <= snapDistance)
            x = 0;
        else if (Math.Abs(rect.Right - imageWidth) <= snapDistance)
            x = imageWidth - rect.Width;

        if (Math.Abs(rect.Top) <= snapDistance)
            y = 0;
        else if (Math.Abs(rect.Bottom - imageHeight) <= snapDistance)
            y = imageHeight - rect.Height;

        x = Math.Clamp(x, 0, Math.Max(0, imageWidth - rect.Width));
        y = Math.Clamp(y, 0, Math.Max(0, imageHeight - rect.Height));
        return new Rect(x, y, rect.Width, rect.Height);
    }
}
