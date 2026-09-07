using System.Reflection;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Paints one selector surface into an offscreen bitmap and scans the pixels. The overlay has
/// no render harness of its own — it is a full-screen layered window, which is exactly what
/// must never appear during a test — but PaintSurface is the whole of what it draws, so this
/// gets the same evidence without a screen.
///
/// Everything here is measured, not eyeballed: the audit's landmine is that both wrong
/// versions of a highlight looked fine in a PNG and were obviously off to Isaiah.
/// </summary>
[Collection("MotionRefcount")]
public class SelectorRenderTests
{
    private const int Width = 400;
    private const int Height = 300;
    private static readonly SD.Rectangle Monitor = new(0, 0, Width, Height);
    private static readonly SD.Color Desktop = SD.Color.FromArgb(255, 128, 128, 128);

    private static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    /// <summary>A selector painting over a flat mid-grey "desktop" on one 400x300 monitor.</summary>
    private static FastRegionSelectorDialog Seed(SD.Bitmap frozen, SD.Point cursor)
    {
        var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        Set(selector, "_frozen", frozen);
        Set(selector, "_vs", Monitor);
        Set(selector, "_monitorBounds", Monitor);
        Set(selector, "_currentScreen", cursor);
        Set(selector, "_magnifierOn", false); // the loupe is its own tested surface
        return selector;
    }

    private static SD.Bitmap FlatDesktop()
    {
        var bmp = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(Desktop);
        return bmp;
    }

    private static SD.Bitmap Paint(FastRegionSelectorDialog selector)
    {
        var surface = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(surface);
        selector.PaintSurface(g, Monitor);
        return surface;
    }

    [Fact]
    public void TheDesktopIsDimmedEverywhereWhileNothingIsSelected()
    {
        using SD.Bitmap frozen = FlatDesktop();
        using var selector = Seed(frozen, new SD.Point(390, 290));
        using SD.Bitmap painted = Paint(selector);

        SD.Color sample = painted.GetPixel(40, 40);
        Assert.True(sample.R < Desktop.R - 30, $"the dim should darken the desktop, got {sample}");
        Assert.Equal(sample.R, sample.G);
        Assert.Equal(sample.G, sample.B);
    }

    [Fact]
    public void AHoveredWindowIsBrighterThanTheDimAndCarriesAnAccentWash()
    {
        using SD.Bitmap frozen = FlatDesktop();
        using var selector = Seed(frozen, new SD.Point(390, 290));
        Set(selector, "_mode", FastRegionSelectorDialog.SelectorMode.Window);
        Set(selector, "_hoverWindow", new WindowInfo(new IntPtr(1), "Notepad", new SD.Rectangle(60, 60, 200, 120)));
        Set(selector, "_hoverAppName", "Notepad");
        Set(selector, "_hoverWash", 1d);

        using SD.Bitmap painted = Paint(selector);

        SD.Color inside = painted.GetPixel(80, 80);
        SD.Color outside = painted.GetPixel(20, 20);

        // Undimmed: the target keeps its own brightness.
        Assert.True(inside.R > outside.R + 20, $"target {inside} should be brighter than the dim {outside}");
        // And it is washed toward the one accent, so a whole window never looks like a
        // hand-drawn marquee: blue clearly leads red, which it does not in the grey dim.
        Assert.True(inside.B > inside.R + 20, $"target {inside} should be washed toward the accent");
        Assert.Equal(outside.R, outside.B);
    }

    [Fact]
    public void TheAppNamePillIsDrawnOverTheTargetedWindow()
    {
        using SD.Bitmap frozen = FlatDesktop();
        using var selector = Seed(frozen, new SD.Point(390, 290));
        var window = new SD.Rectangle(60, 60, 200, 120);
        Set(selector, "_mode", FastRegionSelectorDialog.SelectorMode.Window);
        Set(selector, "_hoverWindow", new WindowInfo(new IntPtr(1), "Notepad", window));
        Set(selector, "_hoverAppName", "Notepad");
        Set(selector, "_hoverWash", 1d);

        using SD.Bitmap painted = Paint(selector);

        // The pill is near-black on a mid-grey washed ground; scan the window's centre row
        // for a run of dark pixels rather than trusting one sample.
        int centreY = window.Top + (window.Height / 2);
        int dark = 0;
        for (int x = window.Left; x < window.Right; x++)
        {
            if (painted.GetPixel(x, centreY).R < 60)
                dark++;
        }

        Assert.True(dark > 20, $"expected a name pill across the window centre, found {dark} dark px");
    }

