using System.Drawing;
using WinShot.History;
using Xunit;

namespace WinShot.Tests;

public class HistoryPreviewLayoutTests
{
    [Fact]
    public void CalculateImageSize_DownscalesLargeImageToWorkAreaLimit()
    {
        var size = HistoryPreviewLayout.CalculateImageSize(
            new Size(4000, 2000),
            new Size(1000, 800));

        Assert.Equal(new Size(816, 416), size);
    }

    [Fact]
    public void CalculateImageSize_KeepsSmallImagesAtNativeSizeWithPadding()
    {
        var size = HistoryPreviewLayout.CalculateImageSize(
            new Size(200, 100),
            new Size(1000, 800));

        Assert.Equal(new Size(216, 116), size);
    }

    [Fact]
    public void CalculateImageSize_UsesMinimumContentSizeForTinyImages()
    {
        var size = HistoryPreviewLayout.CalculateImageSize(
            new Size(1, 1),
            new Size(1000, 800));

        Assert.Equal(new Size(64, 64), size);
    }

    [Fact]
    public void CalculateFallbackSize_IsStable()
    {
        Assert.Equal(new Size(360, 128), HistoryPreviewLayout.FallbackSize);
    }
}
