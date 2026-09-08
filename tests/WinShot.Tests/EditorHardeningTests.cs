using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WinShot.Core;
using WinShot.Editor;
using WinShot.Editor.Background;
using WinShot.Ocr;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Breaker-lens facts about the editor's pure helpers: what the geometry, the parsers and
/// the tween driver do when they are handed something the UI cannot currently produce.
/// Everything here is pure (no Application, no window), which is what lets it assert the
/// hardening rather than describe it — the NaN paths the first review round found are now
/// guarded at the helper, so these are assertions, not documentation.
///
/// The sibling file <c>EditorEdgeCaseTests</c> holds the ones that need a real editor.
/// </summary>
public class EditorHardeningTests
{
    private static void AssertFinite(Rect r, string what)
    {
        Assert.False(double.IsNaN(r.X) || double.IsNaN(r.Y) ||
                     double.IsNaN(r.Width) || double.IsNaN(r.Height), $"{what} produced NaN: {r}");
        Assert.False(double.IsInfinity(r.X) || double.IsInfinity(r.Y) ||
                     double.IsInfinity(r.Width) || double.IsInfinity(r.Height), $"{what} produced Inf: {r}");
    }

    // ---------------------------------------------------------------- scroll bars

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(-1e12, -1e12)]
    [InlineData(1e12, 1e12)]
    public void B1_ScrollBarsNeverProduceNaN(double tx, double ty)
    {
        var (h, v) = EditorScrollBars.Calculate(
            new Size(800, 600), new Size(4000, 6000), 2.0, new Vector(tx, ty));
        // A non-finite translate used to ride straight into the bar rect and land as
        // Canvas.SetLeft(bar, NaN). Nothing live produces one, but the guard is one line.
        if (h is Rect hr) AssertFinite(hr, "horizontal bar");
        if (v is Rect vr) AssertFinite(vr, "vertical bar");
    }

    [Fact]
    public void B1b_ScrollBarsWithNaNZoomAreAbsent()
    {
        var (h, v) = EditorScrollBars.Calculate(
            new Size(800, 600), new Size(4000, 6000), double.NaN, new Vector(0, 0));
        Assert.Null(h);
        Assert.Null(v);
    }

    // --------------------------------------------------------------------- zoom

    [Fact]
    public void B2_FitRectSurvivesDegenerateInput()
    {
        foreach (Rect content in new[]
                 {
                     new Rect(0, 0, 0, 0),
                     new Rect(double.NaN, double.NaN, 10, 10),
                     new Rect(-1e9, -1e9, 1e9, 1e9),
                     new Rect(0, 0, 1e-9, 1e-9),
                 })
        {
            ZoomView view = EditorZoom.FitRect(content, new Size(800, 600), 1, 0.05, 16);
            Assert.False(double.IsNaN(view.Zoom), $"zoom NaN for {content}");
            Assert.InRange(view.Zoom, 0.05, 16);
            // A NaN translate lands on the view transform and blanks the canvas.
            Assert.False(double.IsNaN(view.Translate.X) || double.IsNaN(view.Translate.Y),
                $"translate NaN for {content}");
        }
    }

    [Fact]
    public void B2b_FitRectWithZeroViewportKeepsCurrentZoom()
    {
        ZoomView view = EditorZoom.FitRect(new Rect(0, 0, 100, 100), new Size(0, 0), 3, 0.05, 16);
        Assert.Equal(3, view.Zoom, 6);
    }

    // ---------------------------------------------------------------- magnifier

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(1e12)]
    public void B3_MagnifierRegionStaysFinite(double scale)
    {
        Rect region = MagnifierLayout.SourceRegion(new Rect(10, 10, 80, 60), new Point(50, 40), scale);
        AssertFinite(region, "magnifier source region");
        Assert.True(region.Width > 0 && region.Height > 0);
    }

    [Fact]
    public void B3b_MagnifierFocusClampsNaN()
    {
        Point p = MagnifierLayout.ClampFocus(new Point(double.NaN, double.NaN), new Size(100, 100));
        // Math.Clamp returns NaN unchanged, so the method named Clamp used to not clamp the
        // one value that matters.
        Assert.Equal(new Point(0, 0), p);
        // Finite values are clamped correctly.
        Point q = MagnifierLayout.ClampFocus(new Point(-50, 900), new Size(100, 100));
        Assert.Equal(new Point(0, 100), q);
    }

    // ------------------------------------------------------------ drag constraints

    [Fact]
    public void B4_DragConstraintsStayFinite()
    {
        var anchor = new Point(10, 10);
        foreach (Point cur in new[]
                 {
                     new Point(10, 10),
                     new Point(1e12, -1e12),
                     new Point(10 + 1e-12, 10),
                 })
        {
            Point sq = DragConstraints.Square(anchor, cur);
            Point qb = DragConstraints.QuantizeBearing(anchor, cur);
            Assert.False(double.IsNaN(sq.X) || double.IsNaN(sq.Y), $"Square NaN at {cur}");
            Assert.False(double.IsNaN(qb.X) || double.IsNaN(qb.Y), $"QuantizeBearing NaN at {cur}");
        }
        Vector lockAxis = DragConstraints.LockAxis(new Vector(double.NaN, 3));
        Assert.False(double.IsNaN(lockAxis.Y), "LockAxis let a NaN through on the kept axis");
    }

    // ------------------------------------------------------------------ objectsnap

    [Fact]
    public void B5_SnapIgnoresNaNNeighbours()
    {
        Vector v = ObjectSnap.Adjust(
            new Rect(100, 100, 20, 20),
            new[] { new Rect(double.NaN, double.NaN, 20, 20), new Rect(103, 100, 20, 20) });
        Assert.False(double.IsNaN(v.X) || double.IsNaN(v.Y), $"snap returned NaN: {v}");
        Assert.Equal(3, v.X, 6);
    }

    // ----------------------------------------------------------------- grow layout

    [Fact]
    public void B6_GrowTargetWithMinimumBiggerThanTheScreen()
    {
        // 800x600 work area on a 100 % monitor, but the window declares a 980x520 floor.
        SD.Rectangle to = EditorGrowLayout.Target(
            new SD.Rectangle(-1920, -1080, 800, 600), new Size(1100, 700), 1.0, new Size(980, 520));
        Assert.True(to.Width <= 800, $"target wider than the work area: {to}");
        Assert.True(to.Height <= 600, $"target taller than the work area: {to}");
        Assert.True(to.X >= -1920 && to.Y >= -1080, $"target left its monitor: {to}");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-1.5)]
    public void B6b_GrowTargetSurvivesBadScale(double scale)
    {
        SD.Rectangle to = EditorGrowLayout.Target(
            new SD.Rectangle(0, 0, 1920, 1080), new Size(980, 520), scale, new Size(980, 520));
        Assert.True(to.Width > 0 && to.Height > 0, $"degenerate target: {to}");
    }

    [Fact]
    public void B6c_LerpNeverProducesAZeroSizedWindow()
    {
        var from = new SD.Rectangle(100, 100, 1, 1);
        var to = new SD.Rectangle(-1920, -1080, 900, 500);
        foreach (double t in new[] { 0, 0.001, 0.5, 0.999, 1, double.NaN })
        {
            SD.Rectangle r = EditorGrowLayout.Lerp(from, to, t);
            Assert.True(r.Width >= 1 && r.Height >= 1, $"lerp gave {r} at t={t}");
        }
    }

    // ------------------------------------------------------------------- redaction

    [Fact]
    public void B7_RedactionIsDeterministicForOneSeed()
    {
        using var src = new SD.Bitmap(64, 64, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(src))
        {
            g.Clear(SD.Color.White);
            g.FillRectangle(SD.Brushes.Black, 8, 8, 40, 12);
        }

        var region = new SD.Rectangle(4, 4, 48, 24);
        using SD.Bitmap? a = Redaction.Render(src, region, RedactStyle.Pixelate, 60, 1234);
        using SD.Bitmap? b = Redaction.Render(src, region, RedactStyle.Pixelate, 60, 1234);
        using SD.Bitmap? c = Redaction.Render(src, region, RedactStyle.Pixelate, 60, 9999);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(SamePixels(a!, b!), "same seed produced a different mosaic");
        Assert.False(SamePixels(a!, c!), "a different seed produced an identical mosaic");
    }

    [Fact]
    public void B7b_RedactionActuallyDestroysTheText()
    {
        using var src = new SD.Bitmap(64, 64, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(src))
        {
            g.Clear(SD.Color.White);
            g.FillRectangle(SD.Brushes.Black, 10, 10, 2, 12); // an "I"
        }
        var region = new SD.Rectangle(8, 8, 20, 16);
        using SD.Bitmap? mosaic = Redaction.Render(src, region, RedactStyle.Pixelate, 100, 7);
        Assert.NotNull(mosaic);
        // No column may still be pure black-on-white the way the glyph was.
        bool anyPureBlack = false;
        for (int x = 0; x < mosaic!.Width && !anyPureBlack; x++)
            for (int y = 0; y < mosaic.Height; y++)
                if (mosaic.GetPixel(x, y).R == 0 && mosaic.GetPixel(x, y).G == 0)
                { anyPureBlack = true; break; }
        Assert.False(anyPureBlack, "the glyph survived the strongest mosaic");
    }

    [Fact]
    public void B7c_RedactionStrengthIsClampedNotThrown()
    {
        Assert.Equal(Redaction.MinStrength, Redaction.ClampStrength(int.MinValue));
        Assert.Equal(Redaction.MaxStrength, Redaction.ClampStrength(int.MaxValue));
        Assert.True(Redaction.CellFor(int.MaxValue) > 0);
        Assert.True(Redaction.RadiusFor(int.MinValue) > 0);
    }

    private static bool SamePixels(SD.Bitmap a, SD.Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    // ---------------------------------------------------------------- stroke styles

    [Fact]
    public void B8_StrokeParsersDefaultOnJunk()
    {
        Assert.Equal(StrokeDash.Solid, StrokeStyles.ParseDash(null));
        Assert.Equal(StrokeDash.Solid, StrokeStyles.ParseDash("  "));
        Assert.Equal(StrokeDash.Solid, StrokeStyles.ParseDash("nonsense"));
        Assert.Equal(StrokeRender.Clean, StrokeStyles.ParseRender(null));
        Assert.Equal(StrokeRender.Clean, StrokeStyles.ParseRender("nonsense"));
        // FINDING (non-blocking): Enum.TryParse accepts a bare ORDINAL, so a project file
        // carrying "Dash":"7" yields an undefined StrokeDash. Benign — DashArray's default
        // arm returns null (solid) — and unreachable from anything WinShot writes, since
        // AnnotationData always stores the enum NAME.
        Assert.False(Enum.IsDefined(StrokeStyles.ParseDash("7")));
        Assert.Null(StrokeStyles.DashArray(StrokeStyles.ParseDash("7")));
        Assert.False(Enum.IsDefined(StrokeStyles.ParseRender("42")));
    }

    [Fact]
    public void B8b_SketchIsDeterministicAndFinite()
    {
        var pts = new[] { new Point(0, 0), new Point(100, 0), new Point(100, 60), new Point(0, 60) };
        var a = StrokeStyles.SketchPolygon(pts, 5);
        var b = StrokeStyles.SketchPolygon(pts, 5);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].X, b[i].X, 9);
            Assert.False(double.IsNaN(a[i].X) || double.IsNaN(a[i].Y), "sketch produced NaN");
        }
        // Degenerate: one point, and two identical points.
        Assert.NotNull(StrokeStyles.SketchPath(new[] { new Point(1, 1) }, 3, closed: false));
        Assert.NotNull(StrokeStyles.SketchSegment(new Point(2, 2), new Point(2, 2), 3));
    }

    // ---------------------------------------------------------------- colour picker

    [Fact]
    public void B9_HsvRoundTripsEveryHue()
    {
        for (int h = 0; h < 360; h += 7)
        {
            Color c = ColorPickerMath.FromHsv(h, 1, 1);
            var (hue, sat, val) = ColorPickerMath.ToHsv(c);
            Assert.Equal(1, sat, 2);
            Assert.Equal(1, val, 2);
            double diff = Math.Abs(((hue - h + 540) % 360) - 180);
            Assert.True(diff < 2, $"hue {h} came back as {hue}");
        }
        Assert.Equal(ColorPickerMath.FromHsv(0, 1, 1), ColorPickerMath.FromHsv(360, 1, 1));
        Assert.Equal(ColorPickerMath.FromHsv(0, 1, 1), ColorPickerMath.FromHsv(-360, 1, 1));
        Assert.Equal(ColorPickerMath.FromHsv(0, 1, 1), ColorPickerMath.FromHsv(double.NaN, 1, 1));
    }

    // ------------------------------------------------------------- background state

    [Fact]
    public void B10_ComposerStateSurvivesAnOldSettingsFile()
    {
        // An install from before the sweep has neither key; both read as "".
        Assert.Empty(BackgroundComposerState.ParseAll(null));
        Assert.Empty(BackgroundComposerState.ParseAll(""));
        Assert.Equal(BackgroundComposerState.Default, BackgroundComposerState.Parse(null));
        Assert.Equal(BackgroundComposerState.Default, BackgroundComposerState.Parse(""));
        Assert.Equal(BackgroundComposerState.Default, BackgroundComposerState.Parse("garbage;;;=="));

        // Round trip through the stored line format.
        var one = BackgroundComposerState.Default;
        string all = BackgroundComposerState.SerializeAll(new[] { ("My preset", one) });
        var back = BackgroundComposerState.ParseAll(all);
        Assert.Single(back);
        Assert.Equal("My preset", back[0].Name);
        Assert.Equal(one, back[0].State);
    }

    [Fact]
    public void B10b_ComposerStateIsCultureProof()
    {
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma
        try
        {
            var s = BackgroundComposerState.Default;
            string text = s.Serialize();
            Assert.Equal(s, BackgroundComposerState.Parse(text));
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void B11_SettingsLoadFromAnOldFileMissingEveryNewKey()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"winshot-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // A settings.json from before the sweep: no BackgroundPresets, no
            // BackgroundLastUsed, and the old single after-capture value.
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{"PostCaptureAction":"Edit","SmoothPencil":false,"AlwaysOnTop":true}""");
            var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(
                File.ReadAllText(Path.Combine(dir, "settings.json")))!;
            Assert.Equal("", s.BackgroundPresets);
            Assert.Equal("", s.BackgroundLastUsed);
            Assert.False(s.SmoothPencil);
            Assert.True(s.AlwaysOnTop);
            Assert.Empty(BackgroundComposerState.ParseAll(s.BackgroundPresets));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ----------------------------------------------------------------------- ocr

    [Fact]
    public void B12_WordBoxesRoundOutwardAndNeverInvert()
    {
        var image = new SD.Rectangle(0, 0, 100, 50);
        // A box whose scaled origin lands mid-pixel must not shrink INTO the glyph.
        SD.Rectangle box = OcrService.ScaleWordBox(10.6, 4.4, 9.9, 3.3, 2.0, image);
        Assert.True(box.Left <= 10.6 / 2.0, "left rounded inward");
        Assert.True(box.Right >= (10.6 + 9.9) / 2.0, "right rounded inward");
        Assert.True(box.Width >= 0 && box.Height >= 0);

        Assert.Equal(SD.Rectangle.Empty, OcrService.ScaleWordBox(1, 1, 1, 1, 0, image));
        Assert.Equal(SD.Rectangle.Empty, OcrService.ScaleWordBox(1, 1, 1, 1, double.NaN, image));
        // Entirely off the image clips to nothing rather than to a negative rect.
        SD.Rectangle off = OcrService.ScaleWordBox(500, 500, 10, 10, 1, image);
        Assert.True(off.Width == 0 || off.Height == 0, $"off-image box survived: {off}");
    }

    // ------------------------------------------------------------------ colour math

    [Fact]
    public void B13_TextOnAlwaysClearsTheClaimedFloor()
    {
        double worst = double.MaxValue;
        for (int r = 0; r < 256; r += 17)
            for (int g = 0; g < 256; g += 17)
                for (int b = 0; b < 256; b += 17)
                {
                    var bg = Color.FromRgb((byte)r, (byte)g, (byte)b);
                    worst = Math.Min(worst, ColorMath.ContrastRatio(ColorMath.TextOn(bg), bg));
                }
        Assert.True(worst >= 4.5, $"worst ink contrast was {worst:0.00}:1");
    }

    // ---------------------------------------------------- redaction repaint rate limit

    /// <summary>
    /// Repainting a redaction is linear in its AREA: measured on a 4K source, 400x120
    /// BlurSecure costs ~2.5 ms a frame and 2400x1400 costs 173-267 ms. A FIXED 30 ms floor
    /// therefore buys nothing on exactly the marks that need it — the paint has already
    /// overrun the floor by the time the next mouse-move arrives, so every frame repaints
    /// and the editor drops to ~4 fps for the whole drag. The gate charges the gesture the
    /// last paint's own cost instead.
    /// </summary>
    [Theory]
    // cheap mark: the 30 ms floor is what limits it
    [InlineData(10, 3, false)]
    [InlineData(30, 3, true)]
    // expensive mark: its own cost is what limits it
    [InlineData(30, 250, false)]
    [InlineData(120, 250, false)]
    [InlineData(250, 250, true)]
    public void RedactionRepaintIsRateLimitedByItsOwnCost(long sinceMs, long costMs, bool due)
    {
        Assert.Equal(due, EditorWindow.RedactRepaintDue(sinceMs, costMs));
    }

    /// <summary>A first repaint, with nothing measured yet, must not be blocked forever.</summary>
    [Fact]
    public void RedactionRepaintDueIgnoresANonsenseCost()
    {
        Assert.True(EditorWindow.RedactRepaintDue(30, -5));
        Assert.True(EditorWindow.RedactRepaintDue(long.MaxValue, 250));
    }

    /// <summary>
    /// With animations off, Run applies the end state and runs onDone INLINE — before it
    /// returns. A caller whose onDone nulls its own tween field then had a sentinel handle
    /// assigned back over that null, so the field read "a tween is running" forever after.
    /// Run answers null on that path, which is the only answer a field can hold truthfully.
    /// </summary>
    [Fact]
    public void RunAnswersNullWhenItFinishedBeforeItReturned()
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        try
        {
            Motion.AnimationsEnabled = () => false;
            IDisposable? handle = null;
            double last = 0;
            handle = Motion.Run(200, Motion.EaseOutCubic, p => last = p, () => handle = null);

            Assert.Equal(1d, last, 6);
            Assert.Null(handle);
            Assert.Equal(0, Motion.Holders);
        }
        finally { Motion.AnimationsEnabled = previous; }
    }

    // ----------------------------------- does Motion.Run tick under a WPF Dispatcher loop?

    /// <summary>
    /// The editor's grow tween is the first WPF-window animation driven by Motion.Run's
    /// WinForms timer, and the only test that covers it runs with animations OFF (the end
    /// frame applied inline). If WM_TIMER were not dispatched by the WPF message pump the
    /// editor would sit at the card's rect forever, and nothing would catch it.
    /// No Application is created here — a Dispatcher frame is the same Win32 pump.
    /// </summary>
    [Fact]
    public void B15_MotionTicksUnderAWpfDispatcherPump()
    {
        int frames = 0;
        bool done = false;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var frame = new System.Windows.Threading.DispatcherFrame();
                Motion.Run(120, Motion.EaseOutCubic,
                    _ => frames++,
                    () => { done = true; frame.Continue = false; });
                // Safety net so a dead timer fails the test instead of hanging it.
                var bail = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromSeconds(5), System.Windows.Threading.DispatcherPriority.Normal,
                    (_, _) => frame.Continue = false, dispatcher);
                bail.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                bail.Stop();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20));

        Assert.Null(failure);
        Assert.True(done, $"Motion.Run never finished under a WPF pump ({frames} frames seen)");
        Assert.True(frames > 2, $"only {frames} frames in 120 ms — the tween is not really animating");
        Assert.Equal(0, Motion.Holders);
    }
}