    [Fact]
    public void APendingSelectionDrawsEightWhiteHandles()
    {
        using SD.Bitmap frozen = FlatDesktop();
        using var selector = Seed(frozen, new SD.Point(390, 290));
        var pending = new SD.Rectangle(100, 80, 160, 120);
        Set(selector, "_pendingScreen", pending);
        Set(selector, "_handleScale", 1d);

        using SD.Bitmap painted = Paint(selector);

        foreach (SD.Point handle in SelectionHandles.Points(pending))
        {
            SD.Color dot = painted.GetPixel(handle.X, handle.Y);
            Assert.True(dot.R > 220 && dot.G > 220 && dot.B > 220,
                $"handle at {handle} should be a white dot, got {dot}");
        }
    }

    [Fact]
    public void TheHandlesGrowFromNothingRatherThanPoppingOutAtFullSize()
    {
        var pending = new SD.Rectangle(100, 80, 160, 120);
        SD.Point corner = SelectionHandles.Points(pending)[0];

        int WhiteRunAt(double scale)
        {
            // A fresh snapshot per selector: disposing a selector frees the frozen bitmap
            // it was handed, exactly as it does when a real session ends.
            using var selector = Seed(FlatDesktop(), new SD.Point(390, 290));
            Set(selector, "_pendingScreen", pending);
            Set(selector, "_handleScale", scale);
            using SD.Bitmap painted = Paint(selector);
            int white = 0;
            for (int x = corner.X - 10; x <= corner.X + 10; x++)
            {
                SD.Color pixel = painted.GetPixel(x, corner.Y);
                if (pixel.R > 220 && pixel.G > 220 && pixel.B > 220)
                    white++;
            }
            return white;
        }

        Assert.True(WhiteRunAt(0.2d) < WhiteRunAt(1d),
            "a handle at the start of its pop-in should be smaller than a settled one");
    }

    /// <summary>
    /// The bar's three cells all come from one palette token through one StateFill. The pad
    /// cell used to mix its own #3A3A3C idle and #303032 pressed — greys that exist nowhere in
    /// ThemePalette — so one bar carried five neutral surface shades. Scanned rather than read,
    /// because the fills are composed at paint time from a 0..1 hover/press amount.
    /// </summary>
    [Fact]
    public void TheActionBarsPadAndCancelCellsShareOneRestingFill()
    {
        var pending = new SD.Rectangle(100, 60, 160, 100);
        var (_, pad, cancel, _) = ActionBarRects(pending);

        using var selector = Seed(FlatDesktop(), new SD.Point(390, 290));
        Set(selector, "_pendingScreen", pending);
        using SD.Bitmap painted = Paint(selector);

        SD.Color padFill = painted.GetPixel(pad.Left + (pad.Width / 2), pad.Top + 3);
        SD.Color cancelFill = painted.GetPixel(cancel.Left + (cancel.Width / 2), cancel.Top + 3);

        Assert.Equal(cancelFill.ToArgb(), padFill.ToArgb());
        Assert.Equal(ThemePalette.SurfaceAlt.ToArgb(), padFill.ToArgb());
    }

