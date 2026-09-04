using System.Linq;
using WinShot.Core;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The two-card recording HUD that replaced the centred options modal: its geometry (paint and
/// hit-test share it, so they cannot disagree), its aspect snapping, and the toggle state it
/// carries back to the recorder.
/// </summary>
public class RecordingHudTests
{
    private static readonly SD.Rectangle Monitor = new(0, 0, 1920, 1080);

    // ---- geometry ------------------------------------------------------------------

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Cells_TileTheSettingsCardWithoutGapsOrOverlap(double scale)
    {
        var region = new SD.Rectangle(400, 200, 800, 400);

        RecordingHudGeometry hud = RecordingHudLayout.Build(region, Monitor, scale);

        var settingsCells = hud.Cells
            .Where(c => c.Cell is not (RecordingHudCell.RecordGif or RecordingHudCell.RecordVideo))
            .ToList();
        Assert.Equal(7, settingsCells.Count);
        for (int i = 1; i < settingsCells.Count; i++)
            Assert.Equal(settingsCells[i - 1].Bounds.Right, settingsCells[i].Bounds.Left);
        Assert.All(settingsCells, c => Assert.Equal(hud.SettingsCard.Height, c.Bounds.Height));
        Assert.All(settingsCells, c => Assert.True(hud.SettingsCard.Contains(c.Bounds)));
    }

    [Fact]
    public void ActionRows_StackInsideTheDetachedCard()
    {
        RecordingHudGeometry hud = RecordingHudLayout.Build(new SD.Rectangle(400, 200, 800, 400), Monitor, 1.0);

        SD.Rectangle gif = hud.CellBounds(RecordingHudCell.RecordGif);
        SD.Rectangle video = hud.CellBounds(RecordingHudCell.RecordVideo);
        Assert.True(hud.ActionCard.Contains(gif));
        Assert.True(hud.ActionCard.Contains(video));
        Assert.Equal(gif.Bottom, video.Top);
        // Detached: there is real space between the two cards.
        Assert.True(hud.ActionCard.Top > hud.SettingsCard.Bottom);
    }

    [Fact]
    public void HitTest_AgreesWithTheCellItDrew()
    {
        RecordingHudGeometry hud = RecordingHudLayout.Build(new SD.Rectangle(400, 200, 800, 400), Monitor, 1.0);

        foreach ((RecordingHudCell cell, SD.Rectangle bounds) in hud.Cells)
        {
            var centre = new SD.Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
            Assert.Equal(cell, hud.HitTest(centre));
        }

        Assert.Null(hud.HitTest(new SD.Point(Monitor.Right - 1, Monitor.Bottom - 1)));
    }

    [Fact]
    public void Placement_SitsBelowTheRegion()
    {
        var region = new SD.Rectangle(400, 100, 800, 400);

        RecordingHudGeometry hud = RecordingHudLayout.Build(region, Monitor, 1.0);

        Assert.True(hud.SettingsCard.Top > region.Bottom);
        Assert.Equal(
            region.Left + ((region.Width - hud.SettingsCard.Width) / 2),
            hud.SettingsCard.Left);
    }

    [Fact]
    public void Placement_FlipsAboveWhenThereIsNoRoomBelow()
    {
        var region = new SD.Rectangle(400, 400, 800, 660);

        RecordingHudGeometry hud = RecordingHudLayout.Build(region, Monitor, 1.0);

        Assert.True(hud.Bounds.Bottom < region.Top);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080)]           // a full-screen region: nowhere outside to go
    [InlineData(1850, 20, 60, 40)]           // hard against the right edge
    [InlineData(0, 0, 40, 30)]               // hard against the top-left corner
    public void Placement_AlwaysLandsOnTheMonitor(int x, int y, int width, int height)
    {
        RecordingHudGeometry hud = RecordingHudLayout.Build(new SD.Rectangle(x, y, width, height), Monitor, 1.0);

        Assert.True(Monitor.Contains(hud.Bounds), hud.Bounds.ToString());
    }

    [Fact]
    public void Placement_WorksOnANegativeYMonitor()
    {
        var monitor = new SD.Rectangle(-1920, -1080, 1920, 1080);
        var region = new SD.Rectangle(-1500, -900, 600, 300);

        RecordingHudGeometry hud = RecordingHudLayout.Build(region, monitor, 1.0);

        Assert.True(monitor.Contains(hud.Bounds));
    }

    [Fact]
    public void AspectMenu_OpensOverTheAspectCellAndStaysOnScreen()
    {
        RecordingHudGeometry hud = RecordingHudLayout.Build(new SD.Rectangle(400, 300, 800, 300), Monitor, 1.0);
        SD.Rectangle aspect = hud.CellBounds(RecordingHudCell.Aspect);

        var (card, rows) = RecordingHudLayout.AspectMenu(
            aspect, Monitor, 1.0, RecordingAspectPresets.All.Count);

        Assert.Equal(RecordingAspectPresets.All.Count, rows.Count);
        Assert.True(Monitor.Contains(card));
        Assert.All(rows, r => Assert.True(card.Contains(r)));
        for (int i = 1; i < rows.Count; i++)
            Assert.Equal(rows[i - 1].Bottom, rows[i].Top);
    }

    // ---- aspect presets ------------------------------------------------------------

    [Fact]
    public void Presets_StartFreeformAndCoverLandscapeAndPortrait()
    {
        Assert.Null(RecordingAspectPresets.All[RecordingAspectPresets.FreeformIndex].Ratio);
        Assert.Contains(RecordingAspectPresets.All, p => p.Label == "16:9");
        Assert.Contains(RecordingAspectPresets.All, p => p.Label == "9:16");
        Assert.Contains(RecordingAspectPresets.All, p => p.Label == "2.35:1");
        Assert.All(RecordingAspectPresets.All.Skip(1), p => Assert.True(p.Ratio > 0));
    }

    [Fact]
    public void Apply_CorrectsTheRegionToTheRatioAboutItsCentre()
    {
        var region = new SD.Rectangle(400, 300, 800, 800);
        var bounds = new SD.Rectangle(0, 0, 1920, 1080);

        SD.Rectangle corrected = RecordingAspectPresets.Apply(region, 16.0 / 9, bounds);

        Assert.Equal(800, corrected.Width);
        Assert.Equal(450, corrected.Height);
        Assert.Equal(
            region.Left + (region.Width / 2),
            corrected.Left + (corrected.Width / 2));
    }

    [Fact]
    public void Apply_KeepsTheHeightWhenTheWidthDerivedOneWouldNotFit()
    {
        // A wide selection asked to become 9:16 must end up tall and on screen, not clipped.
        var region = new SD.Rectangle(100, 100, 1600, 400);
        var bounds = new SD.Rectangle(0, 0, 1920, 1080);

        SD.Rectangle corrected = RecordingAspectPresets.Apply(region, 9.0 / 16, bounds);

        Assert.True(bounds.Contains(corrected), corrected.ToString());
        Assert.True(corrected.Height > corrected.Width);
        Assert.Equal(9.0 / 16, corrected.Width / (double)corrected.Height, 1);
    }

    [Fact]
    public void Apply_AlwaysStaysInsideTheBounds()
    {
        var bounds = new SD.Rectangle(0, 0, 1920, 1080);
        foreach (RecordingAspectPreset preset in RecordingAspectPresets.All)
        {
            if (preset.Ratio is not double ratio)
                continue;
            foreach (var region in new[]
            {
                new SD.Rectangle(0, 0, 1920, 1080),
                new SD.Rectangle(1900, 1060, 20, 20),
                new SD.Rectangle(10, 10, 4, 4),
            })
            {
                SD.Rectangle corrected = RecordingAspectPresets.Apply(region, ratio, bounds);
                Assert.True(bounds.Contains(corrected), $"{preset.Label} on {region} gave {corrected}");
                Assert.True(corrected.Width >= 2 && corrected.Height >= 2);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Apply_LeavesTheRegionAloneForANonsenseRatio(double ratio)
    {
        var region = new SD.Rectangle(10, 10, 100, 100);

        Assert.Equal(region, RecordingAspectPresets.Apply(region, ratio, Monitor));
    }

    // ---- toggles -------------------------------------------------------------------

    [Fact]
    public void Toggles_ReadTheirStateFromSettings()
    {
        var settings = new Settings
        {
            RecordAudio = true,
            RecordSystemAudio = false,
            WebcamOverlayPosition = "top-right",
            ShowClickHighlights = true,
            ShowKeystrokes = false,
        };

        RecordingHudToggles toggles = RecordingHudToggles.FromSettings(settings);

        Assert.True(toggles.Microphone);
        Assert.False(toggles.SystemAudio);
        Assert.True(toggles.Webcam);
        Assert.True(toggles.Clicks);
        Assert.False(toggles.Keystrokes);
    }

    [Fact]
    public void Toggles_WebcamIsOffWhenTheStoredPositionIsOff()
    {
        var settings = new Settings { WebcamOverlayPosition = RecordingOptions.WebcamOff };

        Assert.False(RecordingHudToggles.FromSettings(settings).Webcam);
    }

    [Theory]
    [InlineData(RecordingHudCell.Microphone)]
    [InlineData(RecordingHudCell.SystemAudio)]
    [InlineData(RecordingHudCell.Webcam)]
    [InlineData(RecordingHudCell.Clicks)]
    [InlineData(RecordingHudCell.Keystrokes)]
    public void Toggles_FlipExactlyTheCellAsked(RecordingHudCell cell)
    {
        var toggles = new RecordingHudToggles(false, false, false, false, false);

        RecordingHudToggles flipped = toggles.Toggled(cell);

        Assert.True(flipped[cell]);
        foreach (RecordingHudCell other in new[]
        {
            RecordingHudCell.Microphone,
            RecordingHudCell.SystemAudio,
            RecordingHudCell.Webcam,
            RecordingHudCell.Clicks,
            RecordingHudCell.Keystrokes,
        })
        {
            if (other != cell)
                Assert.False(flipped[other]);
        }
    }

    [Fact]
    public void Toggles_IgnoreCellsThatAreNotToggles()
    {
        var toggles = new RecordingHudToggles(true, false, false, false, false);

        Assert.Equal(toggles, toggles.Toggled(RecordingHudCell.RecordVideo));
        Assert.Equal(toggles, toggles.Toggled(RecordingHudCell.Aspect));
    }

    /// <summary>The HUD's webcam cell is on/off, so turning it on has to restore a real corner
    /// rather than leaving the recorder with "off".</summary>
    [Fact]
    public void WebcamPosition_RestoresTheStoredCornerAndFallsBackToBottomRight()
    {
        Assert.Equal("top-left", RecordingController.WebcamPositionFor(true, "top-left"));
        Assert.Equal("bottom-right", RecordingController.WebcamPositionFor(true, RecordingOptions.WebcamOff));
        Assert.Equal("bottom-right", RecordingController.WebcamPositionFor(true, "nonsense"));
        Assert.Equal(RecordingOptions.WebcamOff, RecordingController.WebcamPositionFor(false, "top-left"));
    }

    [Fact]
    public void EveryToggleCellHasAGlyphAndALabel()
    {
        foreach (RecordingHudCell cell in new[]
        {
            RecordingHudCell.Microphone,
            RecordingHudCell.SystemAudio,
            RecordingHudCell.Webcam,
            RecordingHudCell.Clicks,
            RecordingHudCell.Keystrokes,
        })
        {
            Assert.NotEqual("", RecordingHudLayout.GlyphFor(cell));
            Assert.NotEqual("", RecordingHudLayout.LabelFor(cell));
        }

        Assert.NotEqual("", RecordingHudLayout.ShortcutFor(RecordingHudCell.RecordGif));
        Assert.NotEqual("", RecordingHudLayout.ShortcutFor(RecordingHudCell.RecordVideo));
        Assert.NotEqual(
            RecordingHudLayout.ShortcutFor(RecordingHudCell.RecordGif),
            RecordingHudLayout.ShortcutFor(RecordingHudCell.RecordVideo));
    }
}
