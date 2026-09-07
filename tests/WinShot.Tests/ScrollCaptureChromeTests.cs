using System.Reflection;
using WinShot.Core;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

public class ScrollCaptureChromeTests
{
    [Fact]
    public void KeyboardActions_CompletePrimaryFlowWithoutFormActivation()
    {
        using var controls = new ScrollControlsBar(new SD.Rectangle(10, 10, 900, 600));
        int starts = 0, done = 0, cancelled = 0, autoChanges = 0;
        controls.StartRequested += () => starts++;
        controls.DoneRequested += () => done++;
        controls.CancelRequested += () => cancelled++;
        controls.AutoScrollToggled += _ => autoChanges++;

        controls.PerformAction(ScrollChromeAction.Start);
        controls.PerformAction(ScrollChromeAction.Done);
        controls.PerformAction(ScrollChromeAction.ToggleAuto);
        controls.PerformAction(ScrollChromeAction.Cancel);

        Assert.Equal(1, starts);
        Assert.Equal(1, done);
        Assert.Equal(1, autoChanges);
        Assert.Equal(1, cancelled);

        var createParams = (WF.CreateParams)typeof(WF.Control)
            .GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(controls)!;
        Assert.NotEqual(0, createParams.ExStyle & CaptureExclusion.WS_EX_NOACTIVATE);
    }

    [Theory]
    [InlineData(ScrollKeyboardShortcuts.Start)]
    [InlineData(ScrollKeyboardShortcuts.ToggleAuto)]
    [InlineData(ScrollKeyboardShortcuts.Done)]
    [InlineData(ScrollKeyboardShortcuts.Cancel)]
    public void ScopedKeyboardShortcut_IsAValidGlobalGesture(string gesture)
        => Assert.True(HotkeyManager.TryParseGesture(gesture, out _, out _));

    [Fact]
    public void PrimaryControls_HaveAccessibleNamesAndHelp()
    {
        using var controls = new ScrollControlsBar(new SD.Rectangle(10, 10, 900, 600));
        foreach (string name in new[]
        {
            "ScrollCaptureStart", "ScrollCaptureAuto", "ScrollCaptureDone", "ScrollCaptureCancel",
        })
        {
            var control = Assert.Single(controls.Controls.Find(name, searchAllChildren: true));
            Assert.False(string.IsNullOrWhiteSpace(control.AccessibleName));
            Assert.False(string.IsNullOrWhiteSpace(control.AccessibleDescription));
        }

        var status = Assert.Single(controls.Controls.Find("ScrollCaptureStatus", searchAllChildren: true));
        Assert.Equal(WF.AccessibleRole.StaticText, status.AccessibleRole);
        Assert.False(string.IsNullOrWhiteSpace(status.AccessibleName));
    }

    /// <summary>
    /// MouseDownBackColor was set to the hover colour, so holding any of the four buttons down
    /// was pixel-identical to pointing at it and the bar's commit actions — two of them the
    /// accent affirmatives — acknowledged nothing at all on click. The press step comes from
    /// the same function DarkButton paints with, so the bar presses the direction every other
    /// WinForms surface does: neutrals darken past their hover lift, the accent dips to
    /// AccentPressed rather than lightening its white label out.
    /// </summary>
    [Fact]
    public void PrimaryControls_PressToSomethingOtherThanTheirHover()
    {
        using var controls = new ScrollControlsBar(new SD.Rectangle(10, 10, 900, 600));
        foreach (string name in new[]
        {
            "ScrollCaptureStart", "ScrollCaptureAuto", "ScrollCaptureDone", "ScrollCaptureCancel",
        })
        {
            var b = (WF.Button)Assert.Single(controls.Controls.Find(name, searchAllChildren: true));
            SD.Color over = b.FlatAppearance.MouseOverBackColor;
            SD.Color down = b.FlatAppearance.MouseDownBackColor;

            Assert.True(over.ToArgb() != down.ToArgb(), $"{name} presses to its own hover colour");
            // Both ends of the ramp, not just press: hover was a colour the caller handed in,
            // so the neutral buttons hovered to SurfaceHover while every other neutral
            // WinForms surface lifted +18 — one button role, two hover shades.
            Assert.Equal(DarkControlPalette.StateFill(b.BackColor, 1d, 0d), over);
            Assert.Equal(DarkControlPalette.StateFill(b.BackColor, 1d, 1d), down);
            // Press darkens, whichever track the button is on.
            Assert.True(
                Luminance(down) < Luminance(over),
                $"{name} lightens on press ({down}) instead of darkening from hover ({over})");
        }

        var start = (WF.Button)Assert.Single(controls.Controls.Find("ScrollCaptureStart", searchAllChildren: true));
        Assert.Equal(ThemePalette.AccentPressed, start.FlatAppearance.MouseDownBackColor);
    }

