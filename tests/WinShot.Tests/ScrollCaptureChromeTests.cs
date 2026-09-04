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

    private static void PumpUntil(Func<bool> condition, int ms = 5000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < ms)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
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
