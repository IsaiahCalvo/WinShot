using System.IO;
using System.Reflection;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The selector's own render harness: it drives a real selector through idle, window hover,
/// drag, pending selection and shutter hover at four monitor scales, paints each state into an
/// offscreen bitmap, and SCANS THE PIXELS. Nothing here is eyeballed — the sweep's standing
/// landmine is that two different wrong versions of a highlight both looked fine in a PNG and
/// were obviously off to Isaiah.
///
/// The PNGs are a debugging aid, not the verification: set
/// <c>WINSHOT_RENDER_SELECTOR=1</c> and <c>WINSHOT_RENDER_OUT=&lt;dir&gt;</c> and
/// <see cref="RenderSelectorStatesToPng"/> writes one file per state per scale plus the camera
/// cursor bitmaps. Every assertion in this file runs without them.
///
///   powershell -File .\tools\run-tests.ps1 -Filter "FullyQualifiedName~SelectorRenderHarness"
/// </summary>
[Collection("MotionRefcount")]
public class SelectorRenderHarness
{
    private const int Width = 520;
    private const int Height = 360;
    private static readonly SD.Rectangle Monitor = new(0, 0, Width, Height);

    /// <summary>The scales the app actually meets: 96, 120, 144 and 192 DPI.</summary>
    public static TheoryData<double> Scales => new() { 1.0d, 1.25d, 1.5d, 2.0d };

    // ---- camera cursor -------------------------------------------------------------

    /// <summary>
    /// The reported defect: "the right side of the camera icon is a little cut off". The glyph
    /// is drawn with DrawString at the content box's ORIGIN, and DrawString's overload takes no
    /// layout rectangle — so the box's width bounds nothing and the glyph runs as wide as the
    /// font makes it. Whether it fits is a fact about the font at that point size, which is
    /// exactly the kind of thing that must be measured rather than reasoned about.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public void TheCameraCursorGlyphFitsInsideItsBitmapAtEveryMonitorScale(double scale)
    {
        using SD.Bitmap bmp = SelectorCursors.BuildCameraBitmap(scale);
        SD.Rectangle ink = InkBounds(bmp);

        Assert.False(ink.IsEmpty, $"the camera glyph drew nothing at {scale:0.##}x");
        Assert.True(ink.Left >= 0 && ink.Top >= 0,
            $"glyph starts off the bitmap at {scale:0.##}x: ink {ink} in {bmp.Width}x{bmp.Height}");
        // Strictly inside, not merely within: ink touching the last column is ink that the
        // font would have carried further, i.e. a glyph the bitmap cut off.
        Assert.True(ink.Right < bmp.Width,
            $"glyph clipped on the RIGHT at {scale:0.##}x: ink {ink} reaches x={ink.Right - 1} " +
            $"of a {bmp.Width}px bitmap");
        Assert.True(ink.Bottom < bmp.Height,
            $"glyph clipped at the BOTTOM at {scale:0.##}x: ink {ink} reaches y={ink.Bottom - 1} " +
            $"of a {bmp.Height}px bitmap");
    }

    /// <summary>
    /// A cursor whose glyph hugs one edge and leaves a gutter on the other reads as lopsided
    /// even when nothing is technically clipped, and it is how the clipping crept in: the box
    /// is inset equally on all four sides but the glyph is only ANCHORED at its top-left, so
    /// the right and bottom margins are whatever the font leaves over.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public void TheCameraCursorGlyphIsCentredRatherThanAnchoredTopLeft(double scale)
    {
        using SD.Bitmap bmp = SelectorCursors.BuildCameraBitmap(scale);
        SD.Rectangle ink = InkBounds(bmp);

        int left = ink.Left;
        int right = bmp.Width - ink.Right;
        int top = ink.Top;
        int bottom = bmp.Height - ink.Bottom;
        // One physical pixel of slack per side: a glyph's ink box rarely lands on an exact
        // centre, and rounding the centring is allowed to cost a pixel.
        int slack = Math.Max(1, (int)Math.Round(scale));

        Assert.True(Math.Abs(left - right) <= slack,
            $"camera cursor is off-centre horizontally at {scale:0.##}x: {left}px left vs {right}px right " +
            $"(ink {ink} in {bmp.Width}x{bmp.Height})");
        Assert.True(Math.Abs(top - bottom) <= slack,
            $"camera cursor is off-centre vertically at {scale:0.##}x: {top}px top vs {bottom}px bottom " +
            $"(ink {ink} in {bmp.Width}x{bmp.Height})");
    }

