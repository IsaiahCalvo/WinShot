using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Isaiah, 2026-11-03: a capture "clears my desktop" on the laptop screen, and a Start menu
/// that was open is gone from the frozen shot. Two causes, one per half of this file.
/// </summary>
[Collection("MotionRefcount")]
public class CaptureFreshnessTests
{
    // ---- the warm grabber must not serve an old frame ----------------------------------

    /// <summary>
    /// The warm duplication kept the last frame it had copied whenever AcquireNextFrame
    /// failed. That is right for a wait timeout (nothing changed) and wrong for everything
    /// else: after Windows takes a screen's duplication away it never delivers again, so the
    /// freeze showed that screen as it looked before the refresh-rate switch, sleep or lock.
    /// </summary>
    [Theory]
    [InlineData(0x887A0027u, true)]   // DXGI_ERROR_WAIT_TIMEOUT: the screen has not changed
    [InlineData(0x887A0026u, false)]  // DXGI_ERROR_ACCESS_LOST: mode switch, sleep, lock, full screen
    [InlineData(0x887A0005u, false)]  // DXGI_ERROR_DEVICE_REMOVED: driver reset
    [InlineData(0x887A0001u, false)]  // DXGI_ERROR_INVALID_CALL
    [InlineData(0x80070005u, false)]  // E_ACCESSDENIED: secure desktop
    public void OnlyAWaitTimeoutKeepsTheLastFrame(uint hresult, bool keeps)
        => Assert.Equal(keeps, DesktopDuplicationCapture.KeepsLastFrame(hresult));

    private static readonly SD.Rectangle Laptop = new(0, 0, 400, 250);
    private static readonly SD.Rectangle External = new(400, 0, 400, 250);
    private static readonly SD.Rectangle Both = new(0, 0, 800, 250);

