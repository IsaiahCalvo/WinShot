using System.Reflection;
using System.Threading;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The gestures on the region-first recording HUD, driven through the selector's own handlers
/// with a substituted pointer — the suite never moves the real mouse.
///
/// These cover two ways the take used to be abandoned in silence: the Cancel/Done bar stayed
/// live under the HUD that replaced it, and confirming during the aspect glide recorded the
/// half-corrected rectangle instead of the ratio the user picked.
/// </summary>
[Collection("MotionRefcount")]
public class RecordingHudInteractionTests
{
    private static readonly SD.Rectangle Monitor = new(0, 0, 1920, 1080);

    // ---- the invisible Cancel/Done bar --------------------------------------------

    /// <summary>
    /// The two placers legitimately disagree about "below the selection": the HUD block is
    /// ~150 dip tall and the bar 42, so near the screen bottom the HUD flips above while the
    /// bar would stay below it, with nothing painted over the bar. That divergence is fine —
    /// it is only a bug if the bar is still hit-tested, which is what the next test pins.
    /// </summary>
    [Fact]
    public void HudAndActionBar_LandInDifferentPlacesNearTheScreenBottom()
    {
        var region = new SD.Rectangle(600, 500, 700, 480); // bottom = 980
        SD.Size block = RecordingHudLayout.BlockSize(1.0);

        var hud = new SD.Rectangle(RecordingHudLayout.Place(region, Monitor, block, 12), block);
        var bar = new SD.Rectangle(PlaceActionBar(region, 164, 42), new SD.Size(164, 42));

        Assert.True(bar.Top > region.Bottom, $"bar {bar} was not below the region");
        Assert.True(hud.Bottom < region.Top, $"hud {hud} did not flip above the region");
        Assert.False(hud.IntersectsWith(bar));
    }

    /// <summary>
    /// The same press on the real "Done" rect, run twice. Without the HUD it arms Done — which
    /// is what made the phantom reachable. With the HUD up it must arm nothing at all: the HUD
    /// replaced the bar, OnPaint never draws it, and a phantom Done reached Confirm with no
    /// RecordingChoice, so the selector closed and no recording ever started.
    /// </summary>
    [Fact]
    public void WithTheHudUp_TheCancelDoneBarIsNotHitTestedAtAll()
    {
        var region = new SD.Rectangle(600, 400, 700, 300);

        RunSelector(region, SD.Point.Empty, selector =>
        {
            (SD.Rectangle bar, SD.Rectangle _, SD.Rectangle done) = ActionBarRects(selector, region);
            var press = new SD.Point(done.Left + (done.Width / 2), done.Top + (done.Height / 2));
            Assert.True(bar.Contains(press));
            FastRegionSelectorDialog.CursorSource = () => press;

            // Without the HUD the bar owns this press.
            SetField(selector, "_recordingHud", false);
            SetField(selector, "_currentScreen", press);
            selector.HandleMouseDown(Click(press, clicks: 1));
            Assert.Equal(1, Field<int>(selector, "_pressedBarButton"));

            // With the HUD up it must not, whether or not a HUD cell happens to cover it.
            SetField(selector, "_recordingHud", true);
            SetField(selector, "_pressedBarButton", -1);
            SetField(selector, "_pendingScreen", (SD.Rectangle?)region);
            selector.HandleMouseDown(Click(press, clicks: 1));

            Assert.Equal(-1, Field<int>(selector, "_pressedBarButton"));
            Assert.Null(selector.RecordingChoice);
            Assert.Equal(WF.DialogResult.None, selector.DialogResult);
        });
    }

    [Fact]
    public void DoubleClickInsideTheRegion_RecordsVideoRatherThanAbandoningTheTake()
    {
        var region = new SD.Rectangle(600, 300, 700, 300);
        var press = new SD.Point(region.Left + 50, region.Top + 50);

        RunSelector(region, press, selector =>
        {
            selector.HandleMouseDown(Click(press, clicks: 2));

            Assert.NotNull(selector.RecordingChoice);
            Assert.False(selector.RecordingChoice!.Value.IsGif);
            Assert.Equal(WF.DialogResult.OK, selector.DialogResult);
        });
    }

