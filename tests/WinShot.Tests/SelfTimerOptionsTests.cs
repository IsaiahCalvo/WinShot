using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

public class SelfTimerOptionsTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(60, 60)]
    [InlineData(90, 60)]
    public void ClampDelaySeconds_UsesSharedSettingsRange(int input, int expected)
    {
        Assert.Equal(expected, SelfTimerOptions.ClampDelaySeconds(input));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ShouldRunDelay_SkipsNonPositiveDirectCalls(int input, bool expected)
    {
        Assert.Equal(expected, SelfTimerOptions.ShouldRunDelay(input));
    }
}
