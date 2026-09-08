using WinShot.Scrolling;
using Xunit;

namespace WinShot.Tests;

public class ScrollCaptureCommandTests
{
    [Fact]
    public void DirectionForCommand_ReturnsHorizontalPreset()
    {
        Assert.Equal(ScrollDirection.Horizontal, ScrollCaptureCommand.DirectionForCommand("scroll-horizontal"));
    }

    [Fact]
    public void DirectionForCommand_ReturnsNullForAutoDetect()
    {
        Assert.Null(ScrollCaptureCommand.DirectionForCommand("scrolling"));
    }

    /// <summary>Upward capture always presets the vertical axis -- there is no upward
    /// horizontal -- and no other command turns it on.</summary>
    [Fact]
    public void ScrollUp_PresetsVerticalAndReverses()
    {
        Assert.Equal(ScrollDirection.Vertical, ScrollCaptureCommand.DirectionForCommand("scroll-up"));
        Assert.True(ScrollCaptureCommand.ReverseForCommand("scroll-up"));
        Assert.True(ScrollCaptureCommand.ReverseForCommand("SCROLL-UP"));
    }

    [Theory]
    [InlineData("scrolling")]
    [InlineData("scroll-horizontal")]
    [InlineData("capture-area")]
    public void EveryOtherCommand_CapturesDownward(string command)
        => Assert.False(ScrollCaptureCommand.ReverseForCommand(command));
}
