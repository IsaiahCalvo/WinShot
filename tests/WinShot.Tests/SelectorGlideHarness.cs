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
        using var animations = AnimationsOn();
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
    /// the dim wash and the synthetic desktop's greys never are.
    ///
    /// Through LockBits and one Marshal.Copy a row, not GetPixel. A per-pixel GetPixel scan of
    /// one 520x360 frame costs about a fifth of a second — longer than the 200 ms glide it is
    /// sampling — so a loop that painted and scanned each frame measured four frames of a
    /// twenty-five frame animation and called that the glide. Row copies rather than a pointer
    /// walk, so the test project does not have to be opened up to unsafe blocks for one
    /// scan.</summary>
    private static SD.Rectangle AccentBounds(SD.Bitmap bmp)
    {
        SD.Imaging.BitmapData data = bmp.LockBits(
            new SD.Rectangle(0, 0, bmp.Width, bmp.Height),
            SD.Imaging.ImageLockMode.ReadOnly,
            SD.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            int[] row = new int[bmp.Width];
            for (int y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + (y * data.Stride), row, 0, bmp.Width);
                for (int x = 0; x < bmp.Width; x++)
                {
                    int argb = row[x];
                    int a = (argb >> 24) & 0xFF, r = (argb >> 16) & 0xFF, b = argb & 0xFF;
                    if (a <= 8 || b <= r + 24)
                        continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxX < 0 ? SD.Rectangle.Empty : SD.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
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
            SettleHover(selector);
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

    // ------------------------------------------------------------------ round 2

    /// <summary>
    /// A highlight that spans two displays is ONE object, so both halves of it have to be
    /// computed from the same instant. Every surface's paint calls Update(), and the glide
    /// reads a stopwatch, so the second surface used to advance it again 1-3 ms after the
    /// first: the border straddling a seam was drawn from two different moments and landed a
    /// pixel apart for that frame, the whole way across.
    /// </summary>
    [Fact]
    public void ABurstOfSurfacePaintsAdvancesTheGlideOnce()
    {
        using var animations = AnimationsOn();
        var tween = new RectangleTween();
        tween.Retarget(WindowA, WindowB);

        void PaintEightSurfaces()
        {
            for (int surfaces = 0; surfaces < 8; surfaces++)
                tween.Update();
        }

        // A frame's worth of time, so the round has something to advance INTO. The sleeps are
        // the frame clock the tween is throttled against; nothing here asserts on wall time.
        Thread.Sleep(Motion.FrameIntervalMs * 3);
        SD.Rectangle before = tween.Current;
        PaintEightSurfaces();
        SD.Rectangle afterOneRound = tween.Current;
        Assert.True(afterOneRound != before, "the round did not advance the glide at all");

        // Eight more surfaces inside the same frame: the border stays exactly where the first
        // one put it, which is what makes a highlight spanning two displays one object.
        PaintEightSurfaces();
        Assert.Equal(afterOneRound, tween.Current);

        // And the NEXT frame does move, so the throttle is a frame budget and not a freeze.
        Thread.Sleep(Motion.FrameIntervalMs * 3);
        PaintEightSurfaces();
        Assert.True(tween.Current != afterOneRound, "the glide never advanced again");
    }

    /// <summary>
    /// And the same, counted over a whole glide through the real paint path on a two-monitor
    /// desktop: painting BOTH surfaces of every round may not move the border twice as far per
    /// round as painting one would. Counted rather than compared frame by frame, so the
    /// measurement cannot turn into an assertion about how fast this particular box paints.
    /// </summary>
    [Fact]
    public void TwoSurfacesDoNotRunTheGlideAtDoubleSpeed()
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => true;
        try
        {
            var left = new SD.Rectangle(0, 0, Width, Height);
            var right = new SD.Rectangle(Width, 0, Width, Height);
            using SD.Bitmap frozen = SelectorRenderHarness.SyntheticDesktop(Width * 2, Height);
            using FastRegionSelectorDialog selector = Seed(frozen, FastRegionSelectorDialog.SelectorMode.Window);
            Set(selector, "_vs", new SD.Rectangle(0, 0, Width * 2, Height));
            selector.MonitorsForRender = new[] { left, right };

            // A window straddling the seam, sweeping onto another that also straddles it.
            HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, new SD.Rectangle(400, 40, 300, 160));
            SettleHover(selector);
            HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, new SD.Rectangle(380, 170, 340, 150));
            Set(selector, "_hoverWash", 1d);

            var tween = (RectangleTween)Field(selector, "_hoverTween")!;
            using var surface = new SD.Bitmap(Width * 2, Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(surface);

            int moves = 0;
            for (int round = 0; round < 400 && tween.IsActive; round++)
            {
                SD.Rectangle before = tween.Current;
                selector.PaintSurface(g, left);
                SD.Rectangle between = tween.Current;
                selector.PaintSurface(g, right);
                if (between != before)
                    moves++;
                if (tween.Current != between)
                    moves++;
            }

            // A 200 ms glide has 25 frames in it. Anything approaching 50 is the second
            // surface advancing the clock a second time, which is both a double-speed glide
            // and, for the frame it happens on, a border whose two halves are a pixel apart.
            int budget = (Motion.GlideDurationMs / Motion.FrameIntervalMs) + 2;
            Assert.True(moves <= budget,
                $"the glide moved {moves} times over one 200 ms flight, against a budget of {budget}");
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
        }
    }

    /// <summary>
    /// A retarget must not change the SPEED the border is travelling at, only its destination.
    /// The frame the retarget lands on may not move further than the glide's own steady step,
    /// in either direction: a jump forward is the classic "restart from the old target" bug,
    /// and a jump of any size at the moment the pointer is moving fastest is what reads as a
    /// stutter. Measured as the per-frame max edge delta, which is the same number
    /// <see cref="NoGlideFrameJumpsFurtherThanTheOneBeforeIt"/> asserts on.
    /// </summary>
    [Fact]
    public void ARetargetMidGlideDoesNotSnapTheStep()
    {
        var third = new SD.Rectangle(150, 250, 120, 90);
        int steps = Math.Max(1, Motion.GlideDurationMs / Motion.FrameIntervalMs);
        using var animations = AnimationsOn();
        var tween = new RectangleTween();

        tween.Retarget(WindowA, WindowB);
        SD.Rectangle previous = tween.Current;
        int beforeStep = 0;
        for (int i = 1; i <= steps * 2 / 5; i++)
        {
            tween.Update((float)i / steps);
            beforeStep = Math.Max(beforeStep, MaxEdgeDelta(previous, tween.Current));
            previous = tween.Current;
        }

        // The caller's "from" is deliberately wrong, the way SetHoverWindow's is mid-glide.
        tween.Retarget(WindowB, third);
        tween.Update(0f);
        int atRetarget = MaxEdgeDelta(previous, tween.Current);
        previous = tween.Current;

        int afterStep = 0;
        for (int i = 1; i <= steps; i++)
        {
            tween.Update((float)i / steps);
            afterStep = Math.Max(afterStep, MaxEdgeDelta(previous, tween.Current));
            previous = tween.Current;
        }

        int allowed = Math.Max(beforeStep, afterStep);
        Assert.True(atRetarget <= allowed,
            $"the retarget frame moved {atRetarget}px where the glide's steady step is " +
            $"{beforeStep}px before it and {afterStep}px after it");
        Assert.Equal(third, tween.Current);
    }

    /// <summary>
    /// Why the glide stays LINEAR, recorded as a number rather than a preference.
    ///
    /// An ease-out is the obvious upgrade and it is the wrong one here, for a reason specific
    /// to this control: the hover target changes MID-FLIGHT every time the pointer crosses a
    /// window boundary, and a retarget restarts the curve at its own beginning. Ease-out's
    /// beginning is its fastest point, so every retarget would relaunch the border at several
    /// times the speed it was travelling at - a lurch at exactly the moment the pointer is
    /// moving fastest and the border is most closely watched. Linear has no fast point to
    /// restart at, which is the same reason ShareX draws it linear.
    ///
    /// The numbers are the first frame's step as a multiple of the glide's mean step.
    /// </summary>
    [Fact]
    public void TheGlideIsLinearBecauseEveryRetargetWouldRelaunchAnEaseOutAtFullSpeed()
    {
        double linear = FirstFrameStepAsMultipleOfMean(Motion.Linear);
        double cubic = FirstFrameStepAsMultipleOfMean(Motion.EaseOutCubic);

        Assert.True(linear <= 1.2d,
            $"linear's first frame moves {linear:0.00}x the mean step - it is not linear any more");
        Assert.True(cubic >= 2d,
            $"ease-out cubic's first frame moves only {cubic:0.00}x the mean step; if that is " +
            "really so mild, re-run the retarget measurement before rejecting it again");
    }

    /// <summary>How much further the first frame of a curve travels than an average frame
    /// does. 1.0 is a constant-speed glide; anything above it is the size of the lurch a
    /// mid-flight retarget would produce, because a retarget starts the curve over.</summary>
    private static double FirstFrameStepAsMultipleOfMean(Func<double, double> ease)
    {
        int steps = Math.Max(1, Motion.GlideDurationMs / Motion.FrameIntervalMs);
        using var animations = AnimationsOn();
        var tween = new RectangleTween();
        tween.Retarget(WindowA, WindowB);
        SD.Rectangle start = tween.Current;
        tween.Update((float)ease(1d / steps));
        double first = MaxEdgeDelta(start, tween.Current);

        tween.Retarget(WindowA, WindowB);
        SD.Rectangle from = tween.Current;
        tween.Update(1f);
        double total = MaxEdgeDelta(from, tween.Current);
        return total <= 0 ? 0 : first / (total / steps);
    }

    /// <summary>
    /// Border, wash and app-name pill on EVERY frame of a real glide, not one sampled frame.
    /// They are painted from one rect and they must stay painted from one rect the whole way:
    /// a single element left on the settled window for even one frame reads as the highlight
    /// coming apart, and one frame is all a 200 ms glide has 25 of.
    /// </summary>
    [Fact]
    public void TheHighlightIsOneObjectOnEveryFrameOfTheGlide()
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
            SettleHover(selector);
            HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, WindowB);
            Set(selector, "_hoverWash", 1d);

            var tween = (RectangleTween)Field(selector, "_hoverTween")!;
            using var surface = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
            int frames = 0;
            int midFlight = 0;
            while (tween.IsActive && frames < 400)
            {
                using (var g = SD.Graphics.FromImage(surface))
                    selector.PaintSurface(g, Monitor);
                SD.Rectangle painted = tween.IsActive ? tween.Current : WindowB;
                SD.Rectangle accent = AccentBounds(surface);
                frames++;
                if (painted != WindowA && painted != WindowB)
                    midFlight++;
                // The pen is 2px centred on the rect at 1x, so the accent box is the glide
                // rect grown by about a pixel a side. Anything further out is an element still
                // parked on a rect the rest of the highlight has left.
                Assert.True(
                    Math.Abs(accent.Left - painted.Left) <= 2 && Math.Abs(accent.Top - painted.Top) <= 2 &&
                    Math.Abs(accent.Right - painted.Right) <= 2 && Math.Abs(accent.Bottom - painted.Bottom) <= 2,
                    $"frame {frames}: accent ink {accent} against glide rect {painted}");
            }

            // A lower bound on frames is a bound on how fast the box paints and scans, so it
            // stays loose; what actually says the sweep was measured is that some of them were
            // between the two windows rather than parked on one of them.
            Assert.True(frames > 2, $"the glide only produced {frames} frame(s) - nothing was measured");
            Assert.True(midFlight > 0, "no frame was painted between the two windows");
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
            WinShot.Recording.RecordingMonitorDpi.ScaleSource = previousScale;
        }
    }

    /// <summary>
    /// The border's edge has to be the SAME number of device pixels on every frame of a glide.
    /// Its pen is 2 x the monitor scale and the surface paints with SmoothingMode.None, where
    /// an aliased pen covers whole rows and a fractional width lands on a different count
    /// depending on where the edge falls - a 2.5 px pen at 125 % covering 2 rows on one frame
    /// and 3 on the next IS the shimmer, and it is the same quantisation the crosshair guide
    /// was already fixed for.
    /// </summary>
    [Theory]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2.0d)]
    public void TheBorderEdgeIsTheSameWholeNumberOfPixelsOnEveryFrame(double scale)
    {
        var widths = new List<int>();
        int steps = Math.Max(1, Motion.GlideDurationMs / Motion.FrameIntervalMs);
        for (int i = 1; i < steps; i++)
        {
            using SD.Bitmap frame = PaintGlideFrame(Math.Min(1f, (float)i / steps), scale);
            SD.Rectangle accent = AccentBounds(frame);
            if (accent.IsEmpty)
                continue;
            widths.Add(TopEdgeRows(frame, accent));
        }

        Assert.NotEmpty(widths);
        Assert.True(widths.Distinct().Count() == 1,
            $"the border's top edge was {string.Join("/", widths.Distinct().OrderBy(w => w))} px " +
            $"thick across one glide at {scale:0.##}x - an edge that changes width between " +
            "frames is the shimmer");
        Assert.Equal(1, widths[0]);
    }

    /// <summary>Rows of solid accent the border's TOP edge covers, read down the middle of the
    /// highlight so the left and right edges cannot be counted instead.</summary>
    private static int TopEdgeRows(SD.Bitmap bmp, SD.Rectangle accent)
    {
        int x = accent.Left + (accent.Width / 2);
        int rows = 0;
        for (int y = accent.Top; y < accent.Bottom; y++)
        {
            SD.Color c = bmp.GetPixel(x, y);
            // The border is the accent at full strength; the wash under it is the same hue at
            // 40/255 over the desktop, which never reaches the border's blue.
            if (c.B < 200 || c.B <= c.R + 100)
                break;
            rows++;
        }
        return rows;
    }

    /// <summary>
    /// The first window the pointer meets must not appear whole. It used to: SetHoverWindow
    /// stopped the tween whenever there was no previous window, so the border, the wash and
    /// the app name arrived in a single frame on a control whose entire point is that nothing
    /// on it cuts. It grows out of the pointer instead - not out of the last window it saw,
    /// which would start the motion somewhere the user is not looking.
    /// </summary>
    [Fact]
    public void TheFirstHoverGrowsOutOfThePointerRatherThanAppearingWhole()
    {
        using var animations = AnimationsOn();
        using SD.Bitmap frozen = SelectorRenderHarness.SyntheticDesktop(Width, Height);
        using FastRegionSelectorDialog selector = Seed(frozen, FastRegionSelectorDialog.SelectorMode.Window);
        var cursor = new SD.Point(WindowA.Left + 20, WindowA.Top + 20);
        Set(selector, "_currentScreen", cursor);
        selector.SetHoverWindow(new WindowInfo(new IntPtr(1), "Notepad", WindowA));

        var tween = (RectangleTween)Field(selector, "_hoverTween")!;
        Assert.True(tween.IsActive, "the first hover did not glide at all");
        Assert.Equal(new SD.Rectangle(cursor.X, cursor.Y, 0, 0), tween.Current);

        tween.Update(1f);
        Assert.Equal(WindowA, tween.Current);
    }

    /// <summary>
    /// Leaving every window (the pointer reaches the desktop) must fade the highlight out, not
    /// cut it. It cut: BeginHoverWash assigned the wash straight to 0 and the paint gate was
    /// _hoverWindow, which was already null - so the border, the tint and the app name all left
    /// the frame in one step. The exit is the entrance played backwards now, over a rect the
    /// selector deliberately remembers for exactly as long as the fade.
    /// </summary>
    [Fact]
    public void LeavingEveryWindowFadesTheHighlightOutInsteadOfCuttingIt()
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => true;
        try
        {
            using SD.Bitmap frozen = SelectorRenderHarness.SyntheticDesktop(Width, Height);
            using FastRegionSelectorDialog selector = Seed(frozen, FastRegionSelectorDialog.SelectorMode.Window);
            HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, WindowA);
            SettleHover(selector);
            Set(selector, "_hoverWash", 1d);

            Set(selector, "_currentScreen", new SD.Point(WindowA.Right + 40, WindowA.Bottom + 40));
            selector.SetHoverWindow(null);

            // The wash is still up and still has a rect to stand on, and the border is still
            // being painted - the three facts the cut destroyed.
            Assert.True((double)Field(selector, "_hoverWash")! > 0d, "the wash was cut to zero on the way out");
            Assert.NotNull(Field(selector, "_hoverExit"));

            using var surface = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(surface))
                selector.PaintSurface(g, Monitor);
            Assert.False(AccentBounds(surface).IsEmpty, "nothing accent-coloured was painted on the way out");

            // And it shrinks back toward the pointer rather than sitting on the window it left.
            var tween = (RectangleTween)Field(selector, "_hoverTween")!;
            Assert.True(tween.IsActive, "the exit did not glide");
            tween.Update(1f);
            Assert.Equal(0, tween.Current.Width);
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
        }
    }

    /// <summary>
    /// The app-name pill is centred INSIDE the highlight, so it may only be drawn once the
    /// highlight can hold it. It could not, for most of the entrance and the exit — those grow
    /// out of and shrink back into the pointer now — and the name hung out of both sides of a
    /// 20 px box, which reads as a label that has come loose from the thing it names. The same
    /// was already true of any window narrower than its own app name.
    /// </summary>
    [Fact]
    public void TheAppNamePillIsNotDrawnUntilTheHighlightCanHoldIt()
    {
        var cursor = new SD.Point(WindowA.Left + 40, WindowA.Top + 30);
        SD.Size pill = SelectorChrome.MeasureLabel("Notepad", 1d);
        var tooSmall = new SD.Rectangle(cursor.X, cursor.Y, pill.Width - 6, pill.Height - 2);

        using (SD.Bitmap small = PaintHighlight(tooSmall, 1d, cursor))
        {
            SD.Rectangle accent = AccentBounds(small);
            // The border is there; nothing hangs outside it.
            Assert.False(accent.IsEmpty, "the highlight itself did not paint");
            Assert.True(accent.Width <= tooSmall.Width + 4 && accent.Height <= tooSmall.Height + 4,
                $"accent ink {accent} runs outside a {tooSmall.Size} highlight - the name pill is still being drawn");
        }

        using SD.Bitmap roomy = PaintHighlight(
            new SD.Rectangle(cursor.X, cursor.Y, pill.Width + 40, pill.Height + 40), 1d, cursor);
        Assert.False(AccentBounds(roomy).IsEmpty);
    }

    /// <summary>
    /// With animation turned off system-wide the highlight LANDS. Every Motion.Run tween in the
    /// app already honoured that; this one reads its own stopwatch instead and so had been
    /// ignoring the accessibility setting outright — and round 2 widened that from the
    /// window-to-window sweep to the entrance and the exit, which is the whole highlight.
    /// </summary>
    [Fact]
    public void WithAnimationsOffTheHighlightLandsInsteadOfGliding()
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            var tween = new RectangleTween();
            tween.Retarget(WindowA, WindowB);
            Assert.False(tween.IsActive, "the glide ran with animations turned off");
            Assert.Equal(WindowB, tween.Current);

            using SD.Bitmap frozen = SelectorRenderHarness.SyntheticDesktop(Width, Height);
            using FastRegionSelectorDialog selector = Seed(frozen, FastRegionSelectorDialog.SelectorMode.Window);
            HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, WindowA);
            Assert.False(((RectangleTween)Field(selector, "_hoverTween")!).IsActive,
                "the entrance glided with animations turned off");
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
        }
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

        // The easing question, side by side: the same 25 frames of the same sweep, once under
        // the linear curve that ships and once under ease-out cubic.
        WriteFrameStrip(Path.Combine(outDir, $"glide-frames-linear-{tag}.png"), Motion.Linear);
        WriteFrameStrip(Path.Combine(outDir, $"glide-frames-easeout-{tag}.png"), Motion.EaseOutCubic);
        WriteEntranceAndExitStrips(outDir, tag);
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
        text.AppendLine("easing — first frame's step as a multiple of the glide's mean step.");
        text.AppendLine("a retarget restarts the curve, so this IS the size of the lurch each");
        text.AppendLine("window boundary the pointer crosses would produce:");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  linear          {FirstFrameStepAsMultipleOfMean(Motion.Linear):0.00}x");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  ease-out cubic  {FirstFrameStepAsMultipleOfMean(Motion.EaseOutCubic):0.00}x");
        text.AppendLine();
        text.AppendLine("border edge, device-pixel rows covered, constant across one glide:");
        foreach (double scale in new[] { 1.0d, 1.25d, 1.5d, 2.0d })
        {
            using SD.Bitmap frame = PaintGlideFrame(0.5f, scale);
            SD.Rectangle accent = AccentBounds(frame);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {scale * 96,3:0} dpi  pen {2d * scale:0.00}px asked  {TopEdgeRows(frame, accent)} rows drawn");
        }
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
    private static void WriteFrameStrip(string path, Func<double, double>? ease = null)
    {
        var frames = new List<SD.Bitmap>();
        try
        {
            int steps = Math.Max(1, Motion.GlideDurationMs / Motion.FrameIntervalMs);
            for (int i = 0; i <= steps; i++)
            {
                double linear = Math.Min(1d, (double)i / steps);
                frames.Add(PaintGlideFrame((float)(ease is null ? linear : ease(linear))));
            }

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

    /// <summary>
    /// The entrance and the exit, one strip each: the highlight growing out of the pointer as
    /// the first window is met, and shrinking back into it as the pointer reaches the desktop.
    /// Both are new this round — before it, each of these was a single frame of nothing
    /// followed by a single frame of everything.
    /// </summary>
    private static void WriteEntranceAndExitStrips(string outDir, string tag)
    {
        if (typeof(FastRegionSelectorDialog).GetField(
                "_hoverExit", BindingFlags.Instance | BindingFlags.NonPublic) is null)
            return; // measuring the "before" tree, which has no exit to photograph

        WriteRampStrip(Path.Combine(outDir, $"glide-entrance-{tag}.png"), arriving: true);
        WriteRampStrip(Path.Combine(outDir, $"glide-exit-{tag}.png"), arriving: false);
    }

    private static void WriteRampStrip(string path, bool arriving)
    {
        var frames = new List<SD.Bitmap>();
        try
        {
            int steps = Math.Max(1, Motion.QuickMs / Motion.FrameIntervalMs);
            var cursor = new SD.Point(WindowA.Left + 40, WindowA.Top + 30);
            var tween = new RectangleTween();
            var seed = new SD.Rectangle(cursor.X, cursor.Y, 0, 0);
            if (arriving)
                tween.Retarget(seed, WindowA);
            else
                tween.Retarget(WindowA, seed);

            for (int i = 0; i <= steps; i++)
            {
                double at = Math.Min(1d, (double)i / steps);
                // The rect rides the glide's linear clock; the presence rides QuickMs with the
                // curve the ramp actually uses in each direction.
                tween.Update((float)at);
                double wash = arriving ? Motion.EaseOutCubic(at) : 1d - Motion.EaseInCubic(at);
                frames.Add(PaintHighlight(tween.Current, wash, cursor));
            }

            const int columns = 9;
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
                g.DrawString($"{i}  {i * Motion.FrameIntervalMs}ms", font, SD.Brushes.Gainsboro,
                    x, y + cellHeight + 1);
            }
            strip.Save(path, SD.Imaging.ImageFormat.Png);
        }
        finally
        {
            foreach (SD.Bitmap frame in frames)
                frame.Dispose();
        }
    }

    /// <summary>One painted frame of the window highlight at a pinned rect and a pinned
    /// presence, through the real paint path.</summary>
    private static SD.Bitmap PaintHighlight(SD.Rectangle at, double wash, SD.Point cursor)
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        var previousScale = WinShot.Recording.RecordingMonitorDpi.ScaleSource;
        Motion.AnimationsEnabled = () => false;
        WinShot.Recording.RecordingMonitorDpi.ScaleSource = _ => 1d;
        try
        {
            using SD.Bitmap frozen = SelectorRenderHarness.SyntheticDesktop(Width, Height);
            using FastRegionSelectorDialog selector = Seed(frozen, FastRegionSelectorDialog.SelectorMode.Window);
            Set(selector, "_currentScreen", cursor);
            Set(selector, "_hoverWindow", new WindowInfo(new IntPtr(2), "Notepad", at));
            Set(selector, "_hoverAppName", "Notepad");
            Set(selector, "_hoverWash", wash);

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
            SettleHover(selector);
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
                SettleHover(selector);
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
    private static SD.Bitmap PaintGlideFrame(float progress, double scale = 1d)
    {
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        var previousScale = WinShot.Recording.RecordingMonitorDpi.ScaleSource;
        Motion.AnimationsEnabled = () => true;
        WinShot.Recording.RecordingMonitorDpi.ScaleSource = _ => scale;
        try
        {
            using SD.Bitmap frozen = SelectorRenderHarness.SyntheticDesktop(Width, Height);
            using FastRegionSelectorDialog selector = Seed(frozen, FastRegionSelectorDialog.SelectorMode.Window);
            HoverOn(selector, FastRegionSelectorDialog.SelectorMode.Window, WindowA);
            SettleHover(selector);
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

    /// <summary>
    /// Forces animation ON for the scope. Every measurement that drives a RectangleTween
    /// directly needs it: the tween LANDS on Retarget when animation is off, and whether it is
    /// off is a fact about the machine the suite happens to run on, not about the glide.
    /// </summary>
    private static IDisposable AnimationsOn()
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => true;
        return new RestoreAnimations(previous);
    }

    private sealed class RestoreAnimations(Func<bool> previous) : IDisposable
    {
        public void Dispose() => Motion.AnimationsEnabled = previous;
    }

    /// <summary>Lands the entrance glide before a sweep is measured. The FIRST hover of a
    /// session grows the highlight out of the pointer, so without this every "sweep from A to
    /// B" measurement would start from a half-grown A.</summary>
    private static void SettleHover(FastRegionSelectorDialog selector) =>
        ((RectangleTween)Field(selector, "_hoverTween")!).Update(1f);

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
