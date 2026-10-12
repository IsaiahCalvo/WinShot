using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Overlay;
using WinShot.Pin;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The second review round's probes, kept as tests. Every one of these failed on the branch
/// before the fix beside it: the thumbnail render racing a pixel edit, the horizontal
/// axis probe being graded on the vertical axis, the scroll chrome's fade surviving a bare
/// Dispose, a sticky toast with no bound on how long it can hold the screen, and Toggle pins
/// visibility snapping in both directions.
/// </summary>
public class BreakerProbesRound2
{
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static object? Priv(object o, string name)
        => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);

    private static object? Call(object o, string name, params object?[] args)
        => o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args);

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void QueueThumbnailRender(FastQuickActionsWindow card)
        => typeof(FastQuickActionsWindow).GetField("_previewTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(card, Call(card, "LoadPreviewAsync"));

    private static bool Resampling(FastQuickActionsWindow card)
        => Priv(card, "_previewTask") is Task { IsCompleted: false };

    // ------------------------------------------------------------------
    // The thumbnail reads _image in the background, so every pixel edit
    // must wait until it finishes. Hover must not open another preview.
    // ------------------------------------------------------------------

    [Fact]
    public void RenderIdleGate_CoversTheThumbnailRender()
    {
        RunSta(() =>
        {
            // The card takes ownership of the bitmap and disposes it once nothing is reading
            // it, so the test must not also own it.
            var tall = new SD.Bitmap(700, 9000, SD.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = SD.Graphics.FromImage(tall)) g.Clear(SD.Color.SteelBlue);

            using var card = new FastQuickActionsWindow(tall, new SettingsService());
            card.Show();
            PumpFor(600); // let the first thumbnail render land, so _previewTask is complete

            Call(card, "SetHovering", true);
            QueueThumbnailRender(card);
            Assert.DoesNotContain(card.OwnedForms, f => f is TallCapturePreviewWindow);

            var idleTask = (Task<bool>)Call(card, "WaitForRenderIdleAsync")!;
            Assert.False(idleTask.IsCompleted && Resampling(card),
                "WaitForRenderIdleAsync reported idle while the thumbnail render was still reading _image - " +
                "a rotate here lands inside the pool thread's DrawImage.");

            for (int i = 0; i < 800 && !idleTask.IsCompleted; i++)
            { WF.Application.DoEvents(); Thread.Sleep(5); }
            card.Close();
        });
    }

    [Fact]
    public void RotatingWhileTheThumbnailRenders_DoesNotThrow()
    {
        RunSta(() =>
        {
            Exception? crash = null;
            for (int attempt = 0; attempt < 6 && crash is null; attempt++)
            {
                var tall = new SD.Bitmap(900, 16000, SD.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = SD.Graphics.FromImage(tall))
                {
                    g.Clear(SD.Color.SteelBlue);
                    for (int y = 0; y < 16000; y += 40)
                        g.FillRectangle(SD.Brushes.Orange, 0, y, 900, 12);
                }

                using var card = new FastQuickActionsWindow(tall, new SettingsService());
                card.Show();
                PumpFor(240);

                Call(card, "SetHovering", true);
                QueueThumbnailRender(card);
                Assert.DoesNotContain(card.OwnedForms, f => f is TallCapturePreviewWindow);

                int before = (int)Priv(card, "_imageVersion")!;
                var run = typeof(FastQuickActionsWindow)
                    .GetMethod("RunMenuAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try { run.Invoke(card, new object[] { "rotate-right", "Rotate right" }); }
                catch (TargetInvocationException ex) { crash = ex.InnerException; }
                catch (Exception ex) { crash = ex; }

                // The rotate is now deferred behind the thumbnail render, so wait for it to land
                // rather than for the thumbnail render alone -- that is the whole point of the gate.
                for (int i = 0; i < 800 && (int)Priv(card, "_imageVersion")! == before; i++)
                { WF.Application.DoEvents(); Thread.Sleep(5); }
                if (crash is null)
                    Assert.True((int)Priv(card, "_imageVersion")! > before,
                        "the rotate never landed");

                for (int i = 0; i < 400 && Resampling(card); i++)
                { WF.Application.DoEvents(); Thread.Sleep(5); }
                card.Close();
                PumpFor(120);
            }

            Assert.True(crash is null,
                "rotating while the thumbnail renders threw: " + crash);
        });
    }

    [Fact]
    public void ReplacingTheImageWhileTheThumbnailRenders_DoesNotThrow()
    {
        RunSta(() =>
        {
            Exception? crash = null;
            for (int attempt = 0; attempt < 6 && crash is null; attempt++)
            {
                var tall = new SD.Bitmap(900, 16000, SD.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = SD.Graphics.FromImage(tall)) g.Clear(SD.Color.SteelBlue);

                var card = new FastQuickActionsWindow(tall, new SettingsService());
                card.Show();
                PumpFor(240);

                Call(card, "SetHovering", true);

                QueueThumbnailRender(card);
                Assert.DoesNotContain(card.OwnedForms, f => f is TallCapturePreviewWindow);

                // The resize dialog's exit. It disposes the bitmap the pool thread may still
                // be drawing from, so it goes through the same gate a rotate does.
                var replace = typeof(FastQuickActionsWindow)
                    .GetMethod("ReplaceImage", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var smaller = new SD.Bitmap(450, 8000, SD.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    var gate = (Task<bool>)Call(card, "WaitForRenderIdleAsync")!;
                    for (int i = 0; i < 800 && !gate.IsCompleted; i++)
                    { WF.Application.DoEvents(); Thread.Sleep(5); }
                    Assert.True(gate.IsCompleted, "the render-idle gate never completed");
                    Assert.False(Resampling(card),
                        "the gate opened while the thumbnail render was still reading _image");
                    if (gate.Result) replace.Invoke(card, new object[] { smaller });
                    else smaller.Dispose();
                }
                catch (TargetInvocationException ex) { crash = ex.InnerException; }
                catch (Exception ex) { crash = ex; }

                for (int i = 0; i < 400 && Resampling(card); i++)
                { WF.Application.DoEvents(); Thread.Sleep(5); }
                card.Close();
                card.Dispose();
            }

            Assert.True(crash is null,
                "replacing the image while the thumbnail renders threw: " + crash);
        });
    }

    // ------------------------------------------------------------------
    // Scroll chrome: a form disposed without being closed must not strand a
    // tween holding Motion's high-resolution clock.
    // ------------------------------------------------------------------

    [Fact]
    public void ScrollPreviewPanel_DisposedMidFade_ReleasesItsHolder()
        => AssertDisposeReleases(() => new ScrollPreviewPanel(new SD.Rectangle(100, 100, 400, 400)));

    [Fact]
    public void ScrollControlsBar_DisposedMidFade_ReleasesItsHolder()
        => AssertDisposeReleases(() => new ScrollControlsBar(new SD.Rectangle(100, 100, 400, 400)));

    private static void AssertDisposeReleases(Func<WF.Form> create)
    {
        RunSta(() =>
        {
            Assert.Equal(0, Motion.Holders);
            WF.Form form = create();
            form.Show();
            WF.Application.DoEvents();
            int during = Motion.Holders;
            form.Dispose();               // disposed, never closed
            WF.Application.DoEvents();
            int after = Motion.Holders;
            for (int i = 0; i < 200 && Motion.Holders > 0; i++) { WF.Application.DoEvents(); Thread.Sleep(5); }
            Assert.True(during > 0, "expected a fade holder while showing");
            Assert.Equal(0, after);
        });
    }

    // ------------------------------------------------------------------
    // Auto-Scroll axis discovery.
    // ------------------------------------------------------------------

    /// <summary>A wide source strip sampled through a viewport that only responds sideways.</summary>
    private static SD.Bitmap WideStrip(int w, int h)
    {
        var b = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(b);
        g.Clear(SD.Color.White);
        var rand = new Random(11);
        using var f = new SD.Font("Segoe UI", 12);
        for (int x = 0; x < w; x += 24)
        {
            using var brush = new SD.SolidBrush(SD.Color.FromArgb(rand.Next(200), rand.Next(200), rand.Next(200)));
            g.DrawString("c" + x + "|" + rand.Next(100000), f, brush, x, 6 + ((x % 5) * 20));
        }
        return b;
    }

    [Fact]
    public async Task AutoScroll_DiscoversAHorizontalPane()
    {
        using SD.Bitmap page = WideStrip(2400, 400);
        var region = new SD.Rectangle(0, 0, 400, 400);
        int left = 0, steps = 0;

        SD.Bitmap Grab(SD.Rectangle _)
        {
            var frame = new SD.Bitmap(region.Width, region.Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(frame);
            g.DrawImage(page, new SD.Rectangle(0, 0, region.Width, region.Height),
                left, 0, region.Width, region.Height, SD.GraphicsUnit.Pixel);
            return frame;
        }

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        var trace = new List<string>();
        void Scroll(int delta, bool horizontal)
        {
            trace.Add("step h=" + horizontal + " d=" + delta);
            if (!horizontal) return;                 // this pane ignores vertical input
            left = Math.Clamp(left + (delta / 120 * 40), 0, page.Width - region.Width);
            if (++steps >= 20 || left == page.Width - region.Width) cts.CancelAfter(1500);
        }

        SD.Bitmap? result = await ScrollingCaptureService.RunAsync(
            region, presetDirection: null, autoScroll: () => true,
            status: t => trace.Add("s:" + t), hint: h => trace.Add("h:" + h),
            ct: cts.Token, preview: null, frameSource: Grab, paneScoped: false,
            cursorSource: () => new SD.Point(region.X + 10, region.Y + 10),
            scrollInput: Scroll);

        Assert.True(steps > 0, "axis discovery never sent a horizontal step - the flip did not happen.");
        Assert.NotNull(result);
        using (result)
        {
            Assert.True(result!.Width > region.Width,
                "horizontal auto-discovery did not grow: " + result.Width + "x" + result.Height +
                " after " + steps + " steps. trace: " + string.Join(" | ", trace));
        }
    }

    [Fact]
    public void ReverseCaptureNeverPairsWithAHorizontalAxis()
    {
        // Unflip() flips on Y regardless, so a reverse horizontal command would come back
        // upside down. No command may be both.
        foreach (string cmd in new[] { "scrolling", "scroll-horizontal", "scroll-up" })
        {
            bool reverse = ScrollCaptureCommand.ReverseForCommand(cmd);
            ScrollDirection? dir = ScrollCaptureCommand.DirectionForCommand(cmd);
            Assert.False(reverse && dir == ScrollDirection.Horizontal,
                "'" + cmd + "' is reverse+horizontal.");
        }
    }

    // ------------------------------------------------------------------
    // Selector pill: measured with TextRenderer (GDI), filled with DrawString
    // (GDI+). The pill must still hold the text on one line.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Drag over the part of the screen that scrolls")]
    [InlineData("Drag over the part that scrolls, then scroll up")]
    [InlineData("1920 x 1080")]
    public void SelectorPill_IsWideEnoughForTheRendererThatFillsIt(string text)
    {
        using var bmp = new SD.Bitmap(1200, 200, SD.Imaging.PixelFormat.Format32bppPArgb);
        using var g = SD.Graphics.FromImage(bmp);
        using var font = ThemePalette.UiFont(ThemePalette.TextBodyPt, SD.FontStyle.Bold);

        SD.Size gdi = WF.TextRenderer.MeasureText(text, font);
        SD.SizeF gdiPlus = g.MeasureString(text, font);
        Assert.True(gdiPlus.Width <= gdi.Width + 16,
            "'" + text + "' GDI=" + gdi.Width + " pill=" + (gdi.Width + 16) + " GDI+=" + gdiPlus.Width);
    }

    [Fact]
    public void SelectorPill_LongInstructionDoesNotWrap()
    {
        const string text = "Drag over the part of the screen that scrolls";
        using var bmp = new SD.Bitmap(1400, 300, SD.Imaging.PixelFormat.Format32bppPArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.Black);

        // Called directly rather than through reflection: DrawLabel has two public overloads
        // now (the plain one and the one that takes an opacity), and GetMethod by name alone
        // is an AmbiguousMatchException against a chrome API that is visible to this assembly
        // anyway. Scale 1 -- this asserts about wrapping, not about DPI.
        SelectorChrome.DrawLabel(g, new SD.Size(1400, 300), text, 200, 120, 1d);
        SD.Size pill = SelectorChrome.MeasureLabel(text, 1d);

        var rows = new List<int>();
        for (int y = 120; y < 120 + pill.Height; y++)
        {
            for (int x = 200; x < 200 + pill.Width; x++)
            {
                SD.Color c = bmp.GetPixel(x, y);
                if (c.R > 200 && c.G > 200 && c.B > 200) { rows.Add(y); break; }
            }
        }
        Assert.True(rows.Count > 0, "no text drawn at all");
        int span = rows[^1] - rows[0] + 1;
        Assert.True(span <= pill.Height - 6,
            "text spans " + span + "px inside a " + pill.Height + "px pill - it wrapped.");
    }

    // ------------------------------------------------------------------
    // Toast.
    // ------------------------------------------------------------------

    [Fact]
    public void AStickyToastStillHasABoundedLife()
    {
        RunSta(() =>
        {
            using var toast = new ToastWindow("Open this link?", "https://example.com",
                null, () => { }, ToastSeverity.Info, false,
                new ToastExtras { Sticky = true, SecondaryText = "Don't open" });
            toast.Show();
            WF.Application.DoEvents();
            // Sticky means "not a countdown", never "no bound at all": the toast is a TopMost
            // window presenting a decision, so an unanswered one must still go away.
            Assert.InRange(toast.DismissIntervalMs, 10_000, 120_000);
        });
    }

    [Fact]
    public void AFailingToggleLeavesTheToastUp()
    {
        RunSta(() =>
        {
            var extras = new ToastExtras
            {
                ToggleText = "Add breaks",
                Toggle = () => throw new System.Runtime.InteropServices.COMException("clipboard busy"),
            };
            using var toast = new ToastWindow("Text copied", "hello", null, null,
                ToastSeverity.Success, false, extras);
            toast.Show();
            WF.Application.DoEvents();

            DarkButton? toggle = toast.Controls.OfType<DarkButton>()
                .FirstOrDefault(b => b.Text == "Add breaks");
            Assert.NotNull(toggle);
            toggle!.PerformClick();
            PumpFor(300);
            Assert.False(toast.IsDisposed, "a failing toggle tore the toast down");
        });
    }

    // ------------------------------------------------------------------
    // Pins.
    // ------------------------------------------------------------------

    [Fact]
    public void TogglingPinVisibility_RampsAndLeavesNoMotionHolder()
    {
        RunSta(() =>
        {
            Assert.Equal(0, Motion.Holders);
            FastPinWindow.CloseAll();
            Func<bool> previousAnim = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            var capture = new SD.Bitmap(200, 120, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(capture)) g.Clear(SD.Color.Teal);
            var pin = new FastPinWindow(capture, new SettingsService())
            {
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-20_000, -20_000),
            };
            try
            {
                pin.Show();
                PumpFor(60);

                FastPinWindow.ToggleAllVisibility();
                PumpFor(Motion.FrameIntervalMs * 4);
                // Mid-ramp: on its way out rather than already gone. QuickMs is 140.
                Assert.True(pin.Visible, "the pin hid before its ramp had run");
                Assert.True(pin.Opacity < 1d, "hiding a pin snapped instead of ramping");

                PumpFor(600);
                Assert.False(pin.Visible, "the pin never actually hid");
                Assert.Equal(1d, pin.Opacity, 2); // restored while hidden, ready to come back

                FastPinWindow.ToggleAllVisibility();
                PumpFor(600);
                Assert.True(pin.Visible);
                Assert.Equal(1d, pin.Opacity, 2);
            }
            finally
            {
                pin.Close();
                pin.Dispose();
                Motion.AnimationsEnabled = previousAnim;
            }
            for (int i = 0; i < 200 && Motion.Holders > 0; i++) { WF.Application.DoEvents(); Thread.Sleep(5); }
            Assert.Equal(0, Motion.Holders);
        });
    }

    // ------------------------------------------------------------------
    // Settings.
    // ------------------------------------------------------------------

    [Fact]
    public void ASettingsFileWrittenBeforeTheseFieldsExisted_StillLoads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ws-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "settings.json");
        File.WriteAllText(file,
            "{\n  \"SaveFolder\": \"C:\\\\Shots\",\n  \"PostCaptureAction\": \"ShowQuickActions\",\n" +
            "  \"HotkeyCaptureRegion\": \"Ctrl+Shift+1\",\n  \"OcrDetectLinks\": true\n}");

        var loaded = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(file))!;
        Assert.Equal("", loaded.HotkeyPinLast);
        Assert.Equal("", loaded.HotkeyPinCloseAll);
        Assert.Equal("", loaded.HotkeyPinToggleVisibility);
        Assert.NotNull(loaded.SavedColors);
        Assert.Empty(loaded.SavedColors);
        Assert.Equal("auto", loaded.OcrLanguage);

        File.WriteAllText(file, "{ \"SavedColors\": null, \"OcrLanguage\": null }");
        var nulled = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(file))!;
        _ = SavedColors.Sanitize(nulled.SavedColors);
        WinShot.Ocr.OcrService.PreferredLanguage = nulled.OcrLanguage;
        Assert.Equal(WinShot.Ocr.OcrService.AutoLanguage, WinShot.Ocr.OcrService.PreferredLanguage);

        try { Directory.Delete(dir, true); } catch { }
    }
}
