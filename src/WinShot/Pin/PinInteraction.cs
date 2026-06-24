namespace WinShot.Pin;

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
        return Math.Clamp(current * multiplier, MinScale, MaxScale);
    }

    public static double AdjustOpacity(double current, int wheelDelta)
    {
        double next = current + (wheelDelta > 0 ? OpacityStep : -OpacityStep);
        return Math.Clamp(next, MinOpacity, MaxOpacity);
    }

    public static double LockedOpacity(double current) =>
        Math.Max(MinLockedOpacity, current * LockedOpacityFactor);

    public static int NudgeStep(bool shiftPressed) => shiftPressed ? 10 : 1;
}
