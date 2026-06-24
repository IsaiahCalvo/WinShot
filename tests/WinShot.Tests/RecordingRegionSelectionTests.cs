using WinShot.Recording;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class RecordingRegionSelectionTests
{
    [Fact]
    public void FromVirtualSelection_OffsetsByVirtualScreenAndRoundsToEvenSize()
    {
        var region = RecordingRegionSelection.FromVirtualSelection(
            new SD.Rectangle(10, 20, 101, 79),
            new SD.Rectangle(-200, -100, 2400, 1400));

        Assert.Equal(new SD.Rectangle(-190, -80, 100, 78), region.ScreenRect);
        Assert.True(region.IsUsable);
    }

    [Fact]
    public void FromDisplay_UsesDisplayBoundsAndRoundsToEvenSize()
    {
        var region = RecordingRegionSelection.FromDisplay(
            new SD.Rectangle(1920, 0, 1367, 769));

        Assert.Equal(new SD.Rectangle(1920, 0, 1366, 768), region.ScreenRect);
        Assert.True(region.IsUsable);
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(50, 1)]
    [InlineData(0, 50)]
    [InlineData(50, 0)]
    public void FromDisplay_MarksTinyRegionsUnusable(int width, int height)
    {
        var region = RecordingRegionSelection.FromDisplay(
            new SD.Rectangle(0, 0, width, height));

        Assert.False(region.IsUsable);
    }
}
