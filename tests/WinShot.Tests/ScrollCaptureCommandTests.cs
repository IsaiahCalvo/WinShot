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
}
