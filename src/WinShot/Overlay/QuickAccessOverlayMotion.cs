using SD = System.Drawing;

namespace WinShot.Overlay;

internal static class QuickAccessOverlayMotion
{
    public const int FrameIntervalMs = WinShot.Core.Motion.FrameIntervalMs;
    public const int ExitDurationMs = WinShot.Core.Motion.ExitDurationMs;
    public const int RestackDurationMs = WinShot.Core.Motion.RestackDurationMs;
    public const int EnterDurationMs = WinShot.Core.Motion.EnterDurationMs;

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

    /// <summary>
    /// Where an arriving card starts: one card and one gutter past its settled slot, in the
    /// direction of the edge it is anchored to. A fixed travel, not "off the working-area
    /// edge" — <see cref="EaseOutBack"/> overshoots by 10 % of the distance covered, so an
    /// origin pinned to the edge made the third card in a stack overshoot by a third of a
    /// card and slide over its neighbour before dropping back. Clamped so the travel is never
    /// LONGER than "just past the working-area edge" either — the card nearest the edge has
    /// nowhere further to come from, and a longer run there only makes its overshoot bigger.
    /// </summary>
    public static SD.Point EnterOrigin(
        SD.Rectangle workingArea, SD.Rectangle windowBounds, string? position, int stackGutter)
    {
        string normalized = QuickAccessOverlayLayout.NormalizePosition(position);
        // The left/right anchors slide in horizontally while they stack vertically, so their
        // travel never grew with depth; they keep starting clear of their own edge.
        int travel = windowBounds.Height + Math.Max(0, stackGutter);
        return normalized switch
        {
            "left" => new SD.Point(workingArea.Left - windowBounds.Width, windowBounds.Top),
            "right" => new SD.Point(workingArea.Right, windowBounds.Top),
            // Max, not Min: "top" travels UP, so the edge is the smaller Y and clamping to
            // "no further than the edge" is the larger of the two.
            "top" => new SD.Point(
                windowBounds.Left,
                Math.Max(windowBounds.Top - travel, workingArea.Top - windowBounds.Height)),
            // bottom, bottom-left and bottom-right all hug the bottom edge.
            _ => new SD.Point(
                windowBounds.Left,
                Math.Min(windowBounds.Top + travel, workingArea.Bottom)),
        };
    }

    public static double EaseOutBack(double progress) => WinShot.Core.Motion.EaseOutBack(progress);

    public static double EaseInOutSine(double progress) => WinShot.Core.Motion.EaseInOutSine(progress);

    public static double EaseOutCubic(double progress) => WinShot.Core.Motion.EaseOutCubic(progress);

    public static SD.Point Interpolate(SD.Point start, SD.Point end, double progress)
    {
        double value = Math.Clamp(progress, 0d, 1d);
        return new SD.Point(
            (int)Math.Round(start.X + (end.X - start.X) * value),
            (int)Math.Round(start.Y + (end.Y - start.Y) * value));
    }

    /// <summary>
    /// <see cref="Interpolate"/> without the clamp. An entrance eased with
    /// <see cref="EaseOutBack"/> goes past 1 on purpose — clamping would flatten exactly
    /// the overshoot that makes the card read as landing instead of sliding to a halt.
    /// </summary>
    public static SD.Point InterpolateUnclamped(SD.Point start, SD.Point end, double progress)
        => new(
            (int)Math.Round(start.X + (end.X - start.X) * progress),
            (int)Math.Round(start.Y + (end.Y - start.Y) * progress));

    public static double ExitOpacity(double progress)
        => 1d - Math.Clamp(progress, 0d, 1d);
}
