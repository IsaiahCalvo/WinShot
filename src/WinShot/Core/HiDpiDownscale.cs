namespace WinShot.Core;

public static class HiDpiDownscale
{
    public static bool TryGetTargetSize(
        int sourceWidth,
        int sourceHeight,
        double dpiScale,
        out int targetWidth,
        out int targetHeight)
    {
        targetWidth = sourceWidth;
        targetHeight = sourceHeight;

        if (dpiScale <= 1.01)
            return false;

        targetWidth = Math.Max(1, (int)Math.Round(sourceWidth / dpiScale));
        targetHeight = Math.Max(1, (int)Math.Round(sourceHeight / dpiScale));
        return targetWidth != sourceWidth || targetHeight != sourceHeight;
    }
}