    private static double Luminance(SD.Color c) => (0.2126d * c.R) + (0.7152d * c.G) + (0.0722d * c.B);

    /// <summary>
    /// AutoScaleMode.Dpi only reacts to DPI *changes*, so a window born on a 150% monitor
    /// never scales at all: the layout stays at 96-dpi metrics while the point-sized fonts
    /// render at the real DPI, and the status label clips. Both scroll surfaces scale
    /// manually now, like every other WinForms surface in the app.
    /// </summary>
    [Fact]
    public void ScrollChrome_ScalesManually_NotWithAutoScaleModeDpi()
    {
        using var controls = new ScrollControlsBar(new SD.Rectangle(10, 10, 900, 600));
        using var preview = new ScrollPreviewPanel(new SD.Rectangle(10, 10, 900, 600));

        Assert.Equal(WF.AutoScaleMode.None, controls.AutoScaleMode);
        Assert.Equal(WF.AutoScaleMode.None, preview.AutoScaleMode);
    }

    /// <summary>
    /// The arithmetic behind the manual scaling, at the four factors the app has to survive.
    /// Every hand-laid-out surface rounds through this one helper, and _scale comes from a
    /// live monitor, so this is the only seam that can assert 150% behaviour on a machine
    /// that is not at 150%. Deleting ScrollChromeMetrics took the equivalent coverage with it.
    /// </summary>
    /// <summary>
    /// Before capture the bar is one Start Capture capsule. A status line reading "ready"
    /// and an Auto-Scroll toggle for a run that hasn't begun are answers to questions
    /// nobody has asked, so both stay hidden until Start reveals them -- and the bar grows
    /// into them rather than being replaced. Runs the reveal to completion so the tween's
    /// hold on the process-wide timer resolution is released.
    /// </summary>
    [Fact]
    public void PreCaptureBar_IsOneCapsuleAndGrowsIntoTheCaptureControls()
    {
        Assert.Equal(0, Motion.Holders);
        using var controls = new ScrollControlsBar(new SD.Rectangle(200, 200, 900, 600));
        controls.StartPosition = WF.FormStartPosition.Manual;
        controls.Location = new SD.Point(-20_000, -20_000);
        controls.Show();
        controls.Location = new SD.Point(-20_000, -20_000);
        WF.Application.DoEvents();

        var status = Assert.Single(controls.Controls.Find("ScrollCaptureStatus", true));
        var auto = Assert.Single(controls.Controls.Find("ScrollCaptureAuto", true));
        var start = Assert.Single(controls.Controls.Find("ScrollCaptureStart", true));
        Assert.False(status.Visible);
        Assert.False(auto.Visible);
        Assert.True(start.Visible);
        int capsuleWidth = controls.Width;

        controls.PerformAction(ScrollChromeAction.Start);
        PumpUntil(() => Motion.Holders == 0);

        Assert.True(status.Visible);
        Assert.True(auto.Visible);
        Assert.False(start.Visible);
        Assert.True(controls.Width > capsuleWidth,
            $"the bar should grow past the capsule ({capsuleWidth}px), got {controls.Width}px");
        Assert.Equal(0, Motion.Holders);
        controls.Hide();
    }

