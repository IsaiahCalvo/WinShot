using System.Diagnostics;
using System.Drawing;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Measure mode driven through the selector's own input handlers over a synthetic frozen
/// snapshot — no real screen, no real pointer, and the chrome is pixel-scanned rather than
/// eyeballed.
/// </summary>
public class MeasureModeTests
{
    private static readonly Rectangle Screen = new(0, 0, 400, 300);
    private static readonly Rectangle Card = new(50, 40, 100, 60);

    /// <summary>A blue desktop with one solid card on it, which is the whole world these
    /// tests need to measure.</summary>
    private static Bitmap Desktop()
    {
        var bmp = new Bitmap(Screen.Width, Screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0x10, 0x30, 0x80));
        using var card = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0));
        g.FillRectangle(card, Card);
        return bmp;
    }

    private static FastRegionSelectorDialog Measuring(Point cursor, out Bitmap frozen)
    {
        var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        frozen = Desktop();
        selector.EnterMeasureForRender(frozen, Screen, cursor);
        return selector;
    }

    // ----------------------------------------------------------------- measuring

    [Fact]
    public void HoveringAnObjectMeasuresItWithNoClickAndNoModifier()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        Assert.Equal(Card, selector.MeasuredScreenRect);
    }

    [Fact]
    public void HoveringTheGapMeasuresTheGap()
    {
        // Left of the card: the horizontal rays stop at the card and at the screen edge, the
        // vertical ones run the full height. Four independent rays is what buys this.
        using var selector = Measuring(new Point(20, 60), out _);

        Rectangle gap = Assert.IsType<Rectangle>(selector.MeasuredScreenRect);
        Assert.Equal(0, gap.Left);
        Assert.Equal(50, gap.Width);
        Assert.Equal(300, gap.Height);
    }

    [Fact]
    public void TheWheelChangesToleranceAndTheAnswerChangesWithIt()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        Assert.Equal(EdgeSnap.DefaultTolerance, selector.MeasureState.Tolerance);

        // All the way up: every edge on the desktop dissolves and the run is the monitor.
        for (int i = 0; i < 20; i++)
            selector.HandleMouseWheel(new WF.MouseEventArgs(WF.MouseButtons.None, 0, 80, 60, 120));

        Assert.Equal(255, selector.MeasureState.Tolerance);
        Assert.Equal(Screen, selector.MeasuredScreenRect);
    }

    [Fact]
    public void ToleranceIsRememberedInSettingsSoTheNextSessionStartsWhereThisOneEnded()
    {
        var settings = new SettingsService();
        using var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings);
        var frozen = Desktop();
        selector.EnterMeasureForRender(frozen, Screen, new Point(80, 60));

        selector.HandleMouseWheel(new WF.MouseEventArgs(WF.MouseButtons.None, 0, 80, 60, 120));

        Assert.Equal(45, settings.Current.MeasureTolerance);
    }

    // ----------------------------------------------------------------- gestures

    [Fact]
    public void HAndVHoldTheSpanTheyName()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.H));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V));

        Assert.Equal(2, selector.MeasureState.Holds.Count);
        Assert.True(selector.MeasureState.Holds[0].Horizontal);
        Assert.False(selector.MeasureState.Holds[1].Horizontal);
        Assert.Equal(Card, selector.MeasureState.Holds[0].Bounds);
    }

    [Fact]
    public void HoldingDoesNotStopTheMeasuringUnderneath()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.H));

        selector.MoveMeasureCursorForRender(new Point(20, 60));

        Assert.Single(selector.MeasureState.Holds);
        Assert.NotEqual(Card, selector.MeasuredScreenRect);
    }

    [Fact]
    public void BackspaceTakesTheNewestHoldBack()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.H));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V));

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Back));

        Assert.Single(selector.MeasureState.Holds);
        Assert.True(selector.MeasureState.Holds[0].Horizontal);
    }

    [Fact]
    public void ShiftHAndShiftVDropGuidesOnTheAxisTheyName()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.H | WF.Keys.Shift));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V | WF.Keys.Shift));

        Assert.Equal(new[] { 60 }, selector.MeasureState.GuidesY);
        Assert.Equal(new[] { 80 }, selector.MeasureState.GuidesX);
        Assert.Empty(selector.MeasureState.Holds); // Shift+H is a guide, never a hold
    }

    [Fact]
    public void FirstEscapeClearsTheCanvasAndTheSecondOneClosesTheSelector()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.H));

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        Assert.Empty(selector.MeasureState.Holds);
        Assert.Equal(WF.DialogResult.None, selector.DialogResult);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        Assert.Equal(WF.DialogResult.Cancel, selector.DialogResult);
    }

    [Fact]
    public void EscapeClosesStraightAwayWhenThereIsNothingToClear()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));

        Assert.Equal(WF.DialogResult.Cancel, selector.DialogResult);
    }

    [Fact]
    public void AltDoesNotLatchTheMagnifierHereBecauseTheLoupeIsPartOfTheTool()
    {
        // The loupe is ON — that is the point: Alt is the contrast modifier in this mode, so
        // it must not blink the loupe out from under the hand reaching for Alt+click.
        using var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        using Bitmap frozen = Desktop();
        selector.EnterMeasureForRender(frozen, Screen, new Point(80, 60), magnifier: true);
        Assert.True(selector.MagnifierVisible);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Menu | WF.Keys.Alt));

        Assert.True(selector.MagnifierVisible);
    }

    [Fact]
    public void MeasureModeTurnsTheLoupeOnEvenWhenTheSettingHasItOff()
    {
        // Alt is rebound here, so "Show magnifier" off would leave the mode with no loupe and
        // no gesture left to summon one.
        var off = new Settings { ShowMagnifier = false, FreezeScreen = false, CrosshairMode = "never" };
        SelectorOptions measure = SelectorOptions.ForRegion(off).ForMeasure();

        Assert.True(measure.ShowMagnifier);
        Assert.True(measure.ShowCrosshair);
        Assert.True(measure.FreezeScreen);
        Assert.Equal(SelectorCrosshairMode.Always, measure.CrosshairMode);
    }

    // ----------------------------------------------------------------- the deferred snapshot

    [Fact]
    public void TheSnapshotLandingMeasuresWithoutWaitingForThePointerToMove()
    {
        // The overlay opens instantly and the freeze snapshot lands 300-1700 ms later. A
        // cursor that has not moved since the hotkey fired still has to get a reading.
        using var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        using Bitmap frozen = Desktop();
        selector.EnterMeasureForRender(frozen: null, Screen, new Point(80, 60));
        Assert.Null(selector.MeasuredScreenRect);

        selector.LandFrozenSnapshotForRender(frozen);

        Assert.Equal(Card, selector.MeasuredScreenRect);
    }

    [Fact]
    public void TheHintStripSaysItIsStillPreparingUntilTheSnapshotLands()
    {
        using var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        using Bitmap frozen = Desktop();
        selector.EnterMeasureForRender(frozen: null, Screen, new Point(80, 60));

        Assert.Contains("Preparing", selector.HintStripText());
        Assert.DoesNotContain("H / V hold", selector.HintStripText());
        Assert.Contains("preparing", selector.NothingToMeasureMessage);

        selector.LandFrozenSnapshotForRender(frozen);

        Assert.Contains("H / V hold", selector.HintStripText());
        Assert.Equal("Nothing to measure here.", selector.NothingToMeasureMessage);
    }

    // ----------------------------------------------------------------- the right-click menu

    /// <summary>
    /// A drop-down does not stop WM_TIMER, so the 8 ms follow tick keeps re-measuring under
    /// the open menu as the hand walks down it. Every row therefore has to act on where the
    /// right-click landed, not on where the pointer ended up.
    /// </summary>
    private static FastRegionSelectorDialog MenuOpenedAt(Point rightClick, out Bitmap frozen)
    {
        var selector = Measuring(rightClick, out frozen);
        selector.ShowMeasureMenuForRender(rightClick);
        return selector;
    }

    private static void CloseSession(FastRegionSelectorDialog selector)
    {
        // A live drop-down left open hands the next test class a modal-menu hook.
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        WF.Application.DoEvents();
    }

    private static void ClickRow(FastRegionSelectorDialog selector, string text) =>
        selector.MeasureMenuForRender!.Items.Cast<WF.ToolStripItem>()
            .First(i => i.Text == text).PerformClick();

    [Fact]
    public void AGuideFromTheMenuLandsWhereTheRightClickDidNotWhereTheRowIs()
    {
        using var selector = MenuOpenedAt(new Point(80, 60), out _);

        selector.MoveMeasureCursorForRender(new Point(210, 190)); // walking down to the row
        ClickRow(selector, "Add vertical guide");

        Assert.Equal(new[] { 80 }, selector.MeasureState.GuidesX);
        CloseSession(selector);
    }

    [Fact]
    public void HoldWidthFromTheMenuHoldsTheObjectThatWasRightClicked()
    {
        using var selector = MenuOpenedAt(new Point(80, 60), out _);

        selector.MoveMeasureCursorForRender(new Point(300, 250)); // off the card entirely
        ClickRow(selector, "Hold width");

        Assert.Equal(Card.Width, selector.MeasureState.Holds.Single().Bounds.Width);
        CloseSession(selector);
    }

    [Fact]
    public void CaptureThisObjectCapturesTheObjectThatWasRightClicked()
    {
        using var selector = MenuOpenedAt(new Point(80, 60), out _);

        selector.MoveMeasureCursorForRender(new Point(300, 250));
        ClickRow(selector, "Capture this object");

        Assert.Equal(Card, selector.SelectedRegionPx);
        WF.Application.DoEvents();
    }

    [Fact]
    public void ShiftLocksTheReadoutToOneAxisAndLettingGoPutsTheOtherBack()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        // The card is 100 x 60, so the longer span is the width — that is the axis the eye
        // was already on, and the one Shift keeps.
        Assert.Contains("×", selector.LiveMeasurementLabelForRender(axisLocked: false));
        Assert.StartsWith("100", selector.LiveMeasurementLabelForRender(axisLocked: true));
        Assert.DoesNotContain("×", selector.LiveMeasurementLabelForRender(axisLocked: true));
    }

    [Fact]
    public void HeldShiftRepaintsOnceOnTheWayDownAndOnceOnTheWayUp()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        // Key-repeat fires many times a second and each repaint is a whole 4K surface.
        var down = new WF.KeyEventArgs(WF.Keys.ShiftKey | WF.Keys.Shift);
        selector.HandleKeyDown(down);
        Assert.True(down.Handled);

        var repeat = new WF.KeyEventArgs(WF.Keys.ShiftKey | WF.Keys.Shift);
        selector.HandleKeyDown(repeat);
        Assert.False(repeat.Handled);

        selector.HandleKeyUp(new WF.KeyEventArgs(WF.Keys.ShiftKey));
        var again = new WF.KeyEventArgs(WF.Keys.ShiftKey | WF.Keys.Shift);
        selector.HandleKeyDown(again);
        Assert.True(again.Handled);
    }

    [Fact]
    public void EnterCapturesTheMeasuredObject()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Enter));

        Assert.Equal(WF.DialogResult.OK, selector.DialogResult);
        Assert.Equal(Card, selector.SelectedRegionPx);
        Assert.True(selector.SelectedByPaneClick);
    }

    [Fact]
    public void RightClickOpensTheMenuInsteadOfCancellingTheWayItDoesInAreaMode()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        // A right-click in Area mode is Cancel and that reflex must stay trained; in Measure
        // mode it is the only way to discover the six gestures.
        selector.HandleMouseDown(new WF.MouseEventArgs(WF.MouseButtons.Right, 1, 80, 60, 0));

        Assert.Equal(WF.DialogResult.None, selector.DialogResult);

        // Leaving a real drop-down open would hand the next test class a live modal-menu
        // hook; Escape ends the session, which closes it.
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        WF.Application.DoEvents();
    }

    [Fact]
    public void TheHintStripNamesTheToleranceItIsCurrentlyUsing()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        Assert.Contains("Tolerance 30", selector.HintStripText());

        selector.HandleMouseWheel(new WF.MouseEventArgs(WF.MouseButtons.None, 0, 80, 60, 120));

        Assert.Contains("Tolerance 45", selector.HintStripText());
    }

    // ----------------------------------------------------------------- rendering

    /// <summary>Paints one surface into a bitmap the way a pane would.</summary>
    private static Bitmap Render(FastRegionSelectorDialog selector)
    {
        var canvas = new Bitmap(Screen.Width, Screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        selector.PaintSurface(g, Screen);
        return canvas;
    }

    private static bool IsMeasureCrimson(Color c) =>
        c.R == ThemePalette.Measure.R && c.G == ThemePalette.Measure.G && c.B == ThemePalette.Measure.B;

    private static int CountCrimson(Bitmap bmp, Rectangle where)
    {
        int found = 0;
        for (int y = where.Top; y < where.Bottom; y++)
        {
            for (int x = where.Left; x < where.Right; x++)
            {
                if (IsMeasureCrimson(bmp.GetPixel(x, y)))
                    found++;
            }
        }
        return found;
    }

    // The reticle is crimson too and runs full-bleed with a 10 px gap around the cursor, so
    // the scans below sample INSIDE that gap: crimson there can only have come from the
    // measurement itself.
    [Fact]
    public void TheMeasuredSpanIsDrawnInCrimsonAcrossExactlyTheRunItMeasured()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        using Bitmap render = Render(selector);

        Assert.True(IsMeasureCrimson(render.GetPixel(75, 60)), "span, left of the reticle gap");
        Assert.True(IsMeasureCrimson(render.GetPixel(85, 60)), "span, right of the reticle gap");
        Assert.False(IsMeasureCrimson(render.GetPixel(75, 61)), "and one row only");
    }

    [Fact]
    public void TheSpanCarriesATSerifAtEachEnd()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        using Bitmap render = Render(selector);

        // Two px above and below each end, on the end column only — that is the serif, and
        // it is the whole reason the line reads as a measurement rather than a guide.
        Assert.True(IsMeasureCrimson(render.GetPixel(Card.Left, 58)), "left serif, above");
        Assert.True(IsMeasureCrimson(render.GetPixel(Card.Left, 62)), "left serif, below");
        Assert.True(IsMeasureCrimson(render.GetPixel(Card.Right - 1, 58)), "right serif, above");
        Assert.True(IsMeasureCrimson(render.GetPixel(Card.Right - 1, 62)), "right serif, below");
        Assert.False(IsMeasureCrimson(render.GetPixel(Card.Left + 40, 58)), "no serif mid-span");
        Assert.False(IsMeasureCrimson(render.GetPixel(Card.Left - 3, 58)), "no serif outside the run");
    }

    [Fact]
    public void TheVerticalSpanIsDrawnTooSoTheCrossReadsAsWidthByHeight()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        using Bitmap render = Render(selector);

        // Inside the reticle's 10 px gap, so crimson here is the measurement and nothing else.
        Assert.True(IsMeasureCrimson(render.GetPixel(80, 55)), "span, above the reticle gap");
        Assert.True(IsMeasureCrimson(render.GetPixel(80, 65)), "span, below the reticle gap");
        Assert.False(IsMeasureCrimson(render.GetPixel(83, 55)), "and one column only");
    }

    [Fact]
    public void AGuideIsDrawnInAccentBlueEdgeToEdgeWithNoSerifs()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V | WF.Keys.Shift));
        // Step off the guide's own column: the crimson reticle sits on the cursor, and a
        // guide under the pointer would be painted over by it.
        selector.MoveMeasureCursorForRender(new Point(250, 200));
        using Bitmap render = Render(selector);

        bool Accent(int x, int y)
        {
            Color c = render.GetPixel(x, y);
            return c.R == ThemePalette.Accent.R && c.G == ThemePalette.Accent.G && c.B == ThemePalette.Accent.B;
        }

        // Full bleed: a guide reaches the top and bottom of the surface, unlike a dimension,
        // which stops at the run it measures and wears a serif there.
        Assert.True(Accent(80, 0), "guide reaches the top edge");
        Assert.True(Accent(80, Screen.Height - 1), "guide reaches the bottom edge");
        Assert.False(Accent(84, 0), "and it is one pixel wide");
        Assert.False(Accent(82, 0), "no serif at the end of a guide");
    }

    [Fact]
    public void OrdinaryAreaCaptureNeverPaintsMeasurementCrimson()
    {
        // The measure vocabulary must not leak into the mode people use all day.
        using var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        using Bitmap render = Render(selector);

        Assert.Equal(0, CountCrimson(render, Screen));
    }

    [Fact]
    public void PaintingWithNoFrozenSnapshotDegradesToAPlainDimInsteadOfThrowing()
    {
        // Freeze can fail (a locked display, an RDP blip). Measurement then has nothing to
        // read, and the overlay still has to draw.
        using var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        var frozen = Desktop();
        selector.EnterMeasureForRender(frozen, Screen, new Point(80, 60));
        // Complete disposes the snapshot and drops the live run with it.
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        selector.MoveMeasureCursorForRender(new Point(80, 60));

        using Bitmap render = Render(selector);

        // The reticle still tracks the cursor, so the scan avoids its row and column: what
        // must be absent is a measurement, not the crosshair.
        Assert.Null(selector.MeasuredScreenRect);
        Assert.Equal(0, CountCrimson(render, new Rectangle(100, 100, 250, 150)));
    }

    // ------------------------------------------------------- the two pills, side by side

    /// <summary>A card with a dark mark inside it, so the region has two colours and the
    /// contrast readout has something to report. The mark sits clear of the rays cast from
    /// (80, 60), so the run is still exactly the card.</summary>
    private static Bitmap DesktopWithContrast()
    {
        var bmp = new Bitmap(Screen.Width, Screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0x10, 0x30, 0x80));
        using (var card = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0)))
            g.FillRectangle(card, Card);
        // Clear of every row and column the tests cast a ray along, so the run stays the
        // whole card while the region still holds two colours to compare.
        using (var mark = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20)))
            g.FillRectangle(mark, new Rectangle(60, 70, 8, 8));
        return bmp;
    }

    private static FastRegionSelectorDialog MeasuringWithContrast(Point cursor, out Bitmap frozen)
    {
        var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        frozen = DesktopWithContrast();
        selector.EnterMeasureForRender(frozen, Screen, cursor);
        return selector;
    }

    [Theory]
    [InlineData(42)]  // 2 px inside the card's top edge — the worst case
    [InlineData(60)]
    [InlineData(96)]  // and 2 px inside the bottom edge
    public void TheContrastReadoutNeverLandsOnTopOfTheLiveMeasurementPill(int cursorY)
    {
        // Both pills are the same ground colour, so an overlap is one blob with two texts
        // stacked in it — and the live pill is also the click-to-capture target.
        using var selector = MeasuringWithContrast(new Point(80, cursorY), out _);

        Rectangle live = Assert.IsType<Rectangle>(selector.LivePillForRender);
        Rectangle contrast = Assert.IsType<Rectangle>(selector.ContrastPillForRender);

        Assert.False(live.IntersectsWith(contrast),
            $"live {live} and contrast {contrast} overlap at cursor y={cursorY}");
        Assert.True(Screen.Contains(contrast), $"contrast pill {contrast} is off screen");
    }

    [Fact]
    public void BothPillGroundsAreActuallyPaintedOnTheRectsTheyReport()
    {
        // Ties the placement above to the paint: a rect nothing draws on proves nothing.
        using var selector = MeasuringWithContrast(new Point(80, 42), out _);
        Rectangle live = Assert.IsType<Rectangle>(selector.LivePillForRender);
        Rectangle contrast = Assert.IsType<Rectangle>(selector.ContrastPillForRender);
        using Bitmap render = Render(selector);

        // The pill ground is near-black neutral chrome; everything it could be drawn over
        // here (dimmed blue desktop, dimmed white card) is neither.
        static bool IsPillGround(Color c) =>
            c.R < 70 && c.G < 70 && c.B < 70 && Math.Abs(c.R - c.B) < 20;

        Assert.True(IsPillGround(render.GetPixel(live.Left + 6, live.Top + (live.Height / 2))),
            "live pill ground");
        Assert.True(IsPillGround(render.GetPixel(contrast.Left + 6, contrast.Top + (contrast.Height / 2))),
            "contrast pill ground");
        Assert.False(IsPillGround(render.GetPixel(100, Card.Bottom - 4)),
            "and the card between them is not covered by either");
    }

    [Theory]
    [InlineData(42)]
    [InlineData(60)]
    [InlineData(96)]
    public void AClickMeansOneThingBecauseThePillIsNeverUnderThePointer(int cursorY)
    {
        // The pill is placed relative to the cursor's OWN row, so no pointer can ever be
        // inside it — which is why a left click has one meaning (copy) instead of two, and
        // why capturing the object is Enter and the menu row the hint strip names.
        using var selector = Measuring(new Point(80, cursorY), out _);
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
        Assert.False(pill.Contains(new Point(80, cursorY)), $"pill {pill} sits under the cursor");

        selector.HandleMouseDown(new WF.MouseEventArgs(WF.MouseButtons.Left, 1, 80, cursorY, 0));

        // A click never ends the session here; only Enter and the menu do.
        Assert.Equal(WF.DialogResult.None, selector.DialogResult);
    }

    [Fact]
    public void ACursorOffTheFrozenSnapshotMeasuresNothingRatherThanReadingRubbish()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        selector.MoveMeasureCursorForRender(new Point(9999, 9999));

        Assert.Null(selector.MeasuredScreenRect);
    }
}

/// <summary>
/// The hint strip's entrance is the only animation in Measure mode, so it is the only place
/// the mode can leak a high-resolution-clock holder. Motion.Holders is process-global, so
/// this rides the serialized collection.
/// </summary>
[Collection("MotionRefcount")]
public class MeasureMotionTests
{
    private static FastRegionSelectorDialog Selector() =>
        new(() => Task.FromResult(new List<WindowInfo>()), settings: null);

    [Fact]
    public void TheHintStripFadesInAndGivesTheClockBackWhenItLands()
    {
        PumpUntil(() => Motion.Holders == 0);

        using var selector = Selector();
        selector.StartHintEntrance();

        PumpUntil(() => selector.HintAmount >= 1.0);

        Assert.Equal(1.0, selector.HintAmount, 3);
        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void ClosingMidFadeReleasesTheClockInsteadOfStrandingIt()
    {
        PumpUntil(() => Motion.Holders == 0);

        using var selector = Selector();
        selector.StartHintEntrance();
        selector.StopHintEntrance();

        Assert.Equal(0, Motion.Holders);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }

        Assert.True(condition(), "timed out waiting for the hint strip");
    }
}