    /// <summary>The tightest box containing any pixel the glyph actually put ink in. Alpha,
    /// not colour: the glyph is white over a black halo over a transparent ground, so alpha is
    /// the only channel that separates drawn from undrawn.</summary>
    internal static SD.Rectangle InkBounds(SD.Bitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                // Above the antialiasing floor: a stray A=3 pixel is not ink anyone can see,
                // and counting it would make every margin measurement noise.
                if (bmp.GetPixel(x, y).A <= 8)
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        return maxX < 0
            ? SD.Rectangle.Empty
            : SD.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>
    /// "That icon of the camera needs to be filled in" — the Segoe Fluent camera is a stroked
    /// OUTLINE glyph, so the cursor was a white wireframe with a transparent middle and the
    /// window under it showed straight through. The camera is drawn from a path now: a scan
    /// across the body has to be unbroken ink, and the ink inside the contour has to be the
    /// dark token rather than nothing at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public void TheCameraCursorBodyIsFilledWithTheDarkToken(double scale)
    {
        using SD.Bitmap bmp = SelectorCursors.BuildCameraBitmap(scale);
        SD.Rectangle ink = InkBounds(bmp);
        Assert.False(ink.IsEmpty, $"the camera drew nothing at {scale:0.##}x");

        // A row through the body, below the lens: every pixel between the contour's two edges
        // must be drawn. On the outline glyph this row was ink, gap, ink.
        int row = ink.Top + (int)Math.Round(ink.Height * 0.8);
        int first = -1, last = -1, holes = 0;
        for (int x = ink.Left; x < ink.Right; x++)
        {
            bool inkHere = bmp.GetPixel(x, row).A > 8;
            if (inkHere)
            {
                if (first < 0) first = x;
                last = x;
            }
        }
        Assert.True(first >= 0, $"no camera body on row {row} at {scale:0.##}x");
        for (int x = first; x <= last; x++)
            if (bmp.GetPixel(x, row).A <= 8)
                holes++;
        Assert.Equal(0, holes);

        // And what fills it is the palette token, not white and not a hand-picked grey.
        SD.Color token = ThemePalette.ToolbarBg;
        int filled = 0;
        for (int y = ink.Top; y < ink.Bottom; y++)
        {
            for (int x = ink.Left; x < ink.Right; x++)
            {
                SD.Color px = bmp.GetPixel(x, y);
                if (px.A == 255 && px.R == token.R && px.G == token.G && px.B == token.B)
                    filled++;
            }
        }
        Assert.True(filled > ink.Width * ink.Height / 5,
            $"only {filled} of {ink.Width * ink.Height} ink pixels are ToolbarBg at {scale:0.##}x — " +
            "the body is not filled with the token");
    }

    // ---- chrome that has to grow with the monitor ----------------------------------

    /// <summary>
    /// The crosshair's white guide is drawn with an aliased pen (the surface paints with
    /// SmoothingMode.None), and an aliased pen covers floor(width) rows — so the old
    /// weight = scale gave 1.5 -> ONE row, exactly what a 100% monitor gets. The growth was a
    /// no-op at 125% and 150% and only started working at 175%, while the shadow (3x) grew
    /// regardless: a 5 px black band around a 1 px white core on the reporter's own display.
    /// </summary>
    [Fact]
    public void TheCrosshairGuideGetsThickerOnAHigherDpiMonitor()
    {
        int at100 = GuideThickness(1.0d);
        int at150 = GuideThickness(1.5d);
        int at200 = GuideThickness(2.0d);

        Assert.Equal(1, at100);
        Assert.True(at150 > at100, $"the guide is {at150} px at 150% and {at100} px at 100% — it did not grow");
        Assert.True(at200 >= at150, $"the guide shrank from {at150} px at 150% to {at200} px at 200%");
    }

    /// <summary>
    /// Rows of near-white the HORIZONTAL guide actually covers. Drawn straight through
    /// SelectorChrome onto a black ground with SmoothingMode.None, which is how the selector
    /// surface paints it — an antialiased scan would blur the very quantisation being measured.
    /// The column is far to the left of the cursor so it misses the gap and the vertical arm.
    /// </summary>
    private static int GuideThickness(double scale)
    {
        var size = new SD.Size(Width, Height);
        var cursor = new SD.Point(Width / 2, Height / 2);
        using var bmp = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.Clear(SD.Color.Black);
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
            SelectorChrome.DrawCrosshair(g, size, cursor, scale);
        }

        int rows = 0;
        for (int y = 0; y < Height; y++)
        {
            // The guide is 210-alpha white over black; its shadow is black on black. Only the
            // stroke clears this floor.
            if (bmp.GetPixel(20, y).R > 150)
                rows++;
        }

        return rows;
    }