    // ---- the aspect glide -----------------------------------------------------------

    /// <summary>
    /// Picking a ratio and hitting Record a few frames later must record the ratio, not the
    /// rectangle the ease happened to be passing through. Cancelling a Motion.Run skips its
    /// final frame, so StopAspectSnap has to land the target itself.
    /// </summary>
    [Fact]
    public void ConfirmingDuringTheAspectGlide_RecordsThePickedRatio()
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => true;
        try
        {
            var region = new SD.Rectangle(600, 200, 800, 800);
            var press = new SD.Point(region.Left + 10, region.Top + 10);

            RunSelector(region, press, selector =>
            {
                // The snap clamps to the region's own monitor, which is whatever this machine
                // has — so the target has to be computed the same way the selector does.
                SD.Rectangle target = RecordingAspectPresets.Apply(
                    region, 16.0 / 9, WF.Screen.FromRectangle(region).Bounds);
                Assert.NotEqual(region, target);
                int sixteenByNine = IndexOfRatio("16:9");
                Invoke(selector, "PickAspect", sixteenByNine, region);

                // A few frames in — mid-glide, nowhere near the target yet.
                PumpUntil(() => Field<SD.Rectangle?>(selector, "_pendingScreen") != region, 1000);
                Assert.NotEqual(target, Field<SD.Rectangle?>(selector, "_pendingScreen"));

                Invoke(selector, "ConfirmRecording", false);

                // The virtual screen starts at (0,0) here, so the confirmed rect IS the target.
                Assert.NotNull(selector.RecordingChoice);
                Assert.Equal(target, selector.SelectedRegionPx);
            });

            PumpUntil(() => Motion.Holders == 0);
            Assert.Equal(0, Motion.Holders);
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
        }
    }

    /// <summary>
    /// The preset is a LOCK, not a one-shot snap. It used to be neither: PickAspect corrected
    /// the rect once and nothing downstream read the ratio, so the first handle drag left the
    /// chip painting "16:9" over a rectangle that was any shape at all — and Enter recorded
    /// that shape.
    /// </summary>
    [Theory]
    [InlineData(4)]  // BR corner
    [InlineData(0)]  // TL corner
    [InlineData(3)]  // right edge — the ratio has to supply the height
    [InlineData(1)]  // top edge — and here, the width
    public void ResizingUnderALockedRatioKeepsTheRatio(int handle)
    {
        var region = new SD.Rectangle(400, 200, 800, 450);
        var press = new SD.Point(region.Left + 10, region.Top + 10);

        RunSelector(region, press, selector =>
        {
            SetField(selector, "_aspectIndex", IndexOfRatio("1:1"));
            SetField(selector, "_adjustStartRect", region);
            SetField(selector, "_resizeHandle", handle);

            var dragged = new SD.Point(region.Left + 640, region.Top + 90);
            FastRegionSelectorDialog.CursorSource = () => dragged;
            selector.HandleMouseMove();

            SD.Rectangle after = Field<SD.Rectangle?>(selector, "_pendingScreen")!.Value;
            Assert.True(after.Width > 1 && after.Height > 1, $"degenerate rect {after}");
            Assert.True(
                Math.Abs(after.Width - after.Height) <= 2,
                $"handle {handle} left {after.Width}x{after.Height} under a 1:1 lock");
        });
    }

    /// <summary>Drawing a fresh marquee is the other path that used to escape the lock — the
    /// chip stayed lit while the new rectangle was freeform.</summary>
    [Fact]
    public void ANewMarqueeUnderALockedRatioIsDrawnAtThatRatio()
    {
        var region = new SD.Rectangle(400, 200, 800, 450);
        var start = new SD.Point(200, 120);

        RunSelector(region, start, selector =>
        {
            SetField(selector, "_aspectIndex", IndexOfRatio("1:1"));
            SetField(selector, "_pendingScreen", (SD.Rectangle?)null);
            SetField(selector, "_dragStartScreen", start);
            SetField(selector, "_dragging", true);
            SetField(selector, "_dragMoved", true);

            var end = new SD.Point(start.X + 600, start.Y + 150);
            FastRegionSelectorDialog.CursorSource = () => end;
            SetField(selector, "_currentScreen", end);
            selector.HandleMouseUp(new WF.MouseEventArgs(WF.MouseButtons.Left, 1, end.X, end.Y, 0));

            SD.Rectangle drawn = Field<SD.Rectangle?>(selector, "_pendingScreen")!.Value;
            Assert.True(
                Math.Abs(drawn.Width - drawn.Height) <= 2,
                $"a fresh marquee ignored the 1:1 lock: {drawn.Width}x{drawn.Height}");
        });
    }

    /// <summary>Freeform is still freeform: the lock must not leak into ordinary drags.</summary>
    [Fact]
    public void FreeformLeavesEveryResizeAlone()
    {
        var region = new SD.Rectangle(400, 200, 800, 450);

        RunSelector(region, SD.Point.Empty, selector =>
        {
            SetField(selector, "_aspectIndex", RecordingAspectPresets.FreeformIndex);
            SetField(selector, "_adjustStartRect", region);
            SetField(selector, "_resizeHandle", 4);

            var dragged = new SD.Point(region.Left + 640, region.Top + 90);
            FastRegionSelectorDialog.CursorSource = () => dragged;
            selector.HandleMouseMove();

            SD.Rectangle after = Field<SD.Rectangle?>(selector, "_pendingScreen")!.Value;
            Assert.Equal(new SD.Rectangle(region.Left, region.Top, 640, 90), after);
        });
    }

    /// <summary>
    /// The cards carry 8 dip of padding that is not a cell, plus a 10 dip gap between them. A
    /// press there used to fall all the way through to "start a new marquee", which threw the
    /// framed selection away — a near-miss on the Record row cost the whole selection.
    /// </summary>
    [Fact]
    public void AMissInsideTheHudsOwnPaddingIsInertRatherThanDestructive()
    {
        var region = new SD.Rectangle(600, 300, 700, 300);

        RunSelector(region, SD.Point.Empty, selector =>
        {
            RecordingHudGeometry hud = HudGeometry(region);
            SD.Point[] misses =
            [
                new(hud.ActionCard.Left + 2, hud.ActionCard.Top + 2),                     // action card pad
                new(hud.ActionCard.Right - 2, hud.ActionCard.Bottom - 2),
                new(hud.SettingsCard.Left + 2, hud.SettingsCard.Top + 2),                 // settings card pad
                new(hud.SettingsCard.Left + 20, hud.SettingsCard.Bottom + 3),             // the gap between them
            ];

            foreach (SD.Point miss in misses)
            {
                Assert.Null(hud.HitTest(miss));
                SetField(selector, "_pendingScreen", (SD.Rectangle?)region);
                SetField(selector, "_dragging", false);
                FastRegionSelectorDialog.CursorSource = () => miss;
                SetField(selector, "_currentScreen", miss);

                selector.HandleMouseDown(Click(miss, clicks: 1));

                Assert.Equal(region, Field<SD.Rectangle?>(selector, "_pendingScreen"));
                Assert.False(Field<bool>(selector, "_dragging"), $"{miss} started a new marquee");
            }
        });
    }

    // ---- keyboard reach --------------------------------------------------------------

    [Fact]
    public void TabWalksTheHudCellsAndSpaceWorksTheFocusedOne()
    {
        var region = new SD.Rectangle(600, 300, 700, 300);
        var press = new SD.Point(region.Left + 10, region.Top + 10);

        RunSelector(region, press, selector =>
        {
            Assert.Equal(-1, Field<int>(selector, "_hudFocus"));

            // Tab lands on the first cell (the aspect picker), then walks on.
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Tab));
            Assert.Equal(0, Field<int>(selector, "_hudFocus"));
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Tab));
            Assert.Equal(1, Field<int>(selector, "_hudFocus")); // microphone

            RecordingHudToggles before = Field<RecordingHudToggles>(selector, "_recordingToggles");
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Space));
            RecordingHudToggles after = Field<RecordingHudToggles>(selector, "_recordingToggles");

            Assert.NotEqual(before.Microphone, after.Microphone);
            // Shift+Tab walks back the other way.
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Tab | WF.Keys.Shift));
            Assert.Equal(0, Field<int>(selector, "_hudFocus"));
        });
    }

    [Fact]
    public void EveryHudCellHasALabelToAnnounceAndShow()
    {
        foreach (RecordingHudCell cell in Enum.GetValues<RecordingHudCell>())
            Assert.False(string.IsNullOrWhiteSpace(RecordingHudLayout.LabelFor(cell)), cell.ToString());
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>
    /// Builds a selector already in the state the recording flow puts it in — HUD on, a
    /// selection pending — with the pointer parked at <paramref name="cursor"/>, then runs the
    /// body on an STA thread. Nothing is ever shown and the real mouse is never touched.
    /// </summary>
    private static void RunSelector(SD.Rectangle pending, SD.Point cursor, Action<FastRegionSelectorDialog> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            FastRegionSelectorDialog.CursorSource = () => cursor;
            try
            {
                using var selector = new FastRegionSelectorDialog(
                    () => Task.FromResult(new List<WindowInfo>()),
                    settings: null);
                SetField(selector, "_vs", Monitor);
                SetField(selector, "_monitorBounds", Monitor);
                SetField(selector, "_recordingHud", true);
                SetField(selector, "_pendingScreen", (SD.Rectangle?)pending);
                SetField(selector, "_currentScreen", cursor);
                Invoke(selector, "ResetHudMotion");
                Invoke(selector, "ShowHud", true);
                body(selector);
            }
            catch (Exception ex) { failure = ex; }
            finally { FastRegionSelectorDialog.CursorSource = null; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    private static WF.MouseEventArgs Click(SD.Point at, int clicks) =>
        new(WF.MouseButtons.Left, clicks, at.X, at.Y, 0);

    private static SD.Point PlaceActionBar(SD.Rectangle region, int width, int height)
    {
        MethodInfo method = typeof(FastRegionSelectorDialog).GetMethod(
            "PlaceActionBar", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (SD.Point)method.Invoke(null, [region, Monitor, width, height])!;
    }

    /// <summary>The bar's own rects for a pending region, from the selector itself — the sizes
    /// come from a DPI-scaled font, so they cannot be reproduced with constants.</summary>
    private static (SD.Rectangle Bar, SD.Rectangle Cancel, SD.Rectangle Done) ActionBarRects(
        FastRegionSelectorDialog selector, SD.Rectangle region)
    {
        MethodInfo method = typeof(FastRegionSelectorDialog).GetMethod(
            "ActionBarRects", BindingFlags.Static | BindingFlags.NonPublic)!;
        object result = method.Invoke(null, [region])!;
        return ((SD.Rectangle, SD.Rectangle, SD.Rectangle))result;
    }

    private static RecordingHudGeometry HudGeometry(SD.Rectangle region) =>
        (RecordingHudGeometry)typeof(FastRegionSelectorDialog)
            .GetMethod("HudGeometry", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [region])!;

    private static int IndexOfRatio(string label)
    {
        for (int i = 0; i < RecordingAspectPresets.All.Count; i++)
        {
            if (RecordingAspectPresets.All[i].Label == label)
                return i;
        }
        throw new InvalidOperationException($"No {label} preset");
    }

    private static T Field<T>(object target, string name) =>
        (T)typeof(FastRegionSelectorDialog)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        typeof(FastRegionSelectorDialog)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static void Invoke(object target, string name, params object?[] args) =>
        typeof(FastRegionSelectorDialog)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    private static void PumpUntil(Func<bool> done, int ms = 4000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < ms)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }
}
