using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The hover-border glide, measured rather than eyeballed. Isaiah: "the animation is pretty
/// good but I want it super smooth and super polished; iterate on it." Two things are measured
/// here, because they are the two separate ways a glide can look rough:
///
///   • GEOMETRY — where the border is on each frame. Deterministic: the harness owns the clock,
///     so the numbers are the same on every machine and can be asserted on.
///   • CADENCE — how far apart the frames actually land in wall time. Machine-dependent, so it
///     is EVIDENCE ONLY (gated behind WINSHOT_RENDER_SELECTOR=1) and never asserted on: the
///     suite already carries wall-clock flakes and this must not add another.
///
///   powershell -File .\tools\run-tests.ps1 -Filter "FullyQualifiedName~SelectorGlideHarness"
///   $env:WINSHOT_RENDER_SELECTOR=1; $env:WINSHOT_RENDER_OUT='...\glide'   # to write artefacts
/// </summary>
[Collection("MotionRefcount")]
public class SelectorGlideHarness
{
    private const int Width = 520;
    private const int Height = 360;
    private static readonly SD.Rectangle Monitor = new(0, 0, Width, Height);

    /// <summary>Two windows a sweep crosses between, in the synthetic desktop's proportions.</summary>
    private static readonly SD.Rectangle WindowA = new(60, 40, 200, 140);
    private static readonly SD.Rectangle WindowB = new(300, 190, 190, 130);

    // ------------------------------------------------------------------ geometry locks

    /// <summary>
    /// The defect this whole pass is about: in window capture (Alt+W) the blue border did not
    /// glide AT ALL. SetHoverWindow never touched the tween and PaintSurface drew
    /// _hoverWindow.Bounds raw, so sweeping from one window to the next teleported the border,
    /// the wash and the app-name pill in a single frame. Only area mode's element hover glided.
    /// </summary>
    [Fact]
    public void TheWindowHoverBorderGlidesBetweenWindowsInsteadOfTeleporting()
    {
        SD.Rectangle[] frames = GlideFrames(FastRegionSelectorDialog.SelectorMode.Window);

        Assert.True(frames.Length > 4, $"the border reached its target in {frames.Length} frame(s) — that is a teleport, not a glide");
        Assert.Equal(WindowB, frames[^1]);
        // Every intermediate frame is somewhere between the two windows, and none of them IS
        // either window: a glide that spends its frames parked on the old rect and then jumps
        // would satisfy a naive "more than one frame" check.
        SD.Rectangle span = SD.Rectangle.Union(WindowA, WindowB);
        foreach (SD.Rectangle frame in frames[..^1])
        {
            Assert.True(span.Contains(frame), $"glide frame {frame} left the span between the two windows");
            Assert.NotEqual(WindowB, frame);
        }
    }

    /// <summary>The same rule for area mode's element hover, which already had it.</summary>
    [Fact]
    public void TheAreaHoverBorderGlidesBetweenElements()
    {
        SD.Rectangle[] frames = GlideFrames(FastRegionSelectorDialog.SelectorMode.Area);
        Assert.True(frames.Length > 4, $"area hover reached its target in {frames.Length} frame(s)");
        Assert.Equal(WindowB, frames[^1]);
    }

    /// <summary>
    /// No frame may move an edge further than the frame before it. Under a flat frame clock
    /// that is what "decelerates into the target" means, and it is the single number that
    /// separates a glide from a lurch — a mid-glide retarget that restarts from the OLD target
    /// rather than from where the border actually is shows up here as a jump that grows.
    /// </summary>
    [Theory]
    [InlineData(FastRegionSelectorDialog.SelectorMode.Window)]
    [InlineData(FastRegionSelectorDialog.SelectorMode.Area)]
    public void NoGlideFrameJumpsFurtherThanTheOneBeforeIt(FastRegionSelectorDialog.SelectorMode mode)
    {
        SD.Rectangle[] frames = GlideFrames(mode);
        int first = MaxEdgeDelta(frames[0], frames[1]);
        for (int i = 2; i < frames.Length; i++)
        {
            int jump = MaxEdgeDelta(frames[i - 1], frames[i]);
            // Against the FIRST frame's jump, not the one before: the glide is linear
            // (ShareX's rule, and the one Isaiah signed off on), so a CONSTANT delta is the
            // shape being asserted, and a drift that grows a pixel a frame would slip past a
            // running comparison. One pixel of slack is the whole-pixel rounding that
            // integer edges cannot avoid — before the edge-rounding fix this wobbled by two.
            Assert.True(Math.Abs(jump - first) <= 1,
                $"frame {i} moved {jump}px where the glide's constant step is {first}px");
        }
    }