    /// <summary>
    /// The loupe was the one selector surface that took no scale at all: every constant in its
    /// renderer and its layout was consumed as raw device px, so a 120-logical box came out 120
    /// DEVICE px — 80 logical, two thirds of intended — on a 150% display, with 5.3-logical
    /// zoom cells, while its Point-font labels scaled with the primary monitor regardless.
    /// </summary>
    [Fact]
    public void TheLoupeBoxAndItsLabelPillGrowWithTheMonitor()
    {
        SD.Rectangle At(double scale) => FastSelectorLoupeRenderer.OccupiedRect(
            new SD.Size(Width * 3, Height * 3),
            new SD.Rectangle(0, 0, Width * 3, Height * 3),
            new SD.Point(200, 200),
            new SD.Point(200, 200),
            scale);

        SD.Rectangle at100 = At(1.0d);
        SD.Rectangle at150 = At(1.5d);
        SD.Rectangle at200 = At(2.0d);

        Assert.False(at100.IsEmpty, "the loupe should be showing at all");
        // The box is square and 120 logical px, so its width IS the scaling, exactly.
        Assert.Equal(120, at100.Width);
        Assert.Equal(180, at150.Width);
        Assert.Equal(240, at200.Width);
        // And the label pill under it grows too, rather than staying a fixed slab that the
        // box outgrows.
        int Pill(SD.Rectangle r) => r.Height - r.Width;
        Assert.True(Pill(at150) > Pill(at100),
            $"the label pill stayed {Pill(at100)} px tall while the loupe grew to {at150.Width}");
        Assert.True(Pill(at200) > Pill(at150),
            $"the label pill stayed {Pill(at150)} px tall while the loupe grew to {at200.Width}");
    }

    /// <summary>
    /// The gap between a dimension line and its pill goes through the scaler its siblings use.
    /// It was a bare constant, so at 150% the size pill stood 12 device px off its rect while
    /// the measurement pill stood 6 off its line — half the intended distance, and the pill's
    /// own hit corridor in Measure mode was 4 logical px tall.
    /// </summary>
    [Fact]
    public void TheDimensionPillGapScalesLikeEveryOtherSpacingToken()
    {
        Assert.Equal(6, SelectorChrome.DimensionPillGapPx(1.0d));
        Assert.Equal(9, SelectorChrome.DimensionPillGapPx(1.5d));
        Assert.Equal(12, SelectorChrome.DimensionPillGapPx(2.0d));
    }

    // ---- the loupe leaves nothing behind -------------------------------------------

