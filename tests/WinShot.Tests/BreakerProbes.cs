using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Text.Json;
using WinShot.Ocr;
using WinShot.Core;
using WinShot.Overlay;
using WinShot.Pin;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Edge cases the review panel's breaker found, kept as tests: dispose mid-animation, Motion
/// holder leaks, negative-Y monitors, a pin too small for its own toolbar, a working area too
/// short for the hover flyout, settings files written before a field existed, and the two
/// capture loops driven end to end through injected sinks rather than real input.
/// </summary>
public class BreakerProbes
{
    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private static void PumpUntil(Func<bool> c, int ms = 8000)
    {
        var d = Stopwatch.StartNew();
        while (!c() && d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    // ---------------------------------------------------------- ScrollChromeFade

    [Fact]
    public void ScrollChromeFade_Out_ReleasesHolder()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);
        byte last = 255;
        Task done = ScrollChromeFade.Out(a => last = a, 255, out IDisposable? handle);
        PumpUntil(() => done.IsCompleted);
        handle!.Dispose();
        Assert.True(done.IsCompleted);
        Assert.Equal(0, last);
        PumpUntil(() => Motion.Holders == 0, 2000);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>
    /// The In() handle was stored and disposed on close while the Out() handle was thrown
    /// away, so a form closed by the timeout race left an uncancellable tween holding
    /// timeBeginPeriod(1) with its form already gone. Out() hands the handle back now.
    /// </summary>
    [Fact]
    public void ScrollChromeFade_Out_CanBeCancelledWithoutAPump()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);
        Task done = ScrollChromeFade.Out(_ => { }, 255, out IDisposable? handle);
        Assert.Equal(1, Motion.Holders);
        handle!.Dispose();
        Assert.Equal(0, Motion.Holders);
        Assert.False(done.IsCompleted, "a cancelled tween must not report that it finished");
    }

    [Fact]
    public void ScrollChromeFade_AnimationsOff_JumpsToEnds()
    {
        using var _ = new Anim(false);
        var seen = new List<byte>();
        using IDisposable? h = ScrollChromeFade.In(seen.Add);
        Assert.Equal(new byte[] { 0, 255 }, seen);
        seen.Clear();
        Task done = ScrollChromeFade.Out(seen.Add, 255, out IDisposable? outHandle);
        // Both handles are null: with animations off Motion.Run applies the end state and
        // runs its completion INLINE, so there is nothing left to cancel by the time either
        // call returns. Disposing null is the caller's normal `?.Dispose()`.
        Assert.Null(h);
        Assert.Null(outHandle);
        outHandle?.Dispose();
        Assert.True(done.IsCompleted);
        Assert.Equal(new byte[] { 0 }, seen);
        Assert.Equal(0, Motion.Holders);
    }

    // ---------------------------------------------------------- ScrollControlsBar

    [Fact]
    public void ScrollControlsBar_DisposedMidRevealTween_ReleasesHolder()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);
        var region = new SD.Rectangle(200, 200, 400, 400);
        var bar = new ScrollControlsBar(region);
        try
        {
            bar.Show();
            PumpFor(30);
            // Kick Start() (private) to launch the reveal glide, then kill the form mid-flight.
            MethodInfo start = typeof(ScrollControlsBar)
                .GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance)!;
            start.Invoke(bar, null);
            PumpFor(40);
            bar.Close();
            bar.Dispose();
        }
        finally
        {
            if (!bar.IsDisposed) bar.Dispose();
        }
        PumpUntil(() => Motion.Holders == 0, 3000);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void ScrollControlsBar_FadeOutAsync_ThenDispose_ReleasesHolder()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);
        var bar = new ScrollControlsBar(new SD.Rectangle(100, 100, 300, 300));
        bar.Show();
        PumpFor(20);
        Task fade = bar.FadeOutAsync();
        bar.Close();
        bar.Dispose();
        PumpUntil(() => fade.IsCompleted && Motion.Holders == 0, 3000);
        Assert.Equal(0, Motion.Holders);
    }

    // ---------------------------------------------------------- FastPinWindow

    private static SD.Bitmap Solid(int w, int h)
    {
        var b = new SD.Bitmap(w, h);
        using var g = SD.Graphics.FromImage(b);
        g.Clear(SD.Color.CornflowerBlue);
        return b;
    }

    [Fact]
    public void Pin_DisposedMidChromeFade_ReleasesHolder()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);
        var pin = new FastPinWindow(Solid(200, 150));
        pin.Show();
        PumpFor(30);
        MethodInfo update = typeof(FastPinWindow)
            .GetMethod("UpdateChromeAlpha", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo inside = typeof(FastPinWindow)
            .GetField("_mouseInside", BindingFlags.NonPublic | BindingFlags.Instance)!;
        inside.SetValue(pin, true);
        update.Invoke(pin, null);
        PumpFor(30);
        pin.Dispose(); // disposed, never closed
        PumpUntil(() => Motion.Holders == 0, 3000);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void Pin_DisposedWithoutClose_IsNotStillListedAsOpen()
    {
        var pin = new FastPinWindow(Solid(64, 64));
        pin.Show();
        PumpFor(10);
        Assert.True(FastPinWindow.AnyOpen);
        pin.Dispose();
        Assert.False(FastPinWindow.AnyOpen);
    }

    [Fact]
    public void Pin_ZoomChipText_NeverSaysZero()
    {
        Assert.Equal("100%", FastPinWindow.ZoomChipText(1.0));
        Assert.Equal("10%", FastPinWindow.ZoomChipText(0.1));
        Assert.Equal("4.2%", FastPinWindow.ZoomChipText(0.042));
        Assert.Equal("0.4%", FastPinWindow.ZoomChipText(0.004));
        Assert.Equal("0.1%", FastPinWindow.ZoomChipText(0.0000001));
        Assert.Equal("0.1%", FastPinWindow.ZoomChipText(0));
    }

    [Fact]
    public void Pin_TinyImage_StillGetsAWindow()
    {
        var pin = new FastPinWindow(Solid(1, 1));
        try
        {
            pin.Show();
            PumpFor(10);
            Assert.True(pin.ClientSize.Width >= 1 && pin.ClientSize.Height >= 1);
        }
        finally { pin.Dispose(); }
    }

    [Fact]
    public void Pin_LockedThenClosed_StopsTheLockedHoverTimer()
    {
        var pin = new FastPinWindow(Solid(80, 80));
        pin.Show();
        PumpFor(10);
        MethodInfo setLocked = typeof(FastPinWindow)
            .GetMethod("SetLocked", BindingFlags.NonPublic | BindingFlags.Instance)!;
        setLocked.Invoke(pin, new object[] { true });
        MethodInfo note = typeof(FastPinWindow)
            .GetMethod("NoteLockedHover", BindingFlags.NonPublic | BindingFlags.Instance)!;
        note.Invoke(pin, null);
        pin.Close();
        pin.Dispose();
        PumpFor(300);
        PumpUntil(() => Motion.Holders == 0, 2000);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>A band above the card too short to hold a preview yields no preview, rather
    /// than one grown back down over the card it is meant to float above.</summary>
    [Fact]
    public void TallPreview_ShortWorkingArea_IsNotOffered()
    {
        var wa = new SD.Rectangle(0, 0, 800, 260);
        var card = new SD.Rectangle(600, 100, 190, 150);
        Assert.True(TallCapturePreviewLayout.Place(new SD.Size(200, 6000), wa, card, 96).IsEmpty);
    }

    // ---------------------------------------------------- TallCapturePreview

    [Fact]
    public void TallPreview_NegativeYMonitor_StaysOnTheWorkingArea()
    {
        var wa = new SD.Rectangle(-1920, -1080, 1920, 1040);
        var card = new SD.Rectangle(-380, -180, 340, 150);
        SD.Rectangle r = TallCapturePreviewLayout.Place(new SD.Size(1000, 9000), wa, card, 144);
        Assert.True(r.Left >= wa.Left, $"left {r.Left} < {wa.Left}");
        Assert.True(r.Top >= wa.Top, $"top {r.Top} < {wa.Top}");
        Assert.True(r.Right <= wa.Right, $"right {r.Right} > {wa.Right}");
        Assert.True(r.Bottom <= wa.Bottom);
        Assert.True(r.Width >= 1 && r.Height >= 1);
    }

    [Fact]
    public void TallPreview_CardHardAgainstTheTop_IsNotOffered()
    {
        var wa = new SD.Rectangle(0, 0, 800, 300);
        var card = new SD.Rectangle(600, 130, 190, 150);
        Assert.True(TallCapturePreviewLayout.Place(new SD.Size(200, 6000), wa, card, 96).IsEmpty);
    }

    [Fact]
    public void TallPreview_ExtremeAspect_ProducesASaneBitmapSize()
    {
        var wa = new SD.Rectangle(0, 0, 3840, 2100);
        var card = new SD.Rectangle(3500, 1900, 320, 150);
        SD.Rectangle r = TallCapturePreviewLayout.Place(new SD.Size(1080, 32000), wa, card, 144);
        Assert.True(r.Width >= 1 && r.Height >= 1, $"{r}");
        Assert.True((long)r.Width * r.Height < 40_000_000, $"{r}");
    }

    [Fact]
    public void TallPreview_ShouldOffer_Gating()
    {
        Assert.False(TallCapturePreviewLayout.ShouldOffer(new SD.Size(0, 0)));
        Assert.False(TallCapturePreviewLayout.ShouldOffer(new SD.Size(-5, 900)));
        Assert.False(TallCapturePreviewLayout.ShouldOffer(new SD.Size(300, 100)));  // short side
        Assert.True(TallCapturePreviewLayout.ShouldOffer(new SD.Size(400, 100)));
        Assert.True(TallCapturePreviewLayout.ShouldOffer(new SD.Size(200, 6000)));
    }

    [Fact]
    public void TallPreview_FadeOutAndClose_WithAnimationsOff_DisposesInline()
    {
        using var _ = new Anim(false);
        Assert.Equal(0, Motion.Holders);
        using SD.Bitmap image = Solid(200, 3000);
        QuickAccessOverlayVisuals visuals = QuickAccessOverlayThemePalette.For(QuickAccessOverlayThemePalette.Current);
        TallCapturePreviewWindow? p = TallCapturePreviewWindow.TryCreate(
            image, new SD.Rectangle(1200, 700, 320, 150), 96, visuals);
        Assert.NotNull(p);
        p!.Show();
        PumpFor(20);
        p.FadeOutAndClose();
        Assert.True(p.IsDisposed);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void TallPreview_DisposedMidFade_ReleasesHolder()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);
        using SD.Bitmap image = Solid(200, 3000);
        QuickAccessOverlayVisuals visuals = QuickAccessOverlayThemePalette.For(QuickAccessOverlayThemePalette.Current);
        TallCapturePreviewWindow? p = TallCapturePreviewWindow.TryCreate(
            image, new SD.Rectangle(1200, 700, 320, 150), 96, visuals);
        Assert.NotNull(p);
        p!.Show();
        PumpFor(20);
        p.FadeOutAndClose();
        PumpFor(30);
        p.Dispose();
        PumpUntil(() => Motion.Holders == 0, 3000);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void TallPreview_FadeOutAndCloseTwice_DoesNotThrow()
    {
        using var _ = new Anim(true);
        using SD.Bitmap image = Solid(200, 3000);
        QuickAccessOverlayVisuals visuals = QuickAccessOverlayThemePalette.For(QuickAccessOverlayThemePalette.Current);
        TallCapturePreviewWindow? p = TallCapturePreviewWindow.TryCreate(
            image, new SD.Rectangle(1200, 700, 320, 150), 96, visuals);
        Assert.NotNull(p);
        p!.Show();
        PumpFor(20);
        p.FadeOutAndClose();
        p.FadeOutAndClose();
        PumpUntil(() => Motion.Holders == 0, 3000);
        Assert.Equal(0, Motion.Holders);
    }

    // ---------------------------------------------------------------- Toast

    [Fact]
    public void Toast_StickyWithTwoButtons_ButtonsDoNotOverlapAt200Percent()
    {
        using var _ = new Anim(false);
        var extras = new ToastExtras
        {
            Sticky = true,
            SecondaryText = "Don't open",
            CheckboxText = "Don't detect links",
        };
        using var t = new ToastWindow("Link copied", "https://example.com/a/very/long/path/that/wraps/around",
            new SD.Point(400, 400), () => { }, ToastSeverity.Success, false, extras);
        t.Show();
        PumpFor(60);
        var buttons = t.Controls.OfType<DarkButton>().ToList();
        Assert.Equal(2, buttons.Count);
        SD.Rectangle a = buttons[0].Bounds, b = buttons[1].Bounds;
        Assert.False(a.IntersectsWith(b), $"{a} intersects {b}");
        foreach (WF.Control c in t.Controls)
        {
            Assert.True(c.Bounds.Right <= t.ClientSize.Width,
                $"{c.GetType().Name} right {c.Bounds.Right} > client {t.ClientSize.Width}");
            Assert.True(c.Bounds.Bottom <= t.ClientSize.Height,
                $"{c.GetType().Name} bottom {c.Bounds.Bottom} > client {t.ClientSize.Height} ({c.Text})");
            Assert.True(c.Bounds.Left >= 0, $"{c.GetType().Name} left {c.Bounds.Left}");
        }
    }

    /// <summary>
    /// Sticky means the toast is not counting down behind the user's decision -- not that it
    /// has no bound at all. It is a TopMost window that a keyboard-only user reaches only
    /// through its own Escape hook, so an unanswered one still has to leave eventually.
    /// </summary>
    [Fact]
    public void Toast_Sticky_RunsABackstopRatherThanADwell()
    {
        using var _ = new Anim(false);
        using var t = new ToastWindow("Reading text", "Working on the selection.",
            new SD.Point(400, 400), null, ToastSeverity.Info, false, new ToastExtras { Sticky = true });
        t.Show();
        PumpFor(80);
        FieldInfo dismiss = typeof(ToastWindow)
            .GetField("_dismiss", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var timer = (WF.Timer)dismiss.GetValue(t)!;
        Assert.True(timer.Enabled, "a sticky toast still needs a bound on its life");
        Assert.True(timer.Interval >= 10_000,
            $"the backstop must not read as a dwell (was {timer.Interval} ms)");
    }

    [Fact]
    public void Toast_CheckboxOnly_FitsInsideTheCard()
    {
        using var _ = new Anim(false);
        var extras = new ToastExtras { CheckboxText = "Don't detect links" };
        using var t = new ToastWindow("Copied", "one line", new SD.Point(200, 200), null,
            ToastSeverity.Success, false, extras);
        t.Show();
        PumpFor(50);
        var check = t.Controls.OfType<DarkCheckBox>().Single();
        Assert.True(check.Bounds.Bottom <= t.ClientSize.Height,
            $"checkbox bottom {check.Bounds.Bottom} > client {t.ClientSize.Height}");
        Assert.True(check.Bounds.Right <= t.ClientSize.Width,
            $"checkbox right {check.Bounds.Right} > client {t.ClientSize.Width}");
    }

    [Fact]
    public void Toast_ShowingASecondToastDisposesTheSticky()
    {
        using var _ = new Anim(false);
        var working = new ToastWindow("Reading text", "Working.", new SD.Point(300, 300), null,
            ToastSeverity.Info, false, new ToastExtras { Sticky = true });
        working.Show();
        PumpFor(40);
        var result = new ToastWindow("Text copied", "hello", new SD.Point(300, 300), null);
        result.Show();
        PumpFor(40);
        Assert.True(working.IsDisposed);
        result.Dispose();
    }

    // -------------------------------------------------- Settings migration

    [Fact]
    public void Settings_LoadsAnOldFileWithMissingKeys()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winshot-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        // An old file: no SavedColors, no pin hotkeys, PostCaptureAction as a single string.
        File.WriteAllText(path, """
        {
          "SaveFolder": "C:\\shots",
          "PostCaptureAction": "Pin",
          "OcrDetectLinks": true,
          "OcrLanguage": "auto"
        }
        """);
        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))!;
        Assert.NotNull(s.SavedColors);
        Assert.Equal(string.Empty, s.HotkeyPinToggleVisibility);
        Assert.Equal(string.Empty, s.HotkeyPinCloseAll);
        Assert.Equal(string.Empty, s.HotkeyPinLast);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Settings_SavedColorsExplicitlyNull_DoesNotCrashTheHelpers()
    {
        var s = JsonSerializer.Deserialize<Settings>("""{"SavedColors": null}""")!;
        Assert.Null(s.SavedColors);
        Assert.Empty(WinShot.Core.SavedColors.Sanitize(s.SavedColors));
        List<string> next = WinShot.Core.SavedColors.With(s.SavedColors, SD.Color.Red);
        Assert.Single(next);
    }

    [Fact]
    public void Settings_SavedColorsGarbage_IsDropped()
    {
        var stored = new List<string> { "not a colour", "", "#GGGGGG", "#FF0000", "ff0000", "#F00" };
        List<string> clean = WinShot.Core.SavedColors.Sanitize(stored);
        Assert.Equal(new[] { "#FF0000" }, clean);
    }

    [Fact]
    public void SavedColors_KeepsTenAfterElevenPicks()
    {
        List<string> list = new();
        for (int i = 0; i < 11; i++)
            list = WinShot.Core.SavedColors.With(list, SD.Color.FromArgb(255, i * 20, 0, 0));
        Assert.Equal(10, list.Count);
        Assert.Equal("#C80000", list[0]);
    }

    // ------------------------------------------------------------ ColorFormatter

    [Theory]
    [InlineData("#F30", 0xFF, 0x33, 0x00)]
    [InlineData("f30", 0xFF, 0x33, 0x00)]
    [InlineData("##FF0000", 0xFF, 0x00, 0x00)]
    public void ColorFormatter_TryParse_Tolerant(string text, int r, int g, int b)
    {
        Assert.True(ColorFormatter.TryParse(text, out SD.Color c));
        Assert.Equal((r, g, b), (c.R, c.G, c.B));
    }

    [Theory]
    [InlineData("#-10000")]
    [InlineData("#+F0000")]
    [InlineData("# FF0000")]
    [InlineData("FF 00 00")]
    [InlineData(null)]
    [InlineData("")]
    public void ColorFormatter_TryParse_RejectsJunk(string? text)
    {
        Assert.False(ColorFormatter.TryParse(text, out _));
    }

    [Fact]
    public void ColorFormatter_Oklch_MatchesKnownValues()
    {
        // css oklch for pure red is about 62.8% 0.258 29.23
        string s = ColorFormatter.Format(SD.Color.FromArgb(255, 255, 0, 0), ColorFormat.Oklch);
        Assert.StartsWith("oklch(62.8%", s);
        Assert.Equal("oklch(0% 0 0)", ColorFormatter.Format(SD.Color.Black, ColorFormat.Oklch));
        Assert.Equal("oklch(100% 0 0)", ColorFormatter.Format(SD.Color.White, ColorFormat.Oklch));
    }

    [Fact]
    public void ColorFormatter_Hsl_Grey()
    {
        Assert.Equal("hsl(0, 0%, 50%)",
            ColorFormatter.Format(SD.Color.FromArgb(128, 128, 128), ColorFormat.Hsl));
    }

    // ----------------------------------------------------------------- OCR link

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/x?y=1", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/windows/system32", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("see https://example.com now", false)]
    [InlineData("https://example.com\nhttps://evil.com", false)]
    public void OcrLink_OnlyBareHttpUrls(string text, bool expected)
    {
        Assert.Equal(expected, OcrLink.Detect(text, true) is not null);
    }

    [Fact]
    public void OcrLink_DisabledMeansNever()
    {
        Assert.Null(OcrLink.Detect("https://example.com", false));
    }

    // ------------------------------------------------------- Auto-scroll gate

    [Theory]
    [InlineData(true, 250, 250, true)]
    [InlineData(true, 50, 50, false)]
    [InlineData(false, 50, 50, true)]
    public void AutoScrollGate(bool cursorRouted, int x, int y, bool expected)
    {
        Assert.Equal(expected, ScrollAutoScrollGate.MayInject(
            cursorRouted, new SD.Rectangle(100, 100, 300, 300), new SD.Point(x, y)));
    }

    [Fact]
    public void AutoScrollGate_NegativeYMonitor()
    {
        var region = new SD.Rectangle(-1800, -900, 400, 400);
        Assert.True(ScrollAutoScrollGate.MayInject(true, region, new SD.Point(-1700, -800)));
        Assert.False(ScrollAutoScrollGate.MayInject(true, region, new SD.Point(100, 100)));
    }

    // --------------------------------------------------------- Reverse capture

    /// <summary>A synthetic page: a tall source strip sampled through a moving viewport.</summary>
    private static SD.Bitmap Strip(int w, int h)
    {
        var b = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(b);
        g.Clear(SD.Color.White);
        var rand = new Random(7);
        using var f = new SD.Font("Segoe UI", 12);
        for (int y = 0; y < h; y += 24)
        {
            using var brush = new SD.SolidBrush(SD.Color.FromArgb(rand.Next(200), rand.Next(200), rand.Next(200)));
            g.DrawString($"row {y} {rand.Next(100000)}", f, brush, 6, y);
        }
        return b;
    }

    [Fact]
    public async Task Reverse_AutoScroll_SendsTheWheelUpwardAndGrows()
    {
        using SD.Bitmap page = Strip(400, 2400);
        var region = new SD.Rectangle(0, 0, 400, 400);
        int top = page.Height - region.Height;   // start at the BOTTOM of the page
        int steps = 0;
        var deltas = new List<int>();

        SD.Bitmap Grab(SD.Rectangle _)
        {
            var frame = new SD.Bitmap(region.Width, region.Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(frame);
            g.DrawImage(page, new SD.Rectangle(0, 0, region.Width, region.Height),
                0, top, region.Width, region.Height, SD.GraphicsUnit.Pixel);
            return frame;
        }

        using var cts = new CancellationTokenSource();
        void Scroll(int delta, bool horizontal)
        {
            deltas.Add(delta);
            // Positive wheel delta = scroll toward the top of the document.
            top = Math.Clamp(top - (delta / 120 * 40), 0, page.Height - region.Height);
            if (++steps >= 20 || top == 0) cts.CancelAfter(1500);
        }

        SD.Bitmap? result = await ScrollingCaptureService.RunAsync(
            region, ScrollDirection.Vertical, () => true, _ => { }, _ => { }, cts.Token,
            preview: null, frameSource: Grab, paneScoped: false,
            cursorSource: () => new SD.Point(region.X + 10, region.Y + 10),
            scrollInput: Scroll, reverse: true);

        Assert.NotEmpty(deltas);
        Assert.All(deltas, d => Assert.True(d > 0,
            $"reverse capture sent a downward wheel delta {d} — the page would scroll away from the content"));
        Assert.NotNull(result);
        Assert.True(result!.Height > region.Height,
            $"reverse auto-scroll did not grow: {result.Width}x{result.Height} after {steps} steps");
        result.Dispose();
    }

    [Fact]
    public async Task AutoScroll_NoPresetDirection_StillStitchesAVerticalPane()
    {
        // The `_direction ??= Vertical` default was deleted in favour of axis discovery;
        // an auto-scroll run with no preset must still lock vertical and grow.
        using SD.Bitmap page = Strip(400, 2400);
        var region = new SD.Rectangle(0, 0, 400, 400);
        int top = 0, steps = 0;

        SD.Bitmap Grab(SD.Rectangle _)
        {
            var frame = new SD.Bitmap(region.Width, region.Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(frame);
            g.DrawImage(page, new SD.Rectangle(0, 0, region.Width, region.Height),
                0, top, region.Width, region.Height, SD.GraphicsUnit.Pixel);
            return frame;
        }

        using var cts = new CancellationTokenSource();
        void Scroll(int delta, bool horizontal)
        {
            if (horizontal) return;
            top = Math.Clamp(top - (delta / 120 * 40), 0, page.Height - region.Height);
            if (++steps >= 20) cts.CancelAfter(1500);
        }

        SD.Bitmap? result = await ScrollingCaptureService.RunAsync(
            region, presetDirection: null, autoScroll: () => true, status: _ => { }, hint: _ => { },
            ct: cts.Token, preview: null, frameSource: Grab, paneScoped: false,
            cursorSource: () => new SD.Point(region.X + 10, region.Y + 10),
            scrollInput: Scroll);

        Assert.NotNull(result);
        Assert.True(result!.Height > region.Height,
            $"auto-scroll with no preset direction did not grow: {result.Width}x{result.Height} after {steps} steps");
        result.Dispose();
    }

    [Fact]
    public void ReverseForCommand()
    {
        Assert.True(ScrollCaptureCommand.ReverseForCommand("scroll-up"));
        Assert.False(ScrollCaptureCommand.ReverseForCommand("scroll-horizontal"));
        Assert.False(ScrollCaptureCommand.ReverseForCommand("scrolling"));
        Assert.Equal(ScrollDirection.Vertical, ScrollCaptureCommand.DirectionForCommand("scroll-up"));
    }

    // ------------------------------------------------------------- History

    [Fact]
    public void HistoryService_IsVideo_HandlesNullAndOddPaths()
    {
        Assert.False(HistoryService.IsVideo(null));
        Assert.False(HistoryService.IsVideo(""));
        Assert.False(HistoryService.IsVideo("shot.png"));
        Assert.True(HistoryService.IsVideo("clip.MP4"));
        Assert.True(HistoryService.IsVideo(@"C:\a b\clip.webm"));
    }
}
