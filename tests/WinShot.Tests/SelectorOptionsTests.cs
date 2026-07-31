using System.Drawing;
using WinShot.Capture;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class SelectorOptionsTests
{
    [Fact]
    public void Region_MapsPersistedSelectionSettings()
    {
        var settings = new Settings
        {
            FreezeScreen = false,
            ShowCrosshair = false,
            ShowMagnifier = false,
            CrosshairMode = "always",
        };

        SelectorOptions options = SelectorOptions.ForRegion(settings);

        Assert.False(options.FreezeScreen);
        Assert.False(options.ShowCrosshair);
        Assert.False(options.ShowMagnifier);
        Assert.False(options.RememberLastRegion);
        Assert.False(options.IsCrosshairVisible(controlPressed: true));
        Assert.False(options.NeedsCursorFollow);
    }

    [Fact]
    public void Region_MissingCrosshairModeFallsBackToAlways()
    {
        var settings = new Settings
        {
            ShowCrosshair = true,
            CrosshairMode = null!,
        };

        SelectorOptions options = SelectorOptions.ForRegion(settings);

        Assert.True(options.IsCrosshairVisible(controlPressed: false));
    }

    [Fact]
    public void AllInOne_MapsPersistedSelectionSettingsAndRememberLast()
    {
        var settings = new Settings
        {
            FreezeScreen = true,
            ShowCrosshair = true,
            ShowMagnifier = true,
            CrosshairMode = "command",
            AllInOneRememberLast = false,
        };

        SelectorOptions options = SelectorOptions.ForAllInOne(settings);

        Assert.True(options.FreezeScreen);
        Assert.True(options.ShowCrosshair);
        Assert.True(options.ShowMagnifier);
        Assert.False(options.RememberLastRegion);
        Assert.False(options.IsCrosshairVisible(controlPressed: false));
        Assert.True(options.IsCrosshairVisible(controlPressed: true));
        Assert.True(options.NeedsCursorFollow);
    }

    [Fact]
    public void AllInOne_RememberLastFalseDoesNotRestoreSavedRegion()
    {
        SelectorOptions options = SelectorOptions.ForAllInOne(new Settings
        {
            AllInOneRememberLast = false,
        });

        bool restored = options.TryGetRememberedRegion(
            "-1800,100,640,480",
            new Rectangle(-1920, 0, 3840, 1080),
            out Rectangle region);

        Assert.False(restored);
        Assert.Equal(default, region);
    }

    [Fact]
    public void AllInOne_RememberLastTrueRestoresPhysicalRegionAsVirtualPixels()
    {
        SelectorOptions options = SelectorOptions.ForAllInOne(new Settings
        {
            AllInOneRememberLast = true,
        });

        bool restored = options.TryGetRememberedRegion(
            "-1800,100,640,480",
            new Rectangle(-1920, 0, 3840, 1080),
            out Rectangle region);

        Assert.True(restored);
        Assert.Equal(new Rectangle(120, 100, 640, 480), region);
    }

    [Theory]
    [InlineData(0u, 0u, false)]
    [InlineData(42u, 42u, false)]
    [InlineData(42u, 84u, true)]
    public void ForegroundPolicy_AttachesOnlyToAnotherValidThread(
        uint currentThread,
        uint foregroundThread,
        bool expected)
    {
        Assert.Equal(expected, SelectorForeground.ShouldAttachInput(currentThread, foregroundThread));
    }
}
