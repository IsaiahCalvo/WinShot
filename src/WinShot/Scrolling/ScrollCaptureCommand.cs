namespace WinShot.Scrolling;

public static class ScrollCaptureCommand
{
    public static ScrollCaptureChoice? ChoiceForCommand(string command) =>
        command.Equals("scroll-horizontal", StringComparison.OrdinalIgnoreCase)
            ? new ScrollCaptureChoice(ScrollCaptureMode.Auto, ScrollDirection.Horizontal)
            : null;
}