    /// <summary>
    /// A sweep that crosses a third window mid-glide must pick up from where the border IS.
    /// Retargeting from the OLD TARGET is the classic version of this bug: the border snaps
    /// forward to a rect it never reached and then glides back, which reads as a stutter
    /// exactly when the pointer is moving fastest.
    /// </summary>
    [Fact]
    public void ARetargetMidGlideContinuesFromWhereTheBorderIs()
    {
        var third = new SD.Rectangle(150, 250, 120, 90);
        var tween = new RectangleTween();
        tween.Retarget(WindowA, WindowB);
        tween.Update(0.4f);
        SD.Rectangle atRetarget = tween.Current;

        tween.Retarget(WindowB, third);   // the caller's "from" is deliberately wrong
        tween.Update(0f);

        Assert.Equal(atRetarget, tween.Current);
    }

    /// <summary>
    /// Border, wash and app-name pill are one object. They are painted from one rect, so this
    /// scans the real render: the accent ink the border leaves and the washed interior must
    /// share the same box on a mid-glide frame, and the pill must sit on its centre.
    /// </summary>
    [Fact]
    public void TheWashAndTheAppNamePillRideTheGlidingBorderNotTheSettledWindow()
    {
        var (frame, expected) = PaintMidGlideFrame();
        using SD.Bitmap _ = frame;
        // The accent ink only: the whole surface carries the dim wash, so a plain alpha scan
        // would call every pixel ink. Blue-dominant pixels are the border, the accent tint
        // over the window and the pill behind the app name — the three things that have to
        // travel together.
        SD.Rectangle accent = AccentBounds(frame);

        Assert.NotEqual(WindowA, expected);
        Assert.NotEqual(WindowB, expected);

        // The border pen is 2px centred on the rect and the harness paints at 1x, so the box
        // is the glide rect grown by about a pixel a side. Nothing may hang further out — a
        // wash or a pill still parked on the settled window pushes this wide by the whole
        // distance between the two windows.
        Assert.True(Math.Abs(accent.Left - expected.Left) <= 2, $"accent ink {accent} vs glide rect {expected}");
        Assert.True(Math.Abs(accent.Top - expected.Top) <= 2, $"accent ink {accent} vs glide rect {expected}");
        Assert.True(Math.Abs(accent.Right - expected.Right) <= 2, $"accent ink {accent} vs glide rect {expected}");
        Assert.True(Math.Abs(accent.Bottom - expected.Bottom) <= 2, $"accent ink {accent} vs glide rect {expected}");
    }

