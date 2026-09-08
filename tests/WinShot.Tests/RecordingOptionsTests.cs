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

    /// <summary>
    /// Every settings.json written before WebcamOverlayCorner existed has no such key, so it
    /// reads back as the C# initializer ("bottom-right") whatever corner the user actually
    /// picked. Switching the overlay off then banked that initializer and destroyed the real
    /// choice — silently, unrecoverably, on the first off-toggle after upgrading.
    /// </summary>
    [Fact]
    public void SwitchingTheWebcamOffBanksTheCornerTheUserHadBeforeTheCornerKeyExisted()
    {
        var legacy = new Settings
        {
            WebcamOverlayPosition = "top-left",
            // Not written by the old build: this is the property's own default.
            WebcamOverlayCorner = RecordingOptions.WebcamDefaultCorner,
        };

        legacy.SetWebcamOverlayPosition(RecordingOptions.WebcamOff);

        Assert.Equal(RecordingOptions.WebcamOff, legacy.WebcamOverlayPosition);
        Assert.Equal("top-left", legacy.WebcamOverlayCorner);
        Assert.Equal(
            "top-left",
            RecordingController.WebcamPositionFor(
                true, legacy.WebcamOverlayPosition, legacy.WebcamOverlayCorner));
    }

    /// <summary>The corner already banked is not overwritten by a second off-toggle.</summary>
    [Fact]
    public void SwitchingTheWebcamOffTwiceKeepsTheBankedCorner()
    {
        var s = new Settings();
        s.SetWebcamOverlayPosition("top-right");
        s.SetWebcamOverlayPosition(RecordingOptions.WebcamOff);
        s.SetWebcamOverlayPosition(RecordingOptions.WebcamOff);

        Assert.Equal("top-right", s.WebcamOverlayCorner);
    }

    /// <summary>A settings.json carrying an explicit 0 is an unset key or a zero-initialising
    /// migration, not a request for the worst H.264 the encoder can make.</summary>
    [Fact]
    public void ANonPositiveStoredVideoQualityFallsBackToTheDefault()
    {
        Assert.Equal(RecordingOptions.DefaultVideoQuality, RecordingOptions.ClampVideoQuality(0));
        Assert.Equal(RecordingOptions.DefaultVideoQuality, RecordingOptions.ClampVideoQuality(-40));
        Assert.Equal(1, RecordingOptions.ClampVideoQuality(1));
        Assert.Equal(100, RecordingOptions.ClampVideoQuality(400));
    }
}
