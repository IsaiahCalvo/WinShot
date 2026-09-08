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
            (SD.Rectangle bar, SD.Rectangle _, SD.Rectangle _, SD.Rectangle done) = ActionBarRects(selector, region);
            var press = new SD.Point(done.Left + (done.Width / 2), done.Top + (done.Height / 2));
            Assert.True(bar.Contains(press));
            FastRegionSelectorDialog.CursorProvider = () => press;

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

    // ---- the chrome the HUD paints outside its own cards ----------------------------

    /// <summary>
    /// The "Esc to cancel" pill rides in the same ToolbarBg capsule every other floating
    /// string on this surface gets, so it reads as part of the HUD — but it sits below
    /// RecordingHudGeometry.Bounds, which is what the near-miss guard swallows presses with.
    /// A press on it fell straight through to "start a new marquee" and the framed take was
    /// gone. Same failure the near-miss fix set out to remove, one surface over.
    /// </summary>
    [Fact]
    public void ClickingTheEscapeHintPill_KeepsTheFramedSelection()
    {
        var region = new SD.Rectangle(600, 300, 700, 300);
        RecordingHudGeometry hud = HudGeometry(region);
        double scale = RecordingMonitorDpi.ScaleFor(WF.Screen.FromRectangle(region).Bounds);
        var press = new SD.Point(
            hud.ActionCard.Left + (hud.ActionCard.Width / 2),
            hud.ActionCard.Bottom + (int)Math.Round(10 * scale));
        Assert.False(hud.Bounds.Contains(press), "the probe point is inside the cards");

        RunSelector(region, press, selector =>
        {
            selector.HandleMouseDown(Click(press, clicks: 1));

            Assert.Equal(region, Field<SD.Rectangle?>(selector, "_pendingScreen"));
            Assert.False(Field<bool>(selector, "_dragging"), "the hint pill started a marquee");
            Assert.Null(selector.RecordingChoice);
        });
    }

    /// <summary>The hover-tip pill above the settings card is the other surface painted
    /// outside the cards' union, and it is up whenever a cell has focus.</summary>
    [Fact]
    public void ClickingTheHoverTipPill_KeepsTheFramedSelection()
    {
        var region = new SD.Rectangle(600, 400, 700, 300);
        RecordingHudGeometry hud = HudGeometry(region);
        double scale = RecordingMonitorDpi.ScaleFor(WF.Screen.FromRectangle(region).Bounds);
        SD.Rectangle cell = hud.CellBounds(RecordingHudCell.Microphone);
        var press = new SD.Point(
            cell.Left + (cell.Width / 2),
            hud.SettingsCard.Top - (int)Math.Round(10 * scale));
        Assert.False(hud.Bounds.Contains(press), "the probe point is inside the cards");

        RunSelector(region, press, selector =>
        {
            SetField(selector, "_hudFocus", FocusIndexOf(RecordingHudCell.Microphone));
            selector.HandleMouseDown(Click(press, clicks: 1));

            Assert.Equal(region, Field<SD.Rectangle?>(selector, "_pendingScreen"));
            Assert.False(Field<bool>(selector, "_dragging"), "the tip pill started a marquee");
        });
    }

    /// <summary>The pointer must not read as a button over the retired Cancel/Done bar, and
    /// must read as one over the HUD's own rows.</summary>
    [Fact]
    public void TheCursorFollowsTheHudRatherThanThePhantomActionBar()
    {
        // Near the screen bottom the HUD flips above the region while the bar would stay
        // below it, so the phantom lands on empty dimmed desktop — which is exactly where the
        // Hand used to appear. Sized off the REAL primary, because that is the monitor the
        // HUD placer asks about; a made-up 1080 would not flip on a smaller test desktop.
        SD.Rectangle primary = WF.Screen.PrimaryScreen!.Bounds;
        int block = RecordingHudLayout.BlockSize(
            RecordingMonitorDpi.ScaleFor(primary)).Height;
        var region = new SD.Rectangle(
            primary.Left + 200, primary.Bottom - block - 200, 700, 200);

        RunSelector(region, SD.Point.Empty, selector =>
        {
            (SD.Rectangle bar, SD.Rectangle _, SD.Rectangle _, SD.Rectangle done) = ActionBarRects(selector, region);
            var overPhantom = new SD.Point(done.Left + (done.Width / 2), done.Top + (done.Height / 2));
            Assert.True(bar.Contains(overPhantom));
            Assert.False(
                HudGeometry(region).Bounds.Contains(overPhantom),
                "the probe point is on the HUD itself, not on the retired bar");
            SetField(selector, "_currentScreen", overPhantom);
            Assert.Equal(WF.Cursors.Cross, selector.CursorForCurrent());

            SD.Rectangle record = HudGeometry(region).CellBounds(RecordingHudCell.RecordVideo);
            SetField(
                selector, "_currentScreen",
                new SD.Point(record.Left + (record.Width / 2), record.Top + (record.Height / 2)));
            Assert.Equal(WF.Cursors.Hand, selector.CursorForCurrent());
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
            FastRegionSelectorDialog.CursorProvider = () => dragged;
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
            FastRegionSelectorDialog.CursorProvider = () => end;
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
            FastRegionSelectorDialog.CursorProvider = () => dragged;
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
                FastRegionSelectorDialog.CursorProvider = () => miss;
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

    /// <summary>
    /// The HUD draws a focus ring around whichever row has focus, so the primary key has to
    /// work that row. Enter used to be hard-coded to "record video" and ignore the ring
    /// entirely, and the user only found out after the take was made.
    /// </summary>
    [Fact]
    public void EnterWorksTheFocusedRow_AndStillRecordsVideoWithNothingFocused()
    {
        var region = new SD.Rectangle(600, 300, 700, 300);
        var press = new SD.Point(region.Left + 10, region.Top + 10);

        // Nothing focused: Enter is still "record video", exactly as that row advertises.
        RunSelector(region, press, selector =>
        {
            Assert.Equal(-1, Field<int>(selector, "_hudFocus"));
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Enter));
            Assert.NotNull(selector.RecordingChoice);
            Assert.False(selector.RecordingChoice!.Value.IsGif);
        });

        // Ring on "Record GIF": Enter has to make a GIF, the same as Space does.
        RunSelector(region, press, selector =>
        {
            SetField(selector, "_hudFocus", FocusIndexOf(RecordingHudCell.RecordGif));
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Enter));
            Assert.NotNull(selector.RecordingChoice);
            Assert.True(selector.RecordingChoice!.Value.IsGif);
        });

        // Ring on a toggle: Enter works the toggle rather than starting a take at all.
        RunSelector(region, press, selector =>
        {
            SetField(selector, "_hudFocus", FocusIndexOf(RecordingHudCell.Microphone));
            RecordingHudToggles before = Field<RecordingHudToggles>(selector, "_recordingToggles");
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Enter));

            Assert.NotEqual(
                before.Microphone,
                Field<RecordingHudToggles>(selector, "_recordingToggles").Microphone);
            Assert.Null(selector.RecordingChoice);
        });

        // Alt+Enter stays the global force-GIF shortcut whatever has the ring.
        RunSelector(region, press, selector =>
        {
            SetField(selector, "_hudFocus", FocusIndexOf(RecordingHudCell.Microphone));
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Enter | WF.Keys.Alt));
            Assert.NotNull(selector.RecordingChoice);
            Assert.True(selector.RecordingChoice!.Value.IsGif);
        });
    }

    // ---- the aspect popover owns its keys -------------------------------------------

    /// <summary>
    /// The popover is drawn on top of both cards and owns the screen while it is up, so every
    /// key it does not act on stops there. Enter used to fall through to the selector's generic
    /// confirm branch, which started the take with the twelve rows still painted at full size.
    /// </summary>
    [Fact]
    public void EnterInsideTheAspectPopover_CommitsARowInsteadOfStartingTheTake()
    {
        var region = new SD.Rectangle(600, 300, 700, 300);
        var press = new SD.Point(region.Left + 10, region.Top + 10);

        RunSelector(region, press, selector =>
        {
            // Opened by pointer with the pointer still on the aspect cell, so no row is hovered.
            Invoke(selector, "HandleHudClick", RecordingHudCell.Aspect);
            Assert.True(Field<bool>(selector, "_aspectMenuOpen"));
            Assert.Equal(-1, Field<int>(selector, "_aspectHover"));

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Enter));

            // The popover answered its own key: it closed on the current preset, and nothing
            // behind it was committed.
            Assert.False(Field<bool>(selector, "_aspectMenuOpen"));
            Assert.Null(selector.RecordingChoice);
            Assert.Equal(WF.DialogResult.None, selector.DialogResult);

            // With a row highlighted, Enter commits that row.
            Invoke(selector, "HandleHudClick", RecordingHudCell.Aspect);
            int nineSixteen = IndexOfRatio("9:16");
            SetField(selector, "_aspectHover", nineSixteen);
            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Enter));

            Assert.Equal(nineSixteen, Field<int>(selector, "_aspectIndex"));
            Assert.Null(selector.RecordingChoice);
            Invoke(selector, "StopAspectSnap");
        });

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>
    /// A selector disposed with the ratio glide still in flight must not keep the process-wide
    /// high-resolution clock: the glide is HUD motion, so it is swept with the rest of it.
    /// Without that, one app shutdown mid-glide pinned timeBeginPeriod(1) for the whole process.
    /// </summary>
    [Fact]
    public void SelectorDisposedMidAspectGlide_ReleasesTheClock()
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => true;
        try
        {
            var region = new SD.Rectangle(600, 200, 800, 800);
            var press = new SD.Point(region.Left + 10, region.Top + 10);

            RunSelector(region, press, selector =>
            {
                Invoke(selector, "PickAspect", IndexOfRatio("9:16"), region);
                WF.Application.DoEvents();
                Assert.True(Motion.Holders > 0, "the glide never took the clock");
                // RunSelector disposes the selector when this body returns — mid-glide.
            });

            PumpUntil(() => Motion.Holders == 0);
            Assert.Equal(0, Motion.Holders);
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
        }
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
    // ---- aspect-locked resize at the desktop edge ---------------------------------

    /// <summary>
    /// The ratio-derived side is not the one the cursor drives, so clamping the cursor to the
    /// desktop does not bound it. The old fallback re-fitted the rectangle about its OWN
    /// centre, which slid the anchored corner sideways by 20 px in one frame the moment the
    /// derived side crossed the edge — the opposite of what "the corner opposite the handle
    /// stays put" promises.
    /// </summary>
    [Fact]
    public void AspectLockedResize_KeepsTheAnchoredCornerPastTheDesktopEdge()
    {
        var start = new SD.Rectangle(1200, 600, 320, 180);
        const double Ratio = 16.0 / 9;

        RunSelector(start, SD.Point.Empty, selector =>
        {
            // Handle 4 is the bottom-right, so the top-left corner is the anchor. Walk the
            // cursor down past the bottom edge a frame at a time.
            int lastWidth = 0;
            for (int y = 900; y <= 1400; y += 11)
            {
                SD.Rectangle r = LockedResize(selector, start, 4, new SD.Point(1919, y), Ratio);
                Assert.Equal(start.Location, r.Location);
                Assert.True(Monitor.Contains(r), $"{r} left the desktop");
                Assert.InRange(r.Width / (double)Math.Max(1, r.Height), Ratio - 0.02, Ratio + 0.02);
                Assert.True(r.Width >= lastWidth, $"the rect shrank at y={y}: {r}");
                lastWidth = r.Width;
            }

            // The mirror case: handle 0 is the top-left, so the bottom-right corner anchors
            // and the rectangle grows up and to the left, off two edges at once.
            var bottomRight = new SD.Point(start.Right, start.Bottom);
            SD.Rectangle up = LockedResize(selector, start, 0, new SD.Point(-500, -500), Ratio);
            Assert.Equal(bottomRight, new SD.Point(up.Right, up.Bottom));
            Assert.True(Monitor.Contains(up), $"{up} left the desktop");
            Assert.InRange(up.Width / (double)Math.Max(1, up.Height), Ratio - 0.02, Ratio + 0.02);

            // Edge handles drive one axis and derive the other; the anchor holds for them too.
            foreach (int handle in new[] { 1, 3, 5, 7 })
            {
                SD.Rectangle edge = LockedResize(selector, start, handle, new SD.Point(3000, 3000), Ratio);
                Assert.True(Monitor.Contains(edge), $"handle {handle} produced {edge}, off the desktop");
            }
        });
    }

    /// <summary>
    /// The popover is painted through a 0.94→1.0 scale about its own bottom centre while it
    /// opens. The hit-test read the unscaled rows, so a click landing inside those 140 ms
    /// picked the row below the one drawn under the pointer — the same class of bug the HUD
    /// block's own entrance offset had, simply never carried into the popover.
    /// </summary>
    [Fact]
    public void ClickingTheAspectPopoverWhileItOpens_PicksTheRowUnderThePointer()
    {
        var region = new SD.Rectangle(600, 300, 700, 300);
        var press = new SD.Point(region.Left + 10, region.Top + 10);

        RunSelector(region, press, selector =>
        {
            Invoke(selector, "HandleHudClick", RecordingHudCell.Aspect);
            Assert.True(Field<bool>(selector, "_aspectMenuOpen"));
            // The first frame of the open: painted at 0.94 about the card's bottom centre.
            SetField(selector, "_menuAmount", 0d);

            // The selector's OWN rects, not a re-derivation: AspectMenuRects reads the real
            // monitor under the region and its real DPI, so a hard-coded 1920x1080 at 100 %
            // put the row somewhere the code never painted it — and the click landed on
            // whichever row happened to be there on a 150 % display.
            var (card, rows) = AspectMenuRects(region);

            const double Scale = 0.94;
            SD.Rectangle row = rows[0];
            double pivotX = card.Left + (card.Width / 2.0);
            double pivotY = card.Bottom;
            var painted = new SD.Point(
                (int)Math.Round(pivotX + ((row.Left + (row.Width / 2.0) - pivotX) * Scale)),
                (int)Math.Round(pivotY + ((row.Top + (row.Height / 2.0) - pivotY) * Scale)));

            FastRegionSelectorDialog.CursorProvider = () => painted;
            SetField(selector, "_currentScreen", painted);
            selector.HandleMouseDown(Click(painted, 1));

            Assert.False(Field<bool>(selector, "_aspectMenuOpen"));
            Assert.Equal(0, Field<int>(selector, "_aspectIndex"));
            Invoke(selector, "StopAspectSnap");
        });

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>
    /// A fresh marquee that collapses is dropped, but a handle dragged onto its own anchor left
    /// a 0x0 pending rect that still looked framed — and Record on it closed the overlay and did
    /// nothing at all. The release grows it back to something recordable instead.
    /// </summary>
    [Fact]
    public void AHandleDraggedOntoItsAnchor_LeavesARecordableSelection()
    {
        var start = new SD.Rectangle(600, 300, 320, 180);

        RunSelector(start, SD.Point.Empty, selector =>
        {
            SetField(selector, "_adjustStartRect", start);
            SetField(selector, "_resizeHandle", 4); // bottom-right, dragged onto the top-left

            var collapsed = new SD.Point(start.Left, start.Top);
            FastRegionSelectorDialog.CursorProvider = () => collapsed;
            SetField(selector, "_currentScreen", collapsed);
            selector.HandleMouseMove();
            selector.HandleMouseUp(Click(collapsed, 1));

            SD.Rectangle after = Field<SD.Rectangle?>(selector, "_pendingScreen")!.Value;
            Assert.True(
                after.Width >= FastRegionSelectorDialog.MinPendingPx &&
                after.Height >= FastRegionSelectorDialog.MinPendingPx,
                $"the collapsed resize left {after} pending — Record on this is a silent no-op");
            Assert.True(
                RecordingRegionSelection.FromVirtualSelection(after, Monitor).IsUsable,
                $"{after} does not survive the even-rounding the encoder needs");
        });

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    private static SD.Rectangle LockedResize(
        FastRegionSelectorDialog selector, SD.Rectangle start, int handle, SD.Point cursor, double ratio) =>
        (SD.Rectangle)typeof(FastRegionSelectorDialog)
            .GetMethod("LockedResize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(selector, [start, handle, cursor, ratio])!;

    private static void RunSelector(SD.Rectangle pending, SD.Point cursor, Action<FastRegionSelectorDialog> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            FastRegionSelectorDialog.CursorProvider = () => cursor;
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
                // Land the entrance before the body runs. Mid-rise the block is painted below
                // where its geometry says it is and the hit-test takes that offset back out,
                // so a test pressing at raw geometry coordinates would be probing a HUD that
                // is not there yet — which is a real state, but not the one these cover.
                PumpUntil(() => Field<double>(selector, "_hudAmount") >= 1d);
                body(selector);
            }
            catch (Exception ex) { failure = ex; }
            finally { FastRegionSelectorDialog.CursorProvider = null; }
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
    private static (SD.Rectangle Bar, SD.Rectangle Pad, SD.Rectangle Cancel, SD.Rectangle Done)
        ActionBarRects(FastRegionSelectorDialog selector, SD.Rectangle region)
    {
        MethodInfo method = typeof(FastRegionSelectorDialog).GetMethod(
            "ActionBarRects", BindingFlags.Static | BindingFlags.NonPublic)!;
        object result = method.Invoke(null, [region])!;
        return ((SD.Rectangle, SD.Rectangle, SD.Rectangle, SD.Rectangle))result;
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

    /// <summary>Where a cell sits in the HUD's Tab order — the same array the focus ring and
    /// the Space/Enter handler index into.</summary>
    private static int FocusIndexOf(RecordingHudCell cell) =>
        Array.IndexOf(
            (RecordingHudCell[])typeof(FastRegionSelectorDialog)
                .GetField("HudFocusOrder", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!,
            cell);

    /// <summary>The aspect popover's card and rows exactly as the selector lays them out.</summary>
    private static (SD.Rectangle Card, IReadOnlyList<SD.Rectangle> Rows) AspectMenuRects(SD.Rectangle region)
    {
        object result = typeof(FastRegionSelectorDialog)
            .GetMethod("AspectMenuRects", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [region])!;
        Type tuple = result.GetType();
        return ((SD.Rectangle)tuple.GetField("Item1")!.GetValue(result)!,
                (IReadOnlyList<SD.Rectangle>)tuple.GetField("Item2")!.GetValue(result)!);
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
