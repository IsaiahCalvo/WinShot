using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>Persistent guidance shown in the controls bar during a scrolling capture.</summary>
public enum ScrollHint
{
    None,
    /// <summary>Frames stopped aligning (scrolled too fast / blank gap). Scrolling back up
    /// over captured content re-locks the capture and appending resumes.</summary>
    SlowDown,
    /// <summary>The live frame sits entirely over already-captured content (user scrolled
    /// back up). Nothing is appended until they scroll past the capture's end again.</summary>
    AlreadyCaptured,
}

/// <summary>Hard limits that keep an in-memory scroll capture within a predictable budget.</summary>
internal static class ScrollingCaptureLimits
{
    public const int AbsoluteMaxLength = 32000;
    public const long MaxCanvasBytes = 512L * 1024 * 1024;
    private const int BytesPerPixel = 4;

    public static int MaxLengthForCrossAxis(int crossAxisPixels)
    {
        if (crossAxisPixels <= 0) return 0;
        long memoryLimited = MaxCanvasBytes / (BytesPerPixel * (long)crossAxisPixels);
        return (int)Math.Clamp(memoryLimited, 1, AbsoluteMaxLength);
    }

    public static bool IsRegionSafe(SD.Rectangle region)
        => region.Width > 0 && region.Height > 0 &&
           (long)region.Width * region.Height * BytesPerPixel <= MaxCanvasBytes;
}

/// <summary>
/// Repeatedly captures a fixed screen region and stitches the frames into one tall image
/// (or one wide image for <see cref="ScrollDirection.Horizontal"/>). The user scrolls the
/// content themselves; when <paramref name="autoScroll"/> reads true the service also injects
/// wheel input (only while the cursor is inside the region, mirroring CleanShot's Auto-Scroll).
/// Matching runs exact row hashing first and falls back to gradient-profile correlation
/// (<see cref="ScrollMatcher"/>), so browsers that re-rasterize text on scroll still align.
/// When alignment is lost, the frame is re-located against the WHOLE stitched canvas — so
/// scrolling back up re-syncs the capture instead of silently dropping the span, and scrolling
/// over already-captured content never duplicates rows. Sticky footers are detected and
/// excluded from every seam, then re-attached once at the end.
/// The loop runs on the thread pool; all callbacks fire on thread-pool threads.
/// </summary>
public static class ScrollingCaptureService
{
    private const int VeryLargeThreshold = 24000;

    /// <summary>Manual mode: pause between polls. A sparse-hash pre-check makes paused frames
    /// nearly free, so the loop only pays full signature+matching cost while content is
    /// actually moving — real cadence ~15-25 grabs/sec. Higher cadence = more overlap per
    /// frame = comfortable-speed scrolling tracks continuously instead of needing recovery.</summary>
    private const int ManualPollMs = 30;

    /// <summary>Auto mode: wait after injecting wheel input before sampling the frame.</summary>
    private const int AutoSettleMs = 350;

    /// <summary>Auto mode: re-captures while waiting for the frame to settle (animations,
    /// lazy-load) — two consecutive identical grabs = settled.</summary>
    private const int StabilizeAttempts = 4;
    private const int StabilizePollMs = 120;

    /// <summary>Auto mode: consecutive identical frames after a scroll = end of content.
    /// 3 (not 2) so a lazy-loading page gets ~2 extra seconds to produce new content.</summary>
    private const int EndOfContentStreak = 3;

    private const int WheelDelta = 120;
    private const int ManualTimeoutMs = 10 * 60 * 1000;
    private const int PreviewThrottleMs = 150;

