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

    private static FastRegionSelectorDialog Seed(SD.Bitmap frozen)
    {
        var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        Set(selector, "_frozen", frozen);
        Set(selector, "_vs", Monitor);
        Set(selector, "_monitorBounds", Monitor);
        Set(selector, "_currentScreen", new SD.Point(200, 150));
        Set(selector, "_magnifierOn", false); // the loupe is its own tested surface
        return selector;
    }

    /// <summary>
    /// A desktop-like ground rather than a flat fill: bands and blocks of different luminance,
    /// so a scan can tell "the chrome is legible over anything" from "the chrome happens to
    /// contrast with one grey". Deterministic — no randomness in a test that asserts on pixels.
    /// </summary>
    internal static SD.Bitmap SyntheticDesktop()
    {
        var bmp = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.FromArgb(255, 40, 44, 52));
        for (int i = 0; i < 6; i++)
        {
            int v = 24 + (i * 40);
            using var band = new SD.SolidBrush(SD.Color.FromArgb(255, v, v, Math.Min(255, v + 18)));
            g.FillRectangle(band, 0, i * (Height / 6), Width, Height / 6);
        }

        using var pale = new SD.SolidBrush(SD.Color.FromArgb(255, 236, 236, 240));
        g.FillRectangle(pale, 60, 40, 200, 140);   // a light "window"
        using var dark = new SD.SolidBrush(SD.Color.FromArgb(255, 12, 12, 14));
        g.FillRectangle(dark, 300, 190, 190, 130); // and a dark one
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
