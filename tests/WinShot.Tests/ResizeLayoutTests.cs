using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class ResizeLayoutTests
{
    [Fact]
    public void FromWidth_UpdatesHeightWhenRatioLocked()
    {
        var layout = ResizeLayout.FromWidth(
            originalWidth: 400,
            originalHeight: 300,
            currentHeight: 123,
            requestedWidth: 200,
            lockRatio: true);

        Assert.Equal(200, layout.Width);
        Assert.Equal(150, layout.Height);
        Assert.Equal(50, layout.Percent);
    }

    [Fact]
    public void FromWidth_KeepsCurrentHeightWhenRatioUnlocked()
    {
        var layout = ResizeLayout.FromWidth(
            originalWidth: 400,
            originalHeight: 300,
            currentHeight: 123,
            requestedWidth: 200,
            lockRatio: false);

        Assert.Equal(200, layout.Width);
        Assert.Equal(123, layout.Height);
        Assert.Equal(50, layout.Percent);
    }

    [Fact]
    public void FromHeight_UpdatesWidthWhenRatioLocked()
    {
        var layout = ResizeLayout.FromHeight(
            originalWidth: 400,
            originalHeight: 300,
            currentWidth: 123,
            requestedHeight: 150,
            lockRatio: true);

        Assert.Equal(200, layout.Width);
        Assert.Equal(150, layout.Height);
        Assert.Equal(50, layout.Percent);
    }

    [Fact]
    public void FromHeight_KeepsCurrentWidthAndUpdatesPercentWhenRatioUnlocked()
    {
        var layout = ResizeLayout.FromHeight(
            originalWidth: 400,
            originalHeight: 300,
            currentWidth: 123,
            requestedHeight: 150,
            lockRatio: false);

        Assert.Equal(123, layout.Width);
        Assert.Equal(150, layout.Height);
        Assert.Equal(50, layout.Percent);
    }

    [Fact]
    public void FromPercent_RecomputesBothDimensions()
    {
        var layout = ResizeLayout.FromPercent(
            originalWidth: 400,
            originalHeight: 300,
            percent: 33.3);

        Assert.Equal(133, layout.Width);
        Assert.Equal(100, layout.Height);
        Assert.Equal(33, layout.Percent);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(20000, 20000, true)]
    [InlineData(0, 100, false)]
    [InlineData(100, 0, false)]
    [InlineData(20001, 100, false)]
    [InlineData(100, 20001, false)]
    public void IsValid_EnforcesResizeBounds(int width, int height, bool expected)
    {
        Assert.Equal(expected, ResizeLayout.IsValid(width, height));
    }
}
