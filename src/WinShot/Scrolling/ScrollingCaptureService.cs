using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>One-shot, thread-safe "please auto-recover the skipped section now" signal. The UI
/// (Recover button) sets it; the capture loop consumes it. A click that arrives mid-recover is
/// harmlessly coalesced.</summary>
public sealed class ScrollRecoverSignal
{
    private volatile bool _requested;
    public void Request() => _requested = true;
    public bool ConsumeIfRequested()
    {
        if (!_requested) return false;
        _requested = false;
        return true;
    }
}

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
    /// enough that a normal-to-fast user scroll still leaves overlap between consecutive frames
    /// (the stitcher needs that overlap to align them) — if a flick moves more than a full frame
    /// height between grabs there's nothing to align and that span is lost. ShareX/odd-snap beat
    /// this by over-sampling; ~33 grabs/sec (BitBlt is cheap, the preview is throttled separately)
    /// keeps overlap even for fast flicks, and it only runs during an active capture.
    /// </summary>
    private const int ManualPollMs = 30;

    /// <summary>Manual mode: minimum gap between live-preview refreshes. The preview downscales the
    /// WHOLE growing stitch, which gets costly as it grows tall; refreshing it every frame would
    /// inflate the real capture interval (and shrink overlap). Capture fast, preview lazily.</summary>
    private const int PreviewThrottleMs = 150;

    /// <summary>Manual mode: consecutive frames that moved but couldn't be aligned (scrolled faster
    /// than a frame) before we tell the user to slow down. One miss is a benign blip; two in a row
    /// means they're outrunning the capture.</summary>
    private const int TooFastStreakForWarning = 2;

    /// <summary>Auto-recover: pause after each programmatic wheel-up so the content settles before
    /// the next grab.</summary>
    private const int RecoverSettleMs = 110;

    /// <summary>Auto-recover: consecutive no-movement grabs (content didn't scroll — top of page or
    /// nothing to recover) before giving up.</summary>
    private const int RecoverStuckLimit = 4;

    /// <summary>Auto-recover: hard cap on wheel-ups so a never-closing gap can't loop forever.</summary>
    private const int RecoverMaxSteps = 600;

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
        Action<SD.Bitmap>? preview = null, Action<bool>? tooFast = null, ScrollRecoverSignal? recover = null)
    {
        if (screenRegion.Width < 1 || screenRegion.Height < 1)
            return null;

        bool manual = mode == ScrollCaptureMode.Manual;
        bool horizontal = direction == ScrollDirection.Horizontal;

        return await Task.Run(async () =>
        {
            SD.Bitmap? stitched = null;
            SD.Bitmap? previous = null;
            // Manual vertical capture uses the position-aware canvas (scroll up/down freely, gaps
            // refill on re-scroll). Auto mode and the niche manual-horizontal path keep append-only.
            var canvas = (manual && !horizontal) ? new ScrollCanvas() : null;
            try
            {
                int frames = 0;
                int stitchedFrames = 0;
                int zeroOffsetStreak = 0;
                int tooFastStreak = 0;          // consecutive frames that moved but couldn't align
                bool warnedTooFast = false;     // a "slow down" warning is currently showing
                bool hitIterationCap = false;   // ran out of iterations while still moving
                bool reachedContentEnd = false; // genuine no-movement bottom (auto only)
                bool alignmentLost = false;     // scrolled but frames couldn't align (overlap too small)
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var previewClock = System.Diagnostics.Stopwatch.StartNew();

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

                    if (canvas is not null)
                    {
                        var pr = canvas.Place(frame);
                        frame.Dispose();

                        // An open gap = a section was skipped and not yet re-scrolled. Warn while it's
                        // open (the fix is to scroll back over it); clear once it's bridged/refilled.
                        bool gap = canvas.HasGap;
                        if (gap && !warnedTooFast) { warnedTooFast = true; tooFast?.Invoke(true); }
                        else if (!gap && warnedTooFast) { warnedTooFast = false; tooFast?.Invoke(false); }

                        // User hit "Recover": take over scrolling and walk back over the gap to fill it.
                        if (gap && recover is not null && recover.ConsumeIfRequested())
                        {
                            await AutoRecover(canvas, screenRegion, status, preview, previewClock, ct).ConfigureAwait(false);
                            if (!canvas.HasGap && warnedTooFast) { warnedTooFast = false; tooFast?.Invoke(false); }
                        }

                        Log.Info($"Scroll frame {frames}: canvasH={canvas.Height} " +
                                 $"moved={pr.Moved} disconnected={pr.Disconnected} gap={gap}");

                        if (canvas.Height >= MaxStitchedHeight)
                            break;

                        if (frames == 1 || previewClock.ElapsedMilliseconds >= PreviewThrottleMs)
                        {
                            using var snap = canvas.Flatten();
                            if (snap is not null) PushPreview(preview, snap);
                            previewClock.Restart();
                        }
                        status(gap
                            ? $"Section skipped — scroll back over it to fill in. {canvas.Height}px"
                            : $"Scroll to capture — click Done when finished. {canvas.Height}px");

                        if (ShouldStop(ct))
                            break;
                        try { await Task.Delay(ManualPollMs, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                        continue;
                    }

                    if (stitched is null)
                    {
                        stitched = frame;
                        previous = (SD.Bitmap)frame.Clone();
                        stitchedFrames = 1;
                    }
                    else
                    {
                        // Did the page actually move? Distinguishes a paused frame (keep the anchor)
                        // from a too-fast scroll that outran the overlap (re-anchor + warn the user).
                        // ponytail: no sticky-band detection. It conflated slowly-scrolling content
                        // with fixed chrome (a slow scroll leaves most rows matching, so it flagged
                        // them "sticky" and starved the match window — the stall). The longest-run
                        // matcher already ignores sticky chrome naturally (chrome rows don't line up
                        // at the true scroll offset). Tradeoff: a genuinely sticky footer can stitch
                        // more than once; add a ShareX-style bottom-offset trim back if that bites.
                        bool framesDiffer = !ImageStitcher.FramesIdentical(previous!, frame);

                        int offset = horizontal
                            ? ImageStitcher.FindScrollOffsetHorizontal(previous!, frame)
                            : ImageStitcher.FindScrollOffset(previous!, frame);

                        Log.Info($"Scroll frame {frames}: region={frame.Width}x{frame.Height} " +
                                 $"differ={framesDiffer} offset={offset} stitchedH={stitched!.Height}");

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

                                    // A real miss: they're scrolling faster than we can grab. After a
                                    // couple in a row, tell them to slow down (one is a benign blip).
                                    tooFastStreak++;
                                    if (tooFastStreak >= TooFastStreakForWarning && !warnedTooFast)
                                    {
                                        warnedTooFast = true;
                                        tooFast?.Invoke(true);
                                    }
                                }
                                else
                                {
                                    // Paused / nothing changed: keep the anchor, clear any warning.
                                    frame.Dispose();
                                    tooFastStreak = 0;
                                    if (warnedTooFast) { warnedTooFast = false; tooFast?.Invoke(false); }
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
                            tooFastStreak = 0;
                            if (warnedTooFast) { warnedTooFast = false; tooFast?.Invoke(false); }
                            SD.Bitmap grown;
                            if (horizontal)
                            {
                                int cols = Math.Min(offset, MaxStitchedWidth - stitched.Width);
                                grown = ImageStitcher.AppendRight(stitched, frame, cols);
                            }
                            else
                            {
                                int rows = Math.Min(offset, MaxStitchedHeight - stitched.Height);
                                grown = ImageStitcher.AppendBelow(stitched, frame, rows);
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

                    // Throttle the preview: downscaling a tall stitch every frame would inflate the
                    // capture interval and shrink overlap. Always refresh on the first frame so the
                    // panel isn't blank, then at most every PreviewThrottleMs.
                    if (!manual || stitchedFrames <= 1 || previewClock.ElapsedMilliseconds >= PreviewThrottleMs)
                    {
                        PushPreview(preview, stitched);
                        previewClock.Restart();
                    }
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

                // Manual canvas: flatten to the final image (pending gaps become a marker band).
                if (canvas is not null)
                    stitched = canvas.Flatten();

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
                canvas?.Dispose();
            }
            return stitched;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Auto-recovers a skipped section: WinShot takes over and sends gentle wheel-ups, grabbing and
    /// stitching each frame, walking back up over the gap until it bridges (the floating segment
    /// merges into the canvas and <see cref="ScrollCanvas.HasGap"/> clears) — or the content stops
    /// moving (top of page / nothing more to recover). The skipped rows blit into their true place
    /// as the missing content scrolls back into view.
    /// </summary>
    private static async Task AutoRecover(ScrollCanvas canvas, SD.Rectangle region, Action<string> status,
        Action<SD.Bitmap>? preview, System.Diagnostics.Stopwatch previewClock, CancellationToken ct)
    {
        status("Recovering skipped section…");
        int stuck = 0;
        for (int step = 0; canvas.HasGap && stuck < RecoverStuckLimit && step < RecoverMaxSteps; step++)
        {
            if (ShouldStop(ct))
                break;
            ScrollUp(region);
            try { await Task.Delay(RecoverSettleMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var frame = CaptureService.CaptureScreenRegionWithoutLayeredWindows(region);
            var pr = canvas.Place(frame);
            frame.Dispose();
            stuck = pr.Moved ? 0 : stuck + 1; // content stopped scrolling → we've reached the top

            if (previewClock.ElapsedMilliseconds >= PreviewThrottleMs)
            {
                using var snap = canvas.Flatten();
                if (snap is not null) PushPreview(preview, snap);
                previewClock.Restart();
            }
            status($"Recovering skipped section… {canvas.Height}px");
        }
        status(canvas.HasGap
            ? "Couldn't reach the skipped section — keep scrolling. " + $"{canvas.Height}px"
            : $"Recovered. Scroll to capture — click Done when finished. {canvas.Height}px");
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

    /// <summary>Moves the cursor to the region center and sends one gentle wheel-UP step (one notch,
    /// so auto-recover keeps overlap between frames and doesn't open a fresh gap).</summary>
    private static void ScrollUp(SD.Rectangle region)
    {
        SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
        var input = new INPUT
        {
            type = InputMouse,
            mi = new MOUSEINPUT
            {
                mouseData = (uint)WheelDelta, // +1 notch = scroll up (content moves down)
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
