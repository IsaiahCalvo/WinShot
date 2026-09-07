using System.Diagnostics;
using System.Linq;
using System.Threading;
using WinShot.Core;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The recording control pill: cell geometry at every DPI Isaiah runs, the anchor under the
/// recorded rectangle (including the two fallbacks), and the entrance tween's holder discipline.
/// </summary>
public class RecordingControlBarTests
{
    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private static void PumpUntil(Func<bool> done, int ms = 6000)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < ms)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    // ---- layout ------------------------------------------------------------------

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Cells_ButtAgainstEachOtherAndFillTheBar(double scale)
    {
        var cells = RecordingControlBarLayout.Build(showTimer: true, scale);
        SD.Size size = RecordingControlBarLayout.Size(showTimer: true, scale);

        Assert.Equal(5, cells.Count);
        int pad = RecordingMonitorDpi.Px(RecordingControlBarLayout.SidePadDip, scale);
        Assert.Equal(pad, cells[0].Bounds.Left);
        Assert.Equal(size.Width - pad, cells[^1].Bounds.Right);
        for (int i = 1; i < cells.Count; i++)
            Assert.Equal(cells[i - 1].Bounds.Right, cells[i].Bounds.Left);
        Assert.All(cells, c => Assert.Equal(size.Height, c.Bounds.Height));
    }

    [Fact]
    public void Cells_DropTheTimerWhenTheTimerIsOff()
    {
        var cells = RecordingControlBarLayout.Build(showTimer: false, scale: 1.0);

        Assert.DoesNotContain(cells, c => c.Cell == RecordingControlBarCell.Timer);
        Assert.Equal(
            new[]
            {
                RecordingControlBarCell.PauseResume,
                RecordingControlBarCell.Restart,
                RecordingControlBarCell.Stop,
                RecordingControlBarCell.Discard,
            },
            cells.Select(c => c.Cell));
        Assert.True(
            RecordingControlBarLayout.Size(showTimer: false, 1.0).Width <
            RecordingControlBarLayout.Size(showTimer: true, 1.0).Width);
    }

    // ---- placement ---------------------------------------------------------------

    [Fact]
    public void Placement_CentresJustBelowTheRegion()
    {
        var region = new SD.Rectangle(400, 200, 600, 300);
        var work = new SD.Rectangle(0, 0, 1920, 1080);
        var size = new SD.Size(240, 40);

        SD.Point at = RecordingControlBarPlacement.UnderRegion(region, work, size, margin: 12);

        Assert.Equal(400 + ((600 - 240) / 2), at.X);
        Assert.Equal(500 + 12, at.Y);
    }

    [Fact]
    public void Placement_FlipsAboveTheRegionWhenThereIsNoRoomBelow()
    {
        var region = new SD.Rectangle(0, 300, 1920, 760);
        var work = new SD.Rectangle(0, 0, 1920, 1080);
        var size = new SD.Size(240, 40);

        SD.Point at = RecordingControlBarPlacement.UnderRegion(region, work, size, margin: 12);

        Assert.Equal(300 - 12 - 40, at.Y);
    }

    [Fact]
    public void Placement_StaysOnScreenForAFullHeightRegionOnANegativeYMonitor()
    {
        // Isaiah's second monitor sits above the laptop, so its Y coordinates are negative.
        var work = new SD.Rectangle(-1920, -1080, 1920, 1080);
        var region = work;
        var size = new SD.Size(240, 40);

        SD.Point at = RecordingControlBarPlacement.UnderRegion(region, work, size, margin: 12);

        Assert.InRange(at.X, work.Left, work.Right - size.Width);
        Assert.InRange(at.Y, work.Top, work.Bottom - size.Height);
    }

    [Fact]
    public void Placement_ClampsAWiderBarIntoTheWorkingArea()
    {
        var region = new SD.Rectangle(1800, 100, 100, 100);
        var work = new SD.Rectangle(0, 0, 1920, 1080);
        var size = new SD.Size(400, 40);

        SD.Point at = RecordingControlBarPlacement.UnderRegion(region, work, size, margin: 12);

        Assert.Equal(1920 - 400, at.X);
    }

    // ---- the window itself -------------------------------------------------------

    [Fact]
    public void Bar_EntranceFinishesAndReleasesTheMotionClock()
    {
        using var animation = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        var bar = new FastRecordingControlBar(new SD.Rectangle(-6000, -6000, 600, 300), showTimer: true);
        bar.Show();
        PumpUntil(() => bar.Opacity > 0.9);
        Assert.True(bar.Opacity > 0.9);
        bar.Close();
        bar.Dispose();

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void Bar_ClosingMidEntranceLeavesNoClockHolder()
    {
        using var animation = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        for (int i = 0; i < 6; i++)
        {
            var bar = new FastRecordingControlBar(new SD.Rectangle(-6000, -6000, 400, 200), showTimer: false);
            bar.Show();
            WF.Application.DoEvents();
            bar.Close();
            bar.Dispose();
        }

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    // ---- stop: saving state, then a fade -----------------------------------------

    [Fact]
    public void Bar_SavingStateSurvivesUntilTheFadeAndReleasesTheClock()
    {
        using var animation = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        var bar = new FastRecordingControlBar(new SD.Rectangle(-6000, -6000, 600, 300), showTimer: false);
        bar.Show();
        PumpUntil(() => bar.Opacity > 0.9);

        bar.BeginSaving();
        Assert.True(bar.IsSaving);
        // Still on screen: the whole point is that nothing tears down before the card lands.
        WF.Application.DoEvents();
        Assert.False(bar.IsDisposed);

        bar.FadeOutAndClose();
        PumpUntil(() => bar.IsDisposed);
        Assert.True(bar.IsDisposed);

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
        bar.Dispose();
    }

    [Fact]
    public void Bar_FadeOutIsIdempotentAndSafeBeforeItIsEverShown()
    {
        using var animation = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        // Never shown: there is no handle and no tween to run, so this must simply not throw.
        var bar = new FastRecordingControlBar(new SD.Rectangle(-6000, -6000, 600, 300), showTimer: true);
        bar.FadeOutAndClose();
        bar.FadeOutAndClose();
        bar.Dispose();

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>With animation off system-wide the exit must still end with the window gone,
    /// not left on screen waiting for a tween that never runs.</summary>
    [Fact]
    public void Bar_FadeOutStillClosesWithAnimationOff()
    {
        using var animation = new Anim(false);

        var bar = new FastRecordingControlBar(new SD.Rectangle(-6000, -6000, 600, 300), showTimer: false);
        bar.Show();
        WF.Application.DoEvents();
        bar.BeginSaving();
        bar.FadeOutAndClose();
        WF.Application.DoEvents();

        Assert.True(bar.IsDisposed);
        Assert.Equal(0, Motion.Holders);
        bar.Dispose();
    }

    // ---- what it actually paints ---------------------------------------------------

    private static SD.Bitmap Render(FastRecordingControlBar bar) => bar.RenderToBitmap();

    /// <summary>
    /// Counts genuinely red pixels. The green and blue channels have to be BOTH low and close
    /// to each other, because ClearType renders white text with orange sub-pixel fringes that a
    /// naive "red is high" test happily counts as red.
    /// </summary>
    private static int CountRed(SD.Bitmap image)
    {
        int red = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                SD.Color pixel = image.GetPixel(x, y);
                if (pixel.R > 150 &&
                    pixel.R - pixel.G > 90 &&
                    pixel.R - pixel.B > 90 &&
                    Math.Abs(pixel.G - pixel.B) < 40)
                {
                    red++;
                }
            }
        }
        return red;
    }

    /// <summary>
    /// Pixel-scanned rather than eyeballed: the recording dot and the stop glyph are the only
    /// red on the pill, and the saving row has neither — if the saving state ever failed to
    /// replace the cells it would show a live-looking stop button over a finished recording.
    /// </summary>
    [Fact]
    public void Bar_PaintsRedForRecordingAndDropsItWhileSaving()
    {
        using var animation = new Anim(false); // no entrance: DrawToBitmap wants the rest state
        var bar = new FastRecordingControlBar(new SD.Rectangle(-6000, -6000, 600, 300), showTimer: true);
        bar.Show();
        WF.Application.DoEvents();

        using (SD.Bitmap recording = Render(bar))
        {
            Assert.True(CountRed(recording) > 20, "the recording pill painted no red at all");
        }

        bar.BeginSaving();
        WF.Application.DoEvents();
        using (SD.Bitmap saving = Render(bar))
        {
            Assert.True(CountRed(saving) < 5, $"the saving row is still painting {CountRed(saving)} red pixels");
        }

        bar.Close();
        bar.Dispose();
    }

    [Fact]
    public void Bar_PaintsSomethingInEveryCell()
    {
        using var animation = new Anim(false);
        var bar = new FastRecordingControlBar(new SD.Rectangle(-6000, -6000, 600, 300), showTimer: true);
        bar.Show();
        WF.Application.DoEvents();

        using SD.Bitmap image = Render(bar);
        // The bar's own cells, not a fresh layout at 100%: on a scaled display those disagree
        // and the scan lands in the gaps between cells.
        foreach ((RecordingControlBarCell cell, SD.Rectangle bounds) in bar.Cells)
        {
            SD.Rectangle box = SD.Rectangle.Intersect(bounds, new SD.Rectangle(0, 0, image.Width, image.Height));
            if (box.Width < 8 || box.Height < 8)
                continue;

            bool inked = false;
            for (int y = box.Top + 4; y < box.Bottom - 4 && !inked; y++)
            {
                for (int x = box.Left + 2; x < box.Right - 2; x++)
                {
                    SD.Color pixel = image.GetPixel(x, y);
                    if (pixel.R > 120 || pixel.G > 120 || pixel.B > 120)
                    {
                        inked = true;
                        break;
                    }
                }
            }
            Assert.True(inked, $"{cell} rendered as an empty cell");
        }

        bar.Close();
        bar.Dispose();
    }

    [Fact]
    public void Bar_TipsNameTheActionAndItsKey()
    {
        using var bar = new FastRecordingControlBar(
            new SD.Rectangle(-6000, -6000, 400, 200),
            showTimer: true,
            stopHotkey: "Ctrl+Shift+3");

        Assert.Equal("Stop and save (Esc or Ctrl+Shift+3)", bar.TipFor(RecordingControlBarCell.Stop));
        Assert.Equal("Discard the take (Shift+Esc)", bar.TipFor(RecordingControlBarCell.Discard));
        Assert.Equal("Pause recording", bar.TipFor(RecordingControlBarCell.PauseResume));
        bar.SyncPaused(true);
        Assert.Equal("Resume recording", bar.TipFor(RecordingControlBarCell.PauseResume));
    }

    /// <summary>
    /// The pill is the one surface the user touches during a take, and the rewrite from four
    /// DarkButtons to owner-drawn cells dropped the eased hover they had. A snap would be at 1
    /// the instant the hover is set; an ease has to travel there, and the cell being left has
    /// to fall from wherever it had reached.
    /// </summary>
    [Fact]
    public void Bar_HoverAndPressEaseInsteadOfSnapping()
    {
        using var animation = new Anim(true);
        PumpUntil(() => Motion.Holders == 0);

        using var bar = new FastRecordingControlBar(
            new SD.Rectangle(-6000, -6000, 400, 200), showTimer: false, stopHotkey: null);
        bar.Show();
        WF.Application.DoEvents();

        SetHover(bar, 0);
        Assert.True(Wash(bar, "_hover", 0) < 0.99, "hover arrived at full strength in one frame");
        PumpUntil(() => Wash(bar, "_hover", 0) >= 0.999);
        Assert.Equal(1d, Wash(bar, "_hover", 0), 3);

        SetHover(bar, -1);
        Assert.True(Wash(bar, "_hover", 0) > 0.01, "the cell being left vanished in one frame");
        PumpUntil(() => Wash(bar, "_hover", 0) <= 0.001);

        bar.Close();
        bar.Dispose();
        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    private static void SetHover(FastRecordingControlBar bar, int index) =>
        typeof(FastRecordingControlBar)
            .GetMethod("SetHover", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(bar, [index]);

    private static double Wash(FastRecordingControlBar bar, string field, int index)
    {
        var wash = (HoverWash<int>)typeof(FastRecordingControlBar)
            .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(bar)!;
        return wash[index];
    }

    [Fact]
    public void Bar_TipFallsBackToEscapeWhenNoHotkeyIsBound()
    {
        using var bar = new FastRecordingControlBar(
            new SD.Rectangle(-6000, -6000, 400, 200),
            showTimer: false,
            stopHotkey: "   ");

        Assert.Equal("Stop and save (Esc)", bar.TipFor(RecordingControlBarCell.Stop));
    }
}
