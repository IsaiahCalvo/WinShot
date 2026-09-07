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
