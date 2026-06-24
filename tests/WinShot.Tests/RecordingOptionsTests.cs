using WinShot.Core;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class RecordingOptionsTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(10, 10)]
    [InlineData(60, 60)]
    [InlineData(90, 60)]
    public void ClampCountdownSeconds_UsesSharedRange(int input, int expected)
    {
        Assert.Equal(expected, RecordingOptions.ClampCountdownSeconds(input));
    }

    [Theory]
    [InlineData("off", "off")]
    [InlineData("top-left", "top-left")]
    [InlineData("top-right", "top-right")]
    [InlineData("bottom-left", "bottom-left")]
    [InlineData("bottom-right", "bottom-right")]
    [InlineData("fullscreen", "fullscreen")]
    [InlineData("", "off")]
    [InlineData("bad", "off")]
    [InlineData(null, "off")]
    public void NormalizeWebcamPosition_FallsBackToOff(string? input, string expected)
    {
        Assert.Equal(expected, RecordingOptions.NormalizeWebcamPosition(input));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 10)]
    [InlineData(22, 22)]
    [InlineData(45, 45)]
    [InlineData(90, 45)]
    public void ClampWebcamSizePercent_UsesSharedRange(int input, int expected)
    {
        Assert.Equal(expected, RecordingOptions.ClampWebcamSizePercent(input));
    }

    [Fact]
    public void WebcamOverlayLayout_CalculatesCornerOverlaySize()
    {
        bool created = RecordingWebcamOverlayLayout.TryCreate(
            new SD.Rectangle(0, 0, 1000, 800),
            "bottom-right",
            30,
            out var layout);

        Assert.True(created);
        Assert.Equal("bottom-right", layout.Position);
        Assert.Equal(30, layout.SizePercent);
        Assert.Equal(300, layout.Width);
        Assert.Equal(225, layout.Height);
        Assert.Equal(16, layout.OffsetPx);
    }

    [Fact]
    public void WebcamOverlayLayout_SkipsOffPosition()
    {
        bool created = RecordingWebcamOverlayLayout.TryCreate(
            new SD.Rectangle(0, 0, 1000, 800),
            "off",
            30,
            out _);

        Assert.False(created);
    }

    [Fact]
    public void WebcamOverlayLayout_KeepsOverlayInsideSmallRegion()
    {
        bool created = RecordingWebcamOverlayLayout.TryCreate(
            new SD.Rectangle(0, 0, 120, 80),
            "top-left",
            90,
            out var layout);

        Assert.True(created);
        Assert.Equal(45, layout.SizePercent);
        Assert.True(layout.Width + (layout.OffsetPx * 2) <= 120);
        Assert.True(layout.Height + (layout.OffsetPx * 2) <= 80);
    }

    [Fact]
    public void WebcamOverlayLayout_FullscreenFillsRecordingRegion()
    {
        bool created = RecordingWebcamOverlayLayout.TryCreate(
            new SD.Rectangle(10, 20, 1280, 720),
            "fullscreen",
            22,
            out var layout);

        Assert.True(created);
        Assert.Equal("fullscreen", layout.Position);
        Assert.Equal(1280, layout.Width);
        Assert.Equal(720, layout.Height);
        Assert.Equal(0, layout.OffsetPx);
        Assert.True(layout.IsFullscreen);
    }
}
