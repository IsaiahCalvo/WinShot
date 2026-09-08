using System.Windows;
using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class EditorGrowLayoutTests
{
    private static readonly Size Desired = new(1200, 800);

    [Fact]
    public void Target_CentresTheWindowInTheWorkArea()
    {
        SD.Rectangle to = EditorGrowLayout.Target(new SD.Rectangle(0, 0, 1920, 1040), Desired, 1);

        Assert.Equal(new SD.Rectangle(360, 120, 1200, 800), to);
    }

    [Fact]
    public void Target_CapsTheWindowAtNinetyPercentOfEachAxis()
    {
        // Asked for more than the display has: the editor must not open edge to edge,
        // or the grow tween has nowhere to land and the window reads as maximised.
        SD.Rectangle to = EditorGrowLayout.Target(new SD.Rectangle(0, 0, 1000, 800), new Size(4000, 4000), 1);

        Assert.Equal(900, to.Width);
        Assert.Equal(720, to.Height);
        Assert.Equal(50, to.X);
        Assert.Equal(40, to.Y);
    }

    [Fact]
    public void Target_CapsOnlyTheAxisThatOverflows()
    {
        SD.Rectangle to = EditorGrowLayout.Target(new SD.Rectangle(0, 0, 1000, 2000), new Size(4000, 600), 1);

        Assert.Equal(900, to.Width);
        Assert.Equal(600, to.Height);
    }

    [Fact]
    public void Target_OpensOnAMonitorAboveTheLaptop()
    {
        // Isaiah runs two monitors ABOVE the laptop, so the capture display's work area
        // has a negative Y. Centring must land inside that monitor, not on the primary.
        SD.Rectangle to = EditorGrowLayout.Target(new SD.Rectangle(0, -1080, 1920, 1040), Desired, 1);

        Assert.Equal(360, to.X);
        Assert.Equal(-960, to.Y);
        Assert.True(to.Bottom <= -1080 + 1040);
    }

    [Fact]
    public void Target_OpensOnAMonitorLeftOfThePrimary()
    {
        SD.Rectangle to = EditorGrowLayout.Target(new SD.Rectangle(-1920, 0, 1920, 1040), Desired, 1);

        Assert.Equal(-1560, to.X);
        Assert.Equal(120, to.Y);
    }

    [Fact]
    public void Target_SizesForTheDESTINATIONMonitorsDpi()
    {
        // The whole point of the move is that the destination is a different monitor. A
        // 1200x800 DIP window on a 150 % display is 1800x1200 device pixels; sizing it with
        // the primary's scale (the bug) landed it at two thirds of that, off-centre.
        SD.Rectangle to = EditorGrowLayout.Target(new SD.Rectangle(0, 0, 3840, 2000), Desired, 1.5);

        Assert.Equal(1800, to.Width);
        Assert.Equal(1200, to.Height);
        Assert.Equal(1020, to.X);
        Assert.Equal(400, to.Y);
    }

    [Fact]
    public void Target_StillCapsAfterScaling()
    {
        SD.Rectangle to = EditorGrowLayout.Target(new SD.Rectangle(0, 0, 1920, 1040), Desired, 2);

        Assert.Equal(1728, to.Width);   // 90 % of 1920, not 2400
        Assert.Equal(936, to.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Target_TreatsAnUnreadableScaleAsOneHundredPercent(double scale)
    {
        SD.Rectangle to = EditorGrowLayout.Target(new SD.Rectangle(0, 0, 1920, 1040), Desired, scale);

        Assert.Equal(1200, to.Width);
        Assert.Equal(800, to.Height);
    }

    [Fact]
    public void Origin_PassesARealCardRectThrough()
    {
        SD.Rectangle from = EditorGrowLayout.Origin(new SD.Rectangle(1600, 900, 244, 158));

        Assert.Equal(new SD.Rectangle(1600, 900, 244, 158), from);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 158)]
    [InlineData(244, 0)]
    public void Origin_FloorsADegenerateRectToOnePixel(int width, int height)
    {
        // The size is written every frame and a zero-width window is not a window, so a
        // card that reported a collapsed rect must still yield a runnable tween.
        SD.Rectangle from = EditorGrowLayout.Origin(new SD.Rectangle(10, 20, width, height));

        Assert.True(from.Width >= 1);
        Assert.True(from.Height >= 1);
        Assert.Equal(10, from.X);
        Assert.Equal(20, from.Y);
    }

    [Fact]
    public void Lerp_StartsAtTheCardsOwnRect()
    {
        // Frame zero is the card, at the card's SIZE. This is what the window's
        // MinWidth/MinHeight used to clamp away, leaving a full-size editor that only slid.
        var from = new SD.Rectangle(1600, 900, 244, 158);
        var to = new SD.Rectangle(360, 120, 1200, 800);

        Assert.Equal(from, EditorGrowLayout.Lerp(from, to, 0));
    }

    [Fact]
    public void Lerp_EndsExactlyOnTheTarget()
    {
        var from = new SD.Rectangle(1600, 900, 244, 158);
        var to = new SD.Rectangle(360, 120, 1200, 800);

        Assert.Equal(to, EditorGrowLayout.Lerp(from, to, 1));
        Assert.Equal(to, EditorGrowLayout.Lerp(from, to, 4));      // clamped
        Assert.Equal(to, EditorGrowLayout.Lerp(from, to, double.NaN));
    }

    [Fact]
    public void Lerp_GrowsTheSizeAsWellAsThePosition()
    {
        var from = new SD.Rectangle(1600, 900, 244, 158);
        var to = new SD.Rectangle(360, 120, 1200, 800);

        SD.Rectangle half = EditorGrowLayout.Lerp(from, to, 0.5);

        Assert.Equal(722, half.Width);
        Assert.Equal(479, half.Height);
        Assert.Equal(980, half.X);
        Assert.Equal(510, half.Y);
    }

    [Fact]
    public void Lerp_NeverProducesAZeroSizedWindow()
    {
        SD.Rectangle mid = EditorGrowLayout.Lerp(
            new SD.Rectangle(0, 0, 1, 1), new SD.Rectangle(0, 0, 1, 1), 0.5);

        Assert.True(mid.Width >= 1);
        Assert.True(mid.Height >= 1);
    }
}
