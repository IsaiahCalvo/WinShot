using System.Windows;

namespace WinShot.Editor;

public readonly record struct SpotlightLayoutResult(Size Outer, Rect Hole);

public static class SpotlightLayout
{
    public static SpotlightLayoutResult Calculate(Size imageSize, Rect requestedHole)
    {
        double width = ValidLength(imageSize.Width);
        double height = ValidLength(imageSize.Height);
        Rect hole = requestedHole.IsEmpty || !IsFinite(requestedHole)
            ? new Rect(0, 0, 1, 1)
            : requestedHole;

        double left = Math.Clamp(hole.Left, 0, width);
        double top = Math.Clamp(hole.Top, 0, height);
        double right = Math.Clamp(hole.Right, 0, width);
        double bottom = Math.Clamp(hole.Bottom, 0, height);

        if (right <= left)
        {
            left = Math.Clamp(FiniteOrZero(hole.Left), 0, Math.Max(0, width - 1));
            right = left + 1;
        }
        if (bottom <= top)
        {
            top = Math.Clamp(FiniteOrZero(hole.Top), 0, Math.Max(0, height - 1));
            bottom = top + 1;
        }

        return new SpotlightLayoutResult(
            new Size(width, height),
            new Rect(left, top, right - left, bottom - top));
    }

    private static double ValidLength(double value) =>
        double.IsFinite(value) && value >= 1 ? value : 1;

    private static double FiniteOrZero(double value) =>
        double.IsFinite(value) ? value : 0;

    private static bool IsFinite(Rect rect) =>
        double.IsFinite(rect.X) &&
        double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) &&
        double.IsFinite(rect.Height);
}