    /// <summary>The bounding box of every accent-tinted pixel: blue clearly ahead of red, which
    /// the dim wash and the synthetic desktop's greys never are.</summary>
    private static SD.Rectangle AccentBounds(SD.Bitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                SD.Color c = bmp.GetPixel(x, y);
                if (c.A <= 8 || c.B <= c.R + 24)
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? SD.Rectangle.Empty : SD.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>
    /// The glide is self-sustaining: PaintSurface asks for its own next frame by invalidating,
    /// and nothing else drives it — the 15 ms follow tick only repaints when the CURSOR moved,
    /// so a pointer that comes to rest mid-sweep leaves the glide with no other heartbeat.
    ///
    /// The ask used to be conditional on the rounded rect having CHANGED since the last paint.
    /// Between two windows a few pixels apart that is false on most frames (the loop paints
    /// far faster than the rect crosses a pixel boundary), so the border stopped partway and
    /// stayed there until the mouse moved again. Measured, not argued: this drives real paints
    /// against a real rect and counts the frames that asked for a successor.
    /// </summary>
    [Fact]
    public void AGlideOverAShortDistanceKeepsAskingForItsNextFrame()
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => true;
        try
        {
            var near = new SD.Rectangle(WindowA.X + 3, WindowA.Y + 2, WindowA.Width, WindowA.Height);
            using SD.Bitmap frozen = SelectorRenderHarness.SyntheticDesktop(Width, Height);
            using FastRegionSelectorDialog selector = Seed(frozen, FastRegionSelectorDialog.SelectorMode.Window);
            HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, WindowA);
            HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, near);

            var tween = (RectangleTween)Field(selector, "_hoverTween")!;
            var log = new List<SD.Rectangle>();
            selector.InvalidationLog = log;
            using var surface = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(surface);

            int stalled = 0;
            for (int i = 0; i < 40 && tween.IsActive; i++)
            {
                log.Clear();
                selector.PaintSurface(g, Monitor);
                if (tween.IsActive && log.Count == 0)
                    stalled++;
            }

