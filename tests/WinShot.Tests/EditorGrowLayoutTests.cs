using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class EditorGrowLayoutTests
{
    [Fact]
    public void Target_CentresTheWindowInTheWorkArea()
    {
        Rect to = EditorGrowLayout.Target(new Rect(0, 0, 1920, 1040), new Size(1200, 800));

        Assert.Equal(new Rect(360, 120, 1200, 800), to);
    }

    [Fact]
    public void Target_CapsTheWindowAtNinetyPercentOfEachAxis()
    {
        // Asked for more than the display has: the editor must not open edge to edge,
        // or the grow tween has nowhere to land and the window reads as maximised.
        Rect to = EditorGrowLayout.Target(new Rect(0, 0, 1000, 800), new Size(4000, 4000));

        Assert.Equal(900, to.Width);
        Assert.Equal(720, to.Height);
        Assert.Equal(50, to.X);
        Assert.Equal(40, to.Y);
    }

    [Fact]
    public void Target_CapsOnlyTheAxisThatOverflows()
    {
        Rect to = EditorGrowLayout.Target(new Rect(0, 0, 1000, 2000), new Size(4000, 600));

        Assert.Equal(900, to.Width);
        Assert.Equal(600, to.Height);
    }

    [Fact]
    public void Target_OpensOnAMonitorAboveTheLaptop()
    {
        // Isaiah runs two monitors ABOVE the laptop, so the capture display's work area
        // has a negative Y. Centring must land inside that monitor, not on the primary.
        Rect to = EditorGrowLayout.Target(new Rect(0, -1080, 1920, 1040), new Size(1200, 800));

        Assert.Equal(360, to.X);
        Assert.Equal(-960, to.Y);
        Assert.True(to.Bottom <= -1080 + 1040);
    }

    [Fact]
    public void Target_OpensOnAMonitorLeftOfThePrimary()
    {
        Rect to = EditorGrowLayout.Target(new Rect(-1920, 0, 1920, 1040), new Size(1200, 800));

        Assert.Equal(-1560, to.X);
        Assert.Equal(120, to.Y);
    }

    [Fact]
    public void Origin_PassesARealCardRectThrough()
    {
        Rect from = EditorGrowLayout.Origin(new Rect(1600, 900, 244, 158));

        Assert.Equal(new Rect(1600, 900, 244, 158), from);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 158)]
    [InlineData(244, 0)]
    public void Origin_FloorsADegenerateRectToOnePixel(double width, double height)
    {
        // Width/Height are assigned every frame and WPF throws on a negative one, so a
        // card that reported a collapsed rect must still yield a runnable tween.
        Rect from = EditorGrowLayout.Origin(new Rect(10, 20, width, height));

        Assert.True(from.Width >= 1);
        Assert.True(from.Height >= 1);
        Assert.Equal(10, from.X);
        Assert.Equal(20, from.Y);
    }
}
