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

    [Fact]
    public void SpaceIntoWindowModeDropsAMarqueeThatModeCouldNotConfirm()
    {
        using var pointer = new Pointer(100, 100);
        using var selector = CreateSelector(Settings(s => s.AdjustSelectionBeforeCapturing = true));

        selector.HandleMouseDown(Left());
        pointer.MoveTo(selector, 300, 250);
        selector.HandleMouseUp(Left());
        Assert.NotNull(selector.PendingRegion);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Space));
        Assert.Null(selector.PendingRegion);
        Assert.Equal(FastRegionSelectorDialog.SelectorMode.Window, selector.Mode);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }

    [Fact]
    public void ReleasingTheDragTakesTheShot()
    {
        using var pointer = new Pointer(100, 100);
        using var selector = CreateSelector(Settings());

        selector.HandleMouseDown(Left());
        pointer.MoveTo(selector, 340, 260);
        selector.HandleMouseUp(Left());

        // No second gesture: the region is chosen and the session is over.
        Assert.Null(selector.PendingRegion);
        Assert.NotNull(selector.SelectedRegionPx);
        Assert.Equal(240, selector.SelectedRegionPx!.Value.Width);
        Assert.Equal(160, selector.SelectedRegionPx!.Value.Height);
        Assert.Equal(WF.DialogResult.OK, selector.DialogResult);
    }

    [Fact]
    public void AdjustBeforeCapturingLeavesTheSelectionOnScreen()
    {
        using var pointer = new Pointer(100, 100);
        using var selector = CreateSelector(Settings(s => s.AdjustSelectionBeforeCapturing = true));

        selector.HandleMouseDown(Left());
        pointer.MoveTo(selector, 340, 260);
        selector.HandleMouseUp(Left());

        Assert.NotNull(selector.PendingRegion);
        Assert.Null(selector.SelectedRegionPx);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Enter));
        Assert.NotNull(selector.SelectedRegionPx);
    }

    [Theory]
    [InlineData(false, false, false)]  // shipped default: release captures
    [InlineData(false, true, true)]    // Ctrl at release: adjust this once
    [InlineData(true, false, true)]    // configured to adjust
    [InlineData(true, true, false)]    // Ctrl at release: capture this once
    public void ControlAtReleaseAsksForTheOtherBehaviour(bool setting, bool control, bool adjusts)
    {
        SelectorOptions options = SelectorOptions.ForRegion(
            new Settings { AdjustSelectionBeforeCapturing = setting });

        Assert.Equal(adjusts, options.ShouldAdjustSelection(control));
    }

    [Fact]
    public void TheSizePillSitsJustOffTheElementItLabels()
    {
        var monitor = new SD.Rectangle(0, 0, 1920, 1080);
        var element = new SD.Rectangle(100, 200, 400, 300);
        var pill = new SD.Size(64, 24);

        SD.Rectangle rect = SelectorChrome.SizePillRect(element, pill, monitor);

        // Paint and hit-test call this same function, so the pill you can click is exactly
        // the pill you can see — the shutter affordance was the only missing half.
        Assert.Equal(new SD.Rectangle(508, 508, 64, 24), rect);
        Assert.False(rect.IntersectsWith(element));

        // An element flush with the bottom-right corner: the painted pill is clamped inside
        // the monitor, so the clickable one has to be clamped with it or the shutter is dead
        // on exactly the elements most likely to sit against an edge.
        SD.Rectangle corner = SelectorChrome.SizePillRect(
            new SD.Rectangle(1500, 900, 420, 180), pill, monitor);
        Assert.True(monitor.Contains(corner), corner.ToString());
    }

    [Fact]
    public void CopyingAColourAveragesTheFramedRegionRatherThanOnePixel()
    {
        using var pointer = new Pointer(100, 100);
        using var selector = CreateSelector(Settings(s => s.AdjustSelectionBeforeCapturing = true));

        // Nothing framed: the single pixel under the pointer is the answer.
        Assert.Null(selector.ColorSampleRegion());

        selector.HandleMouseDown(Left());
        pointer.MoveTo(selector, 340, 260);
        // Mid-drag the region being framed is already what C should average.
        Assert.Equal(new SD.Rectangle(100, 100, 240, 160), selector.ColorSampleRegion());

        selector.HandleMouseUp(Left());
        Assert.Equal(new SD.Rectangle(100, 100, 240, 160), selector.ColorSampleRegion());

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }

    [Fact]
    public void AHoveredWindowIsTheRegionAveraged()
    {
        using var pointer = new Pointer(120, 90);
        using var selector = CreateSelector();

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Space));
        selector.SetHoverWindow(Window(0, 0, 800, 600));

        Assert.Equal(new SD.Rectangle(0, 0, 800, 600), selector.ColorSampleRegion());

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }

    /// <summary>
    /// The edge snap arms on the press and fires on the release over the same edge — the
    /// press alone used to teleport the edge up to 48 px, under a cursor that had been
    /// showing the four-way move arrow, with nothing to undo it.
    /// </summary>
    [Fact]
    public void ClickingOneEdgeOfTheSelectionSnapsThatEdgeToARealBoundary()
    {
        using var pointer = new Pointer(100, 100);
        using var selector = CreateSelector(Settings(s => s.AdjustSelectionBeforeCapturing = true));
        SetSnapEdges(selector, [96, 520], [180, 496]);

        selector.HandleMouseDown(Left());
        pointer.MoveTo(selector, 340, 260);
        selector.HandleMouseUp(Left());
        Assert.Equal(new SD.Rectangle(100, 100, 240, 160), selector.PendingRegion);

        // Press on the left edge, clear of the corner and mid-edge handles (those win the
        // grab, which is why the edge gesture answers None at a corner).
        pointer.At = new SD.Point(100, 130);
        selector.HandleMouseDown(Left());
        Assert.Equal(new SD.Rectangle(100, 100, 240, 160), selector.PendingRegion); // press only arms
        selector.HandleMouseUp(Left());

        SD.Rectangle? snapped = selector.PendingRegion;
        Assert.NotNull(snapped);
        Assert.Equal(96, snapped!.Value.Left);
        Assert.Equal(340, snapped.Value.Right);
        Assert.Equal(100, snapped.Value.Top);
        Assert.Equal(260, snapped.Value.Bottom);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }

    [Fact]
    public void ARealignsAHandDrawnSelectionToWhatIsActuallyOnScreen()
    {
        using var pointer = new Pointer(100, 100);
        using var selector = CreateSelector(Settings(s => s.AdjustSelectionBeforeCapturing = true));
        SetSnapEdges(selector, [96, 344], [104, 256]);

        selector.HandleMouseDown(Left());
        pointer.MoveTo(selector, 340, 260);
        selector.HandleMouseUp(Left());

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.A));

        Assert.Equal(new SD.Rectangle(96, 104, 248, 152), selector.PendingRegion);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }

    /// <summary>
    /// Turning Windows' animations off must take the message's RAMP, never the message. The
    /// hold used to be a tween, and a tween with animations off applies its end state inline —
    /// so the pill faded in, held and faded out inside one keystroke and no paint ever saw it.
    /// Four actions have this pill as their ONLY feedback channel, including the two that
    /// explain why a key did nothing.
    /// </summary>
    [Fact]
    public void AMessageIsStillOnScreenWhenAnimationsAreOff()
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            using var pointer = new Pointer(50, 50);
            using var selector = CreateSelector(Settings());

            // No frozen snapshot in a test, so C takes the "turn on Freeze screen" branch —
            // a FlashMessage like every other, and the only thing telling the user why.
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.C));

            Assert.Equal(
                "Color needs the frozen screen; turn on Freeze screen in Settings.",
                HintText(selector));
            Assert.True(HintOpacity(selector) > 0d, "the pill is at zero opacity: never painted");

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
            Assert.Equal(0, Motion.Holders);
        }
        finally
        {
            Motion.AnimationsEnabled = previous;
            PumpUntil(() => Motion.Holders == 0);
        }
    }

    /// <summary>
    /// A dwell is not a transition, so it must not run on the frame clock: 1.2 s of tween held
    /// the process-wide 1 ms timer resolution and fired ~150 empty callbacks per confirmation,
    /// on the most paint-sensitive surface in the app.
    /// </summary>
    [Fact]
    public void HoldingAMessageOnScreenDoesNotHoldTheFrameClock()
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            PumpUntil(() => Motion.Holders == 0);
            using var pointer = new Pointer(50, 50);
            using var selector = CreateSelector(Settings());

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.C));
            Assert.Equal(0, Motion.Holders);

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        }
        finally
        {
            Motion.AnimationsEnabled = previous;
            PumpUntil(() => Motion.Holders == 0);
        }
    }

    /// <summary>
    /// Mouse-up confirm took away the step that used to catch a slip, so a press that
    /// travelled the 4 px drag threshold and let go wrote, copied and carded a 4x4 image.
    /// Below the floor the release leaves an adjustable selection instead.
    /// </summary>
    [Fact]
    public void AFourPixelSlipLeavesASelectionRatherThanCapturing()
    {
        using var pointer = new Pointer(200, 200);
        using var selector = CreateSelector(Settings());

        selector.HandleMouseDown(Left());
        pointer.MoveTo(selector, 205, 204);
        selector.HandleMouseUp(Left());

        Assert.Null(selector.SelectedRegionPx);
        Assert.NotNull(selector.PendingRegion);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }

    /// <summary>
    /// A rect the user moved with the keyboard is a hand-drawn rect. ScrollingFlow re-refines
    /// a detector-owned one back onto the pane it found, which silently threw the nudge away.
    /// </summary>
    [Fact]
    public void NudgingASelectionDropsTheDetectorProvenance()
    {
        using var pointer = new Pointer(100, 100);
        using var selector = CreateSelector(Settings(s => s.AdjustSelectionBeforeCapturing = true));

        selector.HandleMouseDown(Left());
        pointer.MoveTo(selector, 340, 260);
        selector.HandleMouseUp(Left());
        SetPaneProvenance(selector); // what the Ctrl+click retarget leaves behind

        SD.Rectangle before = selector.PendingRegion!.Value;
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Right));

        Assert.NotEqual(before, selector.PendingRegion!.Value);
        Assert.False(selector.SelectedByPaneClick);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }

    /// <summary>
    /// Padding and edge snapping move the rect without a press, so neither goes through the
    /// provenance reset a fresh press does — same silent re-refinement as the nudge.
    /// </summary>
    [Fact]
    public void PaddingASelectionDropsTheDetectorProvenance()
    {
        using var pointer = new Pointer(100, 100);
        using var selector = CreateSelector(Settings(s => s.AdjustSelectionBeforeCapturing = true));

        selector.HandleMouseDown(Left());
        pointer.MoveTo(selector, 340, 260);
        selector.HandleMouseUp(Left());
        SetPaneProvenance(selector);

        typeof(FastRegionSelectorDialog)
            .GetMethod("GlideSelectionTo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(selector, [new SD.Rectangle(92, 92, 256, 176)]);

        Assert.False(selector.SelectedByPaneClick);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }

    /// <summary>
    /// Space changes the mode the session actually RUNS in, so the caller cannot key the
    /// window-backdrop composite off the mode it asked for — a capture-window run that flipped
    /// to Area came back as a hand-drawn rectangle floating on the wallpaper.
    /// </summary>
    [Fact]
    public void TheSelectorReportsTheModeItActuallyRanIn()
    {
        using var pointer = new Pointer(100, 100);
        using var selector = CreateSelector(Settings());

        typeof(FastRegionSelectorDialog)
            .GetField("_mode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(selector, FastRegionSelectorDialog.SelectorMode.Window);
        Assert.Equal(FastRegionSelectorDialog.SelectorMode.Window, selector.Mode);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Space));

        selector.HandleMouseDown(Left());
        pointer.MoveTo(selector, 340, 260);
        selector.HandleMouseUp(Left());

        Assert.Equal(new SD.Rectangle(100, 100, 240, 160), selector.SelectedRegionPx);
        Assert.Equal(FastRegionSelectorDialog.SelectorMode.Area, selector.Mode);
    }

    private static void SetPaneProvenance(FastRegionSelectorDialog selector) =>
        typeof(FastRegionSelectorDialog)
            .GetProperty("SelectedByPaneClick")!
            .SetValue(selector, true);

    private static double HintOpacity(FastRegionSelectorDialog selector) =>
        (double)typeof(FastRegionSelectorDialog)
            .GetField("_hintOpacity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(selector)!;

    private static string HintText(FastRegionSelectorDialog selector) =>
        (string)typeof(FastRegionSelectorDialog)
            .GetField("_hintText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(selector)!;

    private static void SetSnapEdges(FastRegionSelectorDialog selector, int[] x, int[] y)
    {
        var type = typeof(FastRegionSelectorDialog);
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        type.GetField("_snapEdgesX", Flags)!.SetValue(selector, x);
        type.GetField("_snapEdgesY", Flags)!.SetValue(selector, y);
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
