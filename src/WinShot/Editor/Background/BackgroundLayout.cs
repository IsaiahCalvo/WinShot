using SD = System.Drawing;

namespace WinShot.Editor.Background;

public readonly record struct BackgroundLayoutResult(SD.Size SourceSize, SD.Size CanvasSize, int Margin);

public static class BackgroundLayout
{
    public static BackgroundLayoutResult Calculate(
        SD.Size sourceSize,
        int padding,
        int inset,
        double? aspectRatio)
    {
        int sourceWidth = Math.Max(1, sourceSize.Width);
        int sourceHeight = Math.Max(1, sourceSize.Height);
        int margin = Math.Max(0, padding) + Math.Max(0, inset);
        int contentWidth = sourceWidth + 2 * margin;
        int contentHeight = sourceHeight + 2 * margin;
        int canvasWidth = contentWidth;
        int canvasHeight = contentHeight;

        if (aspectRatio is double ratio && ratio > 0 && !double.IsNaN(ratio) && !double.IsInfinity(ratio))
        {
            if (contentWidth / (double)contentHeight >= ratio)
                canvasHeight = (int)Math.Ceiling(contentWidth / ratio);
            else
                canvasWidth = (int)Math.Ceiling(contentHeight * ratio);
        }

        return new BackgroundLayoutResult(
            new SD.Size(sourceWidth, sourceHeight),
            new SD.Size(canvasWidth, canvasHeight),
            margin);
    }
}