    /// <summary>
    /// The reported defect: "it shows these weird artifacts … where that magnifier has been."
    ///
    /// The selector repaints only a dirty region as the pointer moves, and that region was a
    /// flat 250-px half-box around the cursor in LOGICAL px, never scaled. `1c9b778` then made
    /// the loupe grow with the monitor: 120 logical px is 180 device px at 150 % and 240 at
    /// 200 %, so from 125 % up the bottom of the box, its whole label pill and its shadow fell
    /// outside every repaint and the old loupe stayed on screen at each position the pointer
    /// had passed through.
    ///
    /// This drives a real pointer path through the real seams, repaints ONLY what the selector
    /// asked to be repainted — which is exactly what WinForms does with an invalidated region —
    /// and compares the result against a full repaint of the same final state. One differing
    /// pixel is one pixel of ghost.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public void TheLoupeLeavesNoPixelsBehindAsThePointerMoves(double scale)
    {
        var previousScale = WinShot.Recording.RecordingMonitorDpi.ScaleSource;
        WinShot.Recording.RecordingMonitorDpi.ScaleSource = _ => scale;
        try
        {
            // Roomy enough that the loupe is not clamped against an edge at 200 %, where it is
            // 240 px square with a 104 px pointer offset and a pill under it.
            var monitor = new SD.Rectangle(0, 0, 1000, 760);
            SD.Point[] path =
            [
                new(180, 140), new(430, 300), new(250, 480), new(620, 210), new(360, 560),
            ];

            using var pointer = new Pointer();
            using SD.Bitmap frozen = SyntheticDesktop(monitor.Width, monitor.Height);
            using var selector = Seed(frozen, monitor, path[0], magnifier: true);
            Set(selector, "_lastFollowScreen", path[0]);

            using var incremental = new SD.Bitmap(
                monitor.Width, monitor.Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(incremental))
                selector.PaintSurface(g, monitor);

            var log = new List<SD.Rectangle>();
            selector.InvalidationLog = log;
            foreach (SD.Point step in path.Skip(1))
            {
                log.Clear();
                pointer.MoveTo(selector, step.X, step.Y);

                using var g = SD.Graphics.FromImage(incremental);
                using var dirty = new System.Drawing.Region(SD.Rectangle.Empty);
                foreach (SD.Rectangle rect in log)
                    dirty.Union(rect);
                g.Clip = dirty;
                selector.PaintSurface(g, monitor);
            }
            selector.InvalidationLog = null;

            using var reference = new SD.Bitmap(
                monitor.Width, monitor.Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(reference))
                selector.PaintSurface(g, monitor);

            SD.Rectangle ghost = FirstDifference(incremental, reference, out int differing);
            Assert.True(
                differing == 0,
                $"{differing} stale pixels left behind at {scale:0.##}x, first at {ghost.Location} — " +
                "the follow invalidation does not cover what the loupe draws");
        }
        finally
        {
            WinShot.Recording.RecordingMonitorDpi.ScaleSource = previousScale;
        }
    }

