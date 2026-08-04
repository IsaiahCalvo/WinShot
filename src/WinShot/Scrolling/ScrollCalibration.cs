namespace WinShot.Scrolling;

/// <summary>Pure, deterministic result of one auto-scroll calibration update.</summary>
internal readonly record struct ScrollCalibrationResult(
    double PixelsPerNotch,
    int Notches,
    int MovingLength,
    int MaxSafeMovement,
    double EstimatedMovement,
    bool CanPreserveOverlap);

/// <summary>Plans auto-scroll steps against the moving body, excluding fixed edge chrome.</summary>
internal static class ScrollCalibration
{
    // Keep at least the same overlap required by ImageStitcher's exact matcher.
    internal const int RequiredOverlap = 24;

    internal static ScrollCalibrationResult Update(double previousPixelsPerNotch,
        int measuredOffset, int sentNotches, int viewportLength, int leadingBand, int trailingBand)
    {
        double measuredPerNotch = measuredOffset / (double)Math.Max(1, sentNotches);
        double pixelsPerNotch = previousPixelsPerNotch <= 0
            ? measuredPerNotch
            : (previousPixelsPerNotch + measuredPerNotch) / 2;

        int movingLength = Math.Max(1, viewportLength - Math.Max(0, leadingBand) - Math.Max(0, trailingBand));
        int maxSafeMovement = Math.Max(0, movingLength - RequiredOverlap);
        int targetMovement = maxSafeMovement == 0 ? 0 : Math.Min(maxSafeMovement,
            Math.Max(1, (int)Math.Round(0.6 * movingLength)));

        int desiredNotches = targetMovement == 0 ? 0 : Math.Max(1,
            (int)Math.Round(targetMovement / Math.Max(1, pixelsPerNotch)));
        int maxSafeNotches = (int)Math.Floor(maxSafeMovement / Math.Max(1, pixelsPerNotch));
        bool canPreserveOverlap = maxSafeNotches >= 1;
        int notches = canPreserveOverlap
            ? Math.Clamp(Math.Min(desiredNotches, maxSafeNotches), 1, 8)
            : 0; // no wheel step is safe; the capture loop stops instead of creating a gap

        return new ScrollCalibrationResult(pixelsPerNotch, notches, movingLength,
            maxSafeMovement, pixelsPerNotch * notches, canPreserveOverlap);
    }
}
