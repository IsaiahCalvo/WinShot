using SD = System.Drawing;

namespace WinShot.Capture;

public readonly record struct FastSelectorGuideLines(
    bool IsVisible,
    SD.Point Cursor,
    SD.Point LeftStart,
    SD.Point LeftEnd,
    SD.Point RightStart,
    SD.Point RightEnd,
    SD.Point TopStart,
    SD.Point TopEnd,
    SD.Point BottomStart,
    SD.Point BottomEnd);

public static class FastSelectorGuideLayout
{
    public static FastSelectorGuideLines Calculate(SD.Size clientSize, SD.Point cursor, int gap)
    {
        if (clientSize.Width < 1 ||
            clientSize.Height < 1 ||
            cursor.X < 0 ||
            cursor.Y < 0 ||
            cursor.X >= clientSize.Width ||
            cursor.Y >= clientSize.Height)
        {
            return default;
        }

        int safeGap = Math.Max(0, gap);
        int right = clientSize.Width - 1;
        int bottom = clientSize.Height - 1;
        int leftEnd = Math.Max(0, cursor.X - safeGap);
        int rightStart = Math.Min(right, cursor.X + safeGap);
        int topEnd = Math.Max(0, cursor.Y - safeGap);
        int bottomStart = Math.Min(bottom, cursor.Y + safeGap);

        return new FastSelectorGuideLines(
            true,
            cursor,
            new SD.Point(0, cursor.Y),
            new SD.Point(leftEnd, cursor.Y),
            new SD.Point(rightStart, cursor.Y),
            new SD.Point(right, cursor.Y),
            new SD.Point(cursor.X, 0),
            new SD.Point(cursor.X, topEnd),
            new SD.Point(cursor.X, bottomStart),
            new SD.Point(cursor.X, bottom));
    }
}
