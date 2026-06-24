using SD = System.Drawing;

namespace WinShot.Capture;

public readonly record struct FastSelectorLoupe(
    bool IsVisible,
    SD.Rectangle SourceScreen,
    SD.Rectangle Bounds,
    SD.Point CrosshairCenter,
    string Coordinates,
    int Zoom,
    SD.Point TargetSample);

public static class FastSelectorLoupeLayout
{
    private const int OffsetX = 24;
    private const int OffsetY = 52;
    private const int FlipY = 56;

    public static FastSelectorLoupe Calculate(
        SD.Size clientSize,
        SD.Rectangle virtualScreen,
        SD.Point cursorClient,
        SD.Point cursorScreen,
        int loupeSize,
        int zoom)
    {
        if (clientSize.Width < 1 ||
            clientSize.Height < 1 ||
            virtualScreen.Width < 1 ||
            virtualScreen.Height < 1 ||
            cursorClient.X < 0 ||
            cursorClient.Y < 0 ||
            cursorClient.X >= clientSize.Width ||
            cursorClient.Y >= clientSize.Height ||
            loupeSize < 1 ||
            zoom < 1)
        {
            return default;
        }

        int sampleSize = Math.Max(1, (int)Math.Ceiling(loupeSize / (double)zoom));
        int sourceLeft = Clamp(cursorScreen.X - sampleSize / 2, virtualScreen.Left, virtualScreen.Right - sampleSize);
        int sourceTop = Clamp(cursorScreen.Y - sampleSize / 2, virtualScreen.Top, virtualScreen.Bottom - sampleSize);
        var source = new SD.Rectangle(sourceLeft, sourceTop, sampleSize, sampleSize);

        int left = cursorClient.X + OffsetX;
        int top = cursorClient.Y + OffsetY;
        if (left + loupeSize > clientSize.Width)
            left = cursorClient.X - OffsetX - loupeSize;
        if (top + loupeSize + 28 > clientSize.Height)
            top = cursorClient.Y - FlipY - loupeSize;

        left = Clamp(left, 0, Math.Max(0, clientSize.Width - loupeSize));
        top = Clamp(top, 0, Math.Max(0, clientSize.Height - loupeSize));
        var bounds = new SD.Rectangle(left, top, loupeSize, loupeSize);

        int crosshairX = bounds.Left + (int)Math.Round((cursorScreen.X - source.Left + 0.5) * zoom);
        int crosshairY = bounds.Top + (int)Math.Round((cursorScreen.Y - source.Top + 0.5) * zoom);
        var crosshair = new SD.Point(
            Clamp(crosshairX, bounds.Left, bounds.Right - 1),
            Clamp(crosshairY, bounds.Top, bounds.Bottom - 1));

        // Offset of the hovered pixel inside the captured sample bitmap. Used to
        // read its hex color and to highlight the exact target cell in the zoom.
        var targetSample = new SD.Point(
            Clamp(cursorScreen.X - source.Left, 0, sampleSize - 1),
            Clamp(cursorScreen.Y - source.Top, 0, sampleSize - 1));

        return new FastSelectorLoupe(
            true,
            source,
            bounds,
            crosshair,
            $"{cursorScreen.X}, {cursorScreen.Y}",
            zoom,
            targetSample);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (max < min)
            return min;
        return Math.Min(Math.Max(value, min), max);
    }
}
