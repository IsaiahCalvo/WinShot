using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The selector's edge cases, kept from the review panel's breaker pass: the desktop shapes
/// Isaiah actually runs (a negative-origin virtual screen, monitors above the laptop), a
/// clipboard held by another process, a settings.json written before any of this existed --
/// plus the four defects that pass found, each pinned so it cannot come back.
/// </summary>
[Collection("MotionRefcount")]
public class SelectorSweepRegressionTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    /// <summary>Picks a ratio the way the crop menu does, without opening a menu.</summary>
    private static void SetToolbarRatioIndex(FastAllInOneSelectorDialog selector, int index)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object toolbar = selector.GetType().GetField("_toolbar", flags)!.GetValue(selector)!;
        toolbar.GetType().GetField("_ratioIndex", flags)!.SetValue(toolbar, index);
    }

    private static void SetPendingAllInOne(FastAllInOneSelectorDialog selector, SD.Rectangle virtualRect) =>
        typeof(FastAllInOneSelectorDialog)
            .GetField("_pendingPx", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(selector, virtualRect);

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 8000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < timeoutMs)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// "Select the whole screen" with something already framed. ApplyExactSize anchored the
    /// new rect at the old top-left and then clipped it to the desktop, so the button
    /// silently selected only the leftover below-and-right of wherever you had drawn -- the
    /// one state nobody clicks it in was the only one it worked in.
    /// </summary>
    [Fact]
    public void ExpandToFullSizeSelectsTheWholeScreenEvenWithSomethingFramed()
    {
        RunSta(() =>
        {
            using var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SD.Rectangle vs = CaptureService.VirtualScreen;

            SetPendingAllInOne(selector, new SD.Rectangle(500, 300, 200, 150));
            selector.SelectWholeScreen();

            Assert.Equal(new SD.Rectangle(0, 0, vs.Width, vs.Height), selector.PendingRegion);
        });
    }

    /// <summary>
    /// Expand-to-full-size bypassed the chosen ratio: pick 16:9, press expand, and the
    /// selection became the desktop's own shape while the accent badge still said 16:9.
    /// </summary>
    [Fact]
    public void ExpandToFullSizeKeepsTheChosenRatio()
    {
        RunSta(() =>
        {
            using var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SD.Rectangle vs = CaptureService.VirtualScreen;

            SetPendingAllInOne(selector, new SD.Rectangle(500, 300, 200, 150));
            SetToolbarRatioIndex(selector, 7); // 16:9
            selector.SelectWholeScreen();
            PumpUntil(() => Motion.Holders == 0);

            SD.Rectangle got = selector.PendingRegion!.Value;
            Assert.True(Math.Abs((got.Width / (double)got.Height) - (16d / 9d)) < 0.02,
                $"{got.Width}x{got.Height} is not 16:9");
            Assert.True(got.Width == vs.Width || got.Height == vs.Height, "it should still fill an axis");
        });
    }

    /// <summary>
    /// Both button-driven size changes moved the selection in one frame -- often across the
    /// whole desktop -- while only the eight handles animated. The rect now eases there, and
    /// the pending rect is the DESTINATION from frame one so Enter still commits it.
    /// </summary>
    [Fact]
    public void ExpandAndTheRatioPresetsEaseTheSelectionRatherThanTeleportIt()
    {
        RunSta(() =>
        {
            using var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SD.Rectangle vs = CaptureService.VirtualScreen;

            SetPendingAllInOne(selector, new SD.Rectangle(500, 300, 200, 150));
            selector.ApplyAspectRatio(16d / 9d);

            Assert.True(Motion.Holders > 0, "the selection should be gliding to its new shape");
            SD.Rectangle destination = selector.PendingRegion!.Value;
            PumpUntil(() => Motion.Holders == 0);
            Assert.Equal(destination, selector.PendingRegion);
            Assert.Equal(0, Motion.Holders);
        });
    }

    /// <summary>Same button with nothing framed — the case that works.</summary>
    [Fact]
    public void ExpandToFullSizeFromNothingSelectsTheWholeScreen()
    {
        RunSta(() =>
        {
            using var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SD.Rectangle vs = CaptureService.VirtualScreen;
            selector.SelectWholeScreen();
            Assert.Equal(new SD.Rectangle(0, 0, vs.Width, vs.Height), selector.PendingRegion);
        });
    }

    /// <summary>
    /// A ratio taller than the selection, picked for a selection that is not near the top of
    /// the desktop, inherited the same anchor-then-clip: the height was clipped and the rect
    /// kept a ratio nobody asked for while the badge under the crop glyph said 9:16. What the
    /// menu promises is the SHAPE, so that is what is asserted -- a preset taller than the
    /// whole desktop scales its width down rather than abandoning the ratio.
    /// </summary>
    [Theory]
    [InlineData(9d / 16d)]
    [InlineData(1d)]
    [InlineData(16d / 9d)]
    public void ATallRatioOnALowSelectionKeepsTheRatio(double ratio)
    {
        RunSta(() =>
        {
            using var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SD.Rectangle vs = CaptureService.VirtualScreen;

            // Wide, and close enough to the desktop bottom that a taller shape has nowhere
            // to grow into without moving.
            SetPendingAllInOne(selector, new SD.Rectangle(10, vs.Height - 200, 400, 150));
            selector.ApplyAspectRatio(ratio);

            SD.Rectangle got = selector.PendingRegion!.Value;
            Assert.True(new SD.Rectangle(0, 0, vs.Width, vs.Height).Contains(got), $"escaped: {got}");
            // Within a pixel of the requested shape: the size is integral, the ratio is not.
            Assert.InRange(
                (double)got.Width / got.Height,
                ratio - (1.5 / got.Height),
                ratio + (1.5 / got.Height));
        });
    }

    /// <summary>Disposing the all-in-one selector mid-chip-glide must drop the 1 ms clock.</summary>
    [Fact]
    public void DisposingTheAllInOneMidGlideReleasesTheClock()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                PumpUntil(() => Motion.Holders == 0);
                var selector = new FastAllInOneSelectorDialog(
                    () => Task.FromResult(new List<WindowInfo>()), settings: null);

                SetPendingAllInOne(selector, new SD.Rectangle(10, 10, 300, 200));
                // Mode change starts the toolbar's chip glide; handle pop starts the owner's.
                selector.SetMode(1, AllInOneAction.Capture);
                selector.ApplyExactSize(120, 90);
                Assert.True(Motion.Holders > 0, "a tween should be in flight");

                selector.Dispose();
                Assert.Equal(0, Motion.Holders);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
                PumpUntil(() => Motion.Holders == 0);
            }
        });
    }

    /// <summary>Same for the region selector: Dispose, not Escape.</summary>
    [Fact]
    public void DisposingTheRegionSelectorMidTweenReleasesTheClock()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                PumpUntil(() => Motion.Holders == 0);
                var selector = new FastRegionSelectorDialog(
                    () => Task.FromResult(new List<WindowInfo>()), settings: null);

                selector.SetHoverWindow(new WindowInfo(new IntPtr(7), "Notepad", new SD.Rectangle(0, 0, 400, 300)));
                Assert.True(Motion.Holders > 0);

                selector.Dispose();
                Assert.Equal(0, Motion.Holders);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
                PumpUntil(() => Motion.Holders == 0);
            }
        });
    }

    /// <summary>A clipboard data object that throws on every call must read as "no image".</summary>
    [Fact]
    public void AHostileClipboardObjectIsNoImage()
    {
        Assert.Null(ClipboardImage.FromDataObject(new ThrowingDataObject()));
        Assert.Null(ClipboardImage.FromDataObject(null));
    }

    private sealed class ThrowingDataObject : WF.IDataObject
    {
        public object GetData(string format, bool autoConvert) => throw new ExternalException("busy");
        public object GetData(string format) => throw new ExternalException("busy");
        public object GetData(Type format) => throw new ExternalException("busy");
        public bool GetDataPresent(string format, bool autoConvert) => throw new ExternalException("busy");
        public bool GetDataPresent(string format) => throw new ExternalException("busy");
        public bool GetDataPresent(Type format) => throw new ExternalException("busy");
        public string[] GetFormats() => throw new ExternalException("busy");
        public string[] GetFormats(bool autoConvert) => throw new ExternalException("busy");
        public void SetData(string format, bool autoConvert, object? data) { }
        public void SetData(string format, object? data) { }
        public void SetData(Type format, object? data) { }
        public void SetData(object? data) { }
    }

    /// <summary>A PNG entry that is present but decodes to nothing must fall through, not throw.</summary>
    [Fact]
    public void AGarbagePngEntryFallsThroughToTheBitmapEntry()
    {
        var data = new WF.DataObject();
        data.SetData("PNG", new MemoryStream(new byte[] { 1, 2, 3, 4 }));
        using var bmp = new SD.Bitmap(4, 4);
        data.SetData(WF.DataFormats.Bitmap, bmp);

        using SD.Bitmap? read = ClipboardImage.FromDataObject(data);
        Assert.NotNull(read);
    }

    /// <summary>Window backdrop on the pathological sizes.</summary>
    [Fact]
    public void BackdropComposesForTinyAndHugeWindows()
    {
        using var tiny = new SD.Bitmap(1, 1);
        using SD.Bitmap composedTiny = WindowBackdrop.Compose(tiny, null, WindowBackdrop.PaddingFor("max"));
        Assert.True(composedTiny.Width > 1 && composedTiny.Height > 1);

        using var wide = new SD.Bitmap(3840, 40);
        using SD.Bitmap composedWide = WindowBackdrop.Compose(wide, null, WindowBackdrop.PaddingFor(null));
        Assert.True(composedWide.Width >= 3840);
    }

    /// <summary>A wallpaper with a zero dimension must not divide by zero.</summary>
    [Fact]
    public void ADegenerateWallpaperDoesNotBreakTheComposite()
    {
        using var source = new SD.Bitmap(200, 100);
        using var backdrop = new SD.Bitmap(1, 1);
        using SD.Bitmap composed = WindowBackdrop.Compose(source, backdrop, 24);
        Assert.Equal(248, composed.Width);
    }

    /// <summary>An old settings.json with none of the new keys, and the legacy single action.</summary>
    [Fact]
    public void AnOldSettingsFileLoadsWithTheNewDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winshot-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? previousDir = SettingsService.DirOverride;
        try
        {
            SettingsService.DirOverride = dir;
            File.WriteAllText(Path.Combine(dir, "settings.json"), """
            {
              "HotkeyCaptureRegion": "Ctrl+Shift+1",
              "PostCaptureAction": "Edit",
              "CrosshairMode": "command",
              "ShowCrosshair": true
            }
            """);

            var service = new SettingsService();
            service.Load();
            Settings s = service.Current;

            Assert.Equal("command", s.CrosshairMode);
            Assert.Equal("wallpaper", s.WindowCaptureBackground);
            Assert.Equal("default", s.WindowCapturePadding);
            Assert.False(s.AdjustSelectionBeforeCapturing);
            Assert.Equal(0, s.SelectorHintSessions);
            Assert.Equal(string.Empty, s.HotkeySelfTimer);
            Assert.Equal(string.Empty, s.HotkeyLoadClipboard);
        }
        finally
        {
            SettingsService.DirOverride = previousDir;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>Nudge/pad math on a desktop whose origin is negative (monitors above the laptop).</summary>
    [Fact]
    public void NudgeAndPadRespectANegativeOriginDesktop()
    {
        var desktop = new SD.Rectangle(-1920, -1080, 5000, 3000);
        var rect = new SD.Rectangle(-1900, -1070, 200, 100);

        SD.Rectangle? up = SelectionNudge.Apply(rect, WF.Keys.Up, shift: true, control: false, desktop);
        Assert.Equal(new SD.Rectangle(-1900, -1080, 200, 100), up);

        SD.Rectangle? left = SelectionNudge.Apply(
            new SD.Rectangle(-1915, -1070, 200, 100), WF.Keys.Left, shift: true, control: false, desktop);
        Assert.Equal(-1920, left!.Value.Left);

        SD.Rectangle padded = MarqueeEdge.Pad(rect, MarqueeEdge.PaddingStepPx, desktop);
        Assert.Equal(new SD.Rectangle(-1908, -1078, 216, 116), padded);
    }

    /// <summary>Growing a selection that already fills the desktop must not shrink it.</summary>
    [Fact]
    public void PaddingAFullScreenSelectionIsANoOp()
    {
        var desktop = new SD.Rectangle(-1920, -1080, 5000, 3000);
        SD.Rectangle padded = MarqueeEdge.Pad(desktop, MarqueeEdge.PaddingStepPx, desktop);
        Assert.Equal(desktop, padded);

        SD.Rectangle? grown = SelectionNudge.Apply(
            desktop, WF.Keys.OemCloseBrackets, shift: false, control: false, desktop);
        Assert.Equal(desktop, grown);
    }

    /// <summary>Ctrl+arrow resize must never invert or escape the desktop.</summary>
    [Fact]
    public void CtrlResizeStaysSane()
    {
        var desktop = new SD.Rectangle(0, 0, 1920, 1080);
        var rect = new SD.Rectangle(0, 0, 1, 1);

        SD.Rectangle? shrunk = SelectionNudge.Apply(rect, WF.Keys.Left, shift: true, control: true, desktop);
        Assert.True(shrunk!.Value.Width >= 1 && shrunk.Value.Height >= 1);

        var atEdge = new SD.Rectangle(1900, 1060, 20, 20);
        SD.Rectangle? grown = SelectionNudge.Apply(atEdge, WF.Keys.Right, shift: true, control: true, desktop);
        Assert.True(grown!.Value.Right <= desktop.Right, $"escaped: {grown}");
    }

    /// <summary>The hint counter must saturate, never hand back an infinite supply.</summary>
    [Fact]
    public void TheHintCounterSaturates()
    {
        int n = 0;
        for (int i = 0; i < 50; i++)
        {
            if (!SelectorHint.ShouldShow(n)) break;
            n = SelectorHint.NextSessionCount(n);
        }
        Assert.False(SelectorHint.ShouldShow(n));
        Assert.False(SelectorHint.ShouldShow(SelectorHint.NextSessionCount(int.MaxValue)));
        Assert.True(SelectorHint.ShouldShow(SelectorHint.NextSessionCount(-5)));
    }

    /// <summary>The pill you can click is the pill you can see, at a monitor's far corner.</summary>
    [Fact]
    public void TheShutterPillStaysOnScreenInTheBottomRightCorner()
    {
        var monitor = new SD.Rectangle(0, 0, 1920, 1080);
        var element = new SD.Rectangle(1800, 1000, 118, 78); // flush with the corner
        SD.Size pill = SelectorChrome.MeasureLabel($"{element.Width} × {element.Height}", 1d);
        SD.Rectangle rect = SelectorChrome.SizePillRect(element, pill, monitor, 1d);

        Assert.True(monitor.Contains(rect), $"pill {rect} left the monitor {monitor}");
    }

    /// <summary>Same, on a monitor at a negative origin.</summary>
    [Fact]
    public void TheShutterPillStaysOnANegativeOriginMonitor()
    {
        var monitor = new SD.Rectangle(-1920, -1080, 1920, 1080);
        var element = new SD.Rectangle(-200, -120, 200, 120);
        SD.Size pill = SelectorChrome.MeasureLabel("200 × 120", 1d);
        SD.Rectangle rect = SelectorChrome.SizePillRect(element, pill, monitor, 1d);

        Assert.True(monitor.Contains(rect), $"pill {rect} left the monitor {monitor}");
    }

    private sealed class Pointer : IDisposable
    {
        public SD.Point At;
        public Pointer(int x, int y)
        {
            At = new SD.Point(x, y);
            FastRegionSelectorDialog.CursorProvider = () => At;
        }
        public void MoveTo(FastRegionSelectorDialog s, int x, int y)
        {
            At = new SD.Point(x, y);
            s.HandleMouseMove();
        }
        public void Dispose() => FastRegionSelectorDialog.CursorProvider = null;
    }

    private static WF.MouseEventArgs Left() => new(WF.MouseButtons.Left, 1, 0, 0, 0);

    private static void SetVirtualScreen(FastRegionSelectorDialog s, SD.Rectangle vs) =>
        typeof(FastRegionSelectorDialog)
            .GetField("_vs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(s, vs);

    /// <summary>A drag on a monitor ABOVE the laptop (negative Y) must confirm virtual px.</summary>
    [Fact]
    public void MouseUpConfirmOnANegativeOriginDesktop()
    {
        RunSta(() =>
        {
            var vs = new SD.Rectangle(-1920, -1080, 5000, 3000);
            using var pointer = new Pointer(-1500, -900);
            using var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SetVirtualScreen(selector, vs);

            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, -1300, -800);
            selector.HandleMouseUp(Left());

            // virtual = screen - vs.Location
            Assert.Equal(new SD.Rectangle(420, 180, 200, 100), selector.SelectedRegionPx);
        });
    }

    /// <summary>The same drag, released 3 px from the press: a slip, not a capture.</summary>
    [Fact]
    public void ASlipOnANegativeOriginDesktopDoesNotCapture()
    {
        RunSta(() =>
        {
            var vs = new SD.Rectangle(-1920, -1080, 5000, 3000);
            using var pointer = new Pointer(-1500, -900);
            using var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SetVirtualScreen(selector, vs);

            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, -1494, -894);
            selector.HandleMouseUp(Left());

            Assert.Null(selector.SelectedRegionPx);
            Assert.NotNull(selector.PendingRegion);
        });
    }

    /// <summary>The composited window must still contain the window's own pixels, unscaled.</summary>
    [Fact]
    public void TheBackdropCompositeKeepsTheWindowPixels()
    {
        using var source = new SD.Bitmap(200, 120);
        using (var g = SD.Graphics.FromImage(source))
            g.Clear(SD.Color.Red);

        using SD.Bitmap composed = WindowBackdrop.Compose(source, null, 48);
        Assert.Equal(296, composed.Width);
        Assert.Equal(216, composed.Height);
        Assert.Equal(SD.Color.Red.ToArgb(), composed.GetPixel(148, 108).ToArgb());
        Assert.NotEqual(SD.Color.Red.ToArgb(), composed.GetPixel(4, 4).ToArgb());
    }

    /// <summary>Keys that act on a selection must be inert when there is none.</summary>
    [Fact]
    public void SelectionKeysAreInertWithNothingFramed()
    {
        RunSta(() =>
        {
            using var pointer = new Pointer(100, 100);
            using var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);

            foreach (WF.Keys key in new[]
            {
                WF.Keys.Left, WF.Keys.Right, WF.Keys.Up, WF.Keys.Down,
                WF.Keys.OemOpenBrackets, WF.Keys.OemCloseBrackets, WF.Keys.A,
            })
            {
                selector.HandleKeyDown(new WF.KeyEventArgs(key));
            }

            Assert.Null(selector.PendingRegion);
            Assert.Null(selector.SelectedRegionPx);
        });
    }

    /// <summary>Two pooled sessions in a row must not leak the baked dim slices.</summary>
    [Fact]
    public void TheAllInOneDimCacheIsDroppedBetweenSessions()
    {
        RunSta(() =>
        {
            using var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            var cache = (System.Collections.IDictionary)typeof(FastAllInOneSelectorDialog)
                .GetField("_dimmedCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(selector)!;

            typeof(FastAllInOneSelectorDialog)
                .GetField("_frozen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(selector, new SD.Bitmap(64, 64));
            typeof(FastAllInOneSelectorDialog)
                .GetMethod("GetDimmedBackground", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(selector, new object[] { new SD.Rectangle(0, 0, 32, 32) });
            Assert.Single(cache);

            typeof(FastAllInOneSelectorDialog)
                .GetMethod("DisposeFrozen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(selector, null);
            Assert.Empty(cache);
        });
    }

    // ------------------------------------------------------- camera cursor pixels

    /// <summary>
    /// The camera cursor has to have pixels in it. It was drawn with WF.TextRenderer, a GDI
    /// (DrawTextW) wrapper, onto a 32bppArgb bitmap cleared to Transparent -- GDI never writes
    /// the alpha byte of a 32-bpp DIB, so the glyph stayed at A=0 and GetHicon, which derives
    /// the icon's AND mask from alpha, produced an invisible cursor for the whole of
    /// window-pick mode. Nothing caught it because the only coverage compared cache identity.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheCameraCursorGlyphIsActuallyOpaque(double scale)
    {
        using SD.Bitmap bmp = SelectorCursors.BuildCameraBitmap(scale);

        int opaque = 0, bright = 0, dark = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                SD.Color px = bmp.GetPixel(x, y);
                if (px.A <= 200)
                    continue;
                opaque++;
                if (px.GetBrightness() > 0.8f) bright++;
                else if (px.GetBrightness() < 0.2f) dark++;
            }
        }

        Assert.True(opaque > 20, $"the cursor is invisible at {scale}x: {opaque} opaque pixels");
        // A white camera over a dark halo, so it reads on any wallpaper -- not one flat blob.
        Assert.True(bright > 10, $"no white body at {scale}x: {bright} bright pixels");
        Assert.True(dark > 10, $"no dark halo at {scale}x: {dark} dark pixels");
    }

    // -------------------------------------------------- action bar follows the glide

    /// <summary>
    /// The Cancel/Done/Pad bar is placed 12 px BELOW the region, so while the selection eased
    /// to a padded/snapped destination the bar was drawn at its old spot and hit-tested at the
    /// new one for the whole 140 ms. Paint and every hit-test now read one anchor: the eased
    /// rect while the glide runs, the settled rect after it.
    /// </summary>
    [Fact]
    public void TheActionBarRidesTheEasedRectWhileTheSelectionGlides()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                PumpUntil(() => Motion.Holders == 0);
                using var pointer = new Pointer(100, 100);
                using var selector = new FastRegionSelectorDialog(
                    () => Task.FromResult(new List<WindowInfo>()), settings: null);

                SetPending(selector, new SD.Rectangle(200, 200, 300, 200));
                var destination = new SD.Rectangle(200, 400, 300, 200);
                Glide(selector, destination);

                // The destination is committed immediately (Enter and Done must land on it)...
                Assert.Equal(destination, selector.PendingRegion);
                // ...but the bar stays where the user can still see it.
                SD.Rectangle? anchor = BarAnchor(selector);
                Assert.NotNull(anchor);
                Assert.NotEqual(destination, anchor!.Value);

                PumpUntil(() => Motion.Holders == 0);
                Assert.Equal(destination, BarAnchor(selector));
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
                PumpUntil(() => Motion.Holders == 0);
            }
        });
    }

    // ------------------------------------------------------------- chrome on mixed DPI

    /// <summary>Every pill measurement scales with the monitor it lands on, not with the
    /// primary's DPI, and the pointer-follow offsets scale with it.</summary>
    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void TheChromeIsSizedForTheMonitorItLandsOn(double scale)
    {
        SD.Size baseline = SelectorChrome.MeasureLabel("1920 × 1080", 1d);
        SD.Size scaled = SelectorChrome.MeasureLabel("1920 × 1080", scale);
        Assert.True(scaled.Width > baseline.Width && scaled.Height > baseline.Height,
            $"pill {scaled} did not grow past {baseline} at {scale:0.##}x");

        SD.Size offset = SelectorChrome.ReadoutOffset(scale);
        Assert.Equal((int)Math.Round(16 * scale), offset.Width);
        Assert.Equal((int)Math.Round(20 * scale), offset.Height);
    }

    /// <summary>
    /// The pill's text has to stay inside the ground the pill was measured for, on a surface
    /// whose DPI is not the primary's. The chrome measures with GDI (TextRenderer) and draws
    /// with GDI+ (DrawString — the only one of the two that honours alpha, which the fades
    /// need), and a GraphicsUnit.Point font resolves its size against the Font's captured
    /// screen DC on one side and against this Graphics' own DpiY on the other. That is two
    /// different monitors under PerMonitorV2, so a point-unit chrome font printed 18 px text
    /// inside a 12 px-wide ground on every non-primary-DPI display.
    /// </summary>
    [Fact]
    public void ALabelsTextStaysInsideItsGroundOnASurfaceAtAnotherMonitorsDpi()
    {
        var clientSize = new SD.Size(600, 200);
        var ink = SD.Color.FromArgb(255, 0, 128, 0); // a colour no part of the chrome paints
        const double scale = 1.5d;
        const string text = "1920 × 1080";

        using var bmp = new SD.Bitmap(clientSize.Width, clientSize.Height, SD.Imaging.PixelFormat.Format32bppArgb);
        bmp.SetResolution(144f, 144f);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.Clear(ink);
            SelectorChrome.DrawLabel(g, clientSize, text, 20, 20, scale);
        }

        SD.Size pill = SelectorChrome.MeasureLabel(text, scale);
        var ground = new SD.Rectangle(20, 20, pill.Width, pill.Height);
        ground.Inflate(1, 1); // the rounded ground antialiases against the background

        for (int y = 0; y < clientSize.Height; y++)
        {
            for (int x = 0; x < clientSize.Width; x++)
            {
                if (ground.Contains(x, y))
                    continue;
                SD.Color px = bmp.GetPixel(x, y);
                Assert.True(px.R == ink.R && px.G == ink.G && px.B == ink.B,
                    $"chrome painted at ({x},{y}) — outside the {ground} ground it was measured for");
            }
        }
    }

    // ------------------------------------------------- the edge band outside the border

    /// <summary>
    /// A framed selection with "Adjust selection before capturing" on, driven through the
    /// selector's own handlers (no real input, no visible desktop).
    /// </summary>
    private static FastRegionSelectorDialog Adjustable()
    {
        var settings = new SettingsService();
        settings.Current.AdjustSelectionBeforeCapturing = true;
        return new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings);
    }

    /// <summary>
    /// MarqueeEdge's grab band reaches as far OUTSIDE the border as inside, so a press a few
    /// px beyond it matched an edge but had no rectangle to move. Arming neither the move
    /// nor a marquee made the whole band dead: the drag did nothing at all, and mouse-up
    /// re-hit-tested under a pointer that had travelled so it did not snap either. The band
    /// is EdgeToleranceAt px wide — 6 at 100%, 9 at 150% — on all four sides.
    /// </summary>
    [Theory]
    [InlineData(-1, 135)]
    [InlineData(-3, 135)]
    [InlineData(-6, 135)]
    [InlineData(-8, 135)]
    [InlineData(-12, 135)]
    public void ADragStartingJustOutsideTheLeftBorderDrawsANewRegion(int dx, int y)
    {
        RunSta(() =>
        {
            using var pointer = new Pointer(100, 100);
            using var selector = Adjustable();

            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, 340, 260);
            selector.HandleMouseUp(Left());
            SD.Rectangle framed = selector.PendingRegion!.Value;
            Assert.Equal(new SD.Rectangle(100, 100, 240, 160), framed);

            pointer.At = new SD.Point(100 + dx, y);
            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, 800, 700);
            selector.HandleMouseUp(Left());

            Assert.True(
                selector.PendingRegion != framed || selector.SelectedRegionPx is not null,
                $"a drag from {dx} px outside the left border was swallowed");
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        });
    }

    /// <summary>The same band on the top edge, clear of the mid-edge handle at x=220.</summary>
    [Fact]
    public void ADragStartingJustAboveTheTopBorderDrawsANewRegion()
    {
        RunSta(() =>
        {
            using var pointer = new Pointer(100, 100);
            using var selector = Adjustable();

            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, 340, 260);
            selector.HandleMouseUp(Left());
            SD.Rectangle framed = selector.PendingRegion!.Value;

            pointer.At = new SD.Point(150, 97);
            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, 800, 700);
            selector.HandleMouseUp(Left());

            Assert.True(
                selector.PendingRegion != framed || selector.SelectedRegionPx is not null,
                "a drag from just above the top border was swallowed");
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        });
    }

    /// <summary>A press just INSIDE the same band still moves the selection, which is the
    /// half of the gesture the outside fix must not take away.</summary>
    [Fact]
    public void ADragStartingJustInsideTheLeftBorderMovesTheSelection()
    {
        RunSta(() =>
        {
            using var pointer = new Pointer(100, 100);
            using var selector = Adjustable();

            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, 340, 260);
            selector.HandleMouseUp(Left());

            pointer.At = new SD.Point(103, 135);
            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, 203, 235);
            selector.HandleMouseUp(Left());

            SD.Rectangle moved = selector.PendingRegion!.Value;
            Assert.Equal(new SD.Size(240, 160), moved.Size);
            Assert.NotEqual(new SD.Point(100, 100), moved.Location);
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        });
    }

    /// <summary>
    /// A pooled selector is parked-but-alive after Complete(); only WS_EX_TRANSPARENT keeps
    /// mouse messages off it in production. The key handler has always carried the guard —
    /// without the same one on the three mouse handlers a trailing press-drag-release built
    /// a fresh selection and started a tween on a hidden window.
    /// </summary>
    [Fact]
    public void MouseInputAfterTheSessionEndsIsIgnored()
    {
        RunSta(() =>
        {
            using var pointer = new Pointer(100, 100);
            using var selector = Adjustable();

            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, 340, 260);
            selector.HandleMouseUp(Left());
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
            SD.Rectangle? afterEscape = selector.PendingRegion;

            pointer.MoveTo(selector, 500, 500);
            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, 700, 700);
            selector.HandleMouseUp(Left());

            Assert.Equal(WF.DialogResult.Cancel, selector.DialogResult);
            Assert.Equal(afterEscape, selector.PendingRegion);
            Assert.Null(selector.SelectedRegionPx);
        });
    }

    private static void SetPending(FastRegionSelectorDialog s, SD.Rectangle screenRect) =>
        typeof(FastRegionSelectorDialog)
            .GetField("_pendingScreen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(s, screenRect);

    private static void Glide(FastRegionSelectorDialog s, SD.Rectangle destination) =>
        typeof(FastRegionSelectorDialog)
            .GetMethod("GlideSelectionTo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(s, new object[] { destination });

    private static SD.Rectangle? BarAnchor(FastRegionSelectorDialog s) =>
        (SD.Rectangle?)typeof(FastRegionSelectorDialog)
            .GetProperty("BarAnchor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(s);

    private static void SetSnapEdges(FastRegionSelectorDialog s, int[] x, int[] y)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(FastRegionSelectorDialog).GetField("_snapEdgesX", flags)!.SetValue(s, x);
        typeof(FastRegionSelectorDialog).GetField("_snapEdgesY", flags)!.SetValue(s, y);
    }

    // ------------------------------------------------------------ the edge gesture

    /// <summary>
    /// Pressing an edge used to snap it on the DOWN event, up to MarqueeEdge.ReachPx away,
    /// under a cursor that was showing the four-way move arrow, with no undo -- and the early
    /// return made dragging the selection from its border impossible. The press now only arms
    /// the gesture: it fires on the release over the same edge, and a press that travels is
    /// still a move.
    /// </summary>
    [Fact]
    public void AnEdgeSnapsOnReleaseAndAPressThatTravelsMovesInstead()
    {
        RunSta(() =>
        {
            using var pointer = new Pointer(100, 130);
            using var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);

            var framed = new SD.Rectangle(100, 100, 240, 160);
            SetPending(selector, framed);
            SetSnapEdges(selector, [96, 520], [180, 496]);

            // Clear of the corner and mid-edge handles, which win the grab.
            pointer.MoveTo(selector, 100, 132);
            selector.HandleMouseDown(Left());
            Assert.Equal(framed, selector.PendingRegion);
            selector.HandleMouseUp(Left());
            Assert.Equal(96, selector.PendingRegion!.Value.Left);
            PumpUntil(() => Motion.Holders == 0);

            // Same band, but the press travels: that is a move, not a snap.
            SD.Rectangle before = selector.PendingRegion!.Value;
            pointer.MoveTo(selector, before.Left, before.Top + 30);
            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, before.Left + 40, before.Top + 30);
            selector.HandleMouseUp(Left());
            Assert.Equal(before.Size, selector.PendingRegion!.Value.Size);
            Assert.Equal(before.Left + 40, selector.PendingRegion!.Value.Left);

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
            PumpUntil(() => Motion.Holders == 0);
        });
    }

    /// <summary>
    /// The cursor has to name the gesture the pixels under it answer. The whole 6 px edge band
    /// sits inside the move body, so SizeAll promised a move over pixels that snap an edge.
    /// </summary>
    [Fact]
    public void TheCursorOverAnEdgeIsASizingArrowNotTheMoveArrow()
    {
        RunSta(() =>
        {
            using var pointer = new Pointer(300, 300);
            using var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SetPending(selector, new SD.Rectangle(200, 200, 300, 200));

            pointer.MoveTo(selector, 200, 302);
            Assert.Equal(WF.Cursors.SizeWE, selector.CursorForCurrent());

            pointer.MoveTo(selector, 350, 200);
            Assert.Equal(WF.Cursors.SizeNS, selector.CursorForCurrent());

            pointer.MoveTo(selector, 350, 300);
            Assert.Equal(WF.Cursors.SizeAll, selector.CursorForCurrent());

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
            PumpUntil(() => Motion.Holders == 0);
        });
    }

    /// <summary>
    /// A key arriving after Complete() ran on a pooled, parked selector: it still holds focus,
    /// so Escape auto-repeat started a tween and a 1.2 s message dwell on a hidden window.
    /// </summary>
    [Fact]
    public void AKeyAfterTheSessionEndedStartsNothing()
    {
        RunSta(() =>
        {
            using var pointer = new Pointer(300, 300);
            using var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SetPending(selector, new SD.Rectangle(200, 200, 300, 200));

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
            PumpUntil(() => Motion.Holders == 0);
            Assert.Equal(0, Motion.Holders);

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.C));
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
            Assert.Equal(0, Motion.Holders);
        });
    }

    /// <summary>
    /// A handle dragged onto its opposite corner left a 0x0 rect that Enter and Done then
    /// silently refused, with all eight handles stacked on one point.
    /// </summary>
    [Fact]
    public void AHandleDraggedOntoItsOppositeCornerLeavesSomethingConfirmable()
    {
        var bounds = new SD.Rectangle(0, 0, 1920, 1080);
        SD.Rectangle collapsed = SelectionHandles.Resize(
            new SD.Rectangle(100, 100, 200, 150), handle: 4, cursor: new SD.Point(100, 100), bounds);

        Assert.True(collapsed.Width >= SelectionHandles.MinSizePx);
        Assert.True(collapsed.Height >= SelectionHandles.MinSizePx);
        Assert.True(bounds.Contains(collapsed));
    }

    // ------------------------------------------------------------ window backdrop

    /// <summary>
    /// The backdrop used to re-decode the desktop wallpaper (~110-150 ms) and re-composite
    /// (~80-120 ms) on the UI thread for every single window capture. It now runs off-thread
    /// against one cached wallpaper, so the composite has to be safe to call concurrently --
    /// two captures drawing from one GDI+ bitmap at once is a corrupt image or a throw.
    /// </summary>
    [Fact]
    public void ComposingOnTheWallpaperIsSafeToCallFromSeveralThreadsAtOnce()
    {
        var results = new SD.Bitmap[4];
        Parallel.For(0, results.Length, i =>
        {
            using var source = new SD.Bitmap(160, 120);
            results[i] = WindowBackdrop.ComposeOnWallpaper(source, WindowBackdrop.PaddingFor("min"));
        });

        foreach (SD.Bitmap composed in results)
        {
            Assert.Equal(160 + (2 * 24), composed.Width);
            Assert.Equal(120 + (2 * 24), composed.Height);
            composed.Dispose();
        }
    }

    // ------------------------------------------------------------ bare-key shortcuts

    /// <summary>
    /// The selector's single-letter shortcuts matched on KeyCode alone, so Ctrl+A -- the
    /// select-all reflex, on an overlay that has a selection on it -- re-snapped every edge of
    /// the user's rectangle, and Ctrl+Space threw it away. Ctrl+C is deliberate and stays.
    /// </summary>
    [Fact]
    public void ModifiedLettersDoNotActOnTheSelection()
    {
        RunSta(() =>
        {
            using var pointer = new Pointer(300, 300);
            using var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);

            var framed = new SD.Rectangle(200, 200, 300, 200);
            SetPending(selector, framed);

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.A | WF.Keys.Control));
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Space | WF.Keys.Control));
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.A | WF.Keys.Alt));

            Assert.Equal(framed, selector.PendingRegion);
        });
    }
}
