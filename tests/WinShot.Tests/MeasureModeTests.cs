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
    public void ClickingTheObjectCopiesBecauseThePillIsParkedOffIt(int cursorY)
    {
        // The pill sits outside the run it labels, so a pointer that is measuring an object
        // is never inside it. That is what lets one button carry two meanings: on the object
        // it copies, and only on the pill — the one thing under the pointer that lights up —
        // does it capture.
        using var selector = Measuring(new Point(80, cursorY), out _);
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
        Assert.False(pill.IntersectsWith(Card), $"pill {pill} overlaps the object it measures");
        Assert.False(pill.Contains(new Point(80, cursorY)), $"pill {pill} sits under the cursor");

        selector.MeasureMouseDownForRender(WF.MouseButtons.Left);

        // A click on the object never ends the session.
        Assert.Equal(WF.DialogResult.None, selector.DialogResult);
    }

    [Fact]
    public void WalkingOntoThePillHoldsTheReadingStillSoThePillCanBeReached()
    {
        // A pill placed relative to the pointer keeps its distance from the pointer forever.
        // This one is parked off the object, and the corridor between the two holds the
        // reading still — otherwise the step that leaves the object would re-measure the
        // desktop behind it and the pill would be gone by the time the pointer arrived.
        using var selector = Measuring(new Point(80, 60), out _);
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
        Rectangle reach = Assert.IsType<Rectangle>(selector.LivePillReachForRender);
        Assert.True(reach.Contains(pill), $"reach {reach} does not contain the pill {pill}");
        Assert.False(selector.PillIsHotForRender);

        // Which side the pill parked on depends on the monitor's DPI — a 150 % screen makes
        // the pill tall enough to flip below the card — so the walk is derived, not assumed.
        int column = pill.Left + (pill.Width / 2);
        int corridorY = pill.Top >= Card.Bottom ? reach.Top + 1 : reach.Bottom - 1;
        Assert.False(Card.Contains(new Point(column, corridorY)), "the corridor is off the object");
        selector.MoveMeasureCursorForRender(new Point(column, corridorY));
        Assert.Equal(Card, selector.MeasuredScreenRect);
        Assert.False(selector.PillIsHotForRender, "the corridor is not itself a target");

        selector.MoveMeasureCursorForRender(new Point(column, pill.Top + 2)); // the pill
        Assert.Equal(Card, selector.MeasuredScreenRect);
        Assert.Equal(pill, selector.LivePillForRender);
        Assert.True(selector.PillIsHotForRender);

        selector.MeasureMouseDownForRender(WF.MouseButtons.Left);

        Assert.Equal(WF.DialogResult.OK, selector.DialogResult);
        Assert.Equal(Card, selector.SelectedRegionPx);
        Assert.True(selector.SelectedByPaneClick);
    }

    [Fact]
    public void SteppingOffThePillStartsMeasuringAgain()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
        selector.MoveMeasureCursorForRender(new Point(pill.Left + (pill.Width / 2), pill.Top + 2));
        Assert.True(selector.PillIsHotForRender);

        // Sideways out of the pill's own column: nothing is holding the reading now.
        selector.MoveMeasureCursorForRender(new Point(300, 250));

        Assert.False(selector.PillIsHotForRender);
        Assert.NotEqual(Card, selector.MeasuredScreenRect);
    }

    // ------------------------------------------------- the walk is the port, and stays it

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + ((b.R - a.R) * t)),
        (int)Math.Round(a.G + ((b.G - a.G) * t)),
        (int)Math.Round(a.B + ((b.B - a.B) * t)));

    /// <summary>The card, but its last two columns fade into the background the way an
    /// anti-aliased glyph stem or a rounded corner reaches the pixels behind it.</summary>
    private static Bitmap DesktopWithASoftEdge()
    {
        Bitmap bmp = Desktop();
        Color bg = Color.FromArgb(0x10, 0x30, 0x80);
        Color card = Color.FromArgb(0xF0, 0xF0, 0xF0);
        for (int y = Card.Top; y < Card.Bottom; y++)
        {
            bmp.SetPixel(Card.Right - 2, y, Blend(card, bg, 0.20));
            bmp.SetPixel(Card.Right - 1, y, Blend(card, bg, 0.35));
        }
        return bmp;
    }

    private static FastRegionSelectorDialog MeasuringOver(Bitmap frozen, Point cursor)
    {
        var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        selector.EnterMeasureForRender(frozen, Screen, cursor);
        return selector;
    }

    [Fact]
    public void AnAntiAliasedEdgeStopsTheWalkAndTheWheelIsWhatAnswersIt()
    {
        // The ray stops at the first pixel that is not the colour it started on, which on a
        // faded edge is the first blended column — so the run is the solid core, two columns
        // short of the card. That is §3.3's walk exactly, and §6 rules out pre-tuning past
        // it ("no anti-aliasing hysteresis ... add only if the shipped tolerance knob proves
        // insufficient"). The knob is the answer that DID ship: loosen it and the fringe
        // stops being an edge.
        using Bitmap frozen = DesktopWithASoftEdge();
        using var selector = MeasuringOver(frozen, new Point(80, 60));

        Rectangle core = Assert.IsType<Rectangle>(selector.MeasuredScreenRect);
        Assert.Equal(Card.Left, core.Left);
        Assert.Equal(Card.Width - 2, core.Width);

        // Twelve notches of the wheel and the fringe is inside the tolerance again.
        for (int i = 0; i < 12; i++)
            selector.HandleMouseWheel(new WF.MouseEventArgs(WF.MouseButtons.None, 0, 80, 60, 120));

        Assert.Equal(Card.Width, Assert.IsType<Rectangle>(selector.MeasuredScreenRect).Width);
    }

    [Fact]
    public void EveryEndOfAMeasuredSpanCarriesItsSerifWhateverTheEdgeLookedLike()
    {
        // §3.5's vocabulary has one dimension mark, not a firm one and a soft one: an
        // aliased hairline with a T-cap at each end, no dashes and no glow.
        using Bitmap frozen = DesktopWithASoftEdge();
        using var selector = MeasuringOver(frozen, new Point(80, 60));
        Rectangle core = Assert.IsType<Rectangle>(selector.MeasuredScreenRect);
        using Bitmap render = Render(selector);

        Assert.True(IsMeasureCrimson(render.GetPixel(core.Left, 58)), "left serif");
        Assert.True(IsMeasureCrimson(render.GetPixel(core.Right - 1, 58)), "right serif");
    }

    [Fact]
    public void ACursorOffTheFrozenSnapshotMeasuresNothingRatherThanReadingRubbish()
    {
        using var selector = Measuring(new Point(80, 60), out _);

        selector.MoveMeasureCursorForRender(new Point(9999, 9999));

        Assert.Null(selector.MeasuredScreenRect);
    }

    // ------------------------------------------------- a drag that outlives its mouse-up

    [Fact]
    public void RightClickingMidDragPutsTheGuideDownInsteadOfGluingItToThePointer()
    {
        using var selector = Measuring(new Point(200, 150), out _);
        selector.MeasureState.AddGuideX(200);
        selector.MeasureMouseDownForRender(WF.MouseButtons.Left);
        selector.MoveMeasureCursorForRender(new Point(210, 150));
        Assert.Equal(new[] { 210 }, selector.MeasureState.GuidesX);

        // The drop-down is modal and swallows the left mouse-up, so the selector never sees
        // one — but the 8 ms follow tick keeps running underneath it.
        selector.MeasureMouseDownForRender(WF.MouseButtons.Right);
        selector.MoveMeasureCursorForRender(new Point(260, 150));

        Assert.False(selector.IsDraggingGuideForRender);
        Assert.Equal(new[] { 210 }, selector.MeasureState.GuidesX);
        CloseSession(selector);
    }

    [Fact]
    public void ClearingTheGuidesMidDragLetsTheModeMeasureAgainWithoutWaitingForTheButton()
    {
        using var selector = Measuring(new Point(200, 150), out _);
        selector.MeasureState.AddGuideX(200);
        selector.MeasureMouseDownForRender(WF.MouseButtons.Left);

        // Escape clears guides and holds while the button is still down. The drag branch
        // returns before ever reading pixels, so a drag left holding a guide that is gone
        // would freeze the reading at whatever it was when the button went down.
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        selector.MoveMeasureCursorForRender(new Point(80, 60));

        Assert.False(selector.IsDraggingGuideForRender);
        Assert.Equal(Card, selector.MeasuredScreenRect);
    }

    // ----------------------------------------------------------------- legibility

    [Fact]
    public void EveryMeasurementHairlineSitsOnItsOwnShadow()
    {
        // A bare 1 px crimson line lands on arbitrary desktop pixels and can vanish into a
        // surface of its own colour, which is the WCAG 1.4.11 case for a graphical object.
        // The crosshair has always carried a 120-alpha black halo; the dimension vocabulary
        // has to as well, or the mode's primary datum is the one thing that can disappear.
        using var selector = Measuring(new Point(80, 60), out _);
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V | WF.Keys.Shift));
        selector.MoveMeasureCursorForRender(new Point(250, 200));
        using Bitmap render = Render(selector);

        // A guide's own column, and the two columns either side of it, on a row far from
        // the reticle: the halo is what separates the two.
        Color line = render.GetPixel(80, 250);
        Color halo = render.GetPixel(79, 250);
        Color ground = render.GetPixel(76, 250);
        Assert.True(
            line.R == ThemePalette.Accent.R && line.G == ThemePalette.Accent.G && line.B == ThemePalette.Accent.B,
            $"the guide itself is no longer accent blue: {line}");
        Assert.True(Luminance(halo) < Luminance(ground) - 8,
            $"guide halo {halo} is not darker than the ground {ground} it lies on");
        Assert.True(Luminance(render.GetPixel(81, 250)) < Luminance(ground) - 8,
            "the halo is on both sides of the guide");
    }

    [Fact]
    public void TheSpanAndItsSerifsAreShadowedToo()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        using Bitmap render = Render(selector);

        // One row above the horizontal span, inside the reticle gap so nothing else is there,
        // against the card the span is drawn over.
        Color card = render.GetPixel(120, 75);
        Assert.True(Luminance(render.GetPixel(75, 59)) < Luminance(card) - 8,
            "the span's halo is missing on the brightened run it is drawn over");
        // And one pixel past a serif's tip.
        Assert.True(Luminance(render.GetPixel(Card.Left, 57)) < Luminance(card) - 8,
            "the serif carries no halo");
    }

    private static double Luminance(Color c) => (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);

    // ----------------------------------------------------------------- no snapshot

    [Fact]
    public void AFailedSnapshotSaysSoInsteadOfClaimingItIsStillLoading()
    {
        using var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        selector.EnterMeasureForRender(null, Screen, new Point(80, 60));

        // Before the attempt resolves, "not yet" is the honest answer.
        Assert.Contains("Preparing", selector.HintStripText());

        selector.NoteFrozenSnapshotFailed();

        // After it fails there is no snapshot coming, so neither surface may keep promising
        // one — and the strip has to say how to leave a mode that cannot work.
        Assert.DoesNotContain("Preparing", selector.HintStripText());
        Assert.Contains("could not be read", selector.HintStripText());
        Assert.Contains("Esc", selector.HintStripText());
        Assert.DoesNotContain("Still preparing", selector.NothingToMeasureMessage);
        Assert.Contains("could not be read", selector.NothingToMeasureMessage);
    }

    [Fact]
    public void AFailedSnapshotDoesNotLeakIntoTheNextPooledSession()
    {
        var selector = FastRegionSelectorDialog.Rent(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        selector.EnterMeasureForRender(null, Screen, new Point(80, 60));
        selector.NoteFrozenSnapshotFailed();

        FastRegionSelectorDialog.Return(selector);
        var again = FastRegionSelectorDialog.Rent(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        again.EnterMeasureForRender(null, Screen, new Point(80, 60));

        Assert.Contains("Preparing", again.HintStripText());
        FastRegionSelectorDialog.Return(again);
    }

    // ----------------------------------------------------------------- the hint strip fits

    [Theory]
    [InlineData(1366)]  // 1366x768, the smallest panel anyone still ships
    [InlineData(1600)]
    [InlineData(1920)]
    [InlineData(3840)]
    public void TheHintStripWrapsRatherThanCollapsingToAStub(int monitorWidth)
    {
        // Nine clauses on one line is 227 characters — 1824 px at 144 dpi and 2456 at 192,
        // so the old single line silently dropped to a two-item stub on most panels at the
        // DPI steps this build is graded at. The strip is the mode's only discoverability
        // surface; every gesture it teaches has to survive.
        using var selector = Measuring(new Point(80, 60), out _);

        string text = selector.HintStripTextFor(monitorWidth);

        foreach (string item in new[]
        {
            "Tolerance 30", "H / V hold", "Shift+H / Shift+V guide", "Click or Ctrl+C copies",
            "Click the pill or Enter captures", "Alt+click for WCAG contrast",
            "Backspace removes the last one", "Right-click for more", "Esc clears, Esc again exits",
        })
        {
            Assert.Contains(item, text);
        }

        // And every line of it actually fits, measured through the font the pill draws with.
        foreach (string line in text.Split('\n'))
        {
            Assert.True(SelectorChrome.MeasureLabel(line).Width <= monitorWidth,
                $"line '{line}' is wider than a {monitorWidth} px monitor");
        }
        Assert.InRange(text.Split('\n').Length, 1, 3);
    }

    [Fact]
    public void TheHintStripStepsAsideForTheLoupeInsteadOfPaintingOverIt()
    {
        // The loupe is forced on in this mode because it is part of the tool, and the strip
        // is painted last with no flip of its own — so near the bottom of the screen the
        // chrome sat on top of the coordinates and the hex swatch the user came for.
        using var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        using Bitmap frozen = Desktop();
        selector.EnterMeasureForRender(frozen, Screen, new Point(200, 270), magnifier: true);

        Rectangle loupe = selector.LoupeRectForRender(Screen);
        Rectangle strip = selector.HintStripRectForRender(Screen);

        Assert.False(loupe.IsEmpty, "the loupe is on in this mode");
        Assert.False(loupe.IntersectsWith(strip), $"strip {strip} sits on the loupe {loupe}");
        Assert.True(strip.Top >= 0, $"strip {strip} was lifted off the top of the screen");
    }

    // ------------------------------------------------- the two targets under one pointer

    [Fact]
    public void AClickOnTheLitPillCapturesEvenWithAGuideRunningThroughIt()
    {
        // A guide is a full-bleed line, grabbable anywhere along its coordinate, and the pill
        // is placed by the run rather than by the pointer — so a guide crossing the pill is
        // reachable in ordinary use. The pill is the one thing in the mode that lights up and
        // shows a hand cursor; the click has to do what the cursor just promised.
        using var selector = Measuring(new Point(80, 60), out _);
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
        int column = pill.Left + (pill.Width / 2);

        selector.MoveMeasureCursorForRender(new Point(column, 250));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V | WF.Keys.Shift));
        Assert.Contains(column, selector.MeasureState.GuidesX);

        selector.MoveMeasureCursorForRender(new Point(80, 60));
        selector.MoveMeasureCursorForRender(new Point(column, pill.Top + 2));
        Assert.True(selector.PillIsHotForRender);

        selector.MeasureMouseDownForRender(WF.MouseButtons.Left);

        Assert.False(selector.IsDraggingGuideForRender, "the click started a guide drag");
        Assert.Equal(WF.DialogResult.OK, selector.DialogResult);
        Assert.Equal(Card, selector.SelectedRegionPx);
    }

    [Fact]
    public void TurningTheWheelWhileParkedOnThePillReMeasuresTheObjectNotTheChrome()
    {
        // The move handler freezes the reading while the pointer is on the pill — that hold
        // is the only reason the pill is reachable. The wheel has to honour it, or the mode's
        // own recovery gesture destroys the measurement the moment the user walks over to it.
        using var selector = Measuring(new Point(80, 60), out _);
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
        selector.MoveMeasureCursorForRender(new Point(pill.Left + (pill.Width / 2), pill.Top + 2));
        Assert.Equal(Card, selector.MeasuredScreenRect);

        selector.HandleMouseWheel(new WF.MouseEventArgs(WF.MouseButtons.None, 0, 0, 0, 120));

        Assert.Equal(Card, selector.MeasuredScreenRect);
        Assert.True(selector.PillIsHotForRender, "the pointer is still on the pill");
    }

    // ----------------------------------------------------------------- removing one thing

    [Fact]
    public void BackspaceTakesBackAGuideInsteadOfClaimingThereIsNothingToUndo()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V | WF.Keys.Shift));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.H));   // a hold on top of it
        Assert.Single(selector.MeasureState.GuidesX);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Back));
        Assert.Empty(selector.MeasureState.Holds);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Back));
        Assert.Empty(selector.MeasureState.GuidesX);
        Assert.False(selector.MeasureState.HasAnything);
    }

    [Fact]
    public void BackspaceWithNothingToUndoStillSwallowsTheKey()
    {
        // Every other gesture in this mode suppresses; letting this one through is a system
        // beep from a key the mode claims to own.
        using var selector = Measuring(new Point(80, 60), out _);

        var back = new WF.KeyEventArgs(WF.Keys.Back);
        selector.HandleKeyDown(back);

        Assert.True(back.Handled);
        Assert.True(back.SuppressKeyPress);
    }

    [Fact]
    public void TheMenuCanRemoveOneGuideWithoutClearingTheCanvas()
    {
        using var selector = Measuring(new Point(80, 60), out _);
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V | WF.Keys.Shift));
        selector.MoveMeasureCursorForRender(new Point(250, 200));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V | WF.Keys.Shift));
        selector.ShowMeasureMenuForRender(new Point(250, 200));

        ClickRow(selector, "Remove last guide");

        Assert.Equal(new[] { 80 }, selector.MeasureState.GuidesX);
        CloseSession(selector);
    }
}

