using System.Drawing;
using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

public class MeasureFormatTests
{
    [Fact]
    public void At96DpiThereIsNothingToConvertAndTheSecondLineIsNotDrawn()
    {
        Assert.Equal("296", MeasureFormat.SpanLabel(296, 96));
        Assert.Equal("296 × 118\n~2.51 : 1", MeasureFormat.CrossLabel(new Size(296, 118), 96));
    }

    [Fact]
    public void At150PercentTheDipLineIsTheOneIsaiahActuallyReads()
    {
        // The example from the spec, on the monitor the spec was written for.
        Assert.Equal("296\n197 dip", MeasureFormat.SpanLabel(296, 144));
        Assert.StartsWith("296 × 118\n197 × 79 dip", MeasureFormat.CrossLabel(new Size(296, 118), 144));
    }

    [Theory]
    [InlineData(1920, 1080, "16 : 9")]
    [InlineData(300, 200, "3 : 2")]
    [InlineData(512, 512, "1 : 1")]
    [InlineData(1080, 1920, "9 : 16")]
    public void AnExactReductionNeedsNoTilde(int w, int h, string expected) =>
        Assert.Equal(expected, MeasureFormat.Ratio(w, h));

    [Fact]
    public void ARatioNearACommonOneSnapsToItAndSaysSoWithATilde()
    {
        // 1.5541 — the number the video shows, which is 3:2 to anyone but a calculator.
        Assert.Equal("~3 : 2", MeasureFormat.Ratio(1554, 1000));
        Assert.Equal("~16 : 9", MeasureFormat.Ratio(1777, 1000));
    }

    [Fact]
    public void ARatioNearNothingFallsBackToADecimalRatherThanLying()
    {
        Assert.Equal("~2.13 : 1", MeasureFormat.Ratio(2131, 1000));
        Assert.Equal("~1 : 2.13", MeasureFormat.Ratio(1000, 2131));
    }

    [Fact]
    public void ADegenerateSizeHasNoRatio()
    {
        Assert.Equal(string.Empty, MeasureFormat.Ratio(0, 10));
        Assert.Equal(string.Empty, MeasureFormat.Ratio(10, -1));
    }

    [Fact]
    public void DipConversionIsTheWindowsDefinitionAndSurvivesAZeroDpi()
    {
        Assert.Equal(16, MeasureFormat.ToDip(24, 144), 3);
        Assert.Equal(24, MeasureFormat.ToDip(24, 96), 3);
        Assert.Equal(24, MeasureFormat.ToDip(24, 0), 3);
    }
}
