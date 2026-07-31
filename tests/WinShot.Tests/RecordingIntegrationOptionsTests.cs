using WinShot.Core;
using WinShot.Recording;
using SD = System.Drawing;
using Xunit;
using System.IO;

namespace WinShot.Tests;

public class RecordingIntegrationOptionsTests
{
    [Fact]
    public void AreaMemory_PrefersAllInOneSelectionWithoutAnotherPicker()
    {
        bool resolved = RecordingAreaMemory.TryResolve(
            new SD.Rectangle(20, 30, 501, 301),
            rememberLastSelection: false,
            savedScreenRegion: null,
            new SD.Rectangle(-100, -50, 2000, 1200),
            out var selection);

        Assert.True(resolved);
        Assert.Equal(new SD.Rectangle(-80, -20, 500, 300), selection.ScreenRect);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AreaMemory_ReusesSavedAreaOnlyWhenEnabled(bool enabled, bool expected)
    {
        bool resolved = RecordingAreaMemory.TryResolve(
            selectedVirtualRegion: null,
            enabled,
            "-300,40,801,603",
            new SD.Rectangle(-500, 0, 2500, 1400),
            out var selection);

        Assert.Equal(expected, resolved);
        if (expected)
            Assert.Equal(new SD.Rectangle(-300, 40, 800, 602), selection.ScreenRect);
    }

    [Fact]
    public void AreaMemory_RejectsRememberedAreaOutsideCurrentDesktop()
    {
        Assert.False(RecordingAreaMemory.TryResolve(
            selectedVirtualRegion: null,
            rememberLastSelection: true,
            savedScreenRegion: "5000,5000,800,600",
            virtualScreen: new SD.Rectangle(-1920, 0, 4480, 1440),
            out _));
    }

    [Fact]
    public void DisplayPlan_FlagsCrossMonitorAndSelectsLargestOverlap()
    {
        var displays = new[]
        {
            new SD.Rectangle(0, 0, 1920, 1080),
            new SD.Rectangle(1920, 0, 2560, 1440),
        };

        RecordingDisplayPlan plan = RecordingDisplayPlan.Create(
            new SD.Rectangle(1700, 100, 800, 600), displays);

        Assert.True(plan.CrossesDisplays);
        Assert.Equal(displays[1], plan.ScreenBounds);
        Assert.Equal(new SD.Rectangle(1920, 100, 580, 600), plan.CaptureRect);
    }

    [Fact]
    public void DisplayPlan_KeepsSingleMonitorAreaExactAndEven()
    {
        var display = new SD.Rectangle(-1920, 0, 1920, 1080);
        RecordingDisplayPlan plan = RecordingDisplayPlan.Create(
            new SD.Rectangle(-1801, 31, 701, 503), [display]);

        Assert.False(plan.CrossesDisplays);
        Assert.Equal(new SD.Rectangle(-1801, 31, 700, 502), plan.CaptureRect);
    }

    [Theory]
    [InlineData("original", false, 1.0, 3000, 2000)]
    [InlineData("1080p", false, 1.0, 1620, 1080)]
    [InlineData("720p", false, 1.0, 1080, 720)]
    [InlineData("4k", false, 1.0, 3000, 2000)]
    [InlineData("original", true, 2.0, 1500, 1000)]
    [InlineData("1080p", true, 2.0, 1500, 1000)]
    public void VideoSize_HonorsResolutionAndHiDpi(
        string maxResolution,
        bool scaleHiDpi,
        double dpiScale,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Equal(
            new SD.Size(expectedWidth, expectedHeight),
            RecordingOutputSize.CalculateVideo(
                new SD.Size(3000, 2000), maxResolution, scaleHiDpi, dpiScale));
    }


    [Fact]
    public void VideoSize_UsesPortraitResolutionBoundsForPortraitCapture()
    {
        Assert.Equal(
            new SD.Size(1080, 1620),
            RecordingOutputSize.CalculateVideo(
                new SD.Size(2000, 3000), "1080p", scaleHiDpi: false, dpiScale: 1));
    }

    [Theory]
    [InlineData("800", 1600, 900, 800, 450)]
    [InlineData("640", 1000, 1001, 640, 640)]
    [InlineData("auto", 1600, 900, 1600, 900)]
    [InlineData("800", 640, 480, 640, 480)]
    public void GifSize_OnlyDownscalesWhenNeeded(
        string setting,
        int sourceWidth,
        int sourceHeight,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Equal(
            new SD.Size(expectedWidth, expectedHeight),
            RecordingOutputSize.CalculateGif(new SD.Size(sourceWidth, sourceHeight), setting));
    }

    [Theory]
    [InlineData(0, true, 16)]
    [InlineData(50, true, 136)]
    [InlineData(100, true, 256)]
    [InlineData(10, false, 256)]
    public void GifQualityAndOptimization_ControlPalette(int quality, bool optimize, int expected)
    {
        Assert.Equal(expected, RecordingOutputSize.GifPaletteColors(quality, optimize));
    }

    [Fact]
    public void GifEncoder_AcceptsOptimizedPaletteAndProducesValidGif()
    {
        using var output = new MemoryStream();
        using (var encoder = new GifEncoder(output, 16, 12, fps: 10, paletteColors: 32))
        {
            using var frame = new SD.Bitmap(16, 12);
            using (SD.Graphics graphics = SD.Graphics.FromImage(frame))
            {
                graphics.Clear(SD.Color.CornflowerBlue);
                graphics.FillRectangle(SD.Brushes.OrangeRed, 2, 2, 8, 6);
            }
            encoder.AddFrame(frame);
            encoder.Finish();
        }

        byte[] bytes = output.ToArray();
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.Equal(0x3B, bytes[^1]);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void MonoAudio_RequiresMicrophoneAndSetting(bool microphone, bool mono, bool expected)
    {
        Assert.Equal(expected, RecordingAudioInputOptions.ShouldForceMono(microphone, mono));
    }

    [Fact]
    public void SessionUi_HonorsControlsTimerAndDimSettings()
    {
        var settings = new Settings
        {
            ShowRecordingControls = true,
            ShowRecordingTimer = true,
            DimScreenWhileRecording = false,
        };

        Assert.Equal(
            new RecordingSessionUiOptions(true, true, false),
            RecordingSessionUiOptions.FromSettings(settings));

        settings.ShowRecordingControls = false;
        Assert.Equal(
            new RecordingSessionUiOptions(false, true, false),
            RecordingSessionUiOptions.FromSettings(settings));
    }

    [Fact]
    public void AfterActions_HonorLocalOverlayCopyAndEditorSettings()
    {
        var settings = new Settings
        {
            RecordingShowOverlay = false,
            RecordingCopy = true,
            RecordingOpenEditor = true,
        };

        Assert.Equal(
            new RecordingAfterActions(false, true, true),
            RecordingAfterActions.FromSettings(settings, isGif: false));
        Assert.Equal(
            new RecordingAfterActions(false, true, false),
            RecordingAfterActions.FromSettings(settings, isGif: true));

        settings.RecordingOpenEditor = false;
        settings.OpenVideoEditorAfterRecording = true;
        Assert.True(RecordingAfterActions.FromSettings(settings, isGif: false).OpenEditor);
    }

    [Fact]
    public void ControlBarPlacement_UsesRecordedMonitorWorkingArea()
    {
        Assert.Equal(
            new SD.Point(2590, 1306),
            RecordingControlBarPlacement.BottomCenter(
                new SD.Rectangle(1920, 0, 2560, 1400),
                new SD.Size(1220, 70)));
    }

    [Fact]
    public void DimOverlay_NeverOverlapsRecordedArea()
    {
        var display = new SD.Rectangle(0, 0, 1920, 1080);
        var recording = new SD.Rectangle(300, 200, 900, 600);
        IReadOnlyList<SD.Rectangle> pieces = RecordingDimOverlayLayout.OutsideRecordingArea(display, recording);

        Assert.Equal(4, pieces.Count);
        Assert.All(pieces, piece =>
        {
            SD.Rectangle overlap = SD.Rectangle.Intersect(piece, recording);
            Assert.True(overlap.Width == 0 || overlap.Height == 0);
        });
        Assert.Equal((long)display.Width * display.Height - (long)recording.Width * recording.Height,
            pieces.Sum(piece => (long)piece.Width * piece.Height));
    }
}
