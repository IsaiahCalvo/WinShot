using SD = System.Drawing;

namespace WinShot.History;

public static class HistoryPreviewLayout
{
    private const int TotalPadding = 16;
    private const int MinContentLength = 48;
    private const double MaxWorkAreaFactor = 0.8;

    /// <summary>Logical (96 dpi) size of the no-preview card; the caller scales it.</summary>
    public static SD.Size FallbackSize => new(360, 128);

    /// <param name="totalPadding">Chrome around the image, in physical pixels — the caller's
    /// inset doubled. Defaults to the 100% value so existing callers are unchanged.</param>
    public static SD.Size CalculateImageSize(
        SD.Size imageSize, SD.Size workAreaSize, int totalPadding = TotalPadding)
    {
        int imageWidth = Math.Max(1, imageSize.Width);
        int imageHeight = Math.Max(1, imageSize.Height);
        int maxWidth = Math.Max(160, (int)Math.Round(workAreaSize.Width * MaxWorkAreaFactor));
        int maxHeight = Math.Max(120, (int)Math.Round(workAreaSize.Height * MaxWorkAreaFactor));
        double scale = Math.Min(1.0, Math.Min(
            maxWidth / (double)imageWidth,
            maxHeight / (double)imageHeight));

        int pad = Math.Max(0, totalPadding);
        int width = Math.Max(MinContentLength, (int)Math.Round(imageWidth * scale)) + pad;
        int height = Math.Max(MinContentLength, (int)Math.Round(imageHeight * scale)) + pad;
        return new SD.Size(width, height);
    }
}
