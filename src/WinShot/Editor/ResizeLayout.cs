namespace WinShot.Editor;

public readonly record struct ResizeLayoutResult(int Width, int Height, int Percent);

public static class ResizeLayout
{
    public const int MaxDimension = 20000;

    public static ResizeLayoutResult FromWidth(
        int originalWidth,
        int originalHeight,
        int currentHeight,
        int requestedWidth,
        bool lockRatio)
    {
        int ow = NormalizeOriginal(originalWidth);
        int oh = NormalizeOriginal(originalHeight);
        int width = NormalizeDimension(requestedWidth);
        int height = lockRatio
            ? NormalizeDimension((int)Math.Round(width * oh / (double)ow))
            : NormalizeDimension(currentHeight);

        return new ResizeLayoutResult(width, height, PercentFrom(width, ow));
    }

    public static ResizeLayoutResult FromHeight(
        int originalWidth,
        int originalHeight,
        int currentWidth,
        int requestedHeight,
        bool lockRatio)
    {
        int ow = NormalizeOriginal(originalWidth);
        int oh = NormalizeOriginal(originalHeight);
        int height = NormalizeDimension(requestedHeight);
        int width = lockRatio
            ? NormalizeDimension((int)Math.Round(height * ow / (double)oh))
            : NormalizeDimension(currentWidth);

        return new ResizeLayoutResult(width, height, PercentFrom(height, oh));
    }

    public static ResizeLayoutResult FromPercent(int originalWidth, int originalHeight, double percent)
    {
        int ow = NormalizeOriginal(originalWidth);
        int oh = NormalizeOriginal(originalHeight);
        double pct = double.IsFinite(percent) && percent > 0 ? percent : 1;
        int width = NormalizeDimension((int)Math.Round(ow * pct / 100.0));
        int height = NormalizeDimension((int)Math.Round(oh * pct / 100.0));
        return new ResizeLayoutResult(width, height, (int)Math.Round(pct));
    }

    public static bool IsValid(int width, int height) =>
        width >= 1 && height >= 1 && width <= MaxDimension && height <= MaxDimension;

    private static int NormalizeOriginal(int value) => Math.Max(1, value);

    private static int NormalizeDimension(int value) => Math.Max(1, value);

    private static int PercentFrom(int value, int original) =>
        (int)Math.Round(value * 100.0 / Math.Max(1, original));
}
