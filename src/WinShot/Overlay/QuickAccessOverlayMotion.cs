using SD = System.Drawing;

namespace WinShot.Overlay;

internal static class QuickAccessOverlayMotion
{
    public const int FrameIntervalMs = 15;
    public const int ExitDurationMs = 320;
    public const int RestackDurationMs = 200;

    public static SD.Point ExitTarget(SD.Rectangle workingArea, SD.Rectangle windowBounds, string? position)
    {
        string normalized = QuickAccessOverlayLayout.NormalizePosition(position);
        bool exitRight = normalized is "right" or "bottom-right";
        bool exitLeft = normalized is "left" or "bottom-left";
        if (!exitRight && !exitLeft)
        {
            int windowCenter = windowBounds.Left + windowBounds.Width / 2;
            int areaCenter = workingArea.Left + workingArea.Width / 2;
            exitRight = windowCenter >= areaCenter;
        }

        int overshoot = Math.Max(16, windowBounds.Width / 6);
        int x = exitRight
            ? workingArea.Right + overshoot
            : workingArea.Left - windowBounds.Width - overshoot;
        return new SD.Point(x, windowBounds.Top);
    }

    public static double EaseInCubic(double progress)
    {
        double value = Math.Clamp(progress, 0d, 1d);
        return value * value * value;
    }

    public static double EaseOutCubic(double progress)
    {
        double value = 1d - Math.Clamp(progress, 0d, 1d);
        return 1d - value * value * value;
    }

    public static SD.Point Interpolate(SD.Point start, SD.Point end, double progress)
    {
        double value = Math.Clamp(progress, 0d, 1d);
        return new SD.Point(
            (int)Math.Round(start.X + (end.X - start.X) * value),
            (int)Math.Round(start.Y + (end.Y - start.Y) * value));
    }

    public static double ExitOpacity(double progress)
        => 1d - Math.Clamp(progress, 0d, 1d);
}
