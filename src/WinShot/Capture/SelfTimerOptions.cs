namespace WinShot.Capture;

public static class SelfTimerOptions
{
    public const int MinDelaySeconds = 1;
    public const int MaxDelaySeconds = 60;

    public static int ClampDelaySeconds(int seconds) =>
        Math.Clamp(seconds, MinDelaySeconds, MaxDelaySeconds);

    public static bool ShouldRunDelay(int seconds) => seconds > 0;
}
