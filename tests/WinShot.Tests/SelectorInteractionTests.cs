using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Keyboard/pointer behaviour of the region selector, driven through the same internal
/// handlers the surfaces call. No real input and no visible window — the selector is
/// constructed, poked, and completed with Escape so its follow-motion clock is released
/// (MotionTests asserts absolute holder counts).
/// </summary>
public class SelectorInteractionTests
{
    private static FastRegionSelectorDialog CreateSelector(SettingsService? settings = null) =>
        new(() => Task.FromResult(new List<WindowInfo>()), settings);

    /// <summary>An unloaded service carrying default settings, mutated by the caller.</summary>
    private static SettingsService Settings(Action<Settings>? configure = null)
    {
        var service = new SettingsService();
        configure?.Invoke(service.Current);
        return service;
    }

    private static WindowInfo Window(int x, int y, int w, int h, string title = "Notepad") =>
        new(new IntPtr(x * 100 + y), title, new SD.Rectangle(x, y, w, h));

    [Fact]
    public void HoverWindowRepaintsOnlyWhenTheTargetChanges()
    {
        using var selector = CreateSelector();
        WindowInfo first = Window(0, 0, 800, 600);

        Assert.True(selector.SetHoverWindow(first));
        // The follow tick asks again every 8 ms while the cursor sits still inside the
        // same window; each repaint would be a full-monitor redraw showing nothing new.
        Assert.False(selector.SetHoverWindow(first));
        Assert.False(selector.SetHoverWindow(Window(0, 0, 800, 600)));

        Assert.True(selector.SetHoverWindow(Window(900, 100, 400, 300, "Explorer")));
        Assert.True(selector.SetHoverWindow(null));
        Assert.False(selector.SetHoverWindow(null));

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }
}
