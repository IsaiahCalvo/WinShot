using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>
/// Repeatedly captures a fixed screen region and stitches the frames into one
/// tall image (or one wide image for <see cref="ScrollDirection.Horizontal"/>).
/// In <see cref="ScrollCaptureMode.Auto"/> it also sends wheel-scroll input to
/// the content under the region; in <see cref="ScrollCaptureMode.Manual"/>
/// it only watches while the user scrolls themselves. The whole loop runs on the
/// thread pool; the status callback fires on a thread-pool thread, so UI consumers
/// must marshal via their Dispatcher.
/// </summary>
public static class ScrollingCaptureService
{
    private const int MaxIterations = 60;
    private const int MaxStitchedHeight = 32000;
    private const int MaxStitchedWidth = 32000;
    private const int ScrollSettleMs = 400;
    private const int WheelDelta = 120;
    private const int ScrollNotchesPerStep = -3; // negative = scroll down (content moves up)
    private const int HorizontalScrollNotchesPerStep = 3; // positive = scroll right (content moves left)

    /// <summary>Auto mode: how many times to re-capture, waiting for the frame to settle
    /// (two consecutive identical captures) before measuring a scroll offset. Guards against
    /// reading mid-animation / mid-lazy-load and mistaking the transient for end-of-content.</summary>
    private const int StabilizeAttempts = 3;

    /// <summary>Auto mode: pause between stabilization re-captures.</summary>
    private const int StabilizePollMs = 120;

    /// <summary>Longest live-preview thumbnail edge (px); the snapshot is downscaled to fit.</summary>
    private const int PreviewMaxEdge = 220;

    /// <summary>
    /// Manual mode: how often the region is re-captured and checked for movement. Must be fast
    /// enough that a normal-to-fast user scroll still leaves a generous overlap between
    /// consecutive frames (the stitcher needs that overlap to align them) — at 300ms a quick
    /// scroll jumped more than a frame and captured nothing. ~16 grabs/sec keeps the overlap
    /// large even for fast flicks; it only runs during an active capture, so the cost is fine.
    /// </summary>
    private const int ManualPollMs = 60;

    /// <summary>Manual mode: generous overall cap so an abandoned capture eventually ends.</summary>
    private const int ManualTimeoutMs = 10 * 60 * 1000;

