namespace WinShot.Scrolling;

public static class ScrollCaptureCommand
{
    /// <summary>Preset direction for a capture command; null lets the capture auto-detect.</summary>
    public static ScrollDirection? DirectionForCommand(string command) =>
        command.Equals("scroll-horizontal", StringComparison.OrdinalIgnoreCase)
            ? ScrollDirection.Horizontal
            : null;
}