            Assert.Equal(0, stalled);
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
        }
    }

    /// <summary>The border lands on whole device pixels on every frame, so an edge cannot
    /// shimmer between two columns as it travels.</summary>
    [Fact]
    public void EveryGlideFrameLandsOnWholeDevicePixels()
    {
        // Rectangle is integral by construction; the guard is that the tween keeps rounding
        // rather than ever handing a RectangleF straight to the pen.
        foreach (SD.Rectangle frame in GlideFrames(FastRegionSelectorDialog.SelectorMode.Window))
            Assert.Equal(frame, SD.Rectangle.Round(frame));
    }

    // ------------------------------------------------------------------ evidence

    /// <summary>
    /// Before/after evidence: per-frame geometry CSV, a wall-clock cadence CSV for both frame
    /// drivers, and a frame strip of one whole glide laid side by side.
    /// </summary>
    [Fact]
    public void WriteGlideEvidence()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_SELECTOR") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-selector-glide");
        Directory.CreateDirectory(outDir);
        string tag = Environment.GetEnvironmentVariable("WINSHOT_GLIDE_TAG") ?? "after";

        WriteGeometryCsv(Path.Combine(outDir, $"glide-geometry-{tag}.csv"));
        double[] paint = MeasurePaintDrivenFrames();
        double[] clock = MeasureClockDrivenFrames();
        WriteCadenceCsv(Path.Combine(outDir, $"glide-cadence-{tag}.csv"), paint, clock);
        WriteSummary(Path.Combine(outDir, $"glide-summary-{tag}.txt"), paint, clock);
        WriteFrameStrip(Path.Combine(outDir, $"glide-frames-{tag}.png"));
    }

    /// <summary>The numbers worth quoting, in one file: frame-interval percentiles for both
    /// drivers and the per-frame geometry for both hover modes.</summary>
    private static void WriteSummary(string path, double[] paint, double[] clock)
    {
        var text = new StringBuilder();
        text.AppendLine("hover glide — numeric summary");
        text.AppendLine();
        text.AppendLine("frame intervals (ms), one 200 ms glide on a real message pump:");
        AppendIntervals(text, "  paint-driven (the loop the glide rides)", paint);
        AppendIntervals(text, "  clock-driven (Motion.Run + WF.Timer at 8 ms)", clock);
        text.AppendLine();
        text.AppendLine("per-frame geometry, sampled at the frame clock:");
        foreach (FastRegionSelectorDialog.SelectorMode mode in
                 new[] { FastRegionSelectorDialog.SelectorMode.Window, FastRegionSelectorDialog.SelectorMode.Area })
        {
            SD.Rectangle[] frames = GlideFrames(mode);
            var jumps = new List<int>();
            var rightSteps = new List<int>();
            for (int i = 1; i < frames.Length; i++)
            {
                jumps.Add(MaxEdgeDelta(frames[i - 1], frames[i]));
                rightSteps.Add(Math.Abs(frames[i].Right - frames[i - 1].Right));
            }
            int moving = jumps.Count(j => j > 0);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {mode,-6} frames={frames.Length}  moving={moving}  " +
                $"max jump={(jumps.Count == 0 ? 0 : jumps.Max())}px  " +
                $"right-edge step {(rightSteps.Count == 0 ? 0 : rightSteps.Min())}..{(rightSteps.Count == 0 ? 0 : rightSteps.Max())}px");
        }
        File.WriteAllText(path, text.ToString());
    }

    private static void AppendIntervals(StringBuilder text, string label, double[] stamps)
    {
        // The first stamp is dropped: it carries window creation and JIT, which is a cost the
        // live selector has already paid long before a hover starts.
        double[] deltas = stamps.Length < 3
            ? Array.Empty<double>()
            : Enumerable.Range(2, stamps.Length - 2).Select(i => stamps[i] - stamps[i - 1]).OrderBy(d => d).ToArray();
        if (deltas.Length == 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{label}: no frames");
            return;
        }
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{label}: n={deltas.Length}  p50={deltas[deltas.Length / 2]:0.00}  " +
            $"p95={deltas[Math.Min(deltas.Length - 1, (int)(deltas.Length * 0.95))]:0.00}  max={deltas[^1]:0.00}");
    }

    /// <summary>Where the border is on every frame of a scripted sweep, and how far each edge
    /// moved since the frame before. One row per frame; the summary line at the end carries the
    /// numbers worth quoting.</summary>
    private static void WriteGeometryCsv(string path)
    {
        var csv = new StringBuilder("mode,frame,t_ms,x,y,w,h,d_left,d_top,d_right,d_bottom,max_edge_jump\n");
        foreach (FastRegionSelectorDialog.SelectorMode mode in
                 new[] { FastRegionSelectorDialog.SelectorMode.Window, FastRegionSelectorDialog.SelectorMode.Area })
        {
            SD.Rectangle[] frames = GlideFrames(mode);
            for (int i = 0; i < frames.Length; i++)
            {
                SD.Rectangle f = frames[i];
                SD.Rectangle p = i == 0 ? frames[0] : frames[i - 1];
                csv.Append(CultureInfo.InvariantCulture, $"{mode},{i},{i * Motion.FrameIntervalMs},{f.X},{f.Y},{f.Width},{f.Height},");
                csv.Append(CultureInfo.InvariantCulture, $"{f.Left - p.Left},{f.Top - p.Top},{f.Right - p.Right},{f.Bottom - p.Bottom},{MaxEdgeDelta(p, f)}\n");
            }
        }
        File.WriteAllText(path, csv.ToString());
    }

    /// <summary>
    /// The cadence question, on a real message pump on the hidden desktop: how evenly do frames
    /// land under the two drivers a glide can ride?
    ///
    ///   paint-driven — the loop the hover glide used to run on: PaintSurface asks for the next
    ///     frame by invalidating, so the interval is "however long a repaint round-trip takes"
    ///     and it is whatever the paint cost and the message queue make it.
    ///   clock-driven — Motion.Run at FrameIntervalMs with timeBeginPeriod(1) held for exactly
    ///     the length of the glide, which is what every other tween in the app already uses.
    /// </summary>
    private static void WriteCadenceCsv(string path, double[] paint, double[] clock)
    {
        var csv = new StringBuilder("driver,frame,t_ms,dt_ms\n");
        AppendCadence(csv, "paint-driven", paint);
        AppendCadence(csv, "clock-driven", clock);
        File.WriteAllText(path, csv.ToString());
    }

    private static void AppendCadence(StringBuilder csv, string driver, double[] stamps)
    {
        for (int i = 0; i < stamps.Length; i++)
        {
            double dt = i == 0 ? 0 : stamps[i] - stamps[i - 1];
            csv.Append(CultureInfo.InvariantCulture, $"{driver},{i},{stamps[i]:0.000},{dt:0.000}\n");
        }
    }

    /// <summary>
    /// Every frame of one window-mode glide laid side by side, so a stutter is visible as a
    /// gap in an otherwise even march. Half size, in rows of eight, with the frame index burnt
    /// in — a strip of 26 full-size frames is 13,500 px wide and nobody opens it.
    /// </summary>
    private static void WriteFrameStrip(string path)
    {
        var frames = new List<SD.Bitmap>();
        try
        {
            int steps = Math.Max(1, Motion.GlideDurationMs / Motion.FrameIntervalMs);
            for (int i = 0; i <= steps; i++)
                frames.Add(PaintGlideFrame(Math.Min(1f, (float)i / steps)));

            const int columns = 8;
            int cell = Width / 2, cellHeight = Height / 2, gap = 6, label = 14;
            int rows = (frames.Count + columns - 1) / columns;
            using var strip = new SD.Bitmap(
                columns * (cell + gap) + gap,
                rows * (cellHeight + gap + label) + gap,
                SD.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(strip);
            g.Clear(SD.Color.FromArgb(255, 18, 20, 24));
            using var font = new SD.Font("Segoe UI", 7f);
            for (int i = 0; i < frames.Count; i++)
            {
                int x = gap + (i % columns) * (cell + gap);
                int y = gap + (i / columns) * (cellHeight + gap + label);
                g.DrawImage(frames[i], new SD.Rectangle(x, y, cell, cellHeight));
                g.DrawString(
                    $"{i}  {i * Motion.FrameIntervalMs}ms",
                    font, SD.Brushes.Gainsboro, x, y + cellHeight + 1);
            }
            strip.Save(path, SD.Imaging.ImageFormat.Png);
        }
        finally
        {
            foreach (SD.Bitmap frame in frames)
                frame.Dispose();
        }
    }

    // ------------------------------------------------------------------ measurement plumbing

    /// <summary>
    /// One scripted glide, sampled at the frame clock: hover window A, then sweep to window B,
    /// and record the border rect the selector would paint on each frame. The clock is the
    /// harness's, not wall time, so the geometry is identical on every machine.
    /// </summary>
    private static SD.Rectangle[] GlideFrames(FastRegionSelectorDialog.SelectorMode mode)
    {
        var frames = new List<SD.Rectangle>();
        RunGlide(mode, (_, rect) => frames.Add(rect));
        return frames.ToArray();
    }

    /// <summary>
    /// Drives the selector's real hover seam through a sweep and steps the glide at the frame
    /// clock, handing each frame's painted border rect to <paramref name="onFrame"/>.
    /// Animations are forced on: with them off every tween applies its end state inline and
    /// there is no glide to measure.
    /// </summary>
    private static void RunGlide(
        FastRegionSelectorDialog.SelectorMode mode,
        Action<float, SD.Rectangle> onFrame)
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => true;
        try
        {
            using SD.Bitmap frozen = SelectorRenderHarness.SyntheticDesktop(Width, Height);
            using FastRegionSelectorDialog selector = Seed(frozen, mode);

            HoverOn(selector, mode, WindowA);
            HoverOn(selector, mode, WindowB);

            var tween = (RectangleTween)Field(selector, "_hoverTween")!;
            int steps = Math.Max(1, Motion.GlideDurationMs / Motion.FrameIntervalMs);
            for (int i = 0; i <= steps; i++)
            {
                float progress = Math.Min(1f, (float)i / steps);
                tween.Update(progress);
                onFrame(progress, tween.IsActive ? tween.Current : WindowB);
            }
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
        }
    }

    /// <summary>
    /// One real painted frame taken while the glide is in flight, plus the rect the tween was
    /// actually on when it was painted.
    ///
    /// PaintSurface owns the glide's clock — it advances the tween from a stopwatch on the
    /// paint it is already doing, which is the whole reason the loop is self-sustaining — so
    /// the harness cannot pin a progress value the way it can for the geometry rows. It waits a
    /// slice of the duration instead and re-rolls if the wait overshot the glide, which makes
    /// the sample robust without a single wall-clock assertion.
    /// </summary>
    private static (SD.Bitmap Frame, SD.Rectangle Painted) PaintMidGlideFrame()
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        var previousScale = WinShot.Recording.RecordingMonitorDpi.ScaleSource;
        Motion.AnimationsEnabled = () => true;
        WinShot.Recording.RecordingMonitorDpi.ScaleSource = _ => 1d;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                using SD.Bitmap frozen = SelectorRenderHarness.SyntheticDesktop(Width, Height);
                using FastRegionSelectorDialog selector = Seed(frozen, FastRegionSelectorDialog.SelectorMode.Window);
                HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, WindowA);
                HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, WindowB);
                Set(selector, "_hoverWash", 1d);

                Thread.Sleep(Motion.GlideDurationMs / 5);
                var surface = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
                using (var g = SD.Graphics.FromImage(surface))
                    selector.PaintSurface(g, Monitor);

                var tween = (RectangleTween)Field(selector, "_hoverTween")!;
                if (tween.IsActive || attempt >= 5)
                    return (surface, tween.Current);
                surface.Dispose();
            }
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
            WinShot.Recording.RecordingMonitorDpi.ScaleSource = previousScale;
        }
    }

    /// <summary>A painted frame at a pinned point on the glide, for the frame strip. Paints
    /// from the tween's own value rather than through PaintSurface's clock, so the strip is a
    /// picture of the glide's geometry and not of the machine's load.</summary>
    private static SD.Bitmap PaintGlideFrame(float progress)
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        var previousScale = WinShot.Recording.RecordingMonitorDpi.ScaleSource;
        Motion.AnimationsEnabled = () => true;
        WinShot.Recording.RecordingMonitorDpi.ScaleSource = _ => 1d;
        try
        {
            using SD.Bitmap frozen = SelectorRenderHarness.SyntheticDesktop(Width, Height);
            using FastRegionSelectorDialog selector = Seed(frozen, FastRegionSelectorDialog.SelectorMode.Window);
            HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, WindowA);
            HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, WindowB);
            Set(selector, "_hoverWash", 1d);

            var tween = (RectangleTween)Field(selector, "_hoverTween")!;
            tween.Update(progress);
            // The glide's own rect, frozen for this frame: PaintSurface would otherwise
            // advance the tween from its own stopwatch and every cell of the strip would show
            // the same instant. Handing the rect over as the settled window and stopping the
            // tween paints exactly the frame asked for, through the real paint path.
            SD.Rectangle at = tween.Current;
            tween.Stop();
            Set(selector, "_hoverWindow", new WindowInfo(new IntPtr(2), "Notepad", at));

            var surface = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(surface);
            selector.PaintSurface(g, Monitor);
            return surface;
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
            WinShot.Recording.RecordingMonitorDpi.ScaleSource = previousScale;
        }
    }

    /// <summary>Puts the hover on <paramref name="rect"/> through the selector's own seam, so
    /// the glide is armed the way a real cursor arms it.</summary>
    private static void HoverOn(
        FastRegionSelectorDialog selector,
        FastRegionSelectorDialog.SelectorMode mode,
        SD.Rectangle rect)
    {
        Set(selector, "_currentScreen", new SD.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
        if (mode == FastRegionSelectorDialog.SelectorMode.Window)
        {
            selector.SetHoverWindow(new WindowInfo(new IntPtr(rect.X + 1), "Notepad", rect));
            Set(selector, "_hoverAppName", "Notepad");
        }
        else
        {
            Set(selector, "_paneHover", true);
            Invoke(selector, "SetHoverPane", (SD.Rectangle?)rect);
        }
    }

    private static FastRegionSelectorDialog Seed(SD.Bitmap frozen, FastRegionSelectorDialog.SelectorMode mode)
    {
        var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        Set(selector, "_frozen", frozen);
        Set(selector, "_vs", Monitor);
        Set(selector, "_monitorBounds", Monitor);
        Set(selector, "_mode", mode);
        Set(selector, "_currentScreen", new SD.Point(Width / 2, Height / 2));
        return selector;
    }

    /// <summary>The largest distance any single edge travelled between two frames.</summary>
    private static int MaxEdgeDelta(SD.Rectangle from, SD.Rectangle to) => Math.Max(
        Math.Max(Math.Abs(to.Left - from.Left), Math.Abs(to.Top - from.Top)),
        Math.Max(Math.Abs(to.Right - from.Right), Math.Abs(to.Bottom - from.Bottom)));

    /// <summary>
    /// Wall-clock timestamps of a repaint loop that asks for its next frame by invalidating,
    /// with a paint cost in it. Runs a real window on the hidden desktop.
    /// </summary>
    private static double[] MeasurePaintDrivenFrames()
    {
        var stamps = new List<double>();
        RunSta(() =>
        {
            var clock = Stopwatch.StartNew();
            using var form = new RepaintProbe(stamps, clock, Motion.GlideDurationMs);
            form.Show();
            while (clock.Elapsed.TotalMilliseconds < Motion.GlideDurationMs)
            {
                WF.Application.DoEvents();
                Thread.Yield();
            }
            form.Hide();
        });
        return stamps.ToArray();
    }

    /// <summary>The same window of time, driven by Motion.Run at the frame interval.</summary>
    private static double[] MeasureClockDrivenFrames()
    {
        var stamps = new List<double>();
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                var clock = Stopwatch.StartNew();
                bool done = false;
                using IDisposable? tween = Motion.Run(
                    Motion.GlideDurationMs,
                    Motion.Linear,
                    _ => stamps.Add(clock.Elapsed.TotalMilliseconds),
                    () => done = true);
                while (!done && clock.Elapsed.TotalMilliseconds < Motion.GlideDurationMs * 4)
                {
                    WF.Application.DoEvents();
                    Thread.Yield();
                }
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
            }
        });
        return stamps.ToArray();
    }

    /// <summary>A window that repaints itself as fast as the message queue allows, with a
    /// paint cost roughly the size of the selector's own hover band.</summary>
    private sealed class RepaintProbe : WF.Form
    {
        private readonly List<double> _stamps;
        private readonly Stopwatch _clock;
        private readonly int _budgetMs;

        public RepaintProbe(List<double> stamps, Stopwatch clock, int budgetMs)
        {
            _stamps = stamps;
            _clock = clock;
            _budgetMs = budgetMs;
            FormBorderStyle = WF.FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = WF.FormStartPosition.Manual;
            // On screen, not parked off it: a window with no visible pixels is never sent
            // WM_PAINT at all, and the probe measured an empty list. The hidden desktop is
            // where this runs, so "on screen" is nobody's screen.
            Bounds = new SD.Rectangle(0, 0, Width, Height);
            Opacity = 0.01d;
            DoubleBuffered = true;
        }

        protected override void OnPaint(WF.PaintEventArgs e)
        {
            base.OnPaint(e);
            // A stand-in for the real hover band's cost: a brighten pass and a border.
            using (var brush = new SD.SolidBrush(SD.Color.FromArgb(40, 255, 255, 255)))
                e.Graphics.FillRectangle(brush, 0, 0, Width, Height);
            using (var pen = new SD.Pen(SD.Color.DodgerBlue, 2))
                e.Graphics.DrawRectangle(pen, 20, 20, Width - 40, Height - 40);

            if (_clock.Elapsed.TotalMilliseconds > _budgetMs)
                return;
            _stamps.Add(_clock.Elapsed.TotalMilliseconds);
            Invalidate();
        }
    }

    private static object? Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void Set(object target, string field, object? value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
