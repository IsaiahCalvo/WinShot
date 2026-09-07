using System.Diagnostics;
using System.Runtime.InteropServices;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The edge cases a breaker pass turned up around the one feedback surface and the WinForms
/// controls it is built from: tween holders after churn and mid-exit resurrection, anchors off
/// every monitor, degenerate bodies, an aborted click, and a segmented control resized mid-glide.
/// Kept as regressions — each one shipped as a real defect at least once.
/// </summary>
public class ToastAndControlEdgeCaseTests
{
    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void PumpUntil(Func<bool> c, int ms = 8000)
    {
        var d = Stopwatch.StartNew();
        while (!c() && d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private const System.Reflection.BindingFlags NonPub =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    /// <summary>
    /// A progress readout is ONE message whose body keeps changing, not twenty messages.
    /// Showing a new toast per step disposes the live one outright — deliberately, so two
    /// different messages never cross-dissolve — which turned the update download into a
    /// strobe that replayed its 280 ms entrance and re-announced itself twenty times.
    /// </summary>
    [Fact]
    public void AProgressToastRewritesTheLiveCardInsteadOfReplacingIt()
    {
        using var _ = new Anim(false);

        // Nothing on screen: the caller has to show one first.
        Assert.False(ToastWindow.TryUpdateBody("Downloading update…", "5%"));

        var toast = new ToastWindow("Downloading update…", "5% of WinShot 2.0.", null, null, holdLong: true);
        try
        {
            toast.Show();
            // OnShown — where the toast becomes the live one — arrives on a posted message,
            // so the pump has to reach it before anything can be asked of it.
            PumpUntil(() => ToastWindow.TryUpdateBody("Downloading update…", "10% of WinShot 2.0."));

            Assert.True(ToastWindow.TryUpdateBody("Downloading update…", "45% of WinShot 2.0."));
            // Same window, new text — no second form, so no second entrance.
            Assert.False(toast.IsDisposed);
            Assert.Contains(
                toast.Controls.OfType<WF.Label>(),
                l => l.Text.StartsWith("45%", StringComparison.Ordinal));

            // A DIFFERENT message is not this message: it must still replace the card, or
            // "Save failed" would quietly overwrite the body of "Copied".
            Assert.False(ToastWindow.TryUpdateBody("Save failed", "Nope."));
        }
        finally
        {
            toast.Dispose();
            PumpUntil(() => Motion.Holders == 0);
        }
    }

    // ---- toast churn: App.ShowToast disposes and replaces the live toast --------------
    [Fact]
    public void ToastChurnLeavesNoClockHolder()
    {
        using var a = new Anim(true);
        Assert.Equal(0, Motion.Holders);
        ToastWindow? live = null;
        for (int i = 0; i < 15; i++)
        {
            live?.Dispose();
            live = new ToastWindow($"t{i}", $"body {i}", null, null, ToastSeverity.Error);
            live.Show();
            PumpFor(12);
        }
        live!.Dispose();
        PumpFor(100);
        Assert.Equal(0, Motion.Holders);
    }

    // ---- close -> hover resurrect mid-exit -> close again -----------------------------
    [Fact]
    public void ToastResurrectedMidExitStillClosesAndReleases()
    {
        using var a = new Anim(true);
        Assert.Equal(0, Motion.Holders);
        var toast = new ToastWindow("Saved", "shot.png", new SD.Point(60, 60), null);
        toast.Show();
        PumpFor(300);
        toast.Close();                       // starts the exit fade
        PumpFor(60);
        // Simulate the pointer landing on the card mid-fade.
        typeof(ToastWindow).GetMethod("HoldOpen", NonPub)!.Invoke(toast, null);
        PumpFor(120);
        Assert.False(toast.IsDisposed);
        toast.Close();
        PumpUntil(() => toast.IsDisposed);
        PumpFor(120);
        Assert.True(toast.IsDisposed);
        Assert.Equal(0, Motion.Holders);
    }

    // ---- anchors nowhere near a monitor ----------------------------------------------
    [Theory]
    [InlineData(-500000, -500000)]
    [InlineData(500000, 500000)]
    [InlineData(-8000, -8000)]
    public void ToastSurvivesAnAnchorOffEveryMonitor(int x, int y)
    {
        using var a = new Anim(false);
        using var toast = new ToastWindow("Text copied", "hello", new SD.Point(x, y), null);
        toast.Show();
        PumpFor(30);
        SD.Rectangle area = WF.Screen.FromPoint(new SD.Point(x, y)).WorkingArea;
        Assert.True(area.IntersectsWith(toast.Bounds), $"toast at {toast.Bounds} not on {area}");
    }

    // ---- degenerate bodies ------------------------------------------------------------
    [Fact]
    public void ToastHandlesEmptyAndEnormousBodies()
    {
        using var a = new Anim(false);
        using var empty = new ToastWindow("", "", null, null);
        empty.Show();

        var clock = Stopwatch.StartNew();
        string huge = new string('x', 200_000);
        using var big = new ToastWindow("Save failed", huge, null, null, ToastSeverity.Error);
        big.Show();
        clock.Stop();
        Assert.True(clock.ElapsedMilliseconds < 3000, $"huge body took {clock.ElapsedMilliseconds} ms");
        Assert.True(big.Height < 400, $"huge body grew the card to {big.Height}");

        // A single space-free token, the case WordBreak cannot split.
        string token = new string('W', 900);
        Assert.True(ToastWindow.Trim(token, 244).Length < token.Length);
    }

    // ---- a keyboard commit that closes its own form mid press-fade --------------------
    [Fact]
    public void DarkButtonThatClosesItsFormOnKeyboardCommitReleasesTheClock()
    {
        using var a = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        var form = new WF.Form { StartPosition = WF.FormStartPosition.Manual, Location = new SD.Point(40, 40), Size = new SD.Size(200, 100) };
        var button = new DarkButton { Text = "OK", Size = new SD.Size(80, 26) };
        button.Click += (_, _) => form.Close();
        form.Controls.Add(button);
        form.Show();
        form.ActiveControl = button;
        PumpFor(150);

        typeof(DarkButton).GetMethod("ProcessDialogKey", NonPub)!
            .Invoke(button, new object?[] { WF.Keys.Enter });

        PumpUntil(() => form.IsDisposed);
        PumpFor(250);
        Assert.True(form.IsDisposed);
        Assert.Equal(0, Motion.Holders);
    }

    // ---- press wash after releasing outside the button --------------------------------
    /// <summary>Press, drag off, release is how a click is aborted. The mouse is captured, so
    /// MouseUp still arrives after MouseLeave already eased the press away — re-lighting it
    /// acknowledged a commit that never happened.</summary>
    [Fact]
    public void DarkButtonReleasedOutsideDoesNotFlashAPress()
    {
        using var a = new Anim(true);
        using var form = new WF.Form { StartPosition = WF.FormStartPosition.Manual, Location = new SD.Point(-4000, -4000) };
        var button = new DarkButton { Text = "OK", Size = new SD.Size(80, 26) };
        form.Controls.Add(button);
        form.Show();

        var down = typeof(WF.Control).GetMethod("OnMouseDown", NonPub);
        var leave = typeof(WF.Control).GetMethod("OnMouseLeave", NonPub);
        var up = typeof(WF.Control).GetMethod("OnMouseUp", NonPub);
        var inside = new WF.MouseEventArgs(WF.MouseButtons.Left, 1, 5, 5, 0);
        var outside = new WF.MouseEventArgs(WF.MouseButtons.Left, 1, 400, 400, 0);
        down!.Invoke(button, new object?[] { inside });
        PumpFor(20);
        leave!.Invoke(button, new object?[] { EventArgs.Empty });
        PumpFor(200);
        up!.Invoke(button, new object?[] { outside });

        double press = PressAmount(button);
        Assert.True(press < 0.05, $"press wash after an outside release was {press}");

        // A release on the button is still a full press landing and easing off.
        down!.Invoke(button, new object?[] { inside });
        PumpFor(20);
        up!.Invoke(button, new object?[] { inside });
        Assert.True(PressAmount(button) > 0.5, "a real click no longer lands the press");

        PumpFor(250);
        Assert.Equal(0, Motion.Holders);
    }

    private static double PressAmount(DarkButton b)
    {
        object state = typeof(DarkButton).GetField("_press", NonPub)!.GetValue(b)!;
        return (double)state.GetType().GetProperty("Value")!.GetValue(state)!;
    }

    /// <summary>
    /// Handing the control a shorter list mid-life used to leave the selection pointing past
    /// the end of it, the pill gliding toward a cell that no longer exists, and nothing
    /// repainted. The setter clamps and abandons the glide instead.
    /// </summary>
    [Fact]
    public void SegmentedShrunkMidGlideClampsItsSelectionAndReleasesTheClock()
    {
        using var a = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        using var form = new WF.Form { StartPosition = WF.FormStartPosition.Manual, Location = new SD.Point(-4000, -4000) };
        var seg = new DarkSegmented { Size = new SD.Size(180, 26), Options = new[] { "A", "B", "C" } };
        form.Controls.Add(seg);
        form.Show();

        seg.SelectedIndex = 2;
        PumpFor(20);                          // mid-glide
        seg.Options = new[] { "A" };
        Assert.Equal(0, seg.SelectedIndex);

        using var bmp = new SD.Bitmap(180, 26);
        seg.DrawToBitmap(bmp, new SD.Rectangle(0, 0, 180, 26));
        form.Close();
        PumpFor(300);
        Assert.Equal(0, Motion.Holders);
    }

    // ---- segmented control edges ------------------------------------------------------
    [Fact]
    public void SegmentedWithNoOptionsAndRapidSelectionChangesSurvives()
    {
        using var a = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        using (var empty = new DarkSegmented { Size = new SD.Size(120, 26) })
        {
            empty.Options = Array.Empty<string>();
            empty.SelectedIndex = 0;
            using var bmp = new SD.Bitmap(120, 26);
            empty.DrawToBitmap(bmp, new SD.Rectangle(0, 0, 120, 26));
        }

        var seg = new DarkSegmented { Size = new SD.Size(180, 26) };
        seg.Options = new[] { "A", "B", "C" };
        using (var form = new WF.Form { StartPosition = WF.FormStartPosition.Manual, Location = new SD.Point(-4000, -4000) })
        {
            form.Controls.Add(seg);
            form.Show();
            for (int i = 0; i < 40; i++)
            {
                seg.SelectedIndex = i % 3;
                PumpFor(5);
            }
            seg.Size = new SD.Size(90, 26);   // resize mid-glide
            PumpFor(20);
        }
        PumpFor(300);
        Assert.Equal(0, Motion.Holders);
    }

    // ---- a zero-size segmented control still yields a paintable pill ------------------
    [Fact]
    public void PillBoundsOnAZeroSizedTrack()
    {
        SD.Rectangle r = DarkSegmented.PillBounds(new SD.Size(0, 0), 3, 1, 3);
        Assert.True(r.Width >= 1 && r.Height >= 1, $"got {r}");
    }

    // ---- RectangleTween fed a hostile amount ------------------------------------------
    [Theory]
    [InlineData(-5f)]
    [InlineData(5f)]
    [InlineData(float.PositiveInfinity)]
    public void RectangleTweenSurvivesAHostileAmount(float amount)
    {
        var t = new RectangleTween();
        t.Retarget(new SD.Rectangle(0, 0, 10, 10), new SD.Rectangle(100, 100, 50, 50));
        t.Update(amount);
        SD.Rectangle c = t.Current;
        Assert.True(c.Width >= 0 && c.Height >= 0, $"got {c}");
        Assert.InRange(c.X, -1, 200);
    }

    /// <summary>
    /// The caller-driven overload used to end on any amount at or past 1, which reads fine for
    /// EaseOutCubic and quietly breaks the entrance ease: EaseOutBack sails past the target
    /// and settles back, so a rect that treats the first frame over 1.0 as the end snaps home
    /// partway through the very curve the caller asked for. The end is Motion.Run's exact 1.0.
    /// </summary>
    [Fact]
    public void RectangleTweenRidesAnEasesOvershootInsteadOfEndingOnIt()
    {
        var tween = new RectangleTween();
        tween.Retarget(new SD.Rectangle(0, 0, 10, 10), new SD.Rectangle(100, 0, 10, 10));

        float overshot = (float)Motion.EaseOutBack(0.72d);
        Assert.True(overshot > 1f, $"EaseOutBack(0.72) is {overshot}, so this test proves nothing");
        Assert.True(tween.Update(overshot), "the tween ended on an overshoot frame");
        Assert.True(tween.Current.X > 100, $"the rect stopped at {tween.Current.X} instead of sailing past 100");

        Assert.False(tween.Update(1f));
        Assert.Equal(100, tween.Current.X);
    }

    // ---- FriendlyError over the failures the breaker list names ----------------------
    /// <summary>COMException derives from ExternalException, so the GDI+ arm caught it too and
    /// told a user whose video stop or history delete failed to "try the capture again".</summary>
    [Fact]
    public void FriendlyErrorDoesNotCallEveryComFailureAnImageFailure()
    {
        var com = new COMException("The RPC server is unavailable.", unchecked((int)0x800706BA));
        Assert.DoesNotContain("image", FriendlyError.For(com), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FriendlyErrorNamesABusyClipboard()
    {
        // CLIPBRD_E_CANT_OPEN — what Clipboard.SetDataObject throws when another app owns it.
        var busy = new COMException("OpenClipboard Failed", unchecked((int)0x800401D0));
        Assert.Contains("clipboard", FriendlyError.For(busy), StringComparison.OrdinalIgnoreCase);
    }

    // ---- the corner toast and the pinned quick-actions card share a corner ------------
    /// <summary>Both default to the working area's bottom-right at the same 16-DIP inset. The
    /// toast is TopMost and dismisses on click, so landing on the card hid it and ate the first
    /// click aimed at Copy or Edit for the whole dwell.</summary>
    [Fact]
    public void CornerToastSitsAboveAnOpenQuickActionsCard()
    {
        var area = new SD.Rectangle(0, 0, 1920, 1040);
        var toast = new SD.Size(300, 90);
        var card = new SD.Rectangle(1920 - 224 - 16, 1040 - 140 - 16, 224, 140);

        SD.Point p = ToastWindow.CornerPlacement(area, toast, card, 16, 8);
        var placed = new SD.Rectangle(p, toast);

        Assert.False(placed.IntersectsWith(card), $"toast at {placed} covers the card at {card}");
        Assert.True(placed.Bottom <= card.Top, "the toast should sit above the stack, not beside it");
        Assert.True(area.Contains(placed), $"toast at {placed} left the working area");
    }

    [Fact]
    public void CornerToastKeepsTheCornerWhenNoCardIsUp()
    {
        var area = new SD.Rectangle(0, 0, 1920, 1040);
        var toast = new SD.Size(300, 90);
        SD.Point p = ToastWindow.CornerPlacement(area, toast, SD.Rectangle.Empty, 16, 8);
        Assert.Equal(new SD.Point(1920 - 300 - 16, 1040 - 90 - 16), p);
    }

    /// <summary>A card stack taller than the screen leaves nowhere above it; the toast clamps
    /// into the working area rather than off the top of it.</summary>
    [Fact]
    public void CornerToastStaysOnScreenUnderAnOversizedStack()
    {
        var area = new SD.Rectangle(0, 0, 1920, 1040);
        var toast = new SD.Size(300, 90);
        var stack = new SD.Rectangle(1600, -400, 224, 1440);
        SD.Point p = ToastWindow.CornerPlacement(area, toast, stack, 16, 8);
        Assert.True(p.Y >= area.Top + 8, $"toast top {p.Y} left the working area");
    }

    // ---- scroll chrome on the topmost (possibly negative-Y) monitor -------------------
    [Fact]
    public void ScrollChromeIsBornOnTheRegionsMonitor()
    {
        // Pick the topmost monitor (Isaiah's stack puts two above the laptop, so this is the
        // negative-Y one) — but take the expectation from Screen.FromRectangle, the same call
        // the chrome's constructor makes. Screen.AllScreens caches its bounds on first use
        // while FromRectangle re-reads them, so when something earlier in the suite changes
        // the process's effective DPI awareness the two answer in different coordinate spaces
        // and the test failed on its own arithmetic rather than on the chrome's placement.
        SD.Rectangle picked = WF.Screen.AllScreens
            .Select(s => s.Bounds)
            .OrderBy(b => b.Y)
            .First();
        var region = new SD.Rectangle(picked.X + 40, picked.Y + 40, 300, 300);
        SD.Rectangle target = WF.Screen.FromRectangle(region).Bounds;

        using var bar = new WinShot.Scrolling.ScrollControlsBar(region);
        Assert.True(target.Contains(bar.Location), $"bar born at {bar.Location}, not on {target}");
        using var preview = new WinShot.Scrolling.ScrollPreviewPanel(region);
        Assert.True(target.Contains(preview.Location), $"preview born at {preview.Location}, not on {target}");
    }

    // ---- animation-off: nothing starts a timer at all --------------------------------
    [Fact]
    public void WithAnimationsOffNothingHoldsTheClock()
    {
        using var a = new Anim(false);
        Assert.Equal(0, Motion.Holders);

        using var form = new WF.Form { StartPosition = WF.FormStartPosition.Manual, Location = new SD.Point(-4000, -4000) };
        var button = new DarkButton { Text = "OK", Size = new SD.Size(80, 26) };
        var seg = new DarkSegmented { Size = new SD.Size(180, 26), Options = new[] { "A", "B" } };
        form.Controls.Add(button);
        form.Controls.Add(seg);
        form.Show();

        typeof(WF.Control).GetMethod("OnMouseEnter", NonPub)!
            .Invoke(button, new object?[] { EventArgs.Empty });
        seg.SelectedIndex = 1;
        Assert.Equal(0, Motion.Holders);

        var toast = new ToastWindow("Saved", "a.png", null, null, ToastSeverity.Success);
        toast.Show();
        Assert.Equal(0, Motion.Holders);
        toast.Close();
        PumpFor(60);
        Assert.True(toast.IsDisposed);   // no exit fade to cancel the close
        Assert.Equal(0, Motion.Holders);
    }
}
