using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class HiDpiDownscaleTests
{
    [Theory]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(1920, 1080, 1.01)]
    public void TryGetTargetSize_ReturnsFalseAtNormalScale(int width, int height, double scale)
    {
        Assert.False(HiDpiDownscale.TryGetTargetSize(width, height, scale, out _, out _));
    }

    [Fact]
    public void TryGetTargetSize_ScalesDownAboveNormalScale()
    {
        bool shouldScale = HiDpiDownscale.TryGetTargetSize(3000, 1500, 1.5, out int width, out int height);

        Assert.True(shouldScale);
        Assert.Equal(2000, width);
        Assert.Equal(1000, height);
    }

    [Fact]
    public void TryGetTargetSize_NeverReturnsZeroDimensions()
    {
        bool shouldScale = HiDpiDownscale.TryGetTargetSize(2, 1, 2.0, out int width, out int height);

        Assert.True(shouldScale);
        Assert.Equal(1, width);
        Assert.Equal(1, height);
    }
}