/// <summary>
/// The hint strip's entrance and the pill's hover outline are the only animations in Measure
/// mode, so they are the only places the mode can leak a high-resolution-clock holder.
/// Motion.Holders is process-global, so this rides the serialized collection.
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

    [Theory]
    [InlineData(true)]   // the snapshot lands: loading copy → nine gestures
    [InlineData(false)]  // it fails: loading copy → the error line
    public void TheStripCrossFadesWhenItsCopyChangesRatherThanCuttingToIt(bool lands)
    {
        // Three states, one surface, and one of them is an error — on the failure path
        // nothing else on screen moves at that moment, so a bare swap is an unaccompanied
        // snap. The strip re-enters through the entrance it already owns.
        PumpUntil(() => Motion.Holders == 0);

        var screen = new Rectangle(0, 0, 400, 300);
        using var selector = Selector();
        using Bitmap frozen = SolidCardOnBlue(screen, new Rectangle(50, 40, 100, 60));
        selector.EnterMeasureForRender(null, screen, new Point(80, 60));
        selector.StartHintEntrance();
        PumpUntil(() => selector.HintAmount >= 1.0);
        Assert.Contains("Preparing", selector.HintStripText());

        if (lands)
            selector.LandFrozenSnapshotForRender(frozen);
        else
            selector.NoteFrozenSnapshotFailed();

        Assert.True(selector.HintAmount < 1.0, "the copy changed without a transition");
        PumpUntil(() => selector.HintAmount >= 1.0);
        Assert.DoesNotContain("Preparing", selector.HintStripText());
        PumpUntil(() => Motion.Holders == 0);
    }

    [Fact]
    public void TheWheelAtTheEndOfItsRangeStillAnswers()
    {
        // Everything else in this mode answers every gesture. A clamped notch used to return
        // in silence: no repaint, no toast, not even a flash on the number it governs.
        PumpUntil(() => Motion.Holders == 0);

        var screen = new Rectangle(0, 0, 400, 300);
        using var selector = Selector();
        using Bitmap frozen = SolidCardOnBlue(screen, new Rectangle(50, 40, 100, 60));
        selector.EnterMeasureForRender(frozen, screen, new Point(80, 60));
        selector.MeasureState.SetTolerance(255);
        selector.StartHintEntrance();
        PumpUntil(() => selector.HintAmount >= 1.0);

        selector.HandleMouseWheel(new WF.MouseEventArgs(WF.MouseButtons.None, 0, 80, 60, 120));

        Assert.Equal(255, selector.MeasureState.Tolerance);
        Assert.True(selector.HintAmount < 1.0, "the end of the range was silent");
        PumpUntil(() => Motion.Holders == 0);
    }

    [Fact]
    public void DisposingMidAnimationGivesTheClockBack()
    {
        PumpUntil(() => Motion.Holders == 0);

        var screen = new Rectangle(0, 0, 400, 300);
        var selector = Selector();
        using Bitmap frozen = SolidCardOnBlue(screen, new Rectangle(50, 40, 100, 60));
        selector.EnterMeasureForRender(frozen, screen, new Point(80, 60));
        selector.StartHintEntrance();
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
        selector.MoveMeasureCursorForRender(new Point(pill.Left + (pill.Width / 2), pill.Top + 2));
        Assert.True(selector.PillIsHotForRender);
        Assert.True(Motion.Holders > 0, "two tweens should be live");

        selector.Dispose();

        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void WithAnimationsOffTheChromeJumpsStraightToItsEndAndHoldsNoClock()
    {
        PumpUntil(() => Motion.Holders == 0);
        Func<bool> restore = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            var screen = new Rectangle(0, 0, 400, 300);
            using var selector = Selector();
            using Bitmap frozen = SolidCardOnBlue(screen, new Rectangle(50, 40, 100, 60));
            selector.EnterMeasureForRender(frozen, screen, new Point(80, 60));
            selector.StartHintEntrance();
            Assert.Equal(1.0, selector.HintAmount, 3);
            Assert.Equal(0, Motion.Holders);

            Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
            selector.MoveMeasureCursorForRender(new Point(pill.Left + (pill.Width / 2), pill.Top + 2));
            Assert.True(selector.PillIsHotForRender);
            Assert.Equal(1.0, selector.PillHoverForRender, 3);
            Assert.Equal(0, Motion.Holders);
        }
        finally
        {
            Motion.AnimationsEnabled = restore;
        }
    }

    [Fact]
    public void SkimmingOnAndOffThePillManyTimesLeavesNoClockHeld()
    {
        PumpUntil(() => Motion.Holders == 0);

        var screen = new Rectangle(0, 0, 400, 300);
        using (var selector = Selector())
        {
            using Bitmap frozen = SolidCardOnBlue(screen, new Rectangle(50, 40, 100, 60));
            selector.EnterMeasureForRender(frozen, screen, new Point(80, 60));
            Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
            for (int i = 0; i < 30; i++)
            {
                selector.MoveMeasureCursorForRender(new Point(pill.Left + (pill.Width / 2), pill.Top + 2));
                selector.MoveMeasureCursorForRender(new Point(80, 60));
            }
        }

        PumpUntil(() => Motion.Holders == 0);
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

    [Fact]
    public void TheHoverOutlineOnThePillFadesInAndOutAndGivesTheClockBack()
    {
        PumpUntil(() => Motion.Holders == 0);

        var screen = new Rectangle(0, 0, 400, 300);
        var card = new Rectangle(50, 40, 100, 60);
        using var selector = Selector();
        using Bitmap frozen = SolidCardOnBlue(screen, card);
        selector.EnterMeasureForRender(frozen, screen, new Point(80, 60));
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);

        // Counted over the capsule's two widest points rather than sampled at named pixels:
        // the outline is a stroke on an anti-aliased rounded path, so which exact pixel
        // carries it is GDI+'s business. The bands sit at the pill's half-height and off its
        // centre column, which is where neither arm of the crimson reticle runs.
        int mid = pill.Top + (pill.Height / 2);
        // Two pixels either side of the boundary: GDI+ strokes the capsule's widest point
        // on the path itself, which is the pixel just past the rect's right edge.
        var leftEdge = new Rectangle(pill.Left - 2, mid - 1, 4, 3);
        var rightEdge = new Rectangle(pill.Right - 2, mid - 1, 4, 3);

        using (Bitmap cold = Paint(selector, screen))
        {
            Assert.Equal(0, CountReddish(cold, leftEdge));
            Assert.Equal(0, CountReddish(cold, rightEdge));
        }

        selector.MoveMeasureCursorForRender(new Point(pill.Left + (pill.Width / 2), pill.Top + 2));
        Assert.True(selector.PillIsHotForRender);
        PumpUntil(() => selector.PillHoverForRender >= 1.0);

        using (Bitmap hot = Paint(selector, screen))
        {
            Assert.True(CountReddish(hot, leftEdge) > 0, "left edge of the outline");
            Assert.True(CountReddish(hot, rightEdge) > 0, "right edge of the outline");
        }

        selector.MoveMeasureCursorForRender(new Point(300, 250));
        PumpUntil(() => selector.PillHoverForRender <= 0.0);
        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>Nothing else this mode paints on a pill is red at all — not the neutral
    /// ground, not the white text, and not the 8 % of the reticle that shows through.</summary>
    private static int CountReddish(Bitmap bmp, Rectangle where)
    {
        int found = 0;
        for (int y = where.Top; y < where.Bottom; y++)
        {
            for (int x = where.Left; x < where.Right; x++)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.R > c.G + 40 && c.R > c.B + 30)
                    found++;
            }
        }
        return found;
    }

    private static Bitmap SolidCardOnBlue(Rectangle screen, Rectangle card)
    {
        var bmp = new Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0x10, 0x30, 0x80));
        using var brush = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0));
        g.FillRectangle(brush, card);
        return bmp;
    }

    private static Bitmap Paint(FastRegionSelectorDialog selector, Rectangle screen)
    {
        var canvas = new Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        selector.PaintSurface(g, screen);
        return canvas;
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