    private static SD.Rectangle FirstDifference(SD.Bitmap a, SD.Bitmap b, out int differing)
    {
        differing = 0;
        var first = SD.Rectangle.Empty;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                if (a.GetPixel(x, y).ToArgb() == b.GetPixel(x, y).ToArgb())
                    continue;
                if (differing == 0)
                    first = new SD.Rectangle(x, y, 1, 1);
                differing++;
            }
        }
        return first;
    }

    // ---- selector states -----------------------------------------------------------

    /// <summary>
    /// Writes a PNG per state per scale for a human to look through afterwards. Gated so a
    /// normal run never touches the disk; the facts above are what actually guard the chrome.
    /// </summary>
    [Fact]
    public void RenderSelectorStatesToPng()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_SELECTOR") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-selector-render");
        Directory.CreateDirectory(outDir);

        foreach (double scale in new[] { 1.0d, 1.25d, 1.5d, 2.0d })
        {
            int dpi = (int)Math.Round(scale * 96);
            using (SD.Bitmap cursor = SelectorCursors.BuildCameraBitmap(scale))
                cursor.Save(Path.Combine(outDir, $"camera-cursor-{dpi}.png"), SD.Imaging.ImageFormat.Png);

            foreach (var (name, state) in States())
            {
                using SD.Bitmap painted = PaintState(state, scale);
                painted.Save(Path.Combine(outDir, $"selector-{name}-{dpi}.png"), SD.Imaging.ImageFormat.Png);
            }
        }
    }

    /// <summary>
    /// Every state the entry sweep cares about, driven through the real seams — the pointer
    /// goes through <c>CursorProvider</c> and <c>HandleMouse*</c>, never the real mouse, and
    /// modifiers through <c>ModifiersProvider</c>, never <c>Control.ModifierKeys</c>.
    /// </summary>
    private static IEnumerable<(string Name, Action<FastRegionSelectorDialog, Pointer> Drive)> States()
    {
        yield return ("idle", static (_, _) => { });

        yield return ("hover-window", static (selector, pointer) =>
        {
            Set(selector, "_mode", FastRegionSelectorDialog.SelectorMode.Window);
            Set(selector, "_hoverWindow", new WindowInfo(new IntPtr(1), "Notepad", new SD.Rectangle(90, 70, 260, 170)));
            Set(selector, "_hoverAppName", "Notepad");
            Set(selector, "_hoverWash", 1d);
            pointer.MoveTo(selector, 200, 150);
        });

        yield return ("dragging", static (selector, pointer) =>
        {
            pointer.MoveTo(selector, 110, 90);
            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, 340, 260);
        });

        yield return ("pending", static (selector, pointer) =>
        {
            Set(selector, "_pendingScreen", new SD.Rectangle(110, 90, 230, 170));
            Set(selector, "_handleScale", 1d);
            pointer.MoveTo(selector, 340, 260);
        });

        yield return ("hover-shutter", static (selector, pointer) =>
        {
            Set(selector, "_mode", FastRegionSelectorDialog.SelectorMode.Window);
            Set(selector, "_paneHover", true);
            Set(selector, "_hoverPane", new SD.Rectangle(90, 70, 260, 170));
            Set(selector, "_hoverWindow", new WindowInfo(new IntPtr(1), "Notepad", new SD.Rectangle(90, 70, 260, 170)));
            Set(selector, "_hoverAppName", "Notepad");
            Set(selector, "_hoverWash", 1d);
            Set(selector, "_pressedShutter", false);
            pointer.MoveTo(selector, 200, 150);
        });
    }

    private static SD.Bitmap PaintState(Action<FastRegionSelectorDialog, Pointer> drive, double scale)
    {
        var previousScale = WinShot.Recording.RecordingMonitorDpi.ScaleSource;
        WinShot.Recording.RecordingMonitorDpi.ScaleSource = _ => scale;
        try
        {
            using var pointer = new Pointer();
            using SD.Bitmap frozen = SyntheticDesktop();
            using var selector = Seed(frozen);
            drive(selector, pointer);

            var surface = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(surface);
            selector.PaintSurface(g, Monitor);
            return surface;
        }
        finally
        {
            WinShot.Recording.RecordingMonitorDpi.ScaleSource = previousScale;
        }
    }

    private static FastRegionSelectorDialog Seed(SD.Bitmap frozen) =>
        Seed(frozen, Monitor, new SD.Point(200, 150), magnifier: false);

    private static FastRegionSelectorDialog Seed(
        SD.Bitmap frozen, SD.Rectangle monitor, SD.Point cursor, bool magnifier)
    {
        var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        Set(selector, "_frozen", frozen);
        Set(selector, "_vs", monitor);
        Set(selector, "_monitorBounds", monitor);
        Set(selector, "_currentScreen", cursor);
        Set(selector, "_magnifierOn", magnifier); // the loupe is its own tested surface
        return selector;
    }

    /// <summary>
    /// A desktop-like ground rather than a flat fill: bands and blocks of different luminance,
    /// so a scan can tell "the chrome is legible over anything" from "the chrome happens to
    /// contrast with one grey". Deterministic — no randomness in a test that asserts on pixels.
    /// </summary>
    internal static SD.Bitmap SyntheticDesktop() => SyntheticDesktop(Width, Height);

    internal static SD.Bitmap SyntheticDesktop(int width, int height)
    {
        var bmp = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.FromArgb(255, 40, 44, 52));
        for (int i = 0; i < 6; i++)
        {
            int v = 24 + (i * 40);
            using var band = new SD.SolidBrush(SD.Color.FromArgb(255, v, v, Math.Min(255, v + 18)));
            g.FillRectangle(band, 0, i * (height / 6), width, height / 6);
        }

        // The two "windows" in the same proportions at any size — at 520x360 this is exactly
        // the 60,40,200,140 and 300,190,190,130 the harness has always drawn.
        int Wx(int v) => v * width / Width;
        int Hy(int v) => v * height / Height;
        using var pale = new SD.SolidBrush(SD.Color.FromArgb(255, 236, 236, 240));
        g.FillRectangle(pale, Wx(60), Hy(40), Wx(200), Hy(140));   // a light "window"
        using var dark = new SD.SolidBrush(SD.Color.FromArgb(255, 12, 12, 14));
        g.FillRectangle(dark, Wx(300), Hy(190), Wx(190), Hy(130)); // and a dark one
        return bmp;
    }

    private static WF.MouseEventArgs Left() => new(WF.MouseButtons.Left, 1, 0, 0, 0);

    private static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    /// <summary>Drives the selector's pointer without touching the real mouse.</summary>
    private sealed class Pointer : IDisposable
    {
        private SD.Point _at = new(200, 150);

        public Pointer()
        {
            FastRegionSelectorDialog.CursorProvider = () => _at;
            FastRegionSelectorDialog.ModifiersProvider = () => WF.Keys.None;
        }

        public void MoveTo(FastRegionSelectorDialog selector, int x, int y)
        {
            _at = new SD.Point(x, y);
            selector.HandleMouseMove();
        }

        public void Dispose()
        {
            FastRegionSelectorDialog.CursorProvider = null;
            FastRegionSelectorDialog.ModifiersProvider = null;
        }
    }
}
