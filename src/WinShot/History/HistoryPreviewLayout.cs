using SD = System.Drawing;

namespace WinShot.History;

public static class HistoryPreviewLayout
{
    public const int TotalPadding = 16;
    private const int MinContentLength = 48;
    private const double MaxWorkAreaFactor = 0.8;

    public static SD.Size FallbackSize => new(360, 128);

    public static SD.Size CalculateImageSize(SD.Size imageSize, SD.Size workAreaSize)
    {
        int imageWidth = Math.Max(1, imageSize.Width);
        int imageHeight = Math.Max(1, imageSize.Height);
        int maxWidth = Math.Max(160, (int)Math.Round(workAreaSize.Width * MaxWorkAreaFactor));
        int maxHeight = Math.Max(120, (int)Math.Round(workAreaSize.Height * MaxWorkAreaFactor));
        double scale = Math.Min(1.0, Math.Min(
            maxWidth / (double)imageWidth,
            maxHeight / (double)imageHeight));

        int width = Math.Max(MinContentLength, (int)Math.Round(imageWidth * scale)) + TotalPadding;
        int height = Math.Max(MinContentLength, (int)Math.Round(imageHeight * scale)) + TotalPadding;
        return new SD.Size(width, height);
    }
}
