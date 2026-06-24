using WinShot.Scrolling;
using Xunit;

namespace WinShot.Tests;

public class ScrollCaptureCommandTests
{
    [Fact]
    public void ChoiceForCommand_ReturnsHorizontalAutoChoice()
    {
        var choice = ScrollCaptureCommand.ChoiceForCommand("scroll-horizontal");

        Assert.Equal(ScrollCaptureMode.Auto, choice?.Mode);
        Assert.Equal(ScrollDirection.Horizontal, choice?.Direction);
    }

    [Fact]
    public void ChoiceForCommand_ReturnsNullForChooserCommand()
    {
        Assert.Null(ScrollCaptureCommand.ChoiceForCommand("scrolling"));
    }
}