    /// <summary>
    /// Runs a scrolling capture over <paramref name="screenRegion"/> (physical screen
    /// coordinates). <paramref name="presetDirection"/> null = auto-detect from the first
    /// aligned movement. Returns the stitched bitmap, or null when cancelled (Esc) or when
    /// nothing was captured. Cancelling <paramref name="ct"/> finalizes with the result so
    /// far (the Done button); Esc discards.
    /// </summary>
    public static async Task<SD.Bitmap?> RunAsync(SD.Rectangle screenRegion,
        ScrollDirection? presetDirection, Func<bool> autoScroll, Action<string> status,
        Action<ScrollHint> hint, CancellationToken ct, Action<SD.Bitmap>? preview = null,
        Func<SD.Rectangle, SD.Bitmap>? frameSource = null, bool paneScoped = false)
    {
        if (screenRegion.Width < 1 || screenRegion.Height < 1)
            return null;
        if (!ScrollingCaptureLimits.IsRegionSafe(screenRegion))
        {
            status("Selected region is too large to capture safely.");
            return null;
        }

        return await Task.Run(() =>
        {
            try
            {
                var loop = new Loop(screenRegion, presetDirection, autoScroll, status, hint, preview, ct, frameSource, paneScoped);
                return loop.Run();
            }
            catch (Exception ex)
            {
                Log.Error("Scrolling capture failed mid-run", ex);
                return null;
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    // =====================================================================
    //  Capture loop
    // =====================================================================

    private sealed class Loop
    {
        private readonly SD.Rectangle _region;
        private readonly Func<bool> _autoScroll;
        private readonly Action<string> _status;
        private readonly Action<ScrollHint> _hint;
        private readonly Action<SD.Bitmap>? _preview;
        private readonly CancellationToken _ct;
        /// <summary>The region was already narrowed to the scrolling pane by the wheel
        /// probe — the sidebar band/compositor heuristics must stay OFF: inside a real
        /// pane they misread wide blank margins and rails as "sidebars" and paint over
        /// content (observed live 2026-08-05: left=64/right=568 claimed inside a clean
        /// 1440px chat pane, gray-filling the right side).</summary>
        private readonly bool _paneScoped;
        /// <summary>Per-column "did this column ever change between consecutive accepted
        /// frames" over the WHOLE capture. Whole-run evidence is what the old two-frame
        /// sidebar detector lacked: a column that never changed across every scroll step
        /// is a static rail (or blank margin) beyond doubt, so de-duplicating it at finish
        /// cannot eat content the way per-pair guesses did.</summary>
        private int[]? _columnChangeCounts;
        private int _columnPairs;
        private ulong[]? _prevColumnHashes;

        private ScrollDirection? _direction;
        private readonly StitchBuffer _stitch;
        private readonly CanvasProfile _canvas = new();
        private readonly PreviewStrip? _strip;
        private readonly HorizontalStitchBuffer _horizontal;
        private readonly HorizontalCanvasProfile _horizontalCanvas = new();
        private readonly HorizontalPreviewStrip? _horizontalStrip;
        private readonly int _verticalLimit;
        private readonly int _horizontalLimit;
        private readonly System.Diagnostics.Stopwatch _previewClock = System.Diagnostics.Stopwatch.StartNew();

        private FrameSignature? _prevSig;
        private FrameSignature? _prevMatchSig;
        private SD.Bitmap? _lastFrame;      // most recent frame, kept for the final footer strip

        private enum Track { Tracking, Reviewing, Lost }
        private Track _state = Track.Tracking;
        private ScrollHint _lastHint = ScrollHint.None;
        /// <summary>Canvas row of the previous frame's top while Reviewing (-1 = unknown).
        /// Lets Reviewing traverse blank stretches by dead-reckoning verified frame-to-frame
        /// moves from the last absolute lock, instead of demanding an absolute re-lock
        /// against featureless canvas rows every step.</summary>
        private int _reviewPos = -1;
        private int _horizontalReviewPos = -1;

        private int _runningHeader;
        private int _runningFooter;
        // Footer growth stabilizer: a single coincidental detection spike must not permanently
        // shrink the matchable area (observed creep 73→334px on a 1005px region — a third of
        // every frame written off as "footer"). Grow only on agreement across consecutive
        // differing pairs; decay back when detections run consistently lower.
        private int _footerGrowCandidate;
        private int _footerGrowStreak;
        private int _footerShrinkStreak;
        private int _footerShrinkMax;
        private int _verticalLeftMatchInset;
        private int _verticalRightMatchInset;
        private int _runningLeftBand;
        private int _runningRightBand;
        private int _stitchedFrames;
        private double _pxPerNotch;
        private int _notches = 1; // start with one notch to calibrate px-per-notch
        private int _autoMisses;
        private int _identicalStreak;
        private bool _scrolledBeforeFrame;
        private long _framesSeen;

        // Auto-scroll input ladder. Injected wheel input is routed like hardware wheel — to
        // the window under the CURSOR (or the focus window, per the "scroll inactive
        // windows" setting) — and is silently discarded for elevated windows (UIPI), so it
        // can stop working mid-run when another window drifts over the cursor. When
        // injected scrolling produces NO movement we escalate: wheel input → WM_MOUSEWHEEL
        // posted straight to the window under the region (focus/routing-independent) →
        // activate + PageDown. "No movement" is only trusted as end-of-content after the
        // remaining methods have been probed too.
        private enum ScrollMethod { WheelInput, WheelMessage, PageKey }
        private ScrollMethod _method = ScrollMethod.WheelInput;
        private bool _methodProducedMovement;
        private bool _everMoved;      // any method ever scrolled this page
        private int _noEffectStreak;
        private int _healSteps;       // auto-recovery scrolls while Lost/Reviewing
        private int _lastSentNotches; // notches of the most recent injected step (dead-reckoning)
        private ulong _lastProbe;     // sparse hash of the last manual-mode frame (pause fast path)

        private readonly Func<SD.Rectangle, SD.Bitmap> _grab;

        public Loop(SD.Rectangle region, ScrollDirection? presetDirection, Func<bool> autoScroll,
            Action<string> status, Action<ScrollHint> hint, Action<SD.Bitmap>? preview,
            CancellationToken ct, Func<SD.Rectangle, SD.Bitmap>? frameSource, bool paneScoped = false)
        {
            _region = region;
            _paneScoped = paneScoped;
            _direction = presetDirection;
            _autoScroll = autoScroll;
            _status = status;
            _hint = hint;
            _preview = preview;
            _ct = ct;
            _grab = frameSource ?? CaptureService.CaptureScreenRegionWithoutLayeredWindows;
            _verticalLimit = ScrollingCaptureLimits.MaxLengthForCrossAxis(region.Width);
            _horizontalLimit = ScrollingCaptureLimits.MaxLengthForCrossAxis(region.Height);
            _stitch = new StitchBuffer(region.Width, _verticalLimit);
            _horizontal = new HorizontalStitchBuffer(region.Height, _horizontalLimit);
            if (preview is not null)
            {
                _strip = new PreviewStrip(region.Width);
                _horizontalStrip = new HorizontalPreviewStrip(region.Height);
            }
        }

        public SD.Bitmap? Run()
        {
            Log.Info($"Scroll: start region={_region.Width}x{_region.Height} at ({_region.X},{_region.Y}) " +
                     $"direction={_direction?.ToString() ?? "auto-detect"}");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            bool discard = false;
            try
            {
                while (elapsed.ElapsedMilliseconds < ManualTimeoutMs)
                {
                    if (EscPressed()) { discard = true; break; }
                    if (_ct.IsCancellationRequested) break; // Done — finalize with result

                    var frame = CaptureFrame();

                    // Manual-mode fast path: a 16-row sparse hash spots the (very common)
                    // "nothing moved since last poll" case for ~0.5ms instead of a full
                    // signature build + match — that's what lets the poll run at 30ms.
                    bool skip = false;
                    if (!_scrolledBeforeFrame && (_prevSig is not null || _horizontal.Width > 0))
                    {
                        ulong probe = ScrollMatcher.SparseProbe(frame);
                        if (probe == _lastProbe)
                        {
                            frame.Dispose();
                            skip = true;
                        }
                        else
                        {
                            _lastProbe = probe;
                        }
                    }

                    if (!skip)
                    {
                        if (_direction == ScrollDirection.Horizontal)
                        {
                            if (!ProcessHorizontal(frame)) break;
                        }
                        else
                        {
                            if (!ProcessVertical(frame)) break;
                        }
                    }

                    if (_ct.IsCancellationRequested) break;
                    if (EscPressed()) { discard = true; break; }
                    if (!Pace()) break; // injects auto-scroll or sleeps the manual poll
                }
            }
            finally
            {
                if (!discard)
                    AttachTrailingBandOnce();
            }

            Log.Info($"Scroll: finished — {( _direction == ScrollDirection.Horizontal ? $"{_horizontal.Width}px wide" : $"{_stitch.Height}px tall")} " +
                     $"frames={_stitchedFrames} seen={_framesSeen} bands={_runningHeader}/{_runningFooter}/" +
                     $"{_runningLeftBand}/{_runningRightBand} method={_method} state={_state}{(discard ? " (discarded)" : "")}");

            if (discard)
            {
                _lastFrame?.Dispose();
                _horizontal.Dispose();
                _stitch.Dispose();
                _strip?.Dispose();
                _horizontalStrip?.Dispose();
                return null;
            }

            _strip?.Dispose();
            _horizontalStrip?.Dispose();
            if (_direction == ScrollDirection.Horizontal)
            {
                _lastFrame?.Dispose();
                _stitch.Dispose();
                return _horizontal.Take();
            }
            _horizontal.Dispose();
            var vertical = _stitch.Take();
            if (vertical is not null)
                DeduplicateStaticRails(vertical);
            _lastFrame?.Dispose();
            return vertical;
        }

        // ------------------------------------------------------------ frames

        private SD.Bitmap CaptureFrame()
        {
            if (!_scrolledBeforeFrame)
                return _grab(_region);

            // After injected scroll input: wait for the region to settle (two consecutive
            // identical grabs) so animations/lazy-load aren't measured mid-flight.
            var current = _grab(_region);
            for (int attempt = 1; attempt < StabilizeAttempts && !_ct.IsCancellationRequested; attempt++)
            {
                Sleep(StabilizePollMs);
                var next = _grab(_region);
                bool settled = ImageStitcher.FramesIdentical(current, next);
                current.Dispose();
                current = next;
                if (settled)
                    break;
            }
            return current;
        }

        // ------------------------------------------------------------ vertical

        /// <summary>Handles one vertical (or not-yet-locked) frame. Returns false to end the capture.</summary>
        private bool ProcessVertical(SD.Bitmap frame)
        {
            _framesSeen++;
            var sig = FrameSignature.Build(frame);
            FrameSignature matchSig = sig;
            bool auto = _scrolledBeforeFrame;

            if (_prevSig is null)
            {
                AdoptFirstFrame(frame, sig);
                return true;
            }

            if (ScrollMatcher.Identical(_prevSig, sig))
            {
                frame.Dispose();
                if (!auto || _state != Track.Tracking)
                    return true; // manual pause / transient during auto-recovery

                // We injected scroll input and NOTHING moved. Two very different causes:
                // the page is at its end, or the injected input never reached the content
                // (Win11 24H2 SendInput-wheel bug can even START working and then die
                // mid-run — an unfocused window losing hover routing looks identical).
                // So "bottom" is only declared after the REMAINING input methods have been
                // probed and none of them moves the page either.
                if (_methodProducedMovement)
                {
                    if (++_identicalStreak >= EndOfContentStreak && !TryNextMethod())
                    {
                        Log.Info($"Scroll: end — reached bottom at {_stitch.Height}px ({_stitchedFrames} frames, method={_method})");
                        _status($"Reached the bottom — {_stitch.Height}px");
                        return false;
                    }
                }
                else if (++_noEffectStreak >= 2 && !TryNextMethod())
                {
                    Log.Info($"Scroll: end — no input method moved the content ({_stitch.Height}px, everMoved={_everMoved})");
                    _status(_everMoved
                        ? $"Reached the bottom — {_stitch.Height}px"
                        : $"Nothing to scroll (or content can't be scrolled here) — {_stitch.Height}px");
                    return false;
                }
                return true;
            }
            _identicalStreak = 0;

            // Sidebars can dominate row matching even though the document beneath them moves.
            // Same-position evidence is only a provisional crop; a measured vertical shift
            // must confirm a stationary region before it becomes part of the running layout.
            StationaryViewportRegions provisionalRegions = _lastFrame is null
                ? default
                : StationaryViewportDetector.Detect(_lastFrame, frame);
            int provisionalLeft = Math.Max(_verticalLeftMatchInset, provisionalRegions.LeftMatchInset);
            int provisionalRight = Math.Max(_verticalRightMatchInset, provisionalRegions.RightMatchInset);
            FrameSignature previousMatch = _prevSig;
            FrameSignature currentMatch = sig;
            if (_lastFrame is not null &&
                (provisionalLeft > FrameSignature.SideMargin(sig.Width) ||
                 provisionalRight > FrameSignature.SideMargin(sig.Width)))
            {
                previousMatch = FrameSignature.Build(_lastFrame, provisionalLeft, provisionalRight);
                currentMatch = FrameSignature.Build(frame, provisionalLeft, provisionalRight);
            }
            int matchedOffset = ScrollMatcher.FindOffset(previousMatch, currentMatch,
                _runningHeader, _runningFooter);
            if (matchedOffset == 0 && _lastFrame is not null)
            {
                FixedBands provisional = FixedRegionDetector.DetectVertical(_lastFrame, frame);
                int legacyFooter = ImageStitcher.DetectConstantBottomBandFromHashes(
                    _prevSig.RowHash, sig.RowHash);
                if (legacyFooter > 0 && !BandHasContent(sig, legacyFooter))
                    legacyFooter = 0;
                int provisionalHeader = Math.Min(Math.Max(_runningHeader, provisional.Leading), sig.Height / 3);
                int provisionalFooter = Math.Min(Math.Max(_runningFooter,
                    Math.Max(Math.Max(provisional.Trailing, legacyFooter), provisionalRegions.BottomOverlay)),
                    sig.Height / 3);
                matchedOffset = ScrollMatcher.FindOffset(previousMatch, currentMatch,
                    provisionalHeader, provisionalFooter);
            }
            int header = _runningHeader;
            int footer = _runningFooter;
            if (matchedOffset > 0 && _lastFrame is not null)
            {
                FixedBands refined = FixedRegionDetector.DetectVertical(_lastFrame, frame, matchedOffset);
                StationaryViewportRegions refinedRegions = StationaryViewportDetector.Detect(
                    _lastFrame, frame, matchedOffset);
                int candidateHeader = Math.Min(Math.Max(_runningHeader, refined.Leading), sig.Height / 3);
                int candidateFooter = StabilizedFooter(
                    Math.Max(refined.Trailing, refinedRegions.BottomOverlay), sig.Height / 3);
                // A canvas re-lock or a few polluted early pairs can push the first verified
                // offset past frame 1 (seen live: re-lock on frame 2 meant the sidebar never
                // locked and kept hijacking the matcher). Allow the lock on any early frame;
                // only the frame-1 case can also rebuild the canvas profile.
                bool canLockSidebars = !_paneScoped && _stitchedFrames <= SidebarLockMaxFrames &&
                    _verticalLeftMatchInset == 0 && _verticalRightMatchInset == 0;
                // Bands are immutable once locked. (A "grow on later agreement" variant
                // ratcheted to 60% of the window live: while the user pauses, consecutive
                // near-identical pairs measure everything as stationary — do NOT retry it.)
                // Sidebar RENDER bands are gone for good: the compositor that repainted
                // "stationary sidebars" misfired on every real capture it touched (gray
                // fills over content, mid-panel slices). Multi-pane selections are handled
                // by pane-scoping (Alt-click a pane); match INSETS below survive — they
                // only exclude static columns from row hashing, never touch pixels.
                int candidateLeftMatch = _verticalLeftMatchInset > 0
                    ? _verticalLeftMatchInset
                    : canLockSidebars ? refinedRegions.LeftMatchInset : 0;
                int candidateRightMatch = _verticalRightMatchInset > 0
                    ? _verticalRightMatchInset
                    : canLockSidebars ? refinedRegions.RightMatchInset : 0;
                FrameSignature refinedPrevious = _prevSig;
                FrameSignature refinedCurrent = sig;
                if (candidateLeftMatch > FrameSignature.SideMargin(sig.Width) ||
                    candidateRightMatch > FrameSignature.SideMargin(sig.Width))
                {
                    refinedPrevious = FrameSignature.Build(_lastFrame,
                        candidateLeftMatch, candidateRightMatch);
                    refinedCurrent = FrameSignature.Build(frame,
                        candidateLeftMatch, candidateRightMatch);
                }
                int verifiedOffset = ScrollMatcher.FindOffset(refinedPrevious, refinedCurrent,
                    candidateHeader, candidateFooter);
                if (verifiedOffset > 0)
                {
                    matchedOffset = verifiedOffset;
                    header = candidateHeader;
                    footer = candidateFooter;
                    bool insetsChanged =
                        _verticalLeftMatchInset != candidateLeftMatch ||
                        _verticalRightMatchInset != candidateRightMatch;
                    if (_stitchedFrames == 1 && insetsChanged)
                    {
                        // Rebuild the one-frame canvas profile with the inset signature so
                        // re-locking matches what future frames hash.
                        _canvas.Retract(_canvas.Height);
                        _canvas.Append(refinedPrevious, 0, refinedPrevious.Height);
                        _prevMatchSig = refinedPrevious;
                    }
                    _verticalLeftMatchInset = candidateLeftMatch;
                    _verticalRightMatchInset = candidateRightMatch;
                    matchSig = refinedCurrent;
                }
            }
            if (matchSig == sig &&
                (_verticalLeftMatchInset > FrameSignature.SideMargin(sig.Width) ||
                 _verticalRightMatchInset > FrameSignature.SideMargin(sig.Width)) &&
                _lastFrame is not null)
            {
                matchSig = FrameSignature.Build(frame,
                    _verticalLeftMatchInset, _verticalRightMatchInset);
            }
            _runningHeader = header;

            // A footer can only be DETECTED from the second frame on, so the rows appended
            // before detection (frame 1's bottom, or any append made with a smaller band)
            // carry the footer misclassified as body — and they sit exactly at the stitch
            // tail. Retract them; AttachFooterOnce puts the footer back at the very end.
            if (footer > _runningFooter && _stitch.Height > 1)
            {
                int delta = Math.Min(footer - _runningFooter, _stitch.Height - 1);
                _stitch.Retract(delta);
                _canvas.Retract(delta);
                _strip?.Retract(delta);
                _runningFooter = footer; // recorded now so a failed match can't re-retract
            }

            // Direction auto-detection: before anything is stitched beyond frame 1, a frame
            // that won't align vertically but aligns horizontally locks horizontal mode.
            if (_direction is null && _stitchedFrames <= 1 && _lastFrame is not null)
            {
                int dv = matchedOffset;
                if (dv > 0)
                {
                    _direction = ScrollDirection.Vertical;
                    // Locate against the canvas (frame 1) rather than blindly appending: if
                    // frames scrolled past without aligning while direction was unknown, a
                    // prev-frame append here would stitch across a content gap.
                    RelockAgainstCanvas(matchSig, frame, header, footer);
                    FinishVerticalFrame(frame, sig, matchSig);
                    return _stitch.Height < _verticalLimit || EndAtCap();
                }
                FixedBands horizontalBands = FixedRegionDetector.DetectHorizontal(_lastFrame, frame);
                int dh = ImageStitcher.FindScrollOffsetHorizontal(_lastFrame, frame,
                    horizontalBands.Leading, horizontalBands.Trailing);
                if (dh > 0)
                {
                    _direction = ScrollDirection.Horizontal;
                    AdoptFirstHorizontalFrame(_lastFrame);
                    _stitch.Dispose();
                    return ProcessHorizontal(frame);
                }
                FinishVerticalFrame(frame, sig, matchSig);
                return true; // keep watching; direction still unknown
            }

            switch (_state)
            {
                case Track.Tracking:
                    int d = matchedOffset;
                    if (d > 0)
                    {
                        AppendVertical(frame, matchSig, d, header, footer);
                        if (auto)
                            Recalibrate(d);
                    }
                    else if (auto)
                    {
                        if (!HandleAutoMiss(frame, matchSig, header, footer))
                            return false;
                    }
                    else
                    {
                        RelockAgainstCanvas(matchSig, frame, header, footer);
                    }
                    break;

                case Track.Reviewing:
                    ProcessReviewing(frame, matchSig, header, footer, auto);
                    break;

                case Track.Lost:
                    RelockAgainstCanvas(matchSig, frame, header, footer);
                    break;
            }

            FinishVerticalFrame(frame, sig, matchSig);
            if (_stitch.Height >= _verticalLimit)
                return EndAtCap();
            return true;
        }

        private void AdoptFirstFrame(SD.Bitmap frame, FrameSignature sig)
        {
            _stitch.Append(frame, 0, frame.Height);
            _canvas.Append(sig, 0, sig.Height);
            _strip?.Append(frame, 0, frame.Height);
            _stitchedFrames = 1;
            _prevSig = sig;
            _prevMatchSig = sig;
            _lastFrame = frame; // owned; disposed when replaced
            PushPreview(force: true);
            _status($"1 frame — {_stitch.Height}px");
        }

        private void FinishVerticalFrame(SD.Bitmap frame, FrameSignature sig, FrameSignature matchSig)
        {
            _prevSig = sig;
            _prevMatchSig = matchSig;
            _lastFrame?.Dispose();
            _lastFrame = frame;
            PushPreview(force: false);
        }

        /// <summary>
        /// Finish-time rail de-duplication on whole-run evidence: a contiguous edge cluster
        /// of columns that never changed between ANY consecutive accepted frames is a static
        /// side rail (or blank margin). It appears once (the stitch's first viewport already
        /// has it) and the repeats below are painted over with its dominant color — the
        /// CleanShot look. Content is safe by construction: scrolled content changes its
        /// columns on every step, so its columns can never qualify.
        /// </summary>
        private void DeduplicateStaticRails(SD.Bitmap vertical)
        {
            if (_paneScoped || _columnChangeCounts is null || _stitchedFrames < 3 ||
                _columnPairs < 3 || vertical.Height <= _region.Height)
                return;
            int width = _columnChangeCounts.Length;
            if (width != vertical.Width)
                return;

            // ponytail: tolerate up to 3 flickering columns inside a cluster (a rail's
            // hover glow or a 1px separator line breathing) — beyond that it isn't static.
            int right = EdgeStaticCluster(fromLeft: false);
            int left = EdgeStaticCluster(fromLeft: true);
            int cap = width * 2 / 5;
            if (right < 64 || right > cap) right = 0;
            if (left < 64 || left > cap) left = 0;
            if (left == 0 && right == 0)
                return;

            SD.Bitmap? leftSnap = null, rightSnap = null;
            try
            {
                int viewport = Math.Min(_region.Height, vertical.Height);
                if (left > 0)
                    leftSnap = vertical.Clone(new SD.Rectangle(0, 0, left, viewport),
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                if (right > 0)
                    rightSnap = vertical.Clone(new SD.Rectangle(width - right, 0, right, viewport),
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                StationaryViewportCompositor.RemoveRepeatedSidebars(vertical, leftSnap, rightSnap, viewport);
                Log.Info($"Scroll: static rails de-duplicated (left={left} right={right}, whole-run evidence)");
            }
            catch (Exception ex)
            {
                Log.Error("Static rail de-duplication failed (non-fatal)", ex);
            }
            finally
            {
                leftSnap?.Dispose();
                rightSnap?.Dispose();
            }
        }

        private int EdgeStaticCluster(bool fromLeft)
        {
            int[] counts = _columnChangeCounts!;
            int width = counts.Length;
            // A rail's columns change RARELY (its content refreshes a few times — hover
            // states, contextual panels updating as the chat streams); scrolled content's
            // columns change on essentially every step. Frequency separates them where the
            // old "never changed" rule refused rails that blinked even once.
            int staticCeiling = Math.Max(1, _columnPairs / 5);
            int flicker = 0, span = 0;
            for (int i = 0; i < width; i++)
            {
                int x = fromLeft ? i : width - 1 - i;
                if (counts[x] > staticCeiling)
                {
                    if (++flicker > 3)
                        break;
                    continue; // may bridge interior busy columns, but never ends the cluster
                }
                span = i + 1;
            }
            return span;
        }

        private const int FooterSlack = 5;
        /// <summary>Latest stitched frame at which stationary side match-insets may still
        /// lock. ponytail: fixed small window — past this the stitch has too much
        /// full-width history for an inset change to leave matching consistent.</summary>
        private const int SidebarLockMaxFrames = 6;

        /// <summary>
        /// Footer band update with hysteresis. Growth needs the same detected value (±slack)
        /// on two consecutive differing pairs; a run of clearly-lower detections shrinks the
        /// band back (shrinking just widens future matching — no retraction needed).
        /// </summary>
        private int StabilizedFooter(int detected, int cap)
        {
            detected = Math.Min(detected, cap);
            // Initial adoption is immediate: the tail retract heals frame 1's misclassified
            // rows only while they are still the stitch tail. Hysteresis applies to GROWTH
            // beyond an established footer — that is where the runaway creep lived.
            if (_runningFooter == 0)
                return detected;
            if (detected > _runningFooter + FooterSlack)
            {
                _footerShrinkStreak = 0;
                if (_footerGrowStreak >= 1 && Math.Abs(detected - _footerGrowCandidate) <= FooterSlack)
                {
                    _footerGrowStreak = 0;
                    return detected;
                }
                _footerGrowCandidate = detected;
                _footerGrowStreak = 1;
                return _runningFooter;
            }
            _footerGrowStreak = 0;
            if (detected < _runningFooter - FooterSlack)
            {
                _footerShrinkMax = _footerShrinkStreak == 0 ? detected : Math.Max(_footerShrinkMax, detected);
                if (++_footerShrinkStreak >= 3)
                {
                    _footerShrinkStreak = 0;
                    return _footerShrinkMax;
                }
                return _runningFooter;
            }
            _footerShrinkStreak = 0;
            return Math.Max(_runningFooter, detected);
        }

        private void AppendVertical(SD.Bitmap frame, FrameSignature sig, int offset, int header, int footer)
        {
            // The newly revealed span is [h-footer-offset, h-footer). When the 32000px cap
            // clamps the row count, take rows from the TOP of that span (seam-adjacent) so
            // the last seam stays continuous.
            int newRows = Math.Min(offset, _verticalLimit - _stitch.Height);
            if (newRows <= 0)
                return;
            int srcStart = Math.Max(header, sig.Height - footer - offset);

            // Heal floating bottom chrome (pinned input bars, scroll-to-bottom buttons —
            // static elements ABOVE the contiguous footer band, which the footer model cannot
            // represent): earlier appends stamped them just above the seam; this frame has
            // TRUE content at that alignment, so overwrite the bottom of the overlap with it.
            // Scroll is top-down, so only the final frame's stamp survives — once, at the
            // true bottom. Top rows stay append-only: that is what keeps sticky headers out.
            // ponytail: fixed bottom-quarter heal zone; a pixel-static mask is the upgrade
            // path if floating chrome ever sits above H/4.
            int heal = Math.Min(Math.Min(srcStart - header, sig.Height / 4), _stitch.Height);
            if (heal > 0)
            {
                _stitch.Overwrite(frame, srcStart - heal, heal, _stitch.Height - heal);
                _canvas.Overwrite(sig, srcStart - heal, heal, _stitch.Height - heal);
            }

            _stitch.Append(frame, srcStart, newRows);
            _canvas.Append(sig, srcStart, newRows);
            _strip?.Append(frame, srcStart, newRows);
            if (!_paneScoped)
            {
                ulong[] cols = ImageStitcher.ComputeColumnHashes(frame);
                if (_prevColumnHashes is not null && _prevColumnHashes.Length == cols.Length)
                {
                    _columnChangeCounts ??= new int[cols.Length];
                    _columnPairs++;
                    for (int x = 0; x < cols.Length; x++)
                    {
                        if (cols[x] != _prevColumnHashes[x])
                            _columnChangeCounts[x]++;
                    }
                }
                _prevColumnHashes = cols;
            }
            _stitchedFrames++;
            _runningFooter = footer;
            _state = Track.Tracking;
            _reviewPos = -1;
            _autoMisses = 0;
            if (_scrolledBeforeFrame)
            {
                _methodProducedMovement = true; // this input method verifiably scrolls this page
                _everMoved = true;
                _noEffectStreak = 0;
            }
            _healSteps = 0;
            SetHint(ScrollHint.None);
            Log.Info($"Scroll: +{newRows}px (offset={offset} footer={footer}) total={_stitch.Height}px frames={_stitchedFrames}");
            string large = _stitch.Height >= VeryLargeThreshold ? " — screenshot is very large" : "";
            _status($"{_stitchedFrames} frames — {_stitch.Height}px{large}");
        }

        /// <summary>Escalates to the next auto-scroll input method; false when exhausted.</summary>
        private bool TryNextMethod()
        {
            // PageDown only makes sense vertically; the wheel-message tier covers horizontal.
            ScrollMethod? next = _method switch
            {
                ScrollMethod.WheelInput => ScrollMethod.WheelMessage,
                ScrollMethod.WheelMessage when _direction != ScrollDirection.Horizontal => ScrollMethod.PageKey,
                _ => null,
            };
            if (next is null)
                return false;
            Log.Info($"Scroll: input method {_method} had no effect — switching to {next}");
            _method = next.Value;
            _methodProducedMovement = false;
            _noEffectStreak = 0;
            _identicalStreak = 0;
            _pxPerNotch = 0; // recalibrate for the new method
            _notches = 1;
            return true;
        }

        /// <summary>
        /// The frame moved but wouldn't align to the previous frame. Try to find it anywhere
        /// in the stitched canvas: overhanging the end = a recovered fast scroll (append the
        /// overhang); fully inside = the user scrolled back up (append nothing, tell them);
        /// nowhere = lost, ask them to scroll back so a future frame re-locks.
        /// </summary>
        private void RelockAgainstCanvas(FrameSignature sig, SD.Bitmap frame, int header, int footer)
        {
            var l = ScrollMatcher.LocateInCanvas(_canvas, sig, int.MaxValue, header, footer);
            if (l is { NewRows: > 0 } overhang)
            {
                Log.Info($"Scroll: re-locked at canvas row {overhang.Position} (+{overhang.NewRows}px recovered)");
                AppendVertical(frame, sig, overhang.NewRows, header, footer);
            }
            else if (l is not null)
            {
                if (_state != Track.Reviewing)
                    Log.Info($"Scroll: frame is inside already-captured content (canvas row {l.Value.Position}) — reviewing");
                _state = Track.Reviewing;
                _reviewPos = l.Value.Position;
                SetHint(ScrollHint.AlreadyCaptured);
            }
            else
            {
                // From Reviewing, a single unlockable frame usually means mid-scroll blur or
                // a blank gap; either way the user needs to slow down / come back.
                if (_state != Track.Lost)
                    Log.Info($"Scroll: lost tracking at {_stitch.Height}px — waiting for the user to scroll back");
                _state = Track.Lost;
                _reviewPos = -1;
                SetHint(ScrollHint.SlowDown);
            }
        }

        /// <summary>
        /// Reviewing = the frame sits over already-captured content at a KNOWN canvas row.
        /// Prefer verified frame-to-frame moves to advance that position (dead-reckoning) —
        /// they keep working across blank stretches where an absolute canvas re-lock cannot
        /// verify anything. The moment the tracked position passes the canvas end, append
        /// the overhang and resume normal tracking.
        /// </summary>
        private void ProcessReviewing(SD.Bitmap frame, FrameSignature sig, int header, int footer, bool auto)
        {
            int d = _reviewPos >= 0 ? ScrollMatcher.FindOffset(_prevMatchSig!, sig, header, footer) : 0;
            if (d > 0)
            {
                _reviewPos += d;
                _healSteps = 0; // verified progress — never time out mid-traversal
                if (!TryFinishReview(frame, sig, header, footer))
                    SetHint(ScrollHint.AlreadyCaptured);
                return;
            }

            if (auto && _reviewPos >= 0 && _pxPerNotch > 0 && _lastSentNotches > 0)
            {
                // No verified relative offset — typical mid-gap, where the two frames hold
                // content slivers that don't overlap each other. We injected a KNOWN number
                // of notches and px-per-notch is calibrated, so advance the tracked position
                // by the estimate. Bookkeeping only; any residual error lands inside the
                // blank stretch (invisible), and verified matches re-anchor as soon as
                // shared content returns.
                _reviewPos += (int)Math.Round(_pxPerNotch * _lastSentNotches);
                TryFinishReview(frame, sig, header, footer);
                return;
            }

            RelockAgainstCanvas(sig, frame, header, footer); // refresh the absolute lock / degrade honestly
        }

        /// <summary>Appends the overhang and resumes tracking once the dead-reckoned review
        /// position passes the canvas end. True when review finished.</summary>
        private bool TryFinishReview(SD.Bitmap frame, FrameSignature sig, int header, int footer)
        {
            int frameRows = sig.Height - header - footer;
            int overhang = _reviewPos + header + frameRows - _canvas.Height;
            if (overhang <= 0)
                return false;
            Log.Info($"Scroll: review reached the capture end (pos {_reviewPos}) — appending {overhang}px");
            AppendVertical(frame, sig, Math.Min(overhang, frameRows), header, footer);
            _reviewPos = -1;
            return true;
        }

        private bool HandleAutoMiss(SD.Bitmap frame, FrameSignature sig, int header, int footer)
        {
            if ((ScrollMatcher.IsLowInformation(sig) || ScrollMatcher.IsLowInformation(_prevMatchSig!)) && _pxPerNotch > 0)
            {
                // A blank stretch (whitespace page section): pixels can't measure the motion,
                // but we injected a known number of wheel notches and know px-per-notch from
                // calibration. Appending the estimate is visually exact on uniform content.
                int est = (int)Math.Round(_pxPerNotch * _notches);
                est = Math.Clamp(est, 1, Math.Max(1, sig.Height - header - footer - 24));
                Log.Info($"Scroll: blank stretch — appending calibrated estimate of {est}px");
                AppendVertical(frame, sig, est, header, footer);
                _status($"{_stitchedFrames} frames — {_stitch.Height}px (blank stretch)");
                return true;
            }

            // Same recovery as manual mode: locate the frame in the stitched canvas. This
            // repairs the case where the PREVIOUS step's motion couldn't be verified (e.g.
            // sampled mid-animation): the previous frame became the match anchor without
            // its rows being appended, so a naive prev-frame append would leave a hole.
            RelockAgainstCanvas(sig, frame, header, footer);
            if (_state == Track.Tracking)
                return true; // recovered — the overhang was appended, nothing lost

            _autoMisses++;
            _notches = Math.Max(1, _notches / 2); // smaller steps give the matcher more overlap
            Log.Info($"Scroll: auto miss {_autoMisses} (frames differ, no canvas lock) — notches now {_notches}, state={_state}");
            return true; // the Lost/Reviewing auto-heal in Pace() takes it from here
        }

        private void Recalibrate(int measuredOffset)
        {
            ScrollCalibrationResult calibration = ScrollCalibration.Update(_pxPerNotch,
                measuredOffset, _notches, _region.Height, _runningHeader, _runningFooter);
            _pxPerNotch = calibration.PixelsPerNotch;
            if (_method == ScrollMethod.PageKey)
            {
                // One PageDown is the smallest key step; stop if its measured movement cannot
                // preserve the matcher's required overlap.
                _notches = calibration.CanPreserveOverlap ? 1 : 0;
                return;
            }
            _notches = calibration.Notches;
        }

        private bool EndAtCap()
        {
            string reason = _verticalLimit < ScrollingCaptureLimits.AbsoluteMaxLength
                ? "safe memory limit"
                : "maximum capture length";
            _status($"Reached the {reason} — {_stitch.Height}px");
            return false;
        }

        /// <summary>Trailing fixed chrome was excluded from every seam; put it back exactly once.</summary>
        private void AttachTrailingBandOnce()
        {
            if (_lastFrame is null)
                return;
            if (_direction == ScrollDirection.Horizontal)
            {
                if (_runningRightBand <= 0 || _horizontal.Width + _runningRightBand > _horizontalLimit)
                    return;
                _horizontal.Append(_lastFrame, _lastFrame.Width - _runningRightBand, _runningRightBand);
                return;
            }
            if (_runningFooter <= 0)
                return;
            if (_stitch.Height + _runningFooter > _verticalLimit)
                return;
            _stitch.Append(_lastFrame, _lastFrame.Height - _runningFooter, _runningFooter);
        }

        // ------------------------------------------------------------ horizontal

        /// <summary>Horizontal capture mirrors the vertical recovery state machine: append
        /// aligned movement, re-lock anywhere in the captured canvas after a miss, and treat
        /// reverse movement as review rather than duplicating columns.</summary>
        private bool ProcessHorizontal(SD.Bitmap frame)
        {
            _framesSeen++;
            bool auto = _scrolledBeforeFrame;
            ulong[] columns = ImageStitcher.ComputeColumnHashes(frame);
            if (_horizontal.Width == 0)
            {
                AdoptFirstHorizontalFrame(frame, columns);
                _lastFrame = frame;
                return true;
            }

            if (ImageStitcher.FramesIdentical(_lastFrame!, frame))
            {
                frame.Dispose();
                if (!auto || _state != Track.Tracking)
                    return true;

                if (_methodProducedMovement)
                {
                    if (++_identicalStreak >= EndOfContentStreak && !TryNextMethod())
                    {
                        _status($"Reached the end — {_horizontal.Width}px wide");
                        return false;
                    }
                }
                else if (++_noEffectStreak >= 2 && !TryNextMethod())
                {
                    _status(_everMoved
                        ? $"Reached the end — {_horizontal.Width}px wide"
                        : $"Nothing to scroll (or content can't be scrolled here) — {_horizontal.Width}px wide");
                    return false;
                }
                return true;
            }
            _identicalStreak = 0;

            int offset = ImageStitcher.FindScrollOffsetHorizontal(_lastFrame!, frame,
                _runningLeftBand, _runningRightBand);
            if (offset == 0)
            {
                FixedBands provisional = FixedRegionDetector.DetectHorizontal(_lastFrame!, frame);
                int provisionalLeft = Math.Min(Math.Max(_runningLeftBand, provisional.Leading), frame.Width / 3);
                int provisionalRight = Math.Min(Math.Max(_runningRightBand, provisional.Trailing), frame.Width / 3);
                offset = ImageStitcher.FindScrollOffsetHorizontal(_lastFrame!, frame,
                    provisionalLeft, provisionalRight);
            }
            int left = _runningLeftBand;
            int right = _runningRightBand;
            if (offset > 0)
            {
                FixedBands refined = FixedRegionDetector.DetectHorizontal(_lastFrame!, frame, offset);
                left = Math.Min(Math.Max(_runningLeftBand, refined.Leading), frame.Width / 3);
                right = Math.Min(Math.Max(_runningRightBand, refined.Trailing), frame.Width / 3);
                offset = ImageStitcher.FindScrollOffsetHorizontal(_lastFrame!, frame, left, right);
            }
            _runningLeftBand = left;
            if (right > _runningRightBand && _horizontal.Width > 1)
            {
                int delta = Math.Min(right - _runningRightBand, _horizontal.Width - 1);
                _horizontal.Retract(delta);
                _horizontalCanvas.Retract(delta);
                _horizontalStrip?.Retract(delta);
                _runningRightBand = right;
            }
            if (_state == Track.Tracking && offset > 0)
            {
                AppendHorizontal(frame, columns, offset, left, right);
                if (auto)
                    RecalibrateHorizontal(offset);
            }
            else if (_state == Track.Reviewing)
            {
                ProcessHorizontalReviewing(frame, columns, left, right, auto);
            }
            else
            {
                RelockHorizontal(frame, columns, left, right);
                if (auto && _state != Track.Tracking)
                {
                    _autoMisses++;
                    _notches = Math.Max(1, _notches / 2);
                }
            }

            _lastFrame?.Dispose();
            _lastFrame = frame;
            PushPreview(force: false);
            if (_horizontal.Width >= _horizontalLimit)
            {
                string reason = _horizontalLimit < ScrollingCaptureLimits.AbsoluteMaxLength
                    ? "safe memory limit"
                    : "maximum capture width";
                return Fail($"Reached the {reason} — {_horizontal.Width}px wide");
            }
            return true;
        }

        private void AdoptFirstHorizontalFrame(SD.Bitmap frame, ulong[]? columns = null)
        {
            columns ??= ImageStitcher.ComputeColumnHashes(frame);
            int count = Math.Min(frame.Width, _horizontalLimit);
            _horizontal.Append(frame, 0, count);
            _horizontalCanvas.Append(columns, 0, count);
            _horizontalStrip?.Append(frame, 0, count);
            _stitchedFrames = 1;
            PushPreview(force: true);
            _status($"1 frame — {_horizontal.Width}px wide");
        }

        private void AppendHorizontal(SD.Bitmap frame, ulong[] columns, int offset, int left, int right)
        {
            int newColumns = Math.Min(offset, _horizontalLimit - _horizontal.Width);
            if (newColumns <= 0) return;
            int sourceX = Math.Max(left, frame.Width - right - offset);
            _horizontal.Append(frame, sourceX, newColumns);
            _horizontalCanvas.Append(columns, sourceX, newColumns);
            _horizontalStrip?.Append(frame, sourceX, newColumns);
            _stitchedFrames++;
            _state = Track.Tracking;
            _horizontalReviewPos = -1;
            _autoMisses = 0;
            if (_scrolledBeforeFrame)
            {
                _methodProducedMovement = true;
                _everMoved = true;
                _noEffectStreak = 0;
            }
            _healSteps = 0;
            SetHint(ScrollHint.None);
            Log.Info($"Scroll horizontal: +{newColumns}px total={_horizontal.Width}px frames={_stitchedFrames}");
            string large = _horizontal.Width >= VeryLargeThreshold ? " — screenshot is very large" : "";
            _status($"{_stitchedFrames} frames — {_horizontal.Width}px wide{large}");
        }

        private void RelockHorizontal(SD.Bitmap frame, ulong[] columns, int left, int right)
        {
            var location = _horizontalCanvas.Locate(columns, left, right);
            if (location is { NewColumns: > 0 } overhang)
            {
                Log.Info($"Scroll horizontal: re-locked at column {overhang.Position} (+{overhang.NewColumns}px)");
                AppendHorizontal(frame, columns, overhang.NewColumns, left, right);
            }
            else if (location is not null)
            {
                Log.Info($"Scroll horizontal: reviewing captured column {location.Value.Position}");
                _state = Track.Reviewing;
                _horizontalReviewPos = location.Value.Position;
                SetHint(ScrollHint.AlreadyCaptured);
            }
            else
            {
                _state = Track.Lost;
                _horizontalReviewPos = -1;
                SetHint(ScrollHint.SlowDown);
            }
        }

        private void ProcessHorizontalReviewing(SD.Bitmap frame, ulong[] columns, int left, int right, bool auto)
        {
            int offset = ImageStitcher.FindScrollOffsetHorizontal(_lastFrame!, frame, left, right);
            if (offset > 0 && _horizontalReviewPos >= 0)
            {
                _horizontalReviewPos += offset;
                _healSteps = 0;
                if (!TryFinishHorizontalReview(frame, columns, left, right))
                    SetHint(ScrollHint.AlreadyCaptured);
                return;
            }

            if (auto && _horizontalReviewPos >= 0 && _pxPerNotch > 0 && _lastSentNotches > 0)
            {
                _horizontalReviewPos += (int)Math.Round(_pxPerNotch * _lastSentNotches);
                TryFinishHorizontalReview(frame, columns, left, right);
                return;
            }
            RelockHorizontal(frame, columns, left, right);
        }

        private bool TryFinishHorizontalReview(SD.Bitmap frame, ulong[] columns, int left, int right)
        {
            int bodyColumns = columns.Length - left - right;
            int overhang = _horizontalReviewPos + left + bodyColumns - _horizontalCanvas.Width;
            if (overhang <= 0) return false;
            AppendHorizontal(frame, columns, Math.Min(overhang, bodyColumns), left, right);
            _horizontalReviewPos = -1;
            return true;
        }

        private void RecalibrateHorizontal(int measuredOffset)
        {
            ScrollCalibrationResult calibration = ScrollCalibration.Update(_pxPerNotch,
                measuredOffset, _notches, _region.Width, _runningLeftBand, _runningRightBand);
            _pxPerNotch = calibration.PixelsPerNotch;
            _notches = calibration.Notches;
        }

        private bool Fail(string message)
        {
            _status(message);
            return false;
        }

        // ------------------------------------------------------------ pacing / input

        /// <summary>End-of-iteration pacing: injects auto-scroll (cursor permitting) or sleeps
        /// the manual poll. Returns false to end the capture (auto-recovery gave up).</summary>
        private bool Pace()
        {
            bool hasFrame = _direction == ScrollDirection.Horizontal
                ? _horizontal.Width > 0
                : _prevSig is not null;
            bool wantAuto = _autoScroll() && hasFrame;
            if (wantAuto && _direction != ScrollDirection.Horizontal)
                _direction ??= ScrollDirection.Vertical; // Auto-Scroll defaults to vertical

            if (wantAuto)
            {
                if (_state == Track.Tracking)
                {
                    if (_notches <= 0)
                    {
                        Log.Info("Scroll: auto-scroll stopped because one notch would leave too little overlap");
                        _status("Stopped — fixed UI leaves too little safe overlap for auto-scroll; continue manually");
                        return false;
                    }
                    SendScrollStep(up: false);
                }
                else
                {
                    // Auto-heal: the automated version of "scroll back up to re-sync".
                    // Lost → nudge UP toward captured content until the canvas re-locks;
                    // Reviewing (inside captured content) → nudge DOWN toward the end.
                    if (++_healSteps > 12)
                    {
                        Log.Info($"Scroll: auto-recovery gave up after {_healSteps} nudges (state={_state}) at {_stitch.Height}px");
                        _status($"Stopped — couldn't re-sync with the page — {_stitch.Height}px");
                        return false;
                    }
                    Log.Info($"Scroll: auto-heal nudge {(_state == Track.Lost ? "up" : "down")} ({_healSteps}/12)");
                    SendScrollStep(up: _state == Track.Lost, singleNotch: true);
                }
                _scrolledBeforeFrame = true;
                return Sleep(AutoSettleMs);
            }

            _scrolledBeforeFrame = false;
            return Sleep(ManualPollMs);
        }

        /// <summary>Injects one auto-scroll step using the current input-ladder method.
        /// <paramref name="up"/> reverses direction (recovery nudges);
        /// <paramref name="singleNotch"/> forces the gentlest step.</summary>
        private void SendScrollStep(bool up, bool singleNotch = false)
        {
            bool horizontal = _direction == ScrollDirection.Horizontal;
            int notches = singleNotch ? 1 : _notches;
            _lastSentNotches = up ? 0 : notches; // dead-reckoning only advances downward
            int sign = horizontal ? 1 : -1;
            if (up) sign = -sign;
            int delta = WheelDelta * notches * sign;
            int centerX = _region.X + _region.Width / 2, centerY = _region.Y + _region.Height / 2;
            switch (_method)
            {
                case ScrollMethod.WheelInput:
                    // Cursor DIP: wheel routing follows the cursor, but a cursor parked in the
                    // region triggers hover effects that end up in the capture. So teleport to
                    // the region center just for the wheel tick and return immediately — the
                    // page gets a mouseleave, hover styles clear, and the frame is only grabbed
                    // after the ~350ms settle. If the user moved the mouse in the same instant,
                    // leave their cursor alone rather than fight them.
                    GetCursorPos(out var orig);
                    SetCursorPos(centerX, centerY);
                    SendWheelInput(delta, horizontal);
                    if (GetCursorPos(out var now) && now.X == centerX && now.Y == centerY)
                        SetCursorPos(orig.X, orig.Y);
                    break;

                case ScrollMethod.WheelMessage:
                    // Post the wheel message straight to the window under the region center —
                    // bypasses input routing entirely (no cursor/focus dependence). Chrome,
                    // Edge and Electron handle it on their deepest child (the render widget),
                    // which is exactly what WindowFromPoint returns.
                    IntPtr hwnd = WindowFromPoint(new WinShot.Recording.Point32 { X = centerX, Y = centerY });
                    if (hwnd != IntPtr.Zero)
                    {
                        nuint wParam = (nuint)(((uint)(short)delta) << 16);
                        nint lParam = (centerY << 16) | (centerX & 0xFFFF);
                        PostMessage(hwnd, horizontal ? WM_MOUSEHWHEEL : WM_MOUSEWHEEL, wParam, lParam);
                    }
                    else
                    {
                        SendWheelInput(delta, horizontal); // nothing under the point; best effort
                    }
                    break;

                case ScrollMethod.PageKey:
                    // Last resort, and keyboard input needs focus: bring the window under
                    // the region to the foreground before the key tap (ShareX v15's rescue).
                    // If focus can't be acquired, DON'T type — a stray PageDown landing in
                    // whatever app is focused would scroll/act on the user's other work.
                    IntPtr target = WindowFromPoint(new WinShot.Recording.Point32 { X = centerX, Y = centerY });
                    if (target != IntPtr.Zero)
                    {
                        IntPtr root = GetAncestor(target, GA_ROOT);
                        SetForegroundWindow(root);
                        if (GetForegroundWindow() == root)
                            SendKeyTap(up ? VkPrior : VkNext);
                        else
                            Log.Info("Scroll: PageKey skipped — couldn't focus the target window");
                    }
                    break;
            }
        }

        private bool Sleep(int ms)
        {
            try
            {
                Task.Delay(ms, _ct).GetAwaiter().GetResult();
                return true;
            }
            catch (OperationCanceledException)
            {
                return true; // Done clicked — the loop head handles finalize
            }
        }

        private void SetHint(ScrollHint hint)
        {
            if (hint == _lastHint) return;
            Log.Info($"Scroll: hint → {hint}");
            _lastHint = hint;
            _hint(hint);
        }

        private void PushPreview(bool force)
        {
            if (_preview is null)
                return;
            if (!force && _previewClock.ElapsedMilliseconds < PreviewThrottleMs)
                return;
            _previewClock.Restart();
            try
            {
                SD.Bitmap? snap = _direction == ScrollDirection.Horizontal
                    ? _horizontalStrip?.SnapshotWhole()
                    : _strip?.SnapshotWhole();
                if (snap is not null)
                    _preview(snap); // callback takes ownership
            }
            catch (Exception ex)
            {
                Log.Error("Scrolling capture: live preview failed (non-fatal)", ex);
            }
        }

        private static bool BandHasContent(FrameSignature sig, int band)
        {
            int informative = 0;
            for (int y = sig.Height - band; y < sig.Height; y++)
            {
                if (sig.Energy[y] > 1.5f && ++informative >= 3)
                    return true;
            }
            return false;
        }

        private static bool EscPressed() => (GetAsyncKeyState(VkEscape) & 0x8000) != 0;

        private static void SendWheelInput(int delta, bool horizontal)
        {
            var input = new INPUT
            {
                type = InputMouse,
                U = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = unchecked((uint)delta),
                        dwFlags = horizontal ? MouseEventFHWheel : MouseEventFWheel,
                    },
                },
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static void SendKeyTap(ushort vk)
        {
            var inputs = new[]
            {
                new INPUT { type = InputKeyboard, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } } },
                new INPUT { type = InputKeyboard, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KeyEventFKeyUp } } },
            };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }
    }

    // =====================================================================
    //  Amortized stitch buffer (audit: AppendBelow re-copied the whole stitch
    //  on every frame — O(n²) over a capture; this grows in chunks instead)
    // =====================================================================

    private sealed class StitchBuffer : IDisposable
    {
        private SD.Bitmap? _buffer;
        private readonly int _width;
        private readonly int _maxHeight;

        public int Height { get; private set; }

        public StitchBuffer(int width, int maxHeight)
        {
            _width = width;
            _maxHeight = maxHeight;
        }

        public void Append(SD.Bitmap frame, int srcY, int rows)
        {
            if (rows <= 0) return;
            EnsureCapacity(Height + rows);
            ImageStitcher.CopyRowsInto(frame, srcY, rows, _buffer!, Height);
            Height += rows;
        }

        /// <summary>Drops the bottom rows (a newly detected sticky footer that was appended
        /// as body before it could be detected). The buffer rows are simply overwritten later.</summary>
        public void Retract(int rows) => Height = Math.Max(0, Height - rows);

        /// <summary>Overwrites already-appended rows [destY, destY+rows) with frame rows
        /// [srcY, srcY+rows) — heals floating bottom chrome stamped by earlier appends.</summary>
        public void Overwrite(SD.Bitmap frame, int srcY, int rows, int destY)
        {
            if (rows <= 0 || _buffer is null || destY < 0 || destY + rows > Height)
                return;
            ImageStitcher.CopyRowsInto(frame, srcY, rows, _buffer, destY);
        }

        /// <summary>Returns the stitched image trimmed to its used rows (ownership transfers)
        /// or null when nothing was appended.</summary>
        public SD.Bitmap? Take()
        {
            if (_buffer is null || Height == 0)
                return null;
            if (_buffer.Height == Height)
            {
                var exact = _buffer;
                _buffer = null;
                return exact;
            }
            var trimmed = new SD.Bitmap(_width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            ImageStitcher.CopyRowsInto(_buffer, 0, Height, trimmed, 0);
            _buffer.Dispose();
            _buffer = null;
            return trimmed;
        }

        public void Dispose()
        {
            _buffer?.Dispose();
            _buffer = null;
        }

        private void EnsureCapacity(int rows)
        {
            if (_buffer is not null && rows <= _buffer.Height)
                return;
            int current = _buffer?.Height ?? Math.Min(2048, _maxHeight);
            int cap = Math.Min(_maxHeight, Math.Max(rows, checked(current * 2)));
            var grown = new SD.Bitmap(_width, cap, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            if (_buffer is not null)
            {
                ImageStitcher.CopyRowsInto(_buffer, 0, Height, grown, 0);
                _buffer.Dispose();
            }
            _buffer = grown;
        }
    }

    private readonly record struct HorizontalLocation(int Position, int NewColumns);

    /// <summary>Exact column profile of the complete horizontal canvas. Tail-overlap matches
    /// are preferred so a fast forward step can append; otherwise a full in-canvas match
    /// identifies reverse/review movement without duplicating pixels.</summary>
    private sealed class HorizontalCanvasProfile
    {
        private const int MinimumOverlap = 24;
        private readonly List<ulong> _columns = new();

        public int Width => _columns.Count;

        public void Append(ulong[] source, int sourceX, int count)
        {
            int end = Math.Min(source.Length, sourceX + count);
            for (int x = Math.Max(0, sourceX); x < end; x++)
                _columns.Add(source[x]);
        }

        public void Retract(int columns)
        {
            int remove = Math.Min(Math.Max(0, columns), _columns.Count);
            if (remove > 0)
                _columns.RemoveRange(_columns.Count - remove, remove);
        }

        public HorizontalLocation? Locate(ulong[] frame, int leftBand, int rightBand)
        {
            int bodyLength = frame.Length - leftBand - rightBand;
            if (bodyLength < MinimumOverlap || Width < MinimumOverlap)
                return null;

            // First seek an overlap at the right edge. This is the only kind of match that
            // can safely extend the image after a missed intermediate frame.
            int firstTail = Math.Max(0, Width - leftBand - bodyLength + 1);
            HorizontalLocation? tail = null;
            int bestTailOverlap = 0;
            for (int position = firstTail; position + leftBand <= Width - MinimumOverlap; position++)
            {
                int overlap = Width - (position + leftBand);
                if (overlap <= bestTailOverlap || overlap > bodyLength ||
                    !Matches(frame, leftBand, position + leftBand, overlap)) continue;
                tail = new HorizontalLocation(position, bodyLength - overlap);
                bestTailOverlap = overlap;
            }
            if (tail is not null)
                return tail;

            // Otherwise the frame is wholly inside captured content. Search from the newest
            // edge backwards so repeated spreadsheet bands do not jump to an older copy.
            int latest = Width - leftBand - bodyLength;
            for (int position = latest; position >= 0; position--)
            {
                if (Matches(frame, leftBand, position + leftBand, bodyLength))
                    return new HorizontalLocation(position, 0);
            }
            return null;
        }

        private bool Matches(ulong[] frame, int frameX, int canvasX, int count)
        {
            if (frameX < 0 || canvasX < 0 || count < MinimumOverlap ||
                canvasX + count > Width || frameX + count > frame.Length)
                return false;
            for (int x = 0; x < count; x++)
            {
                if (_columns[canvasX + x] != frame[frameX + x])
                    return false;
            }
            return true;
        }
    }

    /// <summary>Chunk-growing horizontal bitmap buffer. Growth is geometric, so a capture
    /// copies its existing canvas O(log n) times rather than once for every frame.</summary>
    internal sealed class HorizontalStitchBuffer : IDisposable
    {
        private SD.Bitmap? _buffer;
        private readonly int _height;
        private readonly int _maxWidth;

        public int Width { get; private set; }
        internal int GrowthCount { get; private set; }

        public HorizontalStitchBuffer(int height, int maxWidth)
        {
            _height = height;
            _maxWidth = maxWidth;
        }

        public void Append(SD.Bitmap frame, int sourceX, int columns)
        {
            columns = Math.Min(columns, _maxWidth - Width);
            if (columns <= 0) return;
            EnsureCapacity(Width + columns);
            ImageStitcher.CopyColumnsInto(frame, sourceX, columns, _buffer!, Width);
            Width += columns;
        }

        public void Retract(int columns) => Width = Math.Max(0, Width - columns);

        public SD.Bitmap? Take()
        {
            if (_buffer is null || Width == 0) return null;
            if (_buffer.Width == Width)
            {
                var exact = _buffer;
                _buffer = null;
                return exact;
            }
            var trimmed = new SD.Bitmap(Width, _height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            ImageStitcher.CopyColumnsInto(_buffer, 0, Width, trimmed, 0);
            _buffer.Dispose();
            _buffer = null;
            return trimmed;
        }

        public void Dispose()
        {
            _buffer?.Dispose();
            _buffer = null;
        }

        private void EnsureCapacity(int columns)
        {
            if (_buffer is not null && columns <= _buffer.Width) return;
            int current = _buffer?.Width ?? Math.Min(2048, _maxWidth);
            int capacity = Math.Min(_maxWidth, Math.Max(columns, checked(current * 2)));
            var grown = new SD.Bitmap(capacity, _height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            if (_buffer is not null)
            {
                ImageStitcher.CopyColumnsInto(_buffer, 0, Width, grown, 0);
                _buffer.Dispose();
            }
            _buffer = grown;
            GrowthCount++;
        }
    }

    /// <summary>Incremental low-resolution horizontal preview; appended columns are scaled
    /// once, avoiding repeated downscales of the full growing capture.</summary>
    private sealed class HorizontalPreviewStrip : IDisposable
    {
        private const int MaxPreviewWidth = 204;
        private const int MaxPreviewHeight = 280;
        private readonly int _height;
        private readonly double _scale;
        private SD.Bitmap? _strip;
        private int _used;

        public HorizontalPreviewStrip(int regionHeight)
        {
            _height = Math.Min(MaxPreviewHeight, regionHeight);
            _scale = _height / (double)regionHeight;
        }

        public void Append(SD.Bitmap frame, int sourceX, int columns)
        {
            if (columns <= 0) return;
            int scaled = Math.Max(1, (int)Math.Round(columns * _scale));
            EnsureCapacity(_used + scaled);
            using var graphics = SD.Graphics.FromImage(_strip!);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.DrawImage(frame,
                new SD.Rectangle(_used, 0, scaled, _height),
                new SD.Rectangle(sourceX, 0, columns, frame.Height),
                SD.GraphicsUnit.Pixel);
            _used += scaled;
        }

        public void Retract(int sourceColumns)
        {
            int scaled = Math.Max(1, (int)Math.Round(sourceColumns * _scale));
            _used = Math.Max(0, _used - scaled);
        }

        public SD.Bitmap? SnapshotWhole()
        {
            if (_strip is null || _used == 0) return null;
            double scale = Math.Min(1.0, MaxPreviewWidth / (double)_used);
            int width = Math.Max(1, (int)Math.Round(_used * scale));
            int height = Math.Max(1, (int)Math.Round(_height * scale));
            var snapshot = new SD.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = SD.Graphics.FromImage(snapshot);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.DrawImage(_strip,
                new SD.Rectangle(0, 0, width, height),
                new SD.Rectangle(0, 0, _used, _height),
                SD.GraphicsUnit.Pixel);
            return snapshot;
        }

        public void Dispose()
        {
            _strip?.Dispose();
            _strip = null;
        }

        private void EnsureCapacity(int columns)
        {
            if (_strip is not null && columns <= _strip.Width) return;
            int capacity = Math.Max(columns, (_strip?.Width ?? 1024) * 2);
            var grown = new SD.Bitmap(capacity, _height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            if (_strip is not null)
            {
                using var graphics = SD.Graphics.FromImage(grown);
                graphics.DrawImage(_strip,
                    new SD.Rectangle(0, 0, _used, _height),
                    new SD.Rectangle(0, 0, _used, _height),
                    SD.GraphicsUnit.Pixel);
                _strip.Dispose();
            }
            _strip = grown;
        }
    }

    // =====================================================================
    //  Incremental preview strip (audit: downscaling the whole growing stitch
    //  per refresh stalled the capture thread; this scales only appended rows)
    // =====================================================================

    private sealed class PreviewStrip : IDisposable
    {
        private const int MaxStripWidth = 204;
        private const int WindowHeight = 280;

        private SD.Bitmap? _strip;
        private readonly int _width;
        private readonly double _scale;
        private int _used;

        public PreviewStrip(int regionWidth)
        {
            _width = Math.Min(MaxStripWidth, regionWidth); // never upscale a narrow region
            _scale = _width / (double)regionWidth;
        }

        public void Append(SD.Bitmap frame, int srcY, int rows)
        {
            if (rows <= 0) return;
            int scaled = Math.Max(1, (int)Math.Round(rows * _scale));
            EnsureCapacity(_used + scaled);
            using var g = SD.Graphics.FromImage(_strip!);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(frame,
                new SD.Rectangle(0, _used, _width, scaled),
                new SD.Rectangle(0, srcY, frame.Width, rows),
                SD.GraphicsUnit.Pixel);
            _used += scaled;
        }

        public void Retract(int sourceRows)
        {
            int scaled = Math.Max(1, (int)Math.Round(sourceRows * _scale));
            _used = Math.Max(0, _used - scaled);
        }

        /// <summary>The WHOLE growing capture, downscaled to fit the preview box — CleanShot
        /// shows the entire stitched image shrinking as it grows, and that overview (not just
        /// the newest edge) is what tells the user whether the capture is keeping up.</summary>
        public SD.Bitmap? SnapshotWhole()
        {
            if (_strip is null || _used == 0)
                return null;
            double s = Math.Min(1.0, WindowHeight / (double)_used);
            int w = Math.Max(1, (int)Math.Round(_width * s));
            int h = Math.Max(1, (int)Math.Round(_used * s));
            var snap = new SD.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(snap);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(_strip,
                new SD.Rectangle(0, 0, w, h),
                new SD.Rectangle(0, 0, _width, _used),
                SD.GraphicsUnit.Pixel);
            return snap;
        }

        public void Dispose()
        {
            _strip?.Dispose();
            _strip = null;
        }

        private void EnsureCapacity(int rows)
        {
            if (_strip is not null && rows <= _strip.Height)
                return;
            int cap = Math.Max(rows, (_strip?.Height ?? 1024) * 2);
            var grown = new SD.Bitmap(_width, cap, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            if (_strip is not null)
            {
                using var g = SD.Graphics.FromImage(grown);
                g.DrawImage(_strip, new SD.Rectangle(0, 0, _width, _used),
                    new SD.Rectangle(0, 0, _width, _used), SD.GraphicsUnit.Pixel);
                _strip.Dispose();
            }
            _strip = grown;
        }
    }

    // =====================================================================
    //  Win32
    // =====================================================================

    private const int VkEscape = 0x1B;
    private const ushort VkNext = 0x22;  // PageDown
    private const ushort VkPrior = 0x21; // PageUp
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseEventFWheel = 0x0800;
    private const uint MouseEventFHWheel = 0x1000;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSEHWHEEL = 0x020E;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinShot.Recording.Point32 lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(WinShot.Recording.Point32 point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
