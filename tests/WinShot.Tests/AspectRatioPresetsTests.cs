using WinShot.Capture;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The size bar's ratio list. Before it, the only aspect control was a lock over whatever
/// happened to be typed in the width and height fields, so framing 16:9 meant knowing two
/// numbers first.
/// </summary>
public class AspectRatioPresetsTests
{
    [Fact]
    public void FreeformIsFirstAndConstrainsNothing()
    {
        AspectRatioPreset freeform = AspectRatioPresets.All[0];

        Assert.Equal("Freeform", freeform.Label);
        Assert.Null(freeform.Ratio);
        Assert.Equal(string.Empty, freeform.Badge);
    }

    [Fact]
    public void TheListIsFreeformPlusTheElevenReferenceRatios()
    {
        Assert.Equal(12, AspectRatioPresets.All.Count);
        Assert.Equal(
            new[] { "Freeform", "1:1", "5:4", "4:3", "7:5", "3:2", "16:10", "16:9", "1.85:1", "2.35:1", "2:3", "9:16" },
            AspectRatioPresets.All.Select(p => p.Label));
    }

    [Fact]
    public void EveryPresetExceptFreeformCarriesARatioAndABadge()
    {
        foreach (AspectRatioPreset preset in AspectRatioPresets.All.Skip(1))
        {
            Assert.NotNull(preset.Ratio);
            Assert.True(preset.Ratio > 0, preset.Label);
            Assert.False(string.IsNullOrWhiteSpace(preset.Badge), preset.Label);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(12)]
    [InlineData(9999)]
    public void AnOutOfRangeIndexFallsBackToFreeformRatherThanLockingADrag(int index)
    {
        Assert.Null(AspectRatioPresets.At(index).Ratio);
    }

    [Fact]
    public void HeightForRoundsToTheNearestPixelAndNeverReturnsZero()
    {
        Assert.Equal(1080, AspectRatioPresets.HeightFor(1920, 16d / 9d));
        Assert.Equal(600, AspectRatioPresets.HeightFor(800, 4d / 3d));
        Assert.Equal(1, AspectRatioPresets.HeightFor(1, 16d / 9d));
    }

    [Fact]
    public void ThePresetRatioIsWhatConstrainsTheLiveDrag()
    {
        double ratio = AspectRatioPresets.All.Single(p => p.Label == "16:9").Ratio!.Value;

        // AllInOneDragLayout already owned and tested the constraint; this is the wiring
        // that finally gives it something other than two typed numbers to work from.
        SD.Rectangle rect = AllInOneDragLayout.CreatePixelRectangle(
            new SD.Point(0, 0), new SD.Point(1600, 200), ratio);

        Assert.Equal(1600, rect.Width);
        Assert.Equal(900, rect.Height);
    }
}