    /// <summary>Paints a "desktop": a wallpaper with a few windows on it.</summary>
    private static SD.Bitmap Desktop(SD.Rectangle bounds, int seed)
    {
        var bmp = new SD.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.FromArgb(30, 60 + seed * 40 % 160, 110));
        var rng = new Random(seed);
        for (int i = 0; i < 6; i++)
        {
            using var brush = new SD.SolidBrush(SD.Color.FromArgb(rng.Next(256), rng.Next(256), rng.Next(256)));
            g.FillRectangle(brush, rng.Next(bounds.Width - 60), rng.Next(bounds.Height - 40), 60 + rng.Next(200), 40 + rng.Next(150));
        }
        return bmp;
    }

    private static Func<SD.Rectangle, SD.Bitmap?> RowsOf(SD.Bitmap live, SD.Rectangle liveBounds) => row =>
        live.Clone(new SD.Rectangle(row.X - liveBounds.X, row.Y - liveBounds.Y, row.Width, row.Height),
            System.Drawing.Imaging.PixelFormat.Format32bppRgb);

    [Fact]
    public void AFrameThatMatchesTheScreenPasses()
    {
        using SD.Bitmap screen = Desktop(Both, 1);
        Assert.Null(FreezeFrameCheck.FindStaleScreen(screen, Both, new[] { Laptop, External }, RowsOf(screen, Both)));
    }

    /// <summary>The reported case: the external screens are right and the laptop panel's
    /// frame is an old picture.</summary>
    [Fact]
    public void AnOldPictureOfOneScreenIsCaught()
    {
        using SD.Bitmap now = Desktop(Both, 1);
        using SD.Bitmap frame = (SD.Bitmap)now.Clone();
        using (SD.Bitmap old = Desktop(Laptop, 7))
        using (var g = SD.Graphics.FromImage(frame))
            g.DrawImageUnscaled(old, 0, 0);

        var stale = FreezeFrameCheck.FindStaleScreen(frame, Both, new[] { Laptop, External }, RowsOf(now, Both));
        Assert.NotNull(stale);
        Assert.Equal(Laptop, stale!.Value.Screen);
    }

    /// <summary>A playing video or a ticking clock changes a little of the screen between the
    /// two reads. That must not throw away a good frame.</summary>
    [Fact]
    public void ASmallMovingPartOfTheScreenIsNotStale()
    {
        using SD.Bitmap now = Desktop(Both, 1);
        using SD.Bitmap frame = (SD.Bitmap)now.Clone();
        using (var g = SD.Graphics.FromImage(frame))
            g.FillRectangle(SD.Brushes.White, 20, 0, 40, 250); // a tenth of the laptop's width, every row

        Assert.Null(FreezeFrameCheck.FindStaleScreen(frame, Both, new[] { Laptop, External }, RowsOf(now, Both)));
    }

    [Fact]
    public void AScreenThatCannotBeReadIsSkippedNotCalledStale()
    {
        using SD.Bitmap frame = Desktop(Both, 1);
        Assert.Null(FreezeFrameCheck.FindStaleScreen(frame, Both, new[] { Laptop, External }, _ => null));
    }

    // ---- a deferred open must not take focus before its frame ---------------------------

    [Fact]
    public void OnlyADeferredOpenHoldsFocusBack()
    {
        Assert.True(SelectorFreeze.HoldsFocusUntilFrozen(SelectorEntry.TransparentUntilFrozen));
        Assert.False(SelectorFreeze.HoldsFocusUntilFrozen(SelectorEntry.OpaqueFrozen));
        Assert.False(SelectorFreeze.HoldsFocusUntilFrozen(SelectorEntry.LiveTranslucent));
        // Long enough for a deferred grab (85-160 ms measured), short enough that keys
        // typed into a slow open still reach the selector soon.
        Assert.InRange(SelectorFreeze.HoldFocusMaxMs, 200, 500);
    }

    /// <summary>
    /// Taking focus closes the Start menu, a context menu or a tooltip. A deferred open used
    /// to take it the moment its alpha-0 surfaces went up, then grab the frame: the menu was
    /// closed by the time the picture was taken. Here a plain window stands in for the menu,
    /// the warm grab misses and the deferred grab is held open, and the selector must leave
    /// focus where it was until the frame is in hand.
    /// </summary>
    [Fact]
    public void ADeferredOpenLeavesFocusAloneUntilItsFrameLands()
    {
        RunSta(() =>
        {
            Func<SD.Bitmap?> warm = SelectorFreeze.WarmGrab;
            Func<SD.Bitmap> deferred = SelectorFreeze.DeferredGrab;
            using var release = new ManualResetEventSlim(false);
            SD.Rectangle vs = CaptureService.VirtualScreen;
            SelectorFreeze.WarmGrab = () => null;
            SelectorFreeze.DeferredGrab = () =>
            {
                release.Wait(TimeSpan.FromSeconds(10));
                var frame = new SD.Bitmap(vs.Width, vs.Height);
                using (var g = SD.Graphics.FromImage(frame))
                    g.Clear(SD.Color.FromArgb(120, 140, 160));
                return frame;
            };

            using var menu = new WF.Form
            {
                Text = "menu stand-in",
                StartPosition = WF.FormStartPosition.Manual,
                Bounds = new SD.Rectangle(vs.X + 40, vs.Y + 40, 240, 160),
                ShowInTaskbar = false,
            };
            var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            try
            {
                menu.Show();
                SelectorForeground.Restore(menu);
                Pump(() => GetForegroundWindow() == menu.Handle, 1000);

                Task<WF.DialogResult> session = selector.ShowAsync();

                // Well past where the old code had already activated the selector.
                Pump(() => false, 250);
                Assert.NotEqual(selector.Handle, GetForegroundWindow());
                Assert.False(selector.ContainsFocus, "the selector took focus before its frame was grabbed");

                release.Set();
                bool focused = Pump(() => GetForegroundWindow() == selector.Handle, 3000);
                Assert.True(focused, "the selector never took focus once its frame landed");

                Complete(selector, WF.DialogResult.Cancel);
                Pump(() => session.IsCompleted, 2000);
                Assert.True(session.IsCompleted);
            }
            finally
            {
                release.Set();
                SelectorFreeze.WarmGrab = warm;
                SelectorFreeze.DeferredGrab = deferred;
                selector.Dispose();
            }
        });
    }

    /// <summary>
    /// The hold is bounded: a grab that stalls (remote-control software can hold the capture
    /// path for seconds) must not leave the keyboard going to another window.
    /// </summary>
    [Fact]
    public void AStalledGrabStillHandsTheSelectorFocusSoon()
    {
        RunSta(() =>
        {
            Func<SD.Bitmap?> warm = SelectorFreeze.WarmGrab;
            Func<SD.Bitmap> deferred = SelectorFreeze.DeferredGrab;
            using var release = new ManualResetEventSlim(false);
            SD.Rectangle vs = CaptureService.VirtualScreen;
            SelectorFreeze.WarmGrab = () => null;
            SelectorFreeze.DeferredGrab = () =>
            {
                release.Wait(TimeSpan.FromSeconds(10));
                return new SD.Bitmap(vs.Width, vs.Height);
            };

            var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            try
            {
                var clock = Stopwatch.StartNew();
                Task<WF.DialogResult> session = selector.ShowAsync();
                bool focused = Pump(() => GetForegroundWindow() == selector.Handle, 3000);
                Assert.True(focused, "a stalled grab kept focus away from the selector");
                Assert.True(clock.ElapsedMilliseconds < SelectorFreeze.HoldFocusMaxMs + 1500,
                    $"focus arrived after {clock.ElapsedMilliseconds} ms");

                Complete(selector, WF.DialogResult.Cancel);
                release.Set();
                Pump(() => session.IsCompleted, 2000);
            }
            finally
            {
                release.Set();
                SelectorFreeze.WarmGrab = warm;
                SelectorFreeze.DeferredGrab = deferred;
                selector.Dispose();
            }
        });
    }

    private static void Complete(FastRegionSelectorDialog selector, WF.DialogResult result) =>
        typeof(FastRegionSelectorDialog)
            .GetMethod("Complete", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(selector, new object[] { result });

    private static bool Pump(Func<bool> done, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            WF.Application.DoEvents();
            if (done())
                return true;
            Thread.Sleep(10);
        }
        WF.Application.DoEvents();
        return done();
    }

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
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
