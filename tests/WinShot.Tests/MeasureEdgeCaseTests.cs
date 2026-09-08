using System.Drawing;
using System.Text.Json;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The awkward shapes Measure mode has to survive: monitors at negative origins, a desktop
/// with no edges on it at all, a panel too small for the chrome, snapshots in pixel formats
/// nobody planned for, and every gesture arriving before there is anything to measure.
/// </summary>
public class MeasureEdgeCaseTests
{
    private static readonly Rectangle Screen = new(0, 0, 400, 300);
    private static readonly Rectangle Card = new(50, 40, 100, 60);

    private static Bitmap Desktop(Rectangle screen, Rectangle card,
        System.Drawing.Imaging.PixelFormat format = System.Drawing.Imaging.PixelFormat.Format32bppArgb)
    {
        var bmp = new Bitmap(screen.Width, screen.Height, format);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0x10, 0x30, 0x80));
        using var brush = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0));
        g.FillRectangle(brush, new Rectangle(card.X - screen.X, card.Y - screen.Y, card.Width, card.Height));
        return bmp;
    }

    private static FastRegionSelectorDialog Selector() =>
        new(() => Task.FromResult(new List<WindowInfo>()), settings: null);

    private static Bitmap Paint(FastRegionSelectorDialog selector, Rectangle screen)
    {
        var canvas = new Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        selector.PaintSurface(g, screen);
        return canvas;
    }

    [Fact]
    public void MeasuringOnANegativeOriginMonitorStaysAbsoluteAndPaints()
    {
        // Isaiah's two monitors sit ABOVE the laptop, so their Y is negative. Every rect in
        // this mode is virtual-desktop absolute; a stray Math.Abs anywhere would show here.
        var screen = new Rectangle(-1920, -1080, 400, 300);
        var card = new Rectangle(screen.X + 50, screen.Y + 40, 100, 60);
        using var selector = Selector();
        using Bitmap frozen = Desktop(screen, card);
        selector.EnterMeasureForRender(frozen, screen, new Point(screen.X + 80, screen.Y + 60));

        Assert.Equal(card, selector.MeasuredScreenRect);
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
        Assert.True(screen.Contains(pill), $"pill {pill} escaped monitor {screen}");

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.H));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V | WF.Keys.Shift));
        using Bitmap render = Paint(selector, screen);
        Assert.NotNull(render);
    }

    [Fact]
    public void AUniformScreenMeasuresTheWholeMonitorAndStillPaints()
    {
        using var frozen = new Bitmap(400, 300, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frozen))
            g.Clear(Color.FromArgb(0x20, 0x20, 0x20));
        using var selector = Selector();
        selector.EnterMeasureForRender(frozen, Screen, new Point(200, 150));

        Assert.Equal(Screen, selector.MeasuredScreenRect);
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
        Assert.True(Screen.Contains(pill), $"pill {pill} escaped {Screen}");
        using Bitmap render = Paint(selector, Screen);
        Assert.NotNull(render);
    }

    [Fact]
    public void APanelTooNarrowForThreeLinesFallsBackToTheLineThatPointsAtTheMenu()
    {
        var screen = new Rectangle(0, 0, 160, 90);
        using var selector = Selector();
        using Bitmap frozen = Desktop(screen, new Rectangle(20, 20, 40, 20));
        selector.EnterMeasureForRender(frozen, screen, new Point(30, 30));

        string text = selector.HintStripTextFor(screen.Width);

        // The stub is measured like every wrapped line was: DrawLabel clamps the capsule's
        // LEFT edge only, so a stub too wide for the pane would start at x=0 and run off the
        // right instead of being shortened.
        Assert.Equal("Right-click for more", text);
        Assert.True(SelectorChrome.MeasureLabel(text, 1d).Width <= screen.Width,
            $"the fallback line does not fit {screen.Width} px");
        Rectangle pill = Assert.IsType<Rectangle>(selector.LivePillForRender);
        Assert.True(screen.Contains(pill), $"pill {pill} escaped {screen}");
        using Bitmap render = Paint(selector, screen);
        Assert.NotNull(render);
    }

    [Fact]
    public void AnOldSettingsFileWithoutTheMeasureKeysStillLoads()
    {
        const string old = "{ \"HotkeyAllInOne\": \"Ctrl+Shift+7\", \"ShowMagnifier\": false }";

        Settings settings = JsonSerializer.Deserialize<Settings>(old)!;

        Assert.Equal("Ctrl+Shift+9", settings.HotkeyMeasure);
        Assert.Equal(30, settings.MeasureTolerance);
    }

    [Fact]
    public void AStoredToleranceOutOfRangeStillMapsToAPreset()
    {
        Assert.Equal("60", WinShot.SettingsUi.SettingsWindow.NearestTolerancePreset(9000));
        Assert.Equal("15", WinShot.SettingsUi.SettingsWindow.NearestTolerancePreset(-500));
    }

    [Fact]
    public void A24BppSnapshotStillMeasures()
    {
        // LockBits is asked for the bitmap's own format where that is 32-bit and for 32bppArgb
        // otherwise; a 24 bpp snapshot goes down the conversion path.
        using Bitmap frozen = Desktop(Screen, Card, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var snap = EdgeSnap.Lock(frozen);

        Assert.Equal(Card, snap.DetectEdges(new Point(80, 60), EdgeSnap.DefaultTolerance));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    public void ABitmapWithNoRoomToWalkIsOneRunRatherThanACrash(int width, int height)
    {
        using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var snap = EdgeSnap.Lock(bmp);

        Assert.Equal(
            new Rectangle(0, 0, width, height),
            snap.DetectEdges(new Point(0, 0), EdgeSnap.DefaultTolerance));
    }

    [Fact]
    public void EveryGestureBeforeTheSnapshotAnswersInsteadOfThrowing()
    {
        using var selector = Selector();
        selector.EnterMeasureForRender(frozen: null, Screen, new Point(80, 60));

        foreach (WF.Keys key in new[] { WF.Keys.H, WF.Keys.V, WF.Keys.Back, WF.Keys.Enter })
            selector.HandleKeyDown(new WF.KeyEventArgs(key));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.C | WF.Keys.Control));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.C | WF.Keys.Control | WF.Keys.Shift));
        selector.HandleMouseWheel(new WF.MouseEventArgs(WF.MouseButtons.None, 0, 80, 60, 120));
        selector.MeasureMouseDownForRender(WF.MouseButtons.Left);

        Assert.Null(selector.MeasuredScreenRect);
        Assert.Equal(WF.DialogResult.None, selector.DialogResult);
        using Bitmap render = Paint(selector, Screen);
        Assert.NotNull(render);
    }

    [Fact]
    public void TheWheelWhileDraggingAGuideDoesNotAlsoDropTheDrag()
    {
        using var selector = Selector();
        using Bitmap frozen = Desktop(Screen, Card);
        selector.EnterMeasureForRender(frozen, Screen, new Point(80, 60));
        selector.MoveMeasureCursorForRender(new Point(200, 200));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.V | WF.Keys.Shift));
        selector.MeasureMouseDownForRender(WF.MouseButtons.Left);
        Assert.True(selector.IsDraggingGuideForRender);

        selector.HandleMouseWheel(new WF.MouseEventArgs(WF.MouseButtons.None, 0, 0, 0, 120));

        Assert.True(selector.IsDraggingGuideForRender, "the drag did not survive the wheel");
    }

    [Fact]
    public void DisposingWithTheRightClickMenuOpenDoesNotTakeTheProcessDown()
    {
        // WinForms' modal-menu filter is a process-global hook that keeps touching a
        // drop-down for several messages after it closes.
        var selector = Selector();
        using Bitmap frozen = Desktop(Screen, Card);
        selector.EnterMeasureForRender(frozen, Screen, new Point(80, 60));
        selector.ShowMeasureMenuForRender(new Point(80, 60));
        Assert.NotNull(selector.MeasureMenuForRender);

        selector.Dispose();
        WF.Application.DoEvents();
        WF.Application.DoEvents();
    }
}
