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

    /// <summary>
    /// The scrolling flow reuses the plain region selector, so nothing told the user to drag
    /// over the scrollable part rather than the whole window. The instruction is opt-in per
    /// flow: every other selector still opens with the crosshair as the whole affordance.
    /// </summary>
    [Fact]
    public void Instruction_IsOptInAndSurvivesTheSettingsMapping()
    {
        Assert.Null(SelectorOptions.ForRegion(null).Instruction);
        Assert.Null(SelectorOptions.ForAllInOne(new Settings()).Instruction);

        SelectorOptions scrolling = SelectorOptions.ForRegion(new Settings())
            with { Instruction = "Drag over the part of the screen that scrolls" };
        Assert.Equal("Drag over the part of the screen that scrolls", scrolling.Instruction);
    }

    /// <summary>The pill measures the same whatever alpha it is drawn at, so the fade never
    /// changes the rect the repaint asks for.</summary>
    [Fact]
    public void InstructionPill_MeasuresIndependentlyOfItsAlpha()
    {
        Size measured = SelectorChrome.MeasureLabel("Drag over the part of the screen that scrolls", 1d);
        Assert.True(measured.Width > 0 && measured.Height > 0);

        using var bitmap = new Bitmap(600, 200);
        using var g = Graphics.FromImage(bitmap);
        // Every alpha in the ramp draws without throwing, including the fully faded frame
        // that must draw nothing at all.
        foreach (double alpha in new[] { 0d, 0.25d, 0.5d, 1d })
            SelectorChrome.DrawLabel(g, bitmap.Size, "Drag over the part of the screen that scrolls", 20, 20, 1d, alpha);
    }

    [Fact]
    public void IsCovered_IsFalseForTheSelectorsOwnSurfaces()
    {
        var surfaces = new List<IntPtr> { new(10), new(20), new(30) };

        Assert.False(SelectorForeground.IsCovered(new IntPtr(20), surfaces));
        Assert.False(SelectorForeground.IsCovered(IntPtr.Zero, surfaces));
    }

    [Fact]
    public void IsCovered_IsTrueWhenAForeignWindowSitsOverTheOverlay()
    {
        var surfaces = new List<IntPtr> { new(10), new(20) };

        // A crash dialog / always-on-top utility that raised itself after the overlay opened:
        // clicks inside its rect never reach the selector until it is pushed back down.
        Assert.True(SelectorForeground.IsCovered(new IntPtr(99), surfaces));
        Assert.True(SelectorForeground.IsCovered(new IntPtr(99), new List<IntPtr>()));
    }

    [Fact]
    public void CrosshairGuidesAreOnOutOfTheBox()
    {
        // The shipped default, not the settings-is-null fallback: a fresh install shows
        // the guides without the user holding Ctrl.
        SelectorOptions options = SelectorOptions.ForRegion(new Settings());

        Assert.Equal("always", new Settings().CrosshairMode);
        Assert.True(options.IsCrosshairVisible(controlPressed: false));
        Assert.True(options.NeedsCursorFollow);
    }
}
