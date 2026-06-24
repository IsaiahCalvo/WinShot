using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class VideoExportVideoSettingsTests
{
    [Fact]
    public void FromControls_UsesSelectedFrameRate()
    {
        var settings = VideoExportVideoSettings.FromControls(
            sourceWidth: 1920,
            sourceHeight: 1080,
            sourceFrameRate: 59.94,
            resolutionIndex: 0,
            qualityIndex: 0,
            frameRateIndex: 3);

        Assert.Equal(15, settings.FrameRate);
    }

    [Fact]
    public void FromControls_KeepsEvenScaledDimensions()
    {
        var settings = VideoExportVideoSettings.FromControls(
            sourceWidth: 1921,
            sourceHeight: 1081,
            sourceFrameRate: 30,
            resolutionIndex: 1,
            qualityIndex: 0,
            frameRateIndex: 0);

        Assert.Equal(1440u, settings.Width);
        Assert.Equal(810u, settings.Height);
        Assert.Equal(30, settings.FrameRate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(240)]
    [InlineData(double.NaN)]
    public void FromControls_FallsBackForInvalidSourceFrameRate(double sourceFrameRate)
    {
        var settings = VideoExportVideoSettings.FromControls(
            sourceWidth: 1920,
            sourceHeight: 1080,
            sourceFrameRate,
            resolutionIndex: 0,
            qualityIndex: 0,
            frameRateIndex: 0);

        Assert.Equal(30, settings.FrameRate);
    }
}
