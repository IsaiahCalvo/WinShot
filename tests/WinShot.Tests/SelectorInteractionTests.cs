using System.Diagnostics;
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
[Collection("MotionRefcount")]
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

    /// <summary>
    /// Hovering a window ramps the accent wash in rather than flicking it on, and the tween
    /// holds the process-wide 1 ms clock while it runs — so ending the session has to drop it
    /// even when the pointer is still sitting on a window.
    /// </summary>
    [Fact]
    public void HoverWashRampsInAndReleasesTheClockWhenTheSessionEnds()
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => true;
        try
        {
            PumpUntil(() => Motion.Holders == 0);
            using var selector = CreateSelector();

            Assert.True(selector.SetHoverWindow(Window(0, 0, 800, 600)));
            Assert.True(Motion.Holders > 0, "the wash ramp should hold the high-resolution clock");

            // Escape ends the session with the ramp still in flight.
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
            Assert.Equal(0, Motion.Holders);
        }
        finally
        {
            Motion.AnimationsEnabled = previous;
            PumpUntil(() => Motion.Holders == 0);
        }
    }

    [Fact]
    public void HoverWashIsSkippedEntirelyWhenAnimationsAreOff()
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            PumpUntil(() => Motion.Holders == 0);
            using var selector = CreateSelector();

            Assert.True(selector.SetHoverWindow(Window(0, 0, 800, 600)));
            Assert.Equal(0, Motion.Holders);

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        }
        finally
        {
            Motion.AnimationsEnabled = previous;
        }
    }

    /// <summary>Drives the selector's pointer without touching the real mouse.</summary>
    private sealed class Pointer : IDisposable
    {
        public SD.Point At;

        public Pointer(int x = 0, int y = 0)
        {
            At = new SD.Point(x, y);
            FastRegionSelectorDialog.CursorProvider = () => At;
        }

        public Pointer MoveTo(FastRegionSelectorDialog selector, int x, int y)
        {
            At = new SD.Point(x, y);
            selector.HandleMouseMove();
            return this;
        }

        public void Dispose() => FastRegionSelectorDialog.CursorProvider = null;
    }

    private static WF.MouseEventArgs Left(int clicks = 1) =>
        new(WF.MouseButtons.Left, clicks, 0, 0, 0);

    [Fact]
    public void SpaceFlipsBetweenDrawingARectangleAndPickingAWindow()
    {
        using var pointer = new Pointer(120, 90);
        using var selector = CreateSelector();
        Assert.Equal(FastRegionSelectorDialog.SelectorMode.Area, selector.Mode);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Space));
        Assert.Equal(FastRegionSelectorDialog.SelectorMode.Window, selector.Mode);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Space));
        Assert.Equal(FastRegionSelectorDialog.SelectorMode.Area, selector.Mode);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 8000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < timeoutMs)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }
}
