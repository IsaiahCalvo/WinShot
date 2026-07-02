namespace WinShot.Recording;

public readonly record struct VideoTrimRange(double StartSeconds, double EndSeconds)
{
    public const double MinimumDurationSeconds = 0.1;

    public bool IsExportable => CanExport(StartSeconds, EndSeconds);

    public static VideoTrimRange FromStart(
        double requestedStartSeconds,
        double currentEndSeconds,
        double durationSeconds) =>
        Normalize(requestedStartSeconds, currentEndSeconds, durationSeconds);

    public static VideoTrimRange FromEnd(
        double currentStartSeconds,
        double requestedEndSeconds,
        double durationSeconds) =>
        Normalize(currentStartSeconds, requestedEndSeconds, durationSeconds);

    public static VideoTrimRange Normalize(double startSeconds, double endSeconds, double durationSeconds)
    {
        double duration = CleanDuration(durationSeconds);
        double start = Math.Clamp(CleanSeconds(startSeconds, 0), 0, Math.Max(0, duration - MinimumDurationSeconds));
        double end = Math.Clamp(CleanSeconds(endSeconds, duration), MinimumDurationSeconds, duration);

        if (end - start < MinimumDurationSeconds)
        {
            if (start + MinimumDurationSeconds <= duration)
                end = start + MinimumDurationSeconds;
            else
                start = Math.Max(0, end - MinimumDurationSeconds);
        }

        return new VideoTrimRange(start, end);
    }

    public static bool CanExport(double startSeconds, double endSeconds) =>
        double.IsFinite(startSeconds) &&
        double.IsFinite(endSeconds) &&
        endSeconds - startSeconds >= MinimumDurationSeconds;

    public double TrimFromEndSeconds(double durationSeconds) =>
        Math.Max(0, CleanDuration(durationSeconds) - EndSeconds);

    private static double CleanDuration(double durationSeconds) =>
        double.IsFinite(durationSeconds) && durationSeconds >= MinimumDurationSeconds
            ? durationSeconds
            : MinimumDurationSeconds;

    private static double CleanSeconds(double seconds, double fallback) =>
        double.IsFinite(seconds) ? seconds : fallback;
}
