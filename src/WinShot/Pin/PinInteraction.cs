namespace WinShot.Pin;

using SD = System.Drawing;

public static class PinInteraction
{
    public const double MinScale = 0.2;
    public const double MaxScale = 3.0;

    private const double ScaleStep = 1.1;
    private const double OpacityStep = 0.1;
    private const double MinOpacity = 0.3;
    private const double MaxOpacity = 1.0;
    private const double LockedOpacityFactor = 0.85;
    private const double MinLockedOpacity = 0.1;

    public static double AdjustScale(double current, int wheelDelta)
    {
        double multiplier = wheelDelta > 0 ? ScaleStep : 1 / ScaleStep;
        // A huge image's FIT scale can legitimately sit below MinScale; the floor is
        // then the current scale itself, so zooming steps smoothly instead of snapping
        // up to MinScale (which visibly tripled the pin on the first wheel notch).
        double floor = Math.Min(MinScale, current);
        return Math.Clamp(current * multiplier, floor, MaxScale);
    }

    public static double AdjustOpacity(double current, int wheelDelta)
    {
        double next = current + (wheelDelta > 0 ? OpacityStep : -OpacityStep);
        return Math.Clamp(next, MinOpacity, MaxOpacity);
    }

    public static double LockedOpacity(double current) =>
        Math.Max(MinLockedOpacity, current * LockedOpacityFactor);

    public static int NudgeStep(bool shiftPressed) => shiftPressed ? 10 : 1;

    public static int ScaleLogical(int logicalPixels, int dpi) =>
        Math.Max(1, (int)Math.Round(logicalPixels * Math.Max(96, dpi) / 96d));

    public static SD.Point CascadeLocation(
        SD.Rectangle workingArea,
        SD.Size windowSize,
        int cascadeIndex,
        int logicalOffset,
        int dpi)
    {
        int offset = Math.Max(0, cascadeIndex) * ScaleLogical(logicalOffset, dpi);
        var centered = new SD.Point(
            workingArea.Left + Math.Max(0, (workingArea.Width - windowSize.Width) / 2) + offset,
            workingArea.Top + Math.Max(0, (workingArea.Height - windowSize.Height) / 2) + offset);
        return ClampLocation(workingArea, windowSize, centered);
    }

    public static SD.Point ClampLocation(
        SD.Rectangle workingArea,
        SD.Size windowSize,
        SD.Point requested)
    {
        int maxX = Math.Max(workingArea.Left, workingArea.Right - Math.Max(1, windowSize.Width));
        int maxY = Math.Max(workingArea.Top, workingArea.Bottom - Math.Max(1, windowSize.Height));
        return new SD.Point(
            Math.Clamp(requested.X, workingArea.Left, maxX),
            Math.Clamp(requested.Y, workingArea.Top, maxY));
    }

    public static string ResolveSaveFolder(string? configuredFolder, string fallbackFolder) =>
        string.IsNullOrWhiteSpace(configuredFolder) ? fallbackFolder : configuredFolder.Trim();
}