    /// <summary>Closing the bar mid-reveal must not strand the timer-resolution hold.</summary>
    [Fact]
    public void ClosingTheBarMidReveal_ReleasesTheMotionHold()
    {
        Assert.Equal(0, Motion.Holders);
        var controls = new ScrollControlsBar(new SD.Rectangle(200, 200, 900, 600));
        controls.StartPosition = WF.FormStartPosition.Manual;
        controls.Location = new SD.Point(-20_000, -20_000);
        controls.Show();
        WF.Application.DoEvents();

        controls.PerformAction(ScrollChromeAction.Start);
        controls.Close();
        controls.Dispose();
        PumpUntil(() => Motion.Holders == 0);

        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>
    /// All three scroll surfaces are layered windows, and each one ramps its alpha on show
    /// and on hide. The ramp must always finish -- an in-flight tween holds the process-wide
    /// high-resolution timer -- and the fade-out task must complete even for a window that
    /// was never shown, or the teardown in ScrollingStatusWindow would hang on it.
    /// </summary>
    [Fact]
    public void EveryScrollSurface_FadesInAndOutAndReleasesTheMotionHold()
    {
        Assert.Equal(0, Motion.Holders);
        var region = new SD.Rectangle(300, 300, 700, 500);

        using (var overlay = new ScrollDimOverlay(region))
        using (var preview = new ScrollPreviewPanel(region))
        using (var controls = new ScrollControlsBar(region))
        {
            // Never shown: the fade-out still completes rather than waiting on a tween
            // that will never tick.
            Assert.True(Task.WhenAll(controls.FadeOutAsync(), preview.FadeOutAsync(), overlay.FadeOutAsync())
                .Wait(TimeSpan.FromSeconds(2)));
        }

        Assert.Equal(0, Motion.Holders);

        using (var preview = new ScrollPreviewPanel(region))
        {
            preview.StartPosition = WF.FormStartPosition.Manual;
            preview.Location = new SD.Point(-20_000, -20_000);
            preview.Show();
            preview.Location = new SD.Point(-20_000, -20_000);
            PumpUntil(() => Motion.Holders == 0);
            Assert.Equal(0, Motion.Holders);

            Task fadeOut = preview.FadeOutAsync();
            PumpUntil(() => fadeOut.IsCompleted);
            Assert.True(fadeOut.IsCompleted);
            preview.Hide();
        }

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    private static void PumpUntil(Func<bool> condition, int ms = 5000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < ms)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// The preview's whole job is to read as filling. A fixed 220x300 box does the opposite:
    /// the stitch is scaled to fit from the very first frame, so the longer the capture runs
    /// the thinner it gets. The box now follows the capture axis until it runs out of monitor
    /// -- 60% of the height going down, 55% of the width going sideways -- and only then
    /// starts scaling down.
    /// </summary>
    [Fact]
    public void ThePreviewBox_FollowsTheCaptureAxisUntilItRunsOutOfMonitor()
    {
        var min = new SD.Size(204, 252);
        SD.Size max = ScrollPreviewLayout.Cap(new SD.Rectangle(0, 0, 1920, 1080), min);
        Assert.Equal(new SD.Size(1056, 648), max);

        // Nothing captured yet, and a stitch still shorter than the box: stay at the box.
        Assert.Equal(min, ScrollPreviewLayout.Content(SD.Size.Empty, min, max));
        Assert.Equal(min, ScrollPreviewLayout.Content(new SD.Size(204, 120), min, max));

        // Growing downward: the width is pinned, the height tracks the stitch 1:1.
        Assert.Equal(new SD.Size(204, 400), ScrollPreviewLayout.Content(new SD.Size(204, 400), min, max));

        // A narrow region's strip is narrower than the box and still fits it: shown at 1:1
        // rather than blown up to fill, which would only make it blurrier.
        Assert.Equal(min, ScrollPreviewLayout.Content(new SD.Size(102, 250), min, max));

        // Past 60% of the monitor it stops growing -- from here it scales down, as before.
        Assert.Equal(new SD.Size(204, 648), ScrollPreviewLayout.Content(new SD.Size(204, 6000), min, max));

        // Sideways runs are the mirror image: the height is pinned, the width grows to 55%.
        Assert.Equal(new SD.Size(504, 252), ScrollPreviewLayout.Content(new SD.Size(500, 250), min, max));
        Assert.Equal(new SD.Size(1056, 252), ScrollPreviewLayout.Content(new SD.Size(6000, 252), min, max));
    }

    /// <summary>A monitor shorter than the box it starts at (a 1024x600 netbook panel, or a
    /// portrait strip beside the main display) must not hand back a negative-growth cap.</summary>
    [Fact]
    public void ThePreviewBox_NeverShrinksBelowTheBoxItStartsAt()
    {
        var min = new SD.Size(204, 252);
        SD.Size max = ScrollPreviewLayout.Cap(new SD.Rectangle(0, 0, 320, 240), min);

        Assert.Equal(min, max);
        Assert.Equal(min, ScrollPreviewLayout.Content(new SD.Size(204, 6000), min, max));
        Assert.Equal(min, ScrollPreviewLayout.Content(new SD.Size(6000, 204), min, max));
    }

    /// <summary>
    /// The live panel: it grows into each snapshot, stays pinned to the corner it started in
    /// (so the stitch fills upward instead of the window walking across the screen), stops at
    /// the cap, and leaves no tween holding the process-wide clock.
    /// </summary>
    [Fact]
    public void ThePreviewPanel_GrowsWithTheStitchAndStaysInItsCorner()
    {
        Assert.Equal(0, Motion.Holders);
        var region = new SD.Rectangle(120, 120, 700, 500);
        SD.Rectangle monitor = WF.Screen.FromRectangle(region).Bounds;

        using (var preview = new ScrollPreviewPanel(region))
        {
            preview.Show();
            WF.Application.DoEvents();
            PumpUntil(() => Motion.Holders == 0);

            SD.Size start = preview.ContentSize;
            if (ScrollPreviewLayout.Cap(monitor, start).Height <= start.Height)
                return; // a monitor too short to grow into; the pure tests cover the maths

            var corner = new SD.Point(preview.Right, preview.Bottom);

            preview.SetImage(new SD.Bitmap(start.Width, start.Height * 3), "Capturing…");
            PumpUntil(() => Motion.Holders == 0);

            Assert.True(preview.ContentSize.Height > start.Height,
                $"the box stayed {preview.ContentSize.Height}px tall for a stitch 3x its height");
            Assert.Equal(start.Width, preview.ContentSize.Width);
            Assert.Equal(corner, new SD.Point(preview.Right, preview.Bottom));

            preview.SetImage(new SD.Bitmap(start.Width, start.Height * 40), "Capturing…");
            PumpUntil(() => Motion.Holders == 0);
            SD.Size capped = preview.ContentSize;

            preview.SetImage(new SD.Bitmap(start.Width, start.Height * 80), "Capturing…");
            PumpUntil(() => Motion.Holders == 0);
            Assert.Equal(capped, preview.ContentSize);
            Assert.Equal(corner, new SD.Point(preview.Right, preview.Bottom));
            preview.Hide();
        }

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>A capture that ends mid-glide disposes the panel without closing it; the
    /// growth tween has to go with it.</summary>
    [Fact]
    public void DisposingThePanelMidGrowth_ReleasesTheMotionHold()
    {
        Assert.Equal(0, Motion.Holders);
        var region = new SD.Rectangle(140, 140, 600, 400);
        var preview = new ScrollPreviewPanel(region);
        preview.Show();
        WF.Application.DoEvents();

        preview.SetImage(new SD.Bitmap(preview.ContentSize.Width, preview.ContentSize.Height * 4), "Capturing…");
        preview.Dispose();

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    [Theory]
    [InlineData(1.00, 250, 24, 40)]
    [InlineData(1.25, 312, 30, 50)]   // Math.Round is to-even: 312.5 lands on 312
    [InlineData(1.50, 375, 36, 60)]
    [InlineData(2.00, 500, 48, 80)]
    public void ManualDpiScaling_GrowsTheStatusLabelAndTheButtonFloor(
        double scale, int status, int statusHeight, int buttonFloor)
    {
        Assert.Equal(status, WinShot.Recording.RecordingMonitorDpi.Px(250, scale));
        Assert.Equal(statusHeight, WinShot.Recording.RecordingMonitorDpi.Px(24, scale));
        Assert.Equal(buttonFloor, WinShot.Recording.RecordingMonitorDpi.Px(40, scale));
    }
}
