namespace WinShot.Scrolling;

public static class ScrollCaptureCommand
{
    /// <summary>Preset direction for a capture command; null lets the capture auto-detect.</summary>
    public static ScrollDirection? DirectionForCommand(string command) =>
        command.Equals("scroll-horizontal", StringComparison.OrdinalIgnoreCase) ? ScrollDirection.Horizontal
        : command.Equals("scroll-up", StringComparison.OrdinalIgnoreCase) ? ScrollDirection.Vertical
        : null;

    /// <summary>
    /// Whether the command captures upward -- for a chat thread or a log you are already at
    /// the bottom of, where scrolling to the top first is the whole cost of the capture.
    /// Reverse always presets the vertical axis: there is no upward horizontal.
    /// </summary>
    public static bool ReverseForCommand(string command) =>
        command.Equals("scroll-up", StringComparison.OrdinalIgnoreCase);
}
