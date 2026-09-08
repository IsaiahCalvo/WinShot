namespace WinShot.Recording;

/// <summary>
/// The slice of a clip the trim timeline is currently showing. At full zoom it is the whole
/// clip; zoomed in it is a window you can pan, so a ten-minute recording can be trimmed to a
/// frame instead of to whatever a 700-pixel-wide strip can resolve (about 0.9 s per pixel,
/// nine times coarser than the keyboard step the thumbs already offer).
///
/// Pure: the mapping between seconds and the strip is the whole feature, and it is far easier
/// to be sure of in a test than by dragging a handle.
/// </summary>
public readonly record struct TimelineView(double StartSeconds, double EndSeconds)
{
    /// <summary>The tightest window on offer. Below this the strip shows less than a beat and
    /// panning becomes unusable.</summary>
    public const double MinimumSpanSeconds = 0.5;

    /// <summary>How far in one wheel notch moves.</summary>
    public const double WheelZoomFactor = 1.25;

    public double Span => Math.Max(MinimumSpanSeconds, EndSeconds - StartSeconds);

    public static TimelineView Full(double durationSeconds) =>
        new(0, Math.Max(MinimumSpanSeconds, CleanDuration(durationSeconds)));

    public bool IsFull(double durationSeconds)
    {
        double duration = CleanDuration(durationSeconds);
        return StartSeconds <= 0.0001 && EndSeconds >= duration - 0.0001;
    }

    /// <summary>Fraction across the visible strip for a time, clamped to it.</summary>
    public double ToFraction(double seconds) =>
        Math.Clamp((seconds - StartSeconds) / Span, 0, 1);

    /// <summary>The time at a fraction across the visible strip.</summary>
    public double ToSeconds(double fraction) =>
        StartSeconds + (Math.Clamp(fraction, 0, 1) * Span);

    /// <summary>
    /// Zooms by <paramref name="factor"/> (greater than one zooms in) keeping
    /// <paramref name="anchorSeconds"/> under the same pixel, the way every map does it.
    /// Never escapes the clip, and never inverts.
    /// </summary>
    public TimelineView Zoomed(double factor, double anchorSeconds, double durationSeconds)
    {
        double duration = CleanDuration(durationSeconds);
        if (!double.IsFinite(factor) || factor <= 0)
            return this;

        double span = Math.Clamp(Span / factor, Math.Min(MinimumSpanSeconds, duration), duration);
        double anchor = Math.Clamp(anchorSeconds, StartSeconds, EndSeconds);
        // Keep the anchor at the same relative position it currently occupies.
        double anchorFraction = ToFraction(anchor);
        double start = anchor - (anchorFraction * span);
        return Clamped(start, start + span, duration);
    }

    /// <summary>Slides the window by <paramref name="seconds"/> without changing its span.</summary>
    public TimelineView Panned(double seconds, double durationSeconds)
    {
        double duration = CleanDuration(durationSeconds);
        double span = Math.Min(Span, duration);
        return Clamped(StartSeconds + seconds, StartSeconds + seconds + span, duration);
    }

    /// <summary>Brings <paramref name="seconds"/> into view without changing the span, so the
    /// playhead cannot wander off the strip while zoomed.</summary>
    public TimelineView Containing(double seconds, double durationSeconds)
    {
        // Every other method on this type is total; a NaN or infinite position would otherwise
        // hand back a NaN view and take the whole strip with it.
        if (!double.IsFinite(seconds) || (seconds >= StartSeconds && seconds <= EndSeconds))
            return this;

        double duration = CleanDuration(durationSeconds);
        double span = Math.Min(Span, duration);
        double start = seconds < StartSeconds ? seconds : seconds - span;
        return Clamped(start, start + span, duration);
    }

    /// <summary>Linear interpolation between two views, for animating a return to the full clip.</summary>
    public static TimelineView Lerp(TimelineView from, TimelineView to, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        return new TimelineView(
            from.StartSeconds + ((to.StartSeconds - from.StartSeconds) * t),
            from.EndSeconds + ((to.EndSeconds - from.EndSeconds) * t));
    }

    private static TimelineView Clamped(double start, double end, double duration)
    {
        double span = Math.Clamp(end - start, Math.Min(MinimumSpanSeconds, duration), duration);
        double clampedStart = Math.Clamp(start, 0, Math.Max(0, duration - span));
        return new TimelineView(clampedStart, clampedStart + span);
    }

    private static double CleanDuration(double durationSeconds) =>
        double.IsFinite(durationSeconds) && durationSeconds > MinimumSpanSeconds
            ? durationSeconds
            : MinimumSpanSeconds;
}
