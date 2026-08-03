using SD = System.Drawing;

namespace WinShot.Overlay;

internal readonly record struct QuickAccessOverlayLayout(
    SD.Size ClientSize,
    SD.Rectangle CardBounds,
    SD.Rectangle ThumbnailBounds,
    int CornerRadius,
    int IconSize,
    int PillWidth,
    int PillHeight,
    int ControlPadding,
    int PillGap)
{
    private const int MinCardWidth = 150;
    private const int MinCardHeight = 110;
    private const int MaxCardWidth = 230;
    private const int MaxCardHeight = 152;

    public static QuickAccessOverlayLayout Calculate(SD.Size imageSize, int sizePercent, int dpi)
    {
        int normalizedDpi = Math.Clamp(dpi, 96, 480);
        int normalizedSize = Math.Clamp(sizePercent, 0, 100);
        double dpiScale = normalizedDpi / 96d;
        int logicalMaxWidth = MinCardWidth + (int)Math.Round((MaxCardWidth - MinCardWidth) * normalizedSize / 100d);
        int logicalMaxHeight = MinCardHeight + (int)Math.Round((MaxCardHeight - MinCardHeight) * normalizedSize / 100d);

        int maxWidth = Scale(logicalMaxWidth, dpiScale);
        int maxHeight = Scale(logicalMaxHeight, dpiScale);
        int sourceWidth = Math.Max(1, imageSize.Width);
        int sourceHeight = Math.Max(1, imageSize.Height);
        double fit = Math.Min(maxWidth / (double)sourceWidth, maxHeight / (double)sourceHeight);
        int thumbWidth = Math.Max(1, (int)Math.Round(sourceWidth * fit));
        int thumbHeight = Math.Max(1, (int)Math.Round(sourceHeight * fit));
        int cardWidth = Math.Clamp(thumbWidth, Scale(MinCardWidth, dpiScale), maxWidth);
        int cardHeight = Math.Clamp(thumbHeight, Scale(MinCardHeight, dpiScale), maxHeight);

        var card = new SD.Rectangle(0, 0, cardWidth, cardHeight);
        var thumbnail = new SD.Rectangle(
            (cardWidth - thumbWidth) / 2,
            (cardHeight - thumbHeight) / 2,
            thumbWidth,
            thumbHeight);

        return new QuickAccessOverlayLayout(
            card.Size,
            card,
            thumbnail,
            Scale(11, dpiScale),
            Scale(28, dpiScale),
            Scale(82, dpiScale),
            Scale(34, dpiScale),
            Scale(9, dpiScale),
            Scale(6, dpiScale));
    }

    public static string NormalizePosition(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "left" => "left",
        "right" => "right",
        "top" => "top",
        "bottom" => "bottom",
        "bottom-left" => "bottom-left",
        "bottom-right" => "bottom-right",
        _ => "bottom-right",
    };

    public static string NormalizeAutoCloseAction(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "copy-close" => "copy-close",
        "close" => "close",
        _ => "save-close",
    };

    public static string NormalizeSaveBehavior(string? value) =>
        string.Equals(value, "ask", StringComparison.OrdinalIgnoreCase) ? "ask" : "export";

    public static SD.Point Place(
        SD.Rectangle workingArea,
        SD.Size windowSize,
        string? position,
        int stackOffset,
        int dpi)
    {
        double dpiScale = Math.Clamp(dpi, 96, 480) / 96d;
        int edge = Scale(16, dpiScale);
        string normalized = NormalizePosition(position);
        int x;
        int y;
        switch (normalized)
        {
            case "left":
                x = workingArea.Left + edge;
                y = workingArea.Top + (workingArea.Height - windowSize.Height) / 2 + stackOffset;
                break;
            case "right":
                x = workingArea.Right - windowSize.Width - edge;
                y = workingArea.Top + (workingArea.Height - windowSize.Height) / 2 + stackOffset;
                break;
            case "top":
                x = workingArea.Left + (workingArea.Width - windowSize.Width) / 2;
                y = workingArea.Top + edge + stackOffset;
                break;
            case "bottom":
                x = workingArea.Left + (workingArea.Width - windowSize.Width) / 2;
                y = workingArea.Bottom - windowSize.Height - edge - stackOffset;
                break;
            case "bottom-left":
                x = workingArea.Left + edge;
                y = workingArea.Bottom - windowSize.Height - edge - stackOffset;
                break;
            default:
                x = workingArea.Right - windowSize.Width - edge;
                y = workingArea.Bottom - windowSize.Height - edge - stackOffset;
                break;
        }

        int maxX = Math.Max(workingArea.Left, workingArea.Right - windowSize.Width);
        int maxY = Math.Max(workingArea.Top, workingArea.Bottom - windowSize.Height);
        return new SD.Point(
            Math.Clamp(x, workingArea.Left, maxX),
            Math.Clamp(y, workingArea.Top, maxY));
    }

    private static int Scale(int value, double dpiScale) => Math.Max(1, (int)Math.Round(value * dpiScale));
}
