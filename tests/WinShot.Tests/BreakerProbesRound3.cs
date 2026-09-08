using System.Diagnostics;
using System.Reflection;
using System.Threading;
using WinShot.Core;
using WinShot.Pin;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The third review round's probes, kept as tests. Each one failed on the branch before the
/// fix beside it: Toggle pins visibility ratcheting every pin dimmer when the gesture is
/// repeated inside its own ramp, the locked-pin dim resampling a 9,000 px capture on every
/// frame of that ramp, a working toast arming a machine-wide keyboard hook it has no answer
/// for, and the pin toolbar cutting between its five-chip and two-chip widths on lock.
/// </summary>
public class BreakerProbesRound3
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

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static object? Priv(object o, string name)
        => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);

    private static void SetPriv(object o, string name, object? value)
        => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    private static object? Call(object o, string name, params object?[] args)
        => o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args);

    /// <summary>A pin parked far off screen: these probes drive its own paint and tweens, and
    /// never want a window over the desktop the suite runs on.</summary>
    private static FastPinWindow OffscreenPin(int width, int height)
    {
        var capture = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppPArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(capture)) g.Clear(SD.Color.SteelBlue);
        return new FastPinWindow(capture, new SettingsService())
        {
            StartPosition = WF.FormStartPosition.Manual,
            Location = new SD.Point(-20_000, -20_000),
        };
    }

    // ------------------------------------------------------------------
    // Toggle pins visibility, pressed twice inside its own 140 ms ramp.
    // ------------------------------------------------------------------

    /// <summary>
    /// A pin is still Visible for the whole of its fade-out, so a second press inside the ramp
    /// used to read "still visible" and hide again -- and re-sample the half-faded Opacity as
    /// the value to come back to, so every repeat of the gesture left the pins permanently
    /// dimmer. Both statics are on a global hotkey and a tray row, where a double-tap is the
    /// ordinary way a user says "no, bring them back".
    /// </summary>
    [Fact]
    public void TogglingPinsTwiceInsideTheRampBringsThemBackAtFullOpacity()
    {
        RunSta(() =>
        {
            FastPinWindow.CloseAll();
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            FastPinWindow pin = OffscreenPin(200, 120);
            try
            {
                pin.Show();
                PumpFor(80);
                Assert.True(pin.Visible);

                FastPinWindow.ToggleAllVisibility();  // hide
                PumpFor(60);                          // ~mid ramp (QuickMs = 140)
                FastPinWindow.ToggleAllVisibility();  // "no, bring them back"
                PumpFor(700);

                Assert.True(pin.Visible, "the second press hid the pin again instead of showing it");
                Assert.Equal(1d, pin.Opacity, 2);

                // And the gesture still works from there, at the same opacity.
                FastPinWindow.ToggleAllVisibility();
                PumpFor(700);
                Assert.False(pin.Visible);
                FastPinWindow.ToggleAllVisibility();
                PumpFor(700);
                Assert.True(pin.Visible);
                Assert.Equal(1d, pin.Opacity, 2);
            }
            finally
            {
                pin.Close();
                pin.Dispose();
                Motion.AnimationsEnabled = previous;
                for (int i = 0; i < 400 && Motion.Holders > 0; i++) { WF.Application.DoEvents(); Thread.Sleep(5); }
                Assert.Equal(0, Motion.Holders);
            }
        });
    }

    // ------------------------------------------------------------------
    // The locked-pin hover dim on a tall capture.
    // ------------------------------------------------------------------

    /// <summary>
    /// The dim ramps the image alpha over ~18 frames with an 8 ms budget each, and it
    /// invalidates the whole window (unlike the chrome fade, which clips to its band). While
    /// OnPaint re-resampled the source on every one of those frames, a 1200x9000 pin cost
    /// 68 ms a frame -- a ~15 fps ramp holding the UI thread, twice per hover, on a window
    /// that answers HTTRANSPARENT so the pointer crosses it freely.
    /// </summary>
    [Fact]
    public void ADimmedFrameOnATallPinFitsTheFrameBudget()
    {
        RunSta(() =>
        {
            FastPinWindow pin = OffscreenPin(1200, 9000);
            try
            {
                pin.Show();
                PumpFor(120);

                MethodInfo paint = typeof(WF.Control).GetMethod("OnPaint",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                using var surface = new SD.Bitmap(
                    Math.Max(1, pin.ClientSize.Width), Math.Max(1, pin.ClientSize.Height));
                using SD.Graphics g = SD.Graphics.FromImage(surface);
                var full = new SD.Rectangle(SD.Point.Empty, pin.ClientSize);

                void Frame() => paint.Invoke(pin, new object?[] { new WF.PaintEventArgs(g, full) });

                SetPriv(pin, "_imageAlpha", 0.6d);
                Frame(); // the first frame builds the scaled copy; the ramp's frames reuse it

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 10; i++) Frame();
                double perFrame = sw.Elapsed.TotalMilliseconds / 10;

                Assert.True(perFrame <= Motion.FrameIntervalMs,
                    $"a dimmed frame on a {pin.ClientSize.Width}x{pin.ClientSize.Height} pin took " +
                    $"{perFrame:0.0} ms against a {Motion.FrameIntervalMs} ms budget - the dim ramp " +
                    "is resampling the whole capture every frame again.");
            }
            finally { pin.Close(); pin.Dispose(); }
        });
    }

    // ------------------------------------------------------------------
    // The sticky toast's machine-wide keyboard hook.
    // ------------------------------------------------------------------

    /// <summary>
    /// The hook swallows keys for every process on the machine, so it is only defensible for a
    /// toast that is actually asking something. The OCR working indicator is sticky and
    /// carries no buttons at all: arming it there took Escape away from whatever the user was
    /// typing in for the whole length of a read they never asked to be interrupted by.
    /// </summary>
    [Fact]
    public void OnlyAToastThatAsksSomethingArmsTheKeyboardHook()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => false;
            try
            {
                using (var working = new ToastWindow("Reading text", "Working on the selection.",
                    new SD.Point(400, 400), null, ToastSeverity.Info, false,
                    new ToastExtras { Sticky = true }))
                {
                    working.Show();
                    PumpFor(60);
                    Assert.Equal(IntPtr.Zero, (IntPtr)Priv(working, "_keyHook")!);
                }

                using (var decision = new ToastWindow("Open this link?", "https://example.com",
                    new SD.Point(400, 400), () => { }, ToastSeverity.Info, false,
                    new ToastExtras { Sticky = true, SecondaryText = "Don't open" }))
                {
                    decision.Show();
                    PumpFor(60);
                    Assert.NotEqual(IntPtr.Zero, (IntPtr)Priv(decision, "_keyHook")!);
                }
            }
            finally { Motion.AnimationsEnabled = previous; }
        });
    }

    /// <summary>
    /// Escape is only consumed while the pointer is on the card. The toast is
    /// ShowWithoutActivation and never has focus, so swallowing it everywhere ate the Escape
    /// the user aimed at their own app -- they had to press it twice. Enter is likewise not
    /// swallowed when there is no primary button for it to take.
    /// </summary>
    [Fact]
    public void EscapeIsOnlySwallowedWhileThePointerIsOverTheToast()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => false;
            try
            {
                using var toast = new ToastWindow("Open this link?", "https://example.com",
                    new SD.Point(400, 400), () => { }, ToastSeverity.Info, false,
                    new ToastExtras { Sticky = true, SecondaryText = "Don't open" });
                toast.Show();
                PumpFor(60);

                // The toast is parked off the pointer, so nothing about it is what the user is
                // looking at and it must take no key off the machine.
                Assert.False(toast.Bounds.Contains(WF.Cursor.Position));
                Assert.False(toast.ConsumesKey(WF.Keys.Escape));
                Assert.False(toast.ConsumesKey(WF.Keys.Enter));
                Assert.False(toast.ConsumesKey(WF.Keys.Tab));
            }
            finally { Motion.AnimationsEnabled = previous; }
        });
    }

    /// <summary>Enter is a key eaten for nothing on a toast with no primary button: the hook
    /// swallowed it and then had nothing to click.</summary>
    [Fact]
    public void EnterIsNeverSwallowedByAToastWithNoPrimaryButton()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => false;
            try
            {
                using var toast = new ToastWindow("Text copied", "hello",
                    new SD.Point(400, 400), null, ToastSeverity.Success, false,
                    new ToastExtras { Sticky = true, SecondaryText = "Undo" });
                toast.Show();
                PumpFor(60);
                // Even standing on the card, there is no primary for Enter to take.
                toast.Location = WF.Cursor.Position;
                PumpFor(20);
                Assert.False(toast.ConsumesKey(WF.Keys.Enter));
                Assert.True(toast.ConsumesKey(WF.Keys.Escape));
            }
            finally { Motion.AnimationsEnabled = previous; }
        });
    }

    // ------------------------------------------------------------------
    // The pin toolbar's lock/unlock width swap.
    // ------------------------------------------------------------------

    /// <summary>
    /// Locking takes the row from five chips to two. ToolbarVisible does not change (lock is
    /// always toggled with the pointer on the pin), so UpdateChromeAlpha returns early and
    /// nothing used to move the bar at all: it cut to a different width and three chips
    /// vanished in one frame, beside an image dim that eased over the same gesture.
    /// </summary>
    [Fact]
    public void LockingGlidesTheToolbarBetweenItsTwoWidths()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            FastPinWindow pin = OffscreenPin(900, 600);
            try
            {
                pin.Show();
                PumpFor(60);
                SetPriv(pin, "_chromeAlpha", 1d); // the pointer is on the pin: chrome is up

                int wide = ((SD.Rectangle)Call(pin, "ToolbarBounds")!).Width;
                Call(pin, "SetLocked", true);

                Assert.NotNull((int[]?)Priv(pin, "_lockSwapFrom"));

                // Sampled across the glide: the bar is never at either end for the whole of it.
                var widths = new System.Collections.Generic.List<int>();
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < Motion.SwapMs + 200)
                {
                    widths.Add(((SD.Rectangle)Call(pin, "ToolbarBounds")!).Width);
                    WF.Application.DoEvents();
                    Thread.Sleep(4);
                }

                int settled = ((SD.Rectangle)Call(pin, "ToolbarBounds")!).Width;
                Assert.True(settled < wide, $"locked bar {settled} is not narrower than {wide}");
                Assert.Null(Priv(pin, "_lockSwapFrom"));
                Assert.Contains(widths, w => w < wide && w > settled);
            }
            finally
            {
                pin.Close();
                pin.Dispose();
                Motion.AnimationsEnabled = previous;
                for (int i = 0; i < 400 && Motion.Holders > 0; i++) { WF.Application.DoEvents(); Thread.Sleep(5); }
                Assert.Equal(0, Motion.Holders);
            }
        });
    }

    /// <summary>
    /// Chips are laid out from the bar's right edge so the pair that survives a lock swap
    /// holds its place while the bar glides. That has to leave the settled rows exactly where
    /// the old left-anchored layout put them.
    /// </summary>
    [Fact]
    public void ChipsSitInTheSamePlaceTheLeftAnchoredLayoutPutThem()
    {
        RunSta(() =>
        {
            FastPinWindow pin = OffscreenPin(900, 600);
            try
            {
                pin.Show();
                PumpFor(60);
                foreach (bool locked in new[] { false, true })
                {
                    if (locked) Call(pin, "SetLocked", true);
                    var bar = (SD.Rectangle)Call(pin, "ToolbarBounds")!;
                    var offered = (int[])Priv(pin, locked ? "_lockedButtons" : "_allButtons")!;
                    int size = (int)Call(pin, "Ui", 28)!;  // ToolbarButtonSizeLogical
                    int gap = (int)Call(pin, "Ui", 4)!;    // ToolbarButtonGapLogical
                    int pad = (int)Call(pin, "Ui", 6)!;    // ToolbarPadLogical
                    for (int slot = 0; slot < offered.Length; slot++)
                    {
                        var actual = (SD.Rectangle)Call(pin, "ButtonBounds", offered[slot])!;
                        Assert.Equal(bar.Left + pad + (slot * (size + gap)), actual.Left);
                        Assert.Equal(bar.Top + pad, actual.Top);
                    }
                }
            }
            finally { pin.Close(); pin.Dispose(); }
        });
    }
}