    /// <summary>
    /// Runs a scrolling capture for <paramref name="screenRegion"/> (physical screen
    /// coordinates) and returns whatever was stitched so far (null only if nothing
    /// was captured). <paramref name="direction"/> picks the axis: vertical grows the
    /// stitch downward, horizontal grows it rightward (capped at 32000px width).
    /// Auto mode: sends wheel-scroll input (vertical wheel, or horizontal wheel for
    /// horizontal captures) and stops at the content end (no movement detected twice
    /// in a row), on cancellation, on Esc, after 60 iterations, or at the 32000px
    /// stitched height/width cap.
    /// Manual mode: never scrolls or moves the cursor — it just re-captures every
    /// ~300 ms and appends whenever forward (down/right) movement is detected. Pauses
    /// are fine (zero offset never ends the capture); backward scrolls are ignored.
    /// Ends only on cancellation, Esc, the 32000px cap, or a 10-minute timeout.
    /// </summary>
    public static async Task<SD.Bitmap?> RunAsync(SD.Rectangle screenRegion, ScrollCaptureMode mode,
        ScrollDirection direction, Action<string> status, CancellationToken ct,
        Action<SD.Bitmap>? preview = null)
    {
        if (screenRegion.Width < 1 || screenRegion.Height < 1)
            return null;

        bool manual = mode == ScrollCaptureMode.Manual;
        bool horizontal = direction == ScrollDirection.Horizontal;

        return await Task.Run(async () =>
        {
            SD.Bitmap? stitched = null;
            SD.Bitmap? previous = null;
            SD.Bitmap? footerStrip = null; // sticky footer lifted off the body, re-applied once at the end
            try
            {
                int frames = 0;
                int stitchedFrames = 0;
                int zeroOffsetStreak = 0;
                // Sticky header/footer heights, persisted (grown to the max seen) across the run
                // so a partially-sticky chrome is masked from offset/append once detected.
                int topBand = 0, bottomBand = 0;
                bool hitIterationCap = false;   // ran out of iterations while still moving
                bool reachedContentEnd = false; // genuine no-movement bottom (auto only)
                bool alignmentLost = false;     // scrolled but frames couldn't align (overlap too small)
                var elapsed = System.Diagnostics.Stopwatch.StartNew();

                for (int i = 0; manual ? elapsed.ElapsedMilliseconds < ManualTimeoutMs : i < MaxIterations; i++)
                {
                    if (ShouldStop(ct))
                        break;

                    // Auto mode: wait for the freshly-scrolled region to stabilize (two
                    // consecutive identical captures) before measuring, so animations and
                    // lazy-loaded content settle and aren't mistaken for end-of-content.
                    // Deterministic GDI BitBlt (not WGC): consecutive grabs of unchanged content
                    // are byte-identical, which the exact row-hash matcher relies on, and it
                    // already excludes layered windows (our dim overlay). WGC can return subtly
                    // different pixels frame-to-frame, which breaks the match. (odd-snap/ShareX
                    // use BitBlt for exactly this reason.)
                    var frame = manual
                        ? CaptureService.CaptureScreenRegionWithoutLayeredWindows(screenRegion)
                        : await CaptureStableFrameAsync(screenRegion, ct).ConfigureAwait(false);
                    frames++;

                    if (stitched is null)
                    {
                        stitched = frame;
                        previous = (SD.Bitmap)frame.Clone();
                        stitchedFrames = 1;
                    }
                    else
                    {
                        // Detect sticky bands between consecutive frames and keep the largest
                        // seen so they stay masked even on frames that happen to match fully.
                        if (!horizontal)
                        {
                            topBand = Math.Max(topBand, ImageStitcher.DetectConstantTopBand(previous!, frame));
                            bottomBand = Math.Max(bottomBand, ImageStitcher.DetectConstantBottomBand(previous!, frame));

                            // First time a sticky footer is seen, lift it off the running body
                            // (which still carries frame 0's footer) so it isn't buried mid-stitch.
                            // It's re-applied exactly once at the very bottom when the run ends.
                            if (bottomBand > 0 && footerStrip is null && stitched!.Height > bottomBand)
                            {
                                footerStrip = ImageStitcher.CropBottomRows(stitched, bottomBand);
                                var trimmed = ImageStitcher.RemoveBottomRows(stitched, bottomBand);
                                stitched.Dispose();
                                stitched = trimmed;
                            }
                        }

                        int offset = horizontal
                            ? ImageStitcher.FindScrollOffsetHorizontal(previous!, frame)
                            : ImageStitcher.FindScrollOffset(previous!, frame, topBand, bottomBand);

                        // In auto mode, did the page actually move? Compare against the prior
                        // frame ignoring sticky bands: equal => truly at the bottom; different
                        // but offset==0 => it moved but we couldn't align (overlap too small).
                        bool framesDiffer = !ImageStitcher.FramesIdentical(previous!, frame);

                        // Diagnostic: shows, per frame, whether the content changed (the user
                        // scrolled) and whether we could align it (offset>0). differ=true with
                        // offset=0 means it scrolled but the matcher couldn't lock on.
                        Log.Info($"Scroll frame {frames}: region={frame.Width}x{frame.Height} " +
                                 $"differ={framesDiffer} offset={offset} topBand={topBand} bottomBand={bottomBand} " +
                                 $"stitchedH={stitched!.Height}");

                        if (offset == 0)
                        {
                            zeroOffsetStreak++;
                            if (manual)
                            {
                                if (framesDiffer)
                                {
                                    // The content changed but we couldn't align this frame to the
                                    // anchor (a quick scroll outran the overlap). Re-anchor to the
                                    // CURRENT position so the next small scroll aligns against it.
                                    // Keeping the old anchor instead would strand it behind the
                                    // user — every later frame would then have zero overlap and the
                                    // capture would stall forever (the bug behind "captures a bit
                                    // then stops"). The skipped span is lost, not stitched wrong.
                                    previous!.Dispose();
                                    previous = frame;
                                }
                                else
                                {
                                    // Paused / nothing changed: keep the anchor.
                                    frame.Dispose();
                                }
                            }
                            else
                            {
                                if (framesDiffer)
                                    alignmentLost = true; // it scrolled, just couldn't be stitched
                                previous!.Dispose();
                                previous = frame;
                            }
                        }
                        else
                        {
                            zeroOffsetStreak = 0;
                            alignmentLost = false;
                            SD.Bitmap grown;
                            if (horizontal)
                            {
                                int cols = Math.Min(offset, MaxStitchedWidth - stitched.Width);
                                grown = ImageStitcher.AppendRight(stitched, frame, cols);
                            }
                            else
                            {
                                int rows = Math.Min(offset, MaxStitchedHeight - stitched.Height);
                                // Append only genuinely-new content above the sticky footer, so
                                // a pinned footer is stitched exactly once (at the very bottom).
                                grown = ImageStitcher.AppendBelowExcludingFooter(stitched, frame, rows, bottomBand);
                            }
                            stitched.Dispose();
                            stitched = grown;
                            stitchedFrames++;
                            previous!.Dispose();
                            previous = frame;
                        }

                        if (!manual && zeroOffsetStreak >= 2)
                        {
                            reachedContentEnd = !alignmentLost;
                            break; // content end reached (or overlap too small to continue)
                        }
                        if (horizontal ? stitched.Width >= MaxStitchedWidth : stitched.Height >= MaxStitchedHeight)
                            break;
                    }

                    PushPreview(preview, stitched);
                    string extent = horizontal ? $"{stitched.Width}px wide" : $"{stitched.Height}px";
                    status(manual
                        ? $"Scroll the content yourself — click Stop when done. {stitchedFrames} frames – {extent}"
                        : $"Captured {frames} frames - {extent}");

                    if (ShouldStop(ct))
                        break;

                    if (!manual)
                    {
                        if (horizontal)
                            ScrollRight(screenRegion);
                        else
                            ScrollDown(screenRegion);

                        // Last allowed iteration but we were still making progress: we stopped
                        // at the cap, not at the content's end. Surface that distinction.
                        if (i == MaxIterations - 1 && zeroOffsetStreak == 0)
                            hitIterationCap = true;
                    }
                    try
                    {
                        await Task.Delay(manual ? ManualPollMs : ScrollSettleMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                // Re-apply the sticky footer exactly once, at the very bottom of the finished stitch.
                if (footerStrip is not null && stitched is not null
                    && stitched.Width == footerStrip.Width
                    && stitched.Height + footerStrip.Height <= MaxStitchedHeight)
                {
                    var withFooter = ImageStitcher.AppendBelow(stitched, footerStrip, footerStrip.Height);
                    stitched.Dispose();
                    stitched = withFooter;
                }

                // Final status reflects WHY auto capture stopped, so a truncated page is obvious.
                if (!manual && stitched is not null)
                {
                    string extent = horizontal ? $"{stitched.Width}px wide" : $"{stitched.Height}px";
                    if (hitIterationCap)
                        status($"Stopped at limit — page may be longer. {extent}");
                    else if (alignmentLost && !reachedContentEnd)
                        status($"Stopped — couldn't align frames (overlap too small). {extent}");
                    else if (reachedContentEnd)
                        status($"Reached the bottom. {extent}");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Scrolling capture failed mid-run; returning partial result", ex);
            }
            finally
            {
                previous?.Dispose();
                footerStrip?.Dispose();
            }
            return stitched;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures the region, then re-captures up to <see cref="StabilizeAttempts"/> times until
    /// two consecutive captures are identical (the frame has settled). Returns the last (stable)
    /// capture; intermediate captures are disposed. On cancellation it returns whatever it has.
    /// </summary>
    private static async Task<SD.Bitmap> CaptureStableFrameAsync(SD.Rectangle region, CancellationToken ct)
    {
        var current = CaptureService.CaptureScreenRegionWithoutLayeredWindows(region);
        for (int attempt = 1; attempt < StabilizeAttempts; attempt++)
        {
            if (ShouldStop(ct))
                break;
            try
            {
                await Task.Delay(StabilizePollMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var next = CaptureService.CaptureScreenRegionWithoutLayeredWindows(region);
            bool settled = ImageStitcher.FramesIdentical(current, next);
            current.Dispose();
            current = next;
            if (settled)
                break;
        }
        return current;
    }

    /// <summary>Hands a downscaled snapshot of the growing stitch to the live-preview callback.</summary>
    private static void PushPreview(Action<SD.Bitmap>? preview, SD.Bitmap? stitched)
    {
        if (preview is null || stitched is null)
            return;
        try
        {
            using var thumb = Downscale(stitched, PreviewMaxEdge);
            preview(thumb);
        }
        catch (Exception ex)
        {
            Log.Error("Scrolling capture: live preview snapshot failed (non-fatal)", ex);
        }
    }

    /// <summary>Returns a new bitmap scaled so its longest edge is at most <paramref name="maxEdge"/>.</summary>
    private static SD.Bitmap Downscale(SD.Bitmap source, int maxEdge)
    {
        double scale = Math.Min(1.0, maxEdge / (double)Math.Max(source.Width, source.Height));
        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var thumb = new SD.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(thumb);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        g.DrawImage(source, 0, 0, w, h);
        return thumb;
    }

    private static bool ShouldStop(CancellationToken ct) =>
        ct.IsCancellationRequested || (GetAsyncKeyState(VkEscape) & 0x8000) != 0;

    /// <summary>Moves the cursor to the region center and sends one wheel-down step.</summary>
    private static void ScrollDown(SD.Rectangle region)
    {
        SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
        var input = new INPUT
        {
            type = InputMouse,
            mi = new MOUSEINPUT
            {
                mouseData = unchecked((uint)(WheelDelta * ScrollNotchesPerStep)),
                dwFlags = MouseEventFWheel,
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Moves the cursor to the region center and sends one horizontal wheel-right step.</summary>
    private static void ScrollRight(SD.Rectangle region)
    {
        SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
        var input = new INPUT
        {
            type = InputMouse,
            mi = new MOUSEINPUT
            {
                mouseData = (uint)(WheelDelta * HorizontalScrollNotchesPerStep),
                dwFlags = MouseEventFHWheel,
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private const int VkEscape = 0x1B;
    private const uint InputMouse = 0;
    private const uint MouseEventFWheel = 0x0800;
    private const uint MouseEventFHWheel = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // MOUSEINPUT is the largest member of the INPUT union, so declaring only it
    // keeps the marshalled size correct for SendInput.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