    /// <summary>
    /// Hovering the pad cell lifts it the way hovering Cancel lifts Cancel, and the lift is a
    /// composed state rather than a second hard-coded colour.
    /// </summary>
    [Fact]
    public void HoveringThePadCellLiftsItTheSameWayCancelLifts()
    {
        var pending = new SD.Rectangle(100, 60, 160, 100);
        var (_, pad, _, _) = ActionBarRects(pending);

        SD.Color FillAt(double hover)
        {
            using var selector = Seed(FlatDesktop(), new SD.Point(390, 290));
            Set(selector, "_pendingScreen", pending);
            ((double[])typeof(FastRegionSelectorDialog)
                .GetField("_barHover", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(selector)!)[2] = hover;
            using SD.Bitmap painted = Paint(selector);
            return painted.GetPixel(pad.Left + (pad.Width / 2), pad.Top + 3);
        }

        SD.Color rest = FillAt(0d);
        SD.Color hovered = FillAt(1d);
        Assert.True(hovered.R > rest.R, $"hover should lift the neutral fill: {rest} -> {hovered}");
        Assert.Equal(
            DarkControlPalette.StateFill(ThemePalette.SurfaceAlt, 1d, 0d).ToArgb(),
            hovered.ToArgb());
    }

    private static (SD.Rectangle bar, SD.Rectangle pad, SD.Rectangle cancel, SD.Rectangle done)
        ActionBarRects(SD.Rectangle pending) =>
        ((SD.Rectangle, SD.Rectangle, SD.Rectangle, SD.Rectangle))typeof(FastRegionSelectorDialog)
            .GetMethod("ActionBarRects", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [pending])!;

    [Fact]
    public void TheHintPillIsPaintedNearTheTopAndFadesWithItsOpacity()
    {
        int DarkPixelsAcrossTheHintBand(double opacity)
        {
            // Cursor parked far right so the full-bleed crosshair's own dark shadow pass
            // does not cross the strip being counted.
            using var selector = Seed(FlatDesktop(), new SD.Point(390, 250));
            Set(selector, "_hintOpacity", opacity);
            Set(selector, "_hintText", SelectorHint.Text(windowMode: false));
            using SD.Bitmap painted = Paint(selector);
            int dark = 0;
            for (int y = (Height / 8) - 4; y < (Height / 8) + 28; y++)
            {
                for (int x = 0; x < 350; x++)
                {
                    if (painted.GetPixel(x, y).R < 45)
                        dark++;
                }
            }
            return dark;
        }

        int none = DarkPixelsAcrossTheHintBand(0d);
        int half = DarkPixelsAcrossTheHintBand(0.5d);
        int full = DarkPixelsAcrossTheHintBand(1d);

        Assert.Equal(0, none);
        Assert.True(full > 200, $"the hint pill should cover the band, found {full} px");
        Assert.True(half < full, $"a half-faded pill should be lighter ({half}) than a full one ({full})");
    }

    /// <summary>
    /// While the element-retarget glide is in flight the shutter pill must be painted where a
    /// press will find it. The border rides the interpolation on purpose; the pill does not —
    /// ShutterPillScreenRect() always answers with the settled element, so a pill drawn off the
    /// interpolated rect was a button that visibly sat somewhere it could not be clicked.
    /// </summary>
    [Fact]
    public void TheShutterPillIsPaintedInsideItsHitRectMidGlide()
    {
        var from = new SD.Rectangle(20, 20, 60, 40);
        var to = new SD.Rectangle(180, 150, 120, 80);

        using var selector = Seed(FlatDesktop(), new SD.Point(240, 190));
        Set(selector, "_paneHover", true);
        Set(selector, "_hoverPane", to);
        var tween = (RectangleTween)typeof(FastRegionSelectorDialog)
            .GetField("_hoverTween", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(selector)!;
        tween.Retarget(from, to);
        Assert.True(tween.IsActive && tween.Current != to, "the glide should still be short of the target");

        SD.Rectangle hit = (SD.Rectangle)typeof(FastRegionSelectorDialog)
            .GetMethod("ShutterPillScreenRect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(selector, null)!;

        // Where the pill WOULD have gone when it was placed off the interpolated border —
        // the bug this test exists for. Well clear of the hit rect while the glide is young.
        SD.Rectangle strayed = SelectorChrome.SizePillRect(
            tween.Current, SelectorChrome.MeasureLabel($"{to.Width} × {to.Height}"), Monitor);
        Assert.False(hit.IntersectsWith(strayed), "the two candidate spots must be distinguishable");

        using SD.Bitmap painted = Paint(selector);

        // Count only the pill's own ground: near-black AND neutral, so the accent border (which
        // rides the glide on purpose) and the crosshair's shadow pass are not mistaken for it.
        int PillPixels(SD.Rectangle area)
        {
            int found = 0;
            for (int y = area.Top; y < area.Bottom; y++)
            {
                for (int x = area.Left; x < area.Right; x++)
                {
                    SD.Color px = painted.GetPixel(x, y);
                    if (px.R < 45 && Math.Abs(px.R - px.B) < 20)
                        found++;
                }
            }
            return found;
        }

        Assert.True(PillPixels(hit) > 200, $"the pill should fill its hit rect, found {PillPixels(hit)} px");
        Assert.Equal(0, PillPixels(strayed));

        tween.Stop();
    }

    /// <summary>
    /// The pill's TEXT has to fade with the pill. TextRenderer.DrawText is a GDI wrapper and
    /// GDI drops the alpha byte, so the old call painted opaque text on a ghost background for
    /// the whole tween and then snapped it away. The existing band test only counts the dark
    /// ground, which is why it stayed green through that.
    /// </summary>
    [Fact]
    public void TheHintPillsTextFadesWithThePillAndNotAfterIt()
    {
        int LightTextPixels(double opacity)
        {
            using var selector = Seed(FlatDesktop(), new SD.Point(390, 250));
            Set(selector, "_hintOpacity", opacity);
            Set(selector, "_hintText", SelectorHint.Text(windowMode: false));
            using SD.Bitmap painted = Paint(selector);
            int light = 0;
            for (int y = (Height / 8) - 4; y < (Height / 8) + 28; y++)
            {
                for (int x = 0; x < 350; x++)
                {
                    // Text strokes: much brighter than the pill ground AND than the dim.
                    if (painted.GetPixel(x, y).R > 150)
                        light++;
                }
            }
            return light;
        }

        int full = LightTextPixels(1d);
        int half = LightTextPixels(0.4d);

        Assert.True(full > 80, $"the hint should carry visible text, found {full} px");
        Assert.True(half < full * 3 / 4, $"a fading pill should fade its text too: {half} vs {full}");
    }

    /// <summary>
    /// A framed selection used to swallow every confirmation and every failure: the paint gate
    /// refused the pill whenever _pendingScreen was set, which is the ONLY state three of the
    /// four new actions run in — the "nothing to snap to" line was unreachable by construction.
    /// The hint was suppressed there too, and a framed selection is the only state the arrow
    /// nudge, [ and ], and A work in, so the six new gestures had nowhere on screen to be
    /// mentioned. Both now paint.
    /// </summary>
    [Fact]
    public void AMessageAndTheHintArePaintedOverAFramedSelection()
    {
        // A framed selection paints its own dark chrome (the Cancel/Done bar), so the pill is
        // measured as the DIFFERENCE from the same scene with no pill at all.
        int DarkPixels(double opacity, bool isMessage)
        {
            using var selector = Seed(FlatDesktop(), new SD.Point(390, 250));
            Set(selector, "_pendingScreen", new SD.Rectangle(40, 40, 120, 80));
            Set(selector, "_hintOpacity", opacity);
            Set(selector, "_hintText", "Copied average color #336699");
            Set(selector, "_hintIsMessage", isMessage);
            using SD.Bitmap painted = Paint(selector);
            int dark = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (painted.GetPixel(x, y).R < 45)
                        dark++;
                }
            }
            return dark;
        }

        int baseline = DarkPixels(0d, isMessage: true);
        int message = DarkPixels(1d, isMessage: true) - baseline;
        int hint = DarkPixels(1d, isMessage: false) - baseline;

        Assert.True(message > 200, $"the confirmation should be on screen, found {message} px");
        Assert.True(hint > 200, $"the framed-selection hint should be on screen, found {hint} px");
    }

    [Fact]
    public void TheCrosshairGuidesReachBothEdgesOfTheMonitor()
    {
        using SD.Bitmap frozen = FlatDesktop();
        using var selector = Seed(frozen, new SD.Point(200, 150));
        using SD.Bitmap painted = Paint(selector);

        // Full-bleed, with a gap at the cursor: bright on the cursor's column near both the
        // top and the bottom of the monitor, and nothing right at the cursor itself.
        Assert.True(painted.GetPixel(200, 4).R > 150, "the guide should reach the top edge");
        Assert.True(painted.GetPixel(200, Height - 5).R > 150, "the guide should reach the bottom edge");
        Assert.True(painted.GetPixel(200, 150).R < 150, "the guide should leave a gap at the cursor");
    }
}
