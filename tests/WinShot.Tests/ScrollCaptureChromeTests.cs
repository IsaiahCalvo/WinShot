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
}
