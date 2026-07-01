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
    /// <summary>Auto-scroll is paused because the cursor left the capture region
    /// (wheel input goes to whatever is under the cursor — CleanShot behaves the same).</summary>
    MoveCursorIntoArea,
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
    private const int MaxStitchedHeight = 32000;
    private const int MaxStitchedWidth = 32000;
    private const int VeryLargeThreshold = 24000;

    /// <summary>Manual mode: pause between polls. Frame hashing + matching costs ~20-60ms on
    /// top of this, so the real cadence is ~10 grabs/sec — canvas re-locking (not cadence)
    /// is what makes fast flicks recoverable.</summary>
    private const int ManualPollMs = 60;

    /// <summary>Auto mode: wait after injecting wheel input before sampling the frame.</summary>
    private const int AutoSettleMs = 350;

    /// <summary>Auto mode: re-captures while waiting for the frame to settle (animations,
    /// lazy-load) — two consecutive identical grabs = settled.</summary>
    private const int StabilizeAttempts = 4;
    private const int StabilizePollMs = 120;

    /// <summary>Auto mode: consecutive identical frames after a scroll = end of content.
    /// 3 (not 2) so a lazy-loading page gets ~2 extra seconds to produce new content.</summary>
    private const int EndOfContentStreak = 3;

    /// <summary>Auto mode: consecutive unalignable (but changing) frames before giving up.</summary>
    private const int MaxAutoMisses = 3;

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
        Func<SD.Rectangle, SD.Bitmap>? frameSource = null)
    {
        if (screenRegion.Width < 1 || screenRegion.Height < 1)
            return null;

        return await Task.Run(() =>
        {
            try
            {
                var loop = new Loop(screenRegion, presetDirection, autoScroll, status, hint, preview, ct, frameSource);
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

        private ScrollDirection? _direction;
        private readonly StitchBuffer _stitch;
        private readonly CanvasProfile _canvas = new();
        private readonly PreviewStrip? _strip;
        private readonly System.Diagnostics.Stopwatch _previewClock = System.Diagnostics.Stopwatch.StartNew();

        private FrameSignature? _prevSig;
        private SD.Bitmap? _lastFrame;      // most recent frame, kept for the final footer strip
        private SD.Bitmap? _horizontalStitched; // horizontal path stitches the old way

        private enum Track { Tracking, Reviewing, Lost }
        private Track _state = Track.Tracking;
        private ScrollHint _lastHint = ScrollHint.None;

        private int _runningFooter;
        private int _stitchedFrames;
        private double _pxPerNotch;
        private int _notches = 1; // start with one notch to calibrate px-per-notch
        private int _autoMisses;
        private int _identicalStreak;
        private bool _scrolledBeforeFrame;

        private readonly Func<SD.Rectangle, SD.Bitmap> _grab;

        public Loop(SD.Rectangle region, ScrollDirection? presetDirection, Func<bool> autoScroll,
            Action<string> status, Action<ScrollHint> hint, Action<SD.Bitmap>? preview,
            CancellationToken ct, Func<SD.Rectangle, SD.Bitmap>? frameSource)
        {
            _region = region;
            _direction = presetDirection;
            _autoScroll = autoScroll;
            _status = status;
            _hint = hint;
            _preview = preview;
            _ct = ct;
            _grab = frameSource ?? CaptureService.CaptureScreenRegionWithoutLayeredWindows;
            _stitch = new StitchBuffer(region.Width);
            if (preview is not null)
                _strip = new PreviewStrip(region.Width);
        }

        public SD.Bitmap? Run()
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            bool discard = false;
            try
            {
                while (elapsed.ElapsedMilliseconds < ManualTimeoutMs)
                {
                    if (EscPressed()) { discard = true; break; }
                    if (_ct.IsCancellationRequested) break; // Done — finalize with result

                    var frame = CaptureFrame();
                    if (_direction == ScrollDirection.Horizontal)
                    {
                        if (!ProcessHorizontal(frame)) break;
                    }
                    else
                    {
                        if (!ProcessVertical(frame)) break;
                    }

                    if (_ct.IsCancellationRequested) break;
                    if (EscPressed()) { discard = true; break; }
                    if (!Pace()) break; // injects auto-scroll or sleeps the manual poll
                }
            }
            finally
            {
                if (!discard)
                    AttachFooterOnce();
            }

            if (discard)
            {
                _lastFrame?.Dispose();
                _horizontalStitched?.Dispose();
                _stitch.Dispose();
                _strip?.Dispose();
                return null;
            }

            _lastFrame?.Dispose();
            _strip?.Dispose();
            if (_direction == ScrollDirection.Horizontal)
            {
                _stitch.Dispose();
                return _horizontalStitched;
            }
            return _stitch.Take();
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
            var sig = FrameSignature.Build(frame);
            bool auto = _scrolledBeforeFrame;

            if (_prevSig is null)
            {
                AdoptFirstFrame(frame, sig);
                return true;
            }

            if (ScrollMatcher.Identical(_prevSig, sig))
            {
                frame.Dispose();
                if (auto && ++_identicalStreak >= EndOfContentStreak)
                {
                    _status($"Reached the bottom — {_stitch.Height}px");
                    return false;
                }
                return true; // manual pause / not yet at end
            }
            _identicalStreak = 0;

            // Sticky-footer band: rows byte-identical between differing frames. Require the
            // band to carry visual information — a run of blank rows at the bottom of two
            // frames is scrolling whitespace, not a pinned footer, and must not lock in a
            // permanent exclusion band.
            int detected = ImageStitcher.DetectConstantBottomBandFromHashes(_prevSig.RowHash, sig.RowHash);
            if (detected > 0 && !BandHasContent(sig, detected))
                detected = 0;
            int footer = Math.Min(Math.Max(_runningFooter, detected), sig.Height / 3);

            // A footer can only be DETECTED from the second frame on, so the rows appended
            // before detection (frame 1's bottom, or any append made with a smaller band)
            // carry the footer misclassified as body — and they sit exactly at the stitch
            // tail. Retract them; AttachFooterOnce puts the footer back at the very end.
            if (footer > _runningFooter && _stitch.Height > 1)
            {
                int delta = Math.Min(footer - _runningFooter, _stitch.Height - 1);
                _stitch.Retract(delta);
                _canvas.Retract(delta);
                _runningFooter = footer; // recorded now so a failed match can't re-retract
            }

            // Direction auto-detection: before anything is stitched beyond frame 1, a frame
            // that won't align vertically but aligns horizontally locks horizontal mode.
            if (_direction is null && _stitchedFrames <= 1 && _lastFrame is not null)
            {
                int dv = ScrollMatcher.FindOffset(_prevSig, sig, footer);
                if (dv > 0)
                {
                    _direction = ScrollDirection.Vertical;
                    // Locate against the canvas (frame 1) rather than blindly appending: if
                    // frames scrolled past without aligning while direction was unknown, a
                    // prev-frame append here would stitch across a content gap.
                    RelockAgainstCanvas(sig, frame, footer);
                    FinishVerticalFrame(frame, sig);
                    return _stitch.Height < MaxStitchedHeight || EndAtCap();
                }
                int dh = ImageStitcher.FindScrollOffsetHorizontal(_lastFrame, frame);
                if (dh > 0)
                {
                    _direction = ScrollDirection.Horizontal;
                    _horizontalStitched = (SD.Bitmap)_lastFrame.Clone();
                    return ProcessHorizontal(frame);
                }
                FinishVerticalFrame(frame, sig);
                return true; // keep watching; direction still unknown
            }

            switch (_state)
            {
                case Track.Tracking:
                    int d = ScrollMatcher.FindOffset(_prevSig, sig, footer);
                    if (d > 0)
                    {
                        AppendVertical(frame, sig, d, footer);
                        if (auto)
                            Recalibrate(d);
                    }
                    else if (auto)
                    {
                        if (!HandleAutoMiss(frame, sig, footer))
                            return false;
                    }
                    else
                    {
                        RelockAgainstCanvas(sig, frame, footer);
                    }
                    break;

                case Track.Reviewing:
                case Track.Lost:
                    RelockAgainstCanvas(sig, frame, footer);
                    break;
            }

            FinishVerticalFrame(frame, sig);
            if (_stitch.Height >= MaxStitchedHeight)
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
            _lastFrame = frame; // owned; disposed when replaced
            PushPreview(force: true);
            _status($"1 frame — {_stitch.Height}px");
        }

        private void FinishVerticalFrame(SD.Bitmap frame, FrameSignature sig)
        {
            _prevSig = sig;
            _lastFrame?.Dispose();
            _lastFrame = frame;
            PushPreview(force: false);
        }

        private void AppendVertical(SD.Bitmap frame, FrameSignature sig, int offset, int footer)
        {
            // The newly revealed span is [h-footer-offset, h-footer). When the 32000px cap
            // clamps the row count, take rows from the TOP of that span (seam-adjacent) so
            // the last seam stays continuous.
            int newRows = Math.Min(offset, MaxStitchedHeight - _stitch.Height);
            if (newRows <= 0)
                return;
            int srcStart = sig.Height - footer - offset;
            _stitch.Append(frame, srcStart, newRows);
            _canvas.Append(sig, srcStart, newRows);
            _strip?.Append(frame, srcStart, newRows);
            _stitchedFrames++;
            _runningFooter = footer;
            _state = Track.Tracking;
            _autoMisses = 0;
            SetHint(ScrollHint.None);
            string large = _stitch.Height >= VeryLargeThreshold ? " — screenshot is very large" : "";
            _status($"{_stitchedFrames} frames — {_stitch.Height}px{large}");
        }

        /// <summary>
        /// The frame moved but wouldn't align to the previous frame. Try to find it anywhere
        /// in the stitched canvas: overhanging the end = a recovered fast scroll (append the
        /// overhang); fully inside = the user scrolled back up (append nothing, tell them);
        /// nowhere = lost, ask them to scroll back so a future frame re-locks.
        /// </summary>
        private void RelockAgainstCanvas(FrameSignature sig, SD.Bitmap frame, int footer)
        {
            var l = ScrollMatcher.LocateInCanvas(_canvas, sig, int.MaxValue, footer);
            if (l is { NewRows: > 0 } overhang)
            {
                AppendVertical(frame, sig, overhang.NewRows, footer);
            }
            else if (l is not null)
            {
                _state = Track.Reviewing;
                SetHint(ScrollHint.AlreadyCaptured);
            }
            else
            {
                // From Reviewing, a single unlockable frame usually means mid-scroll blur or
                // a blank gap; either way the user needs to slow down / come back.
                _state = Track.Lost;
                SetHint(ScrollHint.SlowDown);
            }
        }

        private bool HandleAutoMiss(SD.Bitmap frame, FrameSignature sig, int footer)
        {
            if ((ScrollMatcher.IsLowInformation(sig) || ScrollMatcher.IsLowInformation(_prevSig!)) && _pxPerNotch > 0)
            {
                // A blank stretch (whitespace page section): pixels can't measure the motion,
                // but we injected a known number of wheel notches and know px-per-notch from
                // calibration. Appending the estimate is visually exact on uniform content.
                int est = (int)Math.Round(_pxPerNotch * _notches);
                est = Math.Clamp(est, 1, Math.Max(1, sig.Height - footer - 24));
                AppendVertical(frame, sig, est, footer);
                _status($"{_stitchedFrames} frames — {_stitch.Height}px (blank stretch)");
                return true;
            }

            _autoMisses++;
            _notches = Math.Max(1, _notches / 2); // smaller steps give the matcher more overlap
            if (_autoMisses >= MaxAutoMisses)
            {
                _status($"Stopped — content wouldn't align (animation or video in the area?) — {_stitch.Height}px");
                return false;
            }
            return true;
        }

        private void Recalibrate(int measuredOffset)
        {
            double perNotch = measuredOffset / (double)Math.Max(1, _notches);
            _pxPerNotch = _pxPerNotch <= 0 ? perNotch : (_pxPerNotch + perNotch) / 2;
            // Aim each step at ~60% of the usable frame height: fast, with wide overlap.
            int usable = Math.Max(64, _region.Height - _runningFooter);
            _notches = Math.Clamp((int)Math.Round(0.6 * usable / Math.Max(1, _pxPerNotch)), 1, 8);
        }

        private bool EndAtCap()
        {
            _status($"Reached the maximum capture length — {_stitch.Height}px");
            return false;
        }

        /// <summary>Sticky footer rows were excluded from every seam; put them back exactly once.</summary>
        private void AttachFooterOnce()
        {
            if (_direction == ScrollDirection.Horizontal || _runningFooter <= 0 || _lastFrame is null)
                return;
            if (_stitch.Height + _runningFooter > MaxStitchedHeight)
                return;
            _stitch.Append(_lastFrame, _lastFrame.Height - _runningFooter, _runningFooter);
        }

        // ------------------------------------------------------------ horizontal

        /// <summary>
        /// Horizontal captures keep the simpler previous-frame exact matching (no canvas
        /// re-lock). ponytail: horizontal is the rare path; add profile matching here only
        /// if real horizontal captures (spreadsheets over RDP) turn out to need it.
        /// </summary>
        private bool ProcessHorizontal(SD.Bitmap frame)
        {
            bool auto = _scrolledBeforeFrame;
            if (_horizontalStitched is null)
            {
                _horizontalStitched = frame;
                _lastFrame = (SD.Bitmap)frame.Clone();
                _stitchedFrames = 1;
                PushPreview(force: true);
                _status($"1 frame — {_horizontalStitched.Width}px wide");
                return true;
            }

            if (ImageStitcher.FramesIdentical(_lastFrame!, frame))
            {
                frame.Dispose();
                if (auto && ++_identicalStreak >= EndOfContentStreak)
                {
                    _status($"Reached the end — {_horizontalStitched.Width}px wide");
                    return false;
                }
                return true;
            }
            _identicalStreak = 0;

            int offset = ImageStitcher.FindScrollOffsetHorizontal(_lastFrame!, frame);
            if (offset > 0)
            {
                int cols = Math.Min(offset, MaxStitchedWidth - _horizontalStitched.Width);
                if (cols > 0)
                {
                    var grown = ImageStitcher.AppendRight(_horizontalStitched, frame, cols);
                    _horizontalStitched.Dispose();
                    _horizontalStitched = grown;
                    _stitchedFrames++;
                }
                _autoMisses = 0;
                SetHint(ScrollHint.None);
                _status($"{_stitchedFrames} frames — {_horizontalStitched.Width}px wide");
            }
            else if (auto)
            {
                _autoMisses++;
                _notches = Math.Max(1, _notches / 2);
                if (_autoMisses >= MaxAutoMisses)
                {
                    _status($"Stopped — content wouldn't align — {_horizontalStitched.Width}px wide");
                    return false;
                }
            }
            else
            {
                SetHint(ScrollHint.SlowDown);
            }

            _lastFrame?.Dispose();
            _lastFrame = frame;
            PushPreview(force: false);
            return _horizontalStitched.Width < MaxStitchedWidth
                || Fail($"Reached the maximum capture width — {_horizontalStitched.Width}px wide");
        }

        private bool Fail(string message)
        {
            _status(message);
            return false;
        }

        // ------------------------------------------------------------ pacing / input

        /// <summary>End-of-iteration pacing: injects auto-scroll (cursor permitting) or sleeps
        /// the manual poll. Returns false only when the wait was cancelled.</summary>
        private bool Pace()
        {
            bool wantAuto = _autoScroll() && _state == Track.Tracking && _prevSig is not null;
            if (wantAuto && _direction != ScrollDirection.Horizontal)
                _direction ??= ScrollDirection.Vertical; // Auto-Scroll defaults to vertical

            if (wantAuto)
            {
                if (!CursorInRegion())
                {
                    SetHint(ScrollHint.MoveCursorIntoArea);
                    _scrolledBeforeFrame = false;
                    return Sleep(150);
                }
                if (_lastHint == ScrollHint.MoveCursorIntoArea)
                    SetHint(ScrollHint.None);
                SendWheel(_direction == ScrollDirection.Horizontal ? _notches : -_notches,
                    horizontal: _direction == ScrollDirection.Horizontal);
                _scrolledBeforeFrame = true;
                return Sleep(AutoSettleMs);
            }

            _scrolledBeforeFrame = false;
            return Sleep(ManualPollMs);
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
                    ? SnapshotHorizontalPreview()
                    : _strip?.SnapshotBottomWindow();
                if (snap is not null)
                    _preview(snap); // callback takes ownership
            }
            catch (Exception ex)
            {
                Log.Error("Scrolling capture: live preview failed (non-fatal)", ex);
            }
        }

        private SD.Bitmap? SnapshotHorizontalPreview()
        {
            if (_horizontalStitched is null || _horizontalStitched.Width > 8000)
                return null; // ponytail: whole-stitch downscale; skipped once it gets huge
            double scale = Math.Min(1.0, 204.0 / _horizontalStitched.Width);
            int w = Math.Max(1, (int)Math.Round(_horizontalStitched.Width * scale));
            int h = Math.Max(1, (int)Math.Round(_horizontalStitched.Height * scale));
            var thumb = new SD.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(thumb);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(_horizontalStitched, 0, 0, w, h);
            return thumb;
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

        private bool CursorInRegion() => GetCursorPos(out var p) && _region.Contains(p.X, p.Y);

        private static bool EscPressed() => (GetAsyncKeyState(VkEscape) & 0x8000) != 0;

        private static void SendWheel(int notches, bool horizontal)
        {
            var input = new INPUT
            {
                type = InputMouse,
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)(WheelDelta * notches)),
                    dwFlags = horizontal ? MouseEventFHWheel : MouseEventFWheel,
                },
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
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

        public int Height { get; private set; }

        public StitchBuffer(int width) => _width = width;

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
            int cap = Math.Min(MaxStitchedHeight, Math.Max(rows, (_buffer?.Height ?? 2048) * 2));
            var grown = new SD.Bitmap(_width, cap, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            if (_buffer is not null)
            {
                ImageStitcher.CopyRowsInto(_buffer, 0, Height, grown, 0);
                _buffer.Dispose();
            }
            _buffer = grown;
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

        /// <summary>Clone of the most recent ≤280 strip rows (the growing bottom edge).</summary>
        public SD.Bitmap? SnapshotBottomWindow()
        {
            if (_strip is null || _used == 0)
                return null;
            int h = Math.Min(_used, WindowHeight);
            var snap = new SD.Bitmap(_width, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(snap);
            g.DrawImage(_strip,
                new SD.Rectangle(0, 0, _width, h),
                new SD.Rectangle(0, _used - h, _width, h),
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
    private static extern bool GetCursorPos(out WinShot.Recording.Point32 lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
