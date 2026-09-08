using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Native lightweight region/window selector with two modes:
///   • <see cref="SelectorMode.Area"/> — draw a rectangular marquee, then MOVE/RESIZE it
///     (8 handles) and confirm with Enter or double-click. No window highlighting.
///   • <see cref="SelectorMode.Window"/> — highlight the window under the cursor and click
///     to capture that whole window. No marquee.
///
/// The crosshair + magnifier loupe follow the cursor continuously (the surface repaints on
/// every mouse-move). DPI correctness: one 1:1 overlay surface per monitor (primary monitor
/// = this Form/coordinator, others = <see cref="SelectorPane"/> children), and all selection
/// math is anchored to GetCursorPos (true physical px). Screen-freeze uses a desktop snapshot;
/// when disabled, native translucent surfaces leave the live desktop visible.
/// </summary>
public sealed class FastRegionSelectorDialog : WF.Form
{
    public enum SelectorMode { Area, Window }

    private const int DragThresholdPx = 4;

    /// <summary>
    /// Smallest drag a release is allowed to capture outright. Mouse-up confirm removed the
    /// step that used to catch a slip: with only a one-pixel floor, a press that travelled
    /// the 4 px drag threshold and let go wrote, copied and carded a 4x4 image. Anything
    /// under this falls back to the adjustable selection, so a wobble costs a keystroke
    /// instead of a capture the user has to notice, dismiss and redo.
    /// </summary>
    private const int MinConfirmDragPx = 8;

    /// <summary>Where the left button went down, for telling a click from a drag at release.</summary>
    private SD.Point _pressScreen;

    /// <summary>True while the left button is held on the detected element's shutter pill.</summary>
    private bool _pressedShutter;

    /// <summary>Set by <c>Complete</c> and cleared by <c>ResetForUse</c>. A pooled selector is
    /// parked, not destroyed, and keeps keyboard focus, so keys keep arriving after the
    /// session is over.</summary>
    private bool _sessionEnded;

    /// <summary>This session spent one of the first-run hint's five showings and has not
    /// written the new count to disk yet.</summary>
    private bool _hintSessionUnsaved;

    /// <summary>True for the sessions the first-run hint is shown in.</summary>
    private bool _hintEnabled;

    /// <summary>
    /// The edge of the pending selection the pointer is on, and the 0..1 amount its accent
    /// thickening is eased to. Clicking an edge snaps it up to <see cref="MarqueeEdge.ReachPx"/>
    /// away and the selector has no undo, so the edge has to say it is a target BEFORE the
    /// press: it thickens under the pointer and the cursor becomes a sizing arrow.
    /// </summary>
    private MarqueeSide _hoverEdge = MarqueeSide.None;
    private double _edgeGlow;
    private IDisposable? _edgeTween;

    /// <summary>The edge the left button went down on; the snap fires at release over the
    /// same edge, exactly as Cancel / Done / Pad do.</summary>
    private MarqueeSide _pressedEdge = MarqueeSide.None;

    /// <summary>True when the pointer has moved far enough from the press to be a drag.</summary>
    private bool HasTravelled(SD.Point at) =>
        Math.Abs(at.X - _pressScreen.X) >= DragThresholdPx ||
        Math.Abs(at.Y - _pressScreen.Y) >= DragThresholdPx;

    /// <summary>Peak alpha of the accent wash over a hover-targeted window.</summary>
    private const int WindowWashAlpha = 40;
    private static readonly SD.Color Accent = ThemePalette.Accent;

    private SD.Rectangle _vs = CaptureService.VirtualScreen;
    private SD.Rectangle _monitorBounds;
    private SettingsService? _settings;
    private SelectorOptions _options;
    private SelectorMode _mode = SelectorMode.Area;
    private bool _paneHover;
    /// <summary>Alt-hover resolves the deepest UIA ELEMENT (normal captures) instead of the
    /// scrollable pane (scrolling captures).</summary>
    private bool _elementGranularity;
    private SD.Rectangle? _hoverPane;
    private readonly RectangleTween _hoverTween = new();
    /// <summary>Per-edge slack below which a re-detection counts as the same rect.</summary>
    private const int WobbleTolerancePx = 8;
    /// <summary>Every rect under the cursor that could plausibly be "the thing", smallest
    /// first. The wheel walks it, so a detector that picked the right area at the wrong depth
    /// costs one notch instead of a hand-drawn rectangle.</summary>
    private List<SD.Rectangle> _hoverLadder = new();
    private int _ladderIndex = -1;
    /// <summary>Distinct edge coordinates from the snap list, sorted, for marquee snapping.</summary>
    private int[] _snapEdgesX = Array.Empty<int>();
    private int[] _snapEdgesY = Array.Empty<int>();
    /// <summary>How close a dragged edge must come to a real one before it clicks onto it.</summary>
    private const int MarqueeSnapPx = 6;
    /// <summary>A rect the user explicitly picked with the wheel; held while the cursor
    /// stays inside it so the per-move resolution doesn't snap back to the innermost.</summary>
    private SD.Rectangle? _wheelPinned;
    /// <summary>True when the selection came from clicking the highlighted pane rather than
    /// a hand-drawn marquee — only pane clicks opt into probe refinement downstream; a rect
    /// the user deliberately drew is captured exactly as drawn.</summary>
    public bool SelectedByPaneClick { get; private set; }
    private List<WindowInfo> _windows = new();
    private List<SnapRect> _snapRects = new();
    private readonly List<SelectorPane> _panes = new();
    private SD.Point _dragStartScreen;     // physical screen px (GetCursorPos)
    private SD.Point _currentScreen;       // physical screen px (GetCursorPos)
    private SD.Point _lastFollowScreen;    // last cursor pos the crosshair/loupe were painted at
    private bool _dragging;                // drawing a fresh marquee
    private bool _dragMoved;
    private SD.Rectangle? _pendingScreen;  // the adjustable selection (Area mode), screen px
    private int _hoverBarButton = -1;      // 0 = Cancel, 1 = Done while hovered, else -1
    private int _pressedBarButton = -1;    // same encoding while the button is held down
    private int _resizeHandle = -1;        // 0..7 while dragging a handle, else -1
    private bool _movingPending;           // dragging the pending rect body to move it
    private SD.Point _adjustAnchor;        // cursor at the start of a move/resize
    private SD.Rectangle _adjustStartRect; // pending rect at the start of a move/resize
    private WindowInfo? _hoverWindow;
    /// <summary>Name of the app behind <see cref="_hoverWindow"/>, resolved once per target.</summary>
    private string _hoverAppName = string.Empty;
    /// <summary>0..1 ramp on the window-target wash, so retargeting reads as the tint moving
    /// rather than two rectangles blinking.</summary>
    private double _hoverWash;
    private IDisposable? _hoverWashTween;
    private SD.Bitmap? _frozen;            // frozen virtual-desktop snapshot shown under the overlay
    // Per-monitor frozen-slice-with-dim baked once, so each follow-frame is a single opaque
    // blit instead of re-cropping the snapshot AND alpha-blending a full-screen dim every tick
    // (the latter is what made the crosshair lag on large external monitors). Keyed by monitor.
    private readonly Dictionary<SD.Rectangle, SD.Bitmap> _dimmedCache = new();
    private SD.Bitmap? _capturedRegion;    // region cropped from _frozen at confirm; caller takes ownership
    private Func<Task<List<WindowInfo>>> _windowsProvider;
    private bool _windowsLoadStarted;
    private TaskCompletionSource<WF.DialogResult>? _completion;
    // Polls the cursor while a selection is open so the crosshair/loupe follow continuously,
    // independent of whether idle hover WM_MOUSEMOVE reaches the overlay (it doesn't reliably
    // across multiple monitors / when the overlay isn't the foreground window). Runs ONLY while
    // shown — started in ShowAsync, stopped in Complete — so it adds no idle background work.
    private readonly WF.Timer _followTimer;
    private bool _lastCtrlDown;
    // Session-effective loupe visibility: seeded from the "Show magnifier" setting each
    // ShowAsync, flipped by tapping Alt. The latch swallows key-repeat while Alt is held.
    private bool _magnifierOn;
    private bool _altLatched;

    public FastRegionSelectorDialog(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        _settings = settings;
        _windowsProvider = windowsProvider;
        _monitorBounds = PrimaryBounds();

        SelectorChrome.ConfigureSurface(this);
        // Born translucent so the handle is created WS_EX_LAYERED once and never restyled:
        // any later Form.Opacity flip across the 1.0 boundary recreates the handle
        // (~150-500 ms measured). All opacity changes go through SetLayeredAlpha instead.
        SelectorChrome.ConfigurePresentation(this, freezeScreen: false);
        DoubleBuffered = true;
        SetStyle(PaintStyles, true);
        Bounds = _monitorBounds;

        _followTimer = new WF.Timer { Interval = WinShot.Core.Motion.FrameIntervalMs };
        _followTimer.Tick += OnFollowTick;

        // Window-list load is kicked from ShowAsync, not the Shown event: Shown fires only
        // on a form's FIRST display, so a pooled re-show would never load the list.

        ResetForUse(windowsProvider, settings);
    }

    public SD.Rectangle? SelectedRegionPx { get; private set; }

    /// <summary>
    /// Shift was held when the selection was confirmed, which asks for the bare capture with
    /// no composited backdrop. Read at confirm time rather than after the fact, because by
    /// the time the caller looks the key is usually already up.
    /// </summary>
    public bool BareBackgroundRequested { get; private set; }

    // DoubleBuffered + SetStyle are protected on Control, so each surface enables its own
    // flicker-free painting from inside its own constructor.
    private const WF.ControlStyles PaintStyles =
        WF.ControlStyles.AllPaintingInWmPaint |
        WF.ControlStyles.OptimizedDoubleBuffer |
        WF.ControlStyles.ResizeRedraw |
        WF.ControlStyles.UserPaint;

    private static SD.Rectangle PrimaryBounds() =>
        (WF.Screen.PrimaryScreen ?? WF.Screen.AllScreens[0]).Bounds;

    // One warm instance kept alive between captures: re-showing existing window handles
    // is near-instant, while creating the full-screen layered surfaces from scratch costs
    // 200-900 ms under CPU load. Hidden windows do no idle work and hold no bitmaps.
    private static FastRegionSelectorDialog? _pooled;

    public static FastRegionSelectorDialog Rent(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        var pooled = _pooled;
        _pooled = null;
        if (pooled is not null && !pooled.IsDisposed)
        {
            pooled.ResetForUse(windowsProvider, settings);
            return pooled;
        }
        return new FastRegionSelectorDialog(windowsProvider, settings);
    }

    public static void Return(FastRegionSelectorDialog selector)
    {
        if (selector.IsDisposed)
            return;

        selector.DisposeFrozen();
        selector._capturedRegion?.Dispose();
        selector._capturedRegion = null;
        selector.Park();

        var displaced = _pooled;
        _pooled = selector;
        if (displaced is not null && displaced != selector && !displaced.IsDisposed)
        {
            displaced.DisposePanes();
            displaced.Dispose();
        }
    }

    /// <summary>Creates the window handles (coordinator + panes) without showing anything,
    /// so the first hotkey press after app start skips window creation entirely.</summary>
    /// <summary>
    /// Makes the selector invisible without surrendering its DWM composition, so the next
    /// open is an alpha flip rather than ~130 ms of ShowWindow per full-screen surface.
    /// </summary>
    internal void Park()
    {
        SelectorSurfaces.Park(this);
        foreach (var pane in _panes)
            SelectorSurfaces.Park(pane);

        // Shown-but-parked, never hidden: Hide() would throw the composition away again.
        // Disposed panes report Visible==false, so guard: Show() on one would throw
        // ObjectDisposedException out of Complete() and wedge the selector session.
        if (!IsDisposed && !Visible) Show();
        foreach (var pane in _panes)
            if (!pane.IsDisposed && !pane.Visible) pane.Show();
    }

    internal void Prewarm()
    {
        CreatePanes();
        _ = Handle;
        foreach (var pane in _panes)
            _ = pane.Handle;
        // Pay the ShowWindow cost once, at startup, so even the first hotkey is instant.
        Park();
    }

    public async Task<WF.DialogResult> ShowAsync(SelectorMode mode = SelectorMode.Area,
        bool paneHover = false, bool elementHover = false)
    {
        _mode = mode;
        _paneHover = paneHover || elementHover;
        _elementGranularity = elementHover;
        SelectedByPaneClick = false;
        _completion = new TaskCompletionSource<WF.DialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        Bounds = _monitorBounds;
        _options = SelectorOptions.ForRegion(_settings?.Current);
        // Always open translucent over the live desktop so the overlay appears the instant
        // the hotkey fires. The freeze snapshot (WGC setup alone can cost 300-1700 ms under
        // load) is taken in the background — these surfaces are excluded from capture — and
        // bakes in underneath when it lands; the dim level matches, so the swap is invisible.
        SelectorChrome.ConfigurePresentation(this, freezeScreen: false);
        DisposeFrozen();
        // Snapshot every snappable HWND rect BEFORE our own overlay windows go up, so the
        // list can never contain the selector itself (pooled surfaces are hidden here, and
        // invisible windows are filtered). Cheap (a few ms) and done once — hover detection
        // is then a pure in-memory scan with no per-move syscalls. Synchronous on purpose:
        // it must finish before Show(), and a Task.Run hop under CPU load would delay the
        // open longer than the scan itself.
        _snapRects = _paneHover
            ? WindowEnumerator.GetSnapRectangles()
            : new List<SnapRect>();
        _snapEdgesX = _snapRects.SelectMany(r => new[] { r.Bounds.Left, r.Bounds.Right })
            .Distinct().OrderBy(x => x).ToArray();
        _snapEdgesY = _snapRects.SelectMany(r => new[] { r.Bounds.Top, r.Bounds.Bottom })
            .Distinct().OrderBy(y => y).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CreatePanes();
        // Warm (pooled) surfaces may still be opaque from a previous frozen swap.
        SelectorSurfaces.Unpark(this, SelectorChrome.LiveAlpha);
        foreach (var pane in _panes)
            SelectorSurfaces.Unpark(pane, SelectorChrome.LiveAlpha);
        long panesMs = sw.ElapsedMilliseconds;

        Show();
        StartWindowLoad();
        long showMs = sw.ElapsedMilliseconds - panesMs;
        var paneTimings = new List<string>(_panes.Count);
        foreach (var pane in _panes)
        {
            long before = sw.ElapsedMilliseconds;
            bool warm = pane.IsHandleCreated;
            pane.Show();
            paneTimings.Add($"{pane.MonitorBounds.Width}x{pane.MonitorBounds.Height}" +
                $"{(warm ? "" : "(cold)")}={sw.ElapsedMilliseconds - before}");
        }
        long paneShowMs = sw.ElapsedMilliseconds - panesMs - showMs;

        Activate();
        Focus();
        SelectorForeground.Restore(this);
        if (sw.ElapsedMilliseconds > 100)
        {
            Log.Info(
                "Perf selector open breakdown: " +
                $"panes={panesMs} show={showMs} paneShow={paneShowMs} " +
                $"[{string.Join(" ", paneTimings)}] " +
                $"activate={sw.ElapsedMilliseconds - panesMs - showMs - paneShowMs} total={sw.ElapsedMilliseconds} ms");
        }
        _lastCtrlDown = false;
        _magnifierOn = _options.ShowMagnifier;
        _altLatched = false;
        _currentScreen = CursorScreen();
        _lastFollowScreen = _currentScreen;
        _hintOpacity = 0d;
        if (_settings is not null && SelectorHint.ShouldShow(_settings.Current.SelectorHintSessions))
        {
            _settings.Current.SelectorHintSessions =
                SelectorHint.NextSessionCount(_settings.Current.SelectorHintSessions);
            _hintSessionUnsaved = true;
            _hintEnabled = true;
            _hintText = SelectorHint.Text(_mode == SelectorMode.Window);
            _hintIsMessage = false;
            FadeHint(arriving: true);
            StartFollowMotion(); // the pill is drawn on the follow frames
        }
        if (_options.NeedsCursorFollow || _paneHover)
            StartFollowMotion();
        if (_options.FreezeScreen)
            _ = FreezeWhileShownAsync(_completion);
        return await _completion.Task;
    }

    /// <summary>
    /// Grabs the freeze snapshot while the selector is already on screen and switches the
    /// surfaces from translucent-live to opaque-frozen once it lands. Everything after the
    /// await runs on the UI thread, serialized with Complete(), so the session guard is
    /// race-free. If the session ended first the late snapshot is discarded; if the capture
    /// failed we simply stay on the live translucent view.
    /// </summary>
    private async Task FreezeWhileShownAsync(TaskCompletionSource<WF.DialogResult> session)
    {
        // Capture into a LOCAL. The selector is pooled, so a cancelled session's capture
        // can still be in flight when the next session opens; a stale continuation must
        // never assign to (or dispose) the shared _frozen/_dimmedCache the live session
        // paints from — the old code did both, and when the stale (slow) capture landed
        // after the new (fast) one, it nulled the live snapshot while every surface was
        // already opaque: a solid-black overlay across all monitors.
        SD.Bitmap? snapshot = null;
        try
        {
            snapshot = await Task.Run(CaptureService.CaptureVirtualDesktop);
        }
        catch (Exception ex)
        {
            Log.Error("Screen-freeze capture failed; selecting over a plain dim instead", ex);
        }

        if (_completion != session || IsDisposed)
        {
            snapshot?.Dispose(); // stale: touch nothing shared
            return;
        }

        DisposeFrozen();
        _frozen = snapshot;
        if (_frozen is null)
            return;

        // Live mode paints solid black behind a 45% layered alpha; frozen mode paints the
        // dimmed snapshot at full alpha. The two look identical on screen (0.553*desktop vs
        // 0.549*desktop) — but only once the frozen paint has actually landed. Flipping alpha
        // first left every surface fully opaque over its live-mode BLACK for as long as the
        // first frozen paint took, which is a full-monitor bitmap allocation plus two blits
        // per surface: a black flash across every monitor, worst on the first hotkey.
        //
        // So the dimmed slices are built first. There is deliberately no await between here
        // and the repaint, so WinForms cannot pump a paint in the half-swapped state.
        var swap = System.Diagnostics.Stopwatch.StartNew();
        GetDimmedBackground(_monitorBounds);
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed)
                GetDimmedBackground(pane.MonitorBounds);
        }
        long prewarmMs = swap.ElapsedMilliseconds;

        // Per surface: paint the frozen frame FIRST, then go opaque. There is no ordering
        // that is completely invisible — while the live desktop still shows through, the
        // composite cannot equal the frozen one — but the two artifacts are not equal:
        //   flip first  -> the surface is opaque over live mode's BLACK until the paint
        //                  lands: a full-screen black flash, and it reads as a fault.
        //   paint first -> for the length of that one paint the surface shows the frozen
        //                  image THROUGH the 45% alpha, i.e. briefly brighter, which reads
        //                  as the dim settling in.
        // Doing it per surface also means no monitor waits on another monitor's paint.
        long alphaStart = swap.ElapsedMilliseconds;
        Invalidate();
        Update();
        WinShot.Scrolling.CaptureExclusion.SetLayeredAlpha(Handle, 255);
        foreach (var pane in _panes)
        {
            if (pane.IsDisposed || !pane.IsHandleCreated) continue;
            pane.Invalidate();
            pane.Update();
            WinShot.Scrolling.CaptureExclusion.SetLayeredAlpha(pane.Handle, 255);
        }
        Log.Info($"Perf freeze swap: prewarm={prewarmMs} alpha={alphaStart - prewarmMs} " +
            $"paint={swap.ElapsedMilliseconds - alphaStart} ms");
    }

    private void ResetForUse(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        _settings = settings;
        _options = SelectorOptions.ForRegion(settings?.Current);
        _mode = SelectorMode.Area;
        _windowsProvider = windowsProvider;
        _windowsLoadStarted = false;
        _windows = new List<WindowInfo>();
        _snapRects = new List<SnapRect>();
        _snapEdgesX = Array.Empty<int>();
        _snapEdgesY = Array.Empty<int>();
        _dragging = false;
        _dragMoved = false;
        _pendingScreen = null;
        _hoverBarButton = -1;
        _pressedBarButton = -1;
        _pressedShutter = false;
        _resizeHandle = -1;
        _movingPending = false;
        _pressedEdge = MarqueeSide.None;
        _hoverEdge = MarqueeSide.None;
        _edgeTween?.Dispose();
        _edgeTween = null;
        _edgeGlow = 0d;
        _sessionEnded = false;
        _hintEnabled = false;
        _handleTween?.Dispose();
        _handleTween = null;
        _handleScale = 1d;
        _hintTween?.Dispose();
        _hintTween = null;
        _hintHold?.Dispose();
        _hintHold = null;
        _hintOpacity = 0d;
        _hintText = string.Empty;
        _hintIsMessage = false;
        _barTween?.Dispose();
        _barTween = null;
        Array.Clear(_barHover);
        Array.Clear(_barPress);
        _pillTween?.Dispose();
        _pillTween = null;
        _pillHover = false;
        _pillSwap = 0d;
        _pendingGlideTween?.Dispose();
        _pendingGlideTween = null;
        _pendingGlide.Stop();
        _hoverWindow = null;
        _hoverAppName = string.Empty;
        // A pooled selector outlives the windows it saw last session; a stale handle can be
        // reused by a different process, and a fixed-size cache with no clear point grows.
        _appNames.Clear();
        BeginHoverWash(arriving: false, wasHovering: false);
        _paneHover = false;
        _elementGranularity = false;
        _hoverPane = null;
        _hoverTween.Stop();
        _hoverLadder = new List<SD.Rectangle>();
        _ladderIndex = -1;
        _wheelPinned = null;
        SelectedByPaneClick = false;
        SelectedRegionPx = null;
        BareBackgroundRequested = false;
        DisposeFrozen();
        _capturedRegion?.Dispose();
        _capturedRegion = null;
        DialogResult = WF.DialogResult.None;
        Bounds = _monitorBounds;
        Capture = false;
        // Seed at the real cursor so the first paint draws the crosshair/loupe at the
        // pointer instead of a corner until the first mouse-move arrives.
        _currentScreen = CursorScreen();
        _completion = null;
    }

    // ----------------------------------------------------------- per-monitor panes

    private void CreatePanes()
    {
        var targets = new List<SD.Rectangle>();
        foreach (var screen in WF.Screen.AllScreens)
        {
            if (screen.Bounds != _monitorBounds)
                targets.Add(screen.Bounds);
        }

        // Reuse the existing (hidden) panes when the monitor layout is unchanged — their
        // window handles are warm, so re-showing them is near-instant.
        if (_panes.Count == targets.Count &&
            _panes.All(p => !p.IsDisposed) &&
            targets.All(b => _panes.Any(p => p.MonitorBounds == b)))
        {
            return;
        }

        DisposePanes();
        foreach (var bounds in targets)
        {
            // Panes open translucent like the coordinator; FreezeWhileShownAsync flips
            // them opaque when the snapshot lands.
            var pane = new SelectorPane(this, bounds, freezeScreen: false);
            _panes.Add(pane);
        }
    }

    private void DisposePanes()
    {
        foreach (var pane in _panes)
        {
            try { pane.Close(); pane.Dispose(); }
            catch { /* best effort */ }
        }
        _panes.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposePanes();
            DisposeFrozen();
            _hoverWashTween?.Dispose();
            _hoverWashTween = null;
            _handleTween?.Dispose();
            _handleTween = null;
            _hintTween?.Dispose();
            _hintTween = null;
            _hintHold?.Dispose();
            _hintHold = null;
            _barTween?.Dispose();
            _barTween = null;
            _pillTween?.Dispose();
            _pillTween = null;
            _pendingGlideTween?.Dispose();
            _pendingGlideTween = null;
            _edgeTween?.Dispose();
            _edgeTween = null;
            _followTimer.Dispose();
            _followMotionClock?.Dispose();
            _followMotionClock = null;
            _capturedRegion?.Dispose();
            _capturedRegion = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>Repaints the coordinator surface and every pane (the selection can span monitors).</summary>
    private void InvalidateAllSurfaces()
    {
        Invalidate();
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed)
                pane.Invalidate();
        }
        _lastFollowScreen = _currentScreen; // a full repaint redraws the crosshair at the cursor
    }

    // Half-extents of the regions the idle crosshair/loupe occupy around the cursor.
    private const int CrosshairBandHalf = 16;  // guide-line band half-width (covers line + shadow + AA)
    private const int LoupeBoxHalf = 250;       // box that contains the loupe + label in any flip

    /// <summary>
    /// Invalidates just the crosshair guide bands (full monitor height/width strips at the old
    /// and new cursor X/Y) and the loupe boxes, on whichever surfaces they touch. This makes
    /// the follow repaint proportional to the thin bands, not the whole (possibly 4K) monitor.
    /// </summary>
    private void InvalidateFollowRegion(SD.Point oldScreen, SD.Point newScreen)
    {
        InvalidateFollowAt(oldScreen);
        if (newScreen != oldScreen)
            InvalidateFollowAt(newScreen);
    }

    private void InvalidateFollowAt(SD.Point pt)
    {
        SD.Rectangle mon = MonitorBoundsAt(pt);
        InvalidateScreenRect(new SD.Rectangle(pt.X - CrosshairBandHalf, mon.Top, CrosshairBandHalf * 2, mon.Height));
        InvalidateScreenRect(new SD.Rectangle(mon.Left, pt.Y - CrosshairBandHalf, mon.Width, CrosshairBandHalf * 2));
        InvalidateScreenRect(new SD.Rectangle(pt.X - LoupeBoxHalf, pt.Y - LoupeBoxHalf, LoupeBoxHalf * 2, LoupeBoxHalf * 2));
    }

    /// <summary>The physical bounds of the surface (coordinator or pane) containing a screen point.</summary>
    private SD.Rectangle MonitorBoundsAt(SD.Point screen)
    {
        if (_monitorBounds.Contains(screen)) return _monitorBounds;
        foreach (var pane in _panes)
            if (!pane.IsDisposed && pane.MonitorBounds.Contains(screen))
                return pane.MonitorBounds;
        return WF.Screen.FromPoint(screen).Bounds;
    }

    /// <summary>The DPI factor of the surface a screen point lands on. Every piece of
    /// chrome that is measured in logical px goes through this, so the handles, the edge
    /// band and the pills all grow together on Isaiah's 150 % primary.</summary>
    private double ScaleAt(SD.Point screen) =>
        WinShot.Recording.RecordingMonitorDpi.ScaleFor(MonitorBoundsAt(screen));

    /// <summary>Invalidates a screen-space rectangle on every surface it intersects.</summary>
    private void InvalidateScreenRect(SD.Rectangle screenRect)
    {
        InvalidateSurfaceRect(this, _monitorBounds, screenRect);
        foreach (var pane in _panes)
            if (!pane.IsDisposed)
                InvalidateSurfaceRect(pane, pane.MonitorBounds, screenRect);
    }

    private static void InvalidateSurfaceRect(WF.Control surface, SD.Rectangle surfaceScreen, SD.Rectangle screenRect)
    {
        var hit = SD.Rectangle.Intersect(surfaceScreen, screenRect);
        if (hit.Width <= 0 || hit.Height <= 0)
            return;
        surface.Invalidate(new SD.Rectangle(hit.X - surfaceScreen.X, hit.Y - surfaceScreen.Y, hit.Width, hit.Height));
    }

    // ----------------------------------------------------------- window list

    private void StartWindowLoad()
    {
        if (_windowsLoadStarted)
            return;

        _windowsLoadStarted = true;
        _ = LoadWindowsAsync(_windowsProvider());
    }

    private async Task LoadWindowsAsync(Task<List<WindowInfo>> windowsTask)
    {
        try
        {
            var windows = await windowsTask.ConfigureAwait(false);
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed)
                        _windows = windows;
                }));
            }
            catch (InvalidOperationException)
            {
                _windows = windows;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load selector window list", ex);
        }
    }

    // ----------------------------------------------------------- input (coordinator)

    protected override void OnKeyDown(WF.KeyEventArgs e)
    {
        HandleKeyDown(e);
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(WF.KeyEventArgs e)
    {
        HandleKeyUp(e);
        base.OnKeyUp(e);
    }

    protected override void OnFormClosing(WF.FormClosingEventArgs e)
    {
        if (_completion is not null)
        {
            e.Cancel = true;
            Complete(WF.DialogResult.Cancel);
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnMouseDown(WF.MouseEventArgs e)
    {
        HandleMouseDown(e);
        Capture = CapturingPointer;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(WF.MouseEventArgs e)
    {
        HandleMouseMove();
        Cursor = CursorForCurrent();
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(WF.MouseEventArgs e)
    {
        Capture = false;
        HandleMouseUp(e);
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(WF.MouseEventArgs e)
    {
        HandleMouseWheel(e);
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        // No Clear here: PaintSurface's first act covers the whole surface, either with the
        // frozen slice or with a full-surface dim, and a redundant full-monitor fill is
        // expensive enough at 4K-ish sizes to show up in the freeze swap.
        if (!HasFullSurfaceBackground)
            e.Graphics.Clear(SD.Color.Black);
        PaintSurface(e.Graphics, _monitorBounds);
        base.OnPaint(e);
    }

    // The pointer is "captured" (events keep flowing to the pressed surface) while drawing a
    // marquee or dragging a handle/the pending rect.
    internal bool CapturingPointer => _dragging || _resizeHandle >= 0 || _movingPending;

    /// <summary>
    /// Whether a letter key arrived on its own. The selector's single-letter shortcuts match
    /// on KeyCode, so without this Ctrl+A — the near-universal "select all" reflex, on an
    /// overlay that has a selection on it — silently re-snapped every edge of the user's
    /// rectangle. Ctrl+C copying the colour is deliberate and is checked separately.
    /// </summary>
    private static bool IsBareKey(WF.KeyEventArgs e) => !e.Control && !e.Alt && !e.Shift;

    internal void HandleKeyDown(WF.KeyEventArgs e)
    {
        // The selector is pooled: after Complete() it is parked but alive and still holding
        // keyboard focus, so a key arriving in that window (Escape auto-repeat is the usual
        // way) started tweens and a message dwell on a hidden form, holding the 1 ms clock.
        if (_sessionEnded)
            return;

        if (e.KeyCode == WF.Keys.Escape)
        {
            Complete(WF.DialogResult.Cancel);
        }
        else if (e.KeyCode == WF.Keys.Enter && _pendingScreen is SD.Rectangle pending)
        {
            e.Handled = true;
            Confirm(VirtualFromScreen(pending));
        }
        else if (_pendingScreen is SD.Rectangle nudged && SelectionNudge.Handles(e.KeyCode))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (SelectionNudge.Apply(nudged, e.KeyCode, e.Shift, e.Control, _vs) is SD.Rectangle moved &&
                moved != nudged)
            {
                // A nudged rect is a hand-drawn one: ScrollingFlow re-refines a detector-owned
                // rect back onto the pane, which would silently undo the nudge.
                SelectedByPaneClick = false;
                _pendingScreen = moved;
                // A 1 px move at key-repeat rate must not repaint every monitor; the union of
                // where it was and where it is (plus the handle tolerance) is all that changed.
                InvalidateScreenRect(Inflated(SD.Rectangle.Union(nudged, moved)));
            }
        }
        else if (e.KeyCode == WF.Keys.A && IsBareKey(e) && _pendingScreen is SD.Rectangle toSnap)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            SD.Rectangle snapped = MarqueeEdge.SnapAll(toSnap, _snapEdgesX, _snapEdgesY);
            if (snapped != toSnap)
                GlideSelectionTo(snapped);
            else
                FlashMessage("Nothing to snap to near this selection.");
        }
        else if (e.KeyCode == WF.Keys.C && (IsBareKey(e) || (e.Control && !e.Alt && !e.Shift)))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CopyColorUnderCursor();
        }
        else if (e.KeyCode == WF.Keys.Tab && e.Shift)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CopyTextColorUnderCursor();
        }
        else if (e.KeyCode == WF.Keys.Space && IsBareKey(e) && !CapturingPointer)
        {
            // Not mid-drag: Space is "pan" in most tools, and throwing away a marquee the
            // user is still drawing is not what that reflex asks for.
            e.Handled = true;
            e.SuppressKeyPress = true;
            ToggleWindowMode();
        }
        else if (e.KeyCode == WF.Keys.Menu)
        {
            if (!_altLatched)
            {
                _altLatched = true;
                ToggleMagnifier();
            }
            e.Handled = true;
            e.SuppressKeyPress = true; // no menu-loop ding on a borderless overlay
        }
    }

    internal SelectorMode Mode => _mode;

    /// <summary>
    /// Space flips between drawing a rectangle and picking a whole window, mid-selection.
    /// Window capture used to be a different hotkey chosen before the overlay opened, so
    /// realising "actually I want that whole window" cost an Escape and a second gesture.
    /// The window list is already loaded for both modes, so there is nothing to wait for;
    /// the arriving target's wash ramp is what makes the flip read as a change.
    /// </summary>
    private void ToggleWindowMode()
    {
        if (_mode == SelectorMode.Window)
        {
            _mode = SelectorMode.Area;
            SetHoverWindow(null);
        }
        else
        {
            // An in-progress drag or an adjustable pending rect belongs to the mode being
            // left; carrying either into window mode would leave a marquee nothing can
            // confirm on screen.
            _mode = SelectorMode.Window;
            _dragging = false;
            _dragMoved = false;
            _pendingScreen = null;
            _hoverBarButton = -1;
            _pressedBarButton = -1;
            _barTween?.Dispose();
            _barTween = null;
            Array.Clear(_barHover);
            Array.Clear(_barPress);
            _pressedShutter = false;
            // The selection these three animate is gone as of this keystroke. Every other
            // teardown path drops them explicitly; leaving them running kept the process-wide
            // 1 ms clock raised for up to 140 ms against a rect nobody can see.
            _handleTween?.Dispose();
            _handleTween = null;
            _handleScale = 1d;
            _pillTween?.Dispose();
            _pillTween = null;
            _pillHover = false;
            _pillSwap = 0d;
            _pendingGlideTween?.Dispose();
            _pendingGlideTween = null;
            _pendingGlide.Stop();
            _resizeHandle = -1;
            _movingPending = false;
            _pressedEdge = MarqueeSide.None;
            _hoverEdge = MarqueeSide.None;
            _edgeTween?.Dispose();
            _edgeTween = null;
            _edgeGlow = 0d;
            SetHoverPane(null);
            SetHoverWindow(ResolveWindow(_currentScreen));
            StartFollowMotion(); // window mode tracks the cursor even with both chrome toggles off
        }
        if (_hintHold is null && _hintOpacity > 0d)
        {
            _hintText = SelectorHint.Text(_mode == SelectorMode.Window);
            _hintIsMessage = false;
        }
        Cursor = CursorForCurrent();
        InvalidateAllSurfaces();
    }

    internal void HandleKeyUp(WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Menu)
            _altLatched = false;
    }

    internal bool MagnifierVisible => _magnifierOn;

    private void ToggleMagnifier()
    {
        _magnifierOn = !_magnifierOn;
        if (_magnifierOn)
            StartFollowMotion(); // the setting may be off, so the follow tick may not be running
        SD.Point pt = _currentScreen;
        InvalidateScreenRect(new SD.Rectangle(
            pt.X - LoupeBoxHalf, pt.Y - LoupeBoxHalf, LoupeBoxHalf * 2, LoupeBoxHalf * 2));
    }

    internal void HandleMouseDown(WF.MouseEventArgs e)
    {
        // Same guard the key handler carries: a pooled selector is parked-but-alive after
        // Complete(), and only WS_EX_TRANSPARENT keeps mouse messages off it today.
        if (_sessionEnded)
            return;
        SD.Point screen = CursorScreen();
        _currentScreen = screen;

        if (e.Button == WF.MouseButtons.Right)
        {
            Complete(WF.DialogResult.Cancel);
            return;
        }

        if (e.Button != WF.MouseButtons.Left)
            return;

        if (_mode == SelectorMode.Window)
            return; // window mode confirms on mouse-up

        // A press ends any glide in flight. The rect is painted at the eased position but
        // every hit-test reads the destination, so for the 140 ms of a pad / snap / retarget
        // the handles you can see are not the handles you can grab.
        SettlePendingGlide();

        // Area mode. A fresh press is a fresh provenance: the flag below says the rect came
        // from the detector, and downstream probe refinement is only correct for those.
        SelectedByPaneClick = false;
        _pressScreen = screen;

        // The size pill IS the shutter — it cross-fades to a camera glyph and swaps in the
        // camera cursor, so it reads as a button. It must never be the ground a marquee
        // starts on: a press there with a few pixels of trackpad wobble used to confirm a
        // 4x4 capture of the pill's own corner instead of taking the element it labels.
        if (ShutterPillScreenRect() is SD.Rectangle shutter && shutter.Contains(screen))
        {
            _pressedShutter = true;
            return;
        }

        if (_pendingScreen is SD.Rectangle pending)
        {
            // Cancel / Done bar wins over move/resize. The press only arms the button —
            // the action fires on mouse-up over the same button, standard button feel.
            var (barRect, padRect, cancelRect, doneRect) = ActionBarRects(BarAnchor ?? pending);
            if (barRect.Contains(screen))
            {
                _pressedBarButton = doneRect.Contains(screen) ? 1
                    : cancelRect.Contains(screen) ? 0
                    : padRect.Contains(screen) ? 2 : -1;
                AnimateBarFills();
                InvalidateScreenRect(barRect);
                return; // consume any click on the bar (don't start a new marquee under it)
            }

            if (e.Clicks >= 2 && pending.Contains(screen))
            {
                Confirm(VirtualFromScreen(pending));
                return;
            }

            int handle = SelectionHandles.HitTest(screen, pending, ScaleAt(screen));
            if (handle >= 0)
            {
                // Hand-adjusting a detector rect makes it a hand-drawn one: the reset at the
                // top of this method already dropped the provenance for every fresh press.
                _resizeHandle = handle;
                _adjustStartRect = pending;
                return;
            }

            // Handles win the corners; a press on an edge with no drag behind it snaps that
            // ONE edge onto the nearest real boundary, which is the tedious half of drawing a
            // rectangle by hand. The press only ARMS it — the snap fires on mouse-up over the
            // same edge, like every other button here. Firing on the press teleported an edge
            // up to ReachPx away, under a cursor that had been promising a move, with no undo;
            // and returning here made a drag from the border impossible. A press that travels
            // is still a move, so the move state is armed alongside it.
            MarqueeSide side = MarqueeEdge.HitTest(pending, screen, EdgeToleranceAt(screen));
            if (side != MarqueeSide.None)
            {
                _pressedEdge = side;
                if (pending.Contains(screen))
                {
                    _movingPending = true;
                    _adjustAnchor = screen;
                    _adjustStartRect = pending;
                }
                else
                {
                    // The grab band reaches the same distance OUTSIDE the border as inside,
                    // so a press in those few pixels arms the edge snap but has nothing to
                    // move. Arming nothing else made the whole band dead: no move, no new
                    // marquee, and mouse-up re-hit-tested under a pointer that had travelled
                    // and so did not snap either. A fresh marquee is armed alongside the
                    // snap; whichever the gesture turns out to be, mouse-up picks it.
                    _dragStartScreen = screen;
                    _dragging = true;
                    _dragMoved = false;
                }
                return;
            }

            if (pending.Contains(screen))
            {
                _movingPending = true;
                _adjustAnchor = screen;
                _adjustStartRect = pending;
                return;
            }

            // Clicked outside the pending selection — start drawing a new one.
        }

        _pendingScreen = null;
        _dragStartScreen = screen;
        _dragging = true;
        _dragMoved = false;
        InvalidateAllSurfaces();
    }

    /// <summary>The adjustable selection, for tests and for the keyboard nudge helper.</summary>
    internal SD.Rectangle? PendingRegion => _pendingScreen;

    /// <summary>Display-only glide when the selection jumps by itself (an edge snapping, a
    /// padding step). The pending rect is the destination from the first frame, so Enter and
    /// Done always commit the final rectangle even mid-glide.</summary>
    private readonly RectangleTween _pendingGlide = new();
    private IDisposable? _pendingGlideTween;

    /// <summary>
    /// The rectangle the Cancel/Done/Pad bar is placed on — the EASED rect while a glide is
    /// in flight, the settled one otherwise. Paint and every hit-test read this one property.
    ///
    /// <see cref="_pendingScreen"/> is the destination from the glide's first frame (so Enter
    /// and Done always commit the final rectangle), but the bar sits <see cref="BarGap"/> px
    /// below the region: placing it off the destination while the region was still easing
    /// drew the bar in one place and answered clicks in another for the whole 140 ms, and a
    /// pointer move mid-glide painted a second copy of it at the destination.
    /// </summary>
    private SD.Rectangle? BarAnchor => _pendingScreen is SD.Rectangle pending
        ? (_pendingGlide.IsActive ? _pendingGlide.Current : pending)
        : null;

    /// <summary>
    /// Ends a selection glide early, leaving the rect at its destination. <see cref="_pendingScreen"/>
    /// is already the destination, so this only stops paint from lagging behind the hit-tests.
    /// </summary>
    private void SettlePendingGlide()
    {
        if (_pendingGlideTween is null && !_pendingGlide.IsActive)
            return;
        _pendingGlideTween?.Dispose();
        _pendingGlideTween = null;
        _pendingGlide.Stop();
        InvalidateAllSurfaces();
    }

    /// <summary>
    /// The edge grab band, in physical px, for the monitor a point is on. Six px is four
    /// logical px at Isaiah's 150 % primary and three at 200 %, which is a band too thin to
    /// land on deliberately.
    /// </summary>
    private int EdgeToleranceAt(SD.Point screen) => Math.Max(
        MarqueeEdge.TolerancePx,
        (int)Math.Round(MarqueeEdge.TolerancePx * ScaleAt(screen)));

    /// <summary>
    /// Eases the hovered edge's accent thickening toward its target. The gesture moves an edge
    /// by up to <see cref="MarqueeEdge.ReachPx"/> with no undo, so the edge has to announce
    /// itself before the press rather than after it.
    /// </summary>
    private void AnimateEdgeGlow()
    {
        double from = _edgeGlow;
        double to = _hoverEdge == MarqueeSide.None ? 0d : 1d;
        _edgeTween?.Dispose();
        _edgeTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                _edgeGlow = from + ((to - from) * progress);
                if (BarAnchor is SD.Rectangle p)
                    InvalidateScreenRect(Inflated(p));
            },
            () => _edgeTween = null);
    }

    private void GlideSelectionTo(SD.Rectangle destination)
    {
        if (_pendingScreen is not SD.Rectangle from || destination == from)
            return;

        // Padding, an edge snap and a re-snap all move the rect without a press, so none of
        // them go through HandleMouseDown's provenance reset. A rect the user reshaped is no
        // longer the one the detector chose.
        SelectedByPaneClick = false;
        _pendingScreen = destination;
        _pendingGlideTween?.Dispose();
        _pendingGlide.Retarget(from, destination);
        SD.Rectangle band = SD.Rectangle.Union(from, destination);
        // The bar rides BELOW the region, outside `band`. Repainting the band alone left it
        // painted at its old spot for the whole glide, so each frame also repaints where the
        // bar was and where it now is.
        SD.Rectangle previousBar = ActionBarRects(from).bar;
        _pendingGlideTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                _pendingGlide.Update((float)progress);
                SD.Rectangle nowBar = ActionBarRects(BarAnchor ?? destination).bar;
                InvalidateScreenRect(Inflated(
                    SD.Rectangle.Union(band, SD.Rectangle.Union(previousBar, nowBar))));
                previousBar = nowBar;
            },
            () =>
            {
                _pendingGlideTween = null;
                _pendingGlide.Stop();
                InvalidateAllSurfaces();
            });
    }

    /// <summary>
    /// Hands the selector an adjustable selection and pops its handles in. Eight dots
    /// appearing at full size in one frame reads as a glitch; growing them over QuickMs
    /// reads as the selection becoming grabbable.
    /// </summary>
    private void SetPendingRegion(SD.Rectangle rect)
    {
        bool isNew = _pendingScreen is null;
        _pendingScreen = rect;
        // A framed selection has its own vocabulary — nudge, resize, re-snap — and the hint
        // used to be suppressed in exactly that state. Only for the sessions the first-run
        // hint runs at all, and never over a message the user just triggered.
        if (isNew && _hintEnabled && !_hintIsMessage && _hintHold is null)
        {
            _hintText = SelectorHint.PendingText;
            FadeHint(arriving: true);
            StartFollowMotion();
        }
        _handleTween?.Dispose();
        _handleScale = 0d;
        _handleTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutBack,
            progress =>
            {
                _handleScale = progress;
                if (_pendingScreen is SD.Rectangle p)
                    InvalidateScreenRect(Inflated(p));
            },
            () => _handleTween = null);
    }

    /// <summary>0..1 growth of the pending selection's handles (overshoots past 1).</summary>
    private double _handleScale = 1d;
    private IDisposable? _handleTween;

    /// <summary>True while the pointer is over the detected element's size pill.</summary>
    private bool _pillHover;
    /// <summary>0..1 cross-fade from the size text to the camera glyph on that pill.</summary>
    private double _pillSwap;
    private IDisposable? _pillTween;

    /// <summary>
    /// The size pill for the currently detected element, in screen pixels — or null when
    /// there is no detected element to label. Paint and hit-test share it so the pill you can
    /// click is exactly the pill you can see.
    /// </summary>
    private SD.Rectangle? ShutterPillScreenRect()
    {
        if (!_paneHover || _pendingScreen is not null || _dragging || _hoverPane is not SD.Rectangle pane)
            return null;
        string text = $"{pane.Width} × {pane.Height}";
        // Clamped to the monitor the element is on, matching how the painted pill is clamped
        // to its surface's client area — otherwise an element against the right or bottom
        // edge hit-tests somewhere the pill is not drawn. Sized at that monitor's scale for
        // the same reason: the pill you can click has to be the pill you can see.
        SD.Rectangle pillMonitor = WF.Screen.FromRectangle(pane).Bounds;
        double pillScale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(pillMonitor);
        return SelectorChrome.SizePillRect(
            pane, SelectorChrome.MeasureLabel(text, pillScale), pillMonitor, pillScale);
    }

    /// <summary>
    /// Cross-fades the pill between its size text and a camera glyph. The capture half of
    /// this already worked — a bare click takes the detected element — but nothing on screen
    /// said the highlighted thing was clickable.
    /// </summary>
    private void SetPillHover(bool hovering)
    {
        if (_pillHover == hovering)
            return;
        _pillHover = hovering;

        double from = _pillSwap;
        double to = hovering ? 1d : 0d;
        _pillTween?.Dispose();
        _pillTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                _pillSwap = from + ((to - from) * progress);
                if (ShutterPillScreenRect() is SD.Rectangle pill)
                    InvalidateScreenRect(Inflated(pill));
            },
            () => _pillTween = null);
    }

    /// <summary>0..1 opacity of the hint/confirmation pill; 0 means it is not on screen.</summary>
    private double _hintOpacity;
    private IDisposable? _hintTween;
    /// <summary>What that pill currently says — the first-run hint, or a copy confirmation.</summary>
    private string _hintText = string.Empty;
    /// <summary>True while that pill is carrying a confirmation or a failure rather than the
    /// first-run hint. The hint is suppressed once a selection is framed (it is about drawing
    /// one, and the action bar is there now); a MESSAGE is the only feedback channel the
    /// selector has, so it is painted in every state — including the states that are the only
    /// way to reach some of the messages at all.</summary>
    private bool _hintIsMessage;
    private IDisposable? _hintHold;

    /// <summary>How long a copy confirmation stays up before it fades.</summary>
    private const int MessageHoldMs = 1200;

    /// <summary>
    /// Puts a one-line confirmation where the hint pill goes, held briefly and then faded.
    /// A toast window would be the wrong surface here: the selector is a topmost full-screen
    /// overlay, so the toast lands behind it.
    ///
    /// The hold is a <see cref="Motion.Delay"/>, not a tween. Run() applies its end state
    /// inline when animations are off, so a tweened hold collapsed the whole sequence —
    /// in, hold, out — inside one keystroke and the pill was never painted at all. Only the
    /// fades are allowed to disappear with the animation setting; the message is not.
    /// </summary>
    private void FlashMessage(string text)
    {
        _hintText = text;
        _hintIsMessage = true;
        FadeHint(arriving: true);
        _hintHold?.Dispose();
        _hintHold = WinShot.Core.Motion.Delay(MessageHoldMs, () =>
        {
            _hintHold = null;
            FadeHint(arriving: false);
        });
    }

    /// <summary>Box, in px, that the darkest-pixel sampler searches around the pointer.</summary>
    private const int TextColorSampleBox = 20;

    /// <summary>
    /// Copies the colour under the pointer as hex — or, when a region is framed, that region's
    /// AVERAGE colour. The loupe has always shown the single-pixel value and there was no way
    /// to take it, and off a gradient, a photo or an antialiased logo the single pixel is the
    /// wrong answer anyway.
    /// </summary>
    /// <summary>
    /// Why a colour cannot be sampled yet. The freeze is deferred on purpose (WGC setup alone
    /// can cost 300–1700 ms under load), so <c>_frozen</c> is null for the first fraction of
    /// every session even with the setting already on — sending that user to a checkbox that
    /// is already ticked is the wrong answer to the wrong question.
    /// </summary>
    private string NoFrozenScreenMessage => _options.FreezeScreen
        ? "Still capturing the screen — try again in a moment."
        : "Color needs the frozen screen; turn on Freeze screen in Settings.";

    private void CopyColorUnderCursor()
    {
        if (_frozen is null)
        {
            FlashMessage(NoFrozenScreenMessage);
            return;
        }

        if (ColorSampleRegion() is SD.Rectangle region)
        {
            CopyColor(WinShot.Editor.EyedropperSampler.Average(_frozen, FrozenRect(region)), "average");
            return;
        }

        CopyColor(WinShot.Editor.EyedropperSampler.SampleAt(_frozen, FrozenPoint(_currentScreen)));
    }

    /// <summary>
    /// Copies the darkest pixel in a small box around the pointer — the colour of a line of
    /// type, without having to land the cursor on a glyph stroke, which at 9 pt is a coin toss.
    /// </summary>
    private void CopyTextColorUnderCursor()
    {
        if (_frozen is null)
        {
            FlashMessage(NoFrozenScreenMessage);
            return;
        }

        CopyColor(
            WinShot.Editor.EyedropperSampler.Darkest(_frozen, FrozenPoint(_currentScreen), TextColorSampleBox),
            "text");
    }

    /// <summary>The framed region C should average, or null when nothing is framed and the
    /// single pixel under the pointer is the answer.</summary>
    internal SD.Rectangle? ColorSampleRegion()
    {
        if (_pendingScreen is SD.Rectangle pending)
            return pending;
        if (_dragging && _dragMoved)
            return DragRect();
        if (_mode == SelectorMode.Window && _hoverWindow is not null)
            return _hoverWindow.Bounds;
        if (_paneHover && _hoverPane is SD.Rectangle pane)
            return pane;
        return null;
    }

    /// <summary>
    /// Copies a sampled colour as hex and says what happened. The write is awaited rather
    /// than dropped: a clipboard held open by another process used to fault an unobserved
    /// Task behind a confident "Copied #RRGGBB", so the one action with no other feedback
    /// channel reported success for a paste that would return the previous contents.
    /// </summary>
    private async void CopyColor(SD.Color color, string? kind = null)
    {
        // The selector is POOLED: Complete() parks it, it is not disposed, so IsDisposed is
        // never true and a retrying clipboard write (up to 300 ms) that landed after Escape
        // started a tween and a 1.2 s hold on a hidden window — and could paint a stale
        // "Copied #RRGGBB" over the NEXT session. Guard on the session, as the freeze does.
        TaskCompletionSource<WF.DialogResult>? session = _completion;
        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        try
        {
            await CaptureService.SetTextToClipboardAsync(hex).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("Copying a sampled colour to the clipboard failed", ex);
            if (IsLiveSession(session))
                FlashMessage(FriendlyError.For(ex));
            return;
        }

        if (IsLiveSession(session))
            FlashMessage(kind is null ? $"Copied {hex}" : $"Copied {kind} color {hex}");
    }

    /// <summary>Whether the session an async continuation started in is still the open one.</summary>
    private bool IsLiveSession(TaskCompletionSource<WF.DialogResult>? session) =>
        !IsDisposed && session is not null && _completion == session && !session.Task.IsCompleted;

    /// <summary>A screen rectangle as an offset into the frozen virtual-desktop snapshot.</summary>
    private SD.Rectangle FrozenRect(SD.Rectangle screen) =>
        new(screen.X - _vs.X, screen.Y - _vs.Y, screen.Width, screen.Height);

    /// <summary>A screen point as an offset into the frozen virtual-desktop snapshot.</summary>
    private SD.Point FrozenPoint(SD.Point screen) => new(screen.X - _vs.X, screen.Y - _vs.Y);

    /// <summary>
    /// Fades the hint in when a session opens and out on the first drag. Both directions go
    /// through here so the pill can never be left half-faded by a retarget mid-flight.
    /// </summary>
    private void FadeHint(bool arriving)
    {
        _hintTween?.Dispose();
        _hintTween = null;
        if (!arriving && _hintOpacity <= 0d)
            return;

        double from = _hintOpacity;
        double to = arriving ? 1d : 0d;
        // The hint is drawn on the follow frames, so ShowAsync starts the 8 ms tick for it.
        // Nothing used to stop it, so a user with the crosshair off and the magnifier off —
        // exactly the configuration NeedsCursorFollow exists to detect — paid a full-session
        // cursor-follow tick for the first five sessions after install.
        // Re-evaluated in onDone, never captured: Alt (magnifier) or Space (window mode)
        // arriving DURING the fade starts a follow tick that a decision made 140 ms earlier
        // would then stop, and the loupe stops tracking an idle cursor.
        bool ReleaseFollowTick() => !arriving && !_options.NeedsCursorFollow && !_paneHover &&
            !_magnifierOn && _mode != SelectorMode.Window;
        _hintTween = WinShot.Core.Motion.Run(
            arriving ? WinShot.Core.Motion.EnterDurationMs : WinShot.Core.Motion.QuickMs,
            arriving ? WinShot.Core.Motion.EaseOutCubic : WinShot.Core.Motion.EaseInCubic,
            progress =>
            {
                _hintOpacity = from + ((to - from) * progress);
                InvalidateHintBand();
            },
            () =>
            {
                _hintTween = null;
                // A message that has faded is over. Leaving the flag set meant one C press
                // early in a session permanently ate the framed-selection hint, which is the
                // only place the nudge, [ / ] and A gestures are advertised.
                if (!arriving && _hintOpacity <= 0d)
                    _hintIsMessage = false;
                if (ReleaseFollowTick() && _hintOpacity <= 0d)
                    StopFollowMotion();
            });
    }

    /// <summary>
    /// Where the pill goes on this surface: its usual spot, or pushed below the action bar
    /// when the two would overlap. A confirmation that lands on top of Cancel/Done covers the
    /// controls it is confirming, which is worse than showing it a little lower.
    /// </summary>
    private int? MessagePillTop(SD.Size clientSize, SD.Rectangle monitorBounds)
    {
        if (_pendingScreen is not SD.Rectangle pending)
            return null;
        SD.Rectangle pill = SelectorChrome.HintPillRect(
            clientSize, _hintText, WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds));
        var (bar, _, _, _) = ActionBarRects(BarAnchor ?? pending);
        SD.Rectangle barLocal = ToLocal(bar, monitorBounds);
        if (!pill.IntersectsWith(barLocal))
            return null;
        int below = barLocal.Bottom + 8;
        return below + pill.Height <= clientSize.Height ? below : Math.Max(0, barLocal.Top - 8 - pill.Height);
    }

    /// <summary>Repaints only the strip the hint pill occupies, on every surface.</summary>
    private void InvalidateHintBand()
    {
        foreach (SD.Rectangle monitor in HintMonitors())
        {
            InvalidateScreenRect(new SD.Rectangle(
                monitor.Left, monitor.Top + (monitor.Height / 8) - 8, monitor.Width, 64));
        }

        // A message shown over a framed selection dodges the action bar, so the strip it can
        // land in is around the bar, not one eighth down.
        if (BarAnchor is SD.Rectangle pending)
        {
            var (bar, _, _, _) = ActionBarRects(pending);
            InvalidateScreenRect(new SD.Rectangle(
                bar.Left - 200, bar.Top - 64, bar.Width + 400, bar.Height + 128));
        }
    }

    private IEnumerable<SD.Rectangle> HintMonitors()
    {
        yield return _monitorBounds;
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed)
                yield return pane.MonitorBounds;
        }
    }

    internal void HandleMouseMove()
    {
        if (_sessionEnded)
            return;
        _currentScreen = CursorScreen();

        if (_dragging)
        {
            if (!_dragMoved &&
                Math.Abs(_currentScreen.X - _dragStartScreen.X) < DragThresholdPx &&
                Math.Abs(_currentScreen.Y - _dragStartScreen.Y) < DragThresholdPx)
            {
                return;
            }

            if (!_dragMoved)
            {
                _dragMoved = true;
                // A drag armed in the band just outside a framed selection still carries
                // that selection; it replaces it, exactly like a drag started further out.
                _pendingScreen = null;
                // The hint has done its job the moment a drag starts — once, on the
                // transition. Re-running it every move restarted a 140 ms tween per frame,
                // so the fade never finished and each frame cost a timer plus a full-width
                // hint-band invalidate on every monitor for a pill that is not even painted
                // while dragging.
                FadeHint(arriving: false);
            }
            InvalidateAllSurfaces();
            return;
        }

        if (_resizeHandle >= 0)
        {
            _pendingScreen = ResizeRect(_adjustStartRect, _resizeHandle, _currentScreen);
            InvalidateAllSurfaces();
            return;
        }

        if (_movingPending)
        {
            _pendingScreen = MoveRect(_adjustStartRect, _currentScreen.X - _adjustAnchor.X, _currentScreen.Y - _adjustAnchor.Y);
            InvalidateAllSurfaces();
            return;
        }

        // Cancel / Done hover feedback (only the bar rect repaints on a state change).
        if (BarAnchor is SD.Rectangle barPending)
        {
            var (barRect, padRect, cancelRect, doneRect) = ActionBarRects(barPending);
            int hover = doneRect.Contains(_currentScreen) ? 1
                : cancelRect.Contains(_currentScreen) ? 0
                : padRect.Contains(_currentScreen) ? 2 : -1;
            if (hover != _hoverBarButton)
            {
                _hoverBarButton = hover;
                AnimateBarFills();
                InvalidateScreenRect(barRect);
            }

            // Edge affordance. Nothing else on the selection says which pixels answer the
            // click-to-snap gesture, so the edge under the pointer thickens and the cursor
            // becomes a sizing arrow before anything is committed.
            MarqueeSide edge = barRect.Contains(_currentScreen) ||
                    SelectionHandles.HitTest(_currentScreen, barPending, ScaleAt(_currentScreen)) >= 0
                ? MarqueeSide.None
                : MarqueeEdge.HitTest(barPending, _currentScreen, EdgeToleranceAt(_currentScreen));
            if (edge != _hoverEdge)
            {
                _hoverEdge = edge;
                AnimateEdgeGlow();
            }
        }
        else if (_hoverEdge != MarqueeSide.None)
        {
            _hoverEdge = MarqueeSide.None;
            AnimateEdgeGlow();
        }

        if (_mode == SelectorMode.Window)
        {
            // Window highlight tracks the cursor. Repaint the highlight only when the
            // TARGET changed — this runs off the 8 ms follow tick, so repainting every
            // monitor per cursor pixel is a full-screen redraw for nothing. The size
            // readout rides the cursor, so its band still repaints every move.
            SetHoverWindow(ResolveWindow(_currentScreen));
            InvalidateFollowRegion(_lastFollowScreen, _currentScreen);
            _lastFollowScreen = _currentScreen;
            return;
        }

        // Element detection is always on, ShareX-style: the rect under the cursor lights up
        // the moment the overlay opens, no modifier to discover. Click it to take it;
        // click-drag instead and the marquee wins — a drawn rect is never second-guessed.
        if (_paneHover && _pendingScreen is null && !_dragging)
        {
            // Moving onto the pill must not re-resolve the element out from under it — the
            // pill sits outside the rect it labels, so re-resolving would swap the highlight
            // the instant you reached for the shutter.
            bool onPill = ShutterPillScreenRect() is SD.Rectangle pill && pill.Contains(_currentScreen);
            SetPillHover(onPill);
            if (!onPill)
                UpdateHoverElement(_currentScreen);
        }
        else if (_pillHover)
        {
            SetPillHover(false);
        }

        // Area mode, idle: keep the crosshair + loupe glued to the cursor. Repaint only the
        // old+new crosshair bands and loupe box (not the whole monitor), so a large external
        // display stays smooth. Skip while adjusting a pending rect (no crosshair then).
        if (_pendingScreen is null)
        {
            InvalidateFollowRegion(_lastFollowScreen, _currentScreen);
            _lastFollowScreen = _currentScreen;
        }
    }

    /// <summary>
    /// Cursor-follow heartbeat (~66 fps) while a selection is open. Repaints when the cursor
    /// moves or the Ctrl state changes (Ctrl gates the crosshair in "command" mode), so the
    /// crosshair/loupe track the cursor even when no hover WM_MOUSEMOVE reaches the overlay.
    /// </summary>
    private void OnFollowTick(object? sender, EventArgs e)
    {
        try
        {
            SD.Point p = CursorScreen();
            // Before anything else: a window that raised itself after the overlay opened
            // (a crash dialog, an always-on-top utility) would otherwise sit on top and
            // swallow every click inside its rect while the highlight below kept tracking.
            SelectorForeground.KeepOnTop(SurfaceHandles(), p);
            bool ctrl = (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control;
            if (p == _currentScreen && ctrl == _lastCtrlDown)
                return;
            _lastCtrlDown = ctrl;
            HandleMouseMove();
        }
        catch (Exception ex)
        {
            // A transient GDI/device error (e.g. an RDP display blip) must not crash the app
            // from the timer callback; the next tick recovers on its own.
            Log.Error("Selector follow-tick failed (non-fatal)", ex);
        }
    }

    /// <summary>Whether the full-bleed crosshair guide lines should be drawn, per the
    /// Screenshots "Crosshair mode" setting (always / only while Ctrl is held / never).</summary>
    private bool CrosshairLinesVisible()
    {
        bool controlPressed = (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control;
        return _options.IsCrosshairVisible(controlPressed);
    }

    /// <summary>Wheel up widens the highlight to the next enclosing candidate, wheel down
    /// narrows it back. The detector is usually right about WHERE and only sometimes right
    /// about HOW MUCH; this makes the second half a one-notch correction.</summary>
    internal void HandleMouseWheel(WF.MouseEventArgs e)
    {
        if (_sessionEnded) return;
        if (!_paneHover || _pendingScreen is not null || _dragging || _hoverLadder.Count == 0)
            return;

        int next = Math.Clamp(_ladderIndex + (e.Delta > 0 ? 1 : -1), 0, _hoverLadder.Count - 1);
        if (next == _ladderIndex)
            return;

        _ladderIndex = next;
        // An explicit choice must not be second-guessed by the next mouse twitch.
        _wheelPinned = _hoverLadder[next];
        SetHoverPane(_hoverLadder[next]);
    }

    internal void HandleMouseUp(WF.MouseEventArgs e)
    {
        if (_sessionEnded || e.Button != WF.MouseButtons.Left)
            return;

        _currentScreen = CursorScreen();

        if (_mode == SelectorMode.Window)
        {
            if (_hoverWindow is not null)
                Confirm(VirtualFromScreen(_hoverWindow.Bounds), _hoverWindow.Handle);
            return;
        }

        if (_pressedBarButton >= 0)
        {
            int pressed = _pressedBarButton;
            _pressedBarButton = -1;
            AnimateBarFills();
            if (_pendingScreen is SD.Rectangle pend)
            {
                var (bar, padButton, cancel, done) = ActionBarRects(BarAnchor ?? pend);
                InvalidateScreenRect(bar);
                if (pressed == 1 && done.Contains(_currentScreen)) Confirm(VirtualFromScreen(pend));
                else if (pressed == 0 && cancel.Contains(_currentScreen)) Complete(WF.DialogResult.Cancel);
                else if (pressed == 2 && padButton.Contains(_currentScreen))
                {
                    // Pad returns the rect unchanged when the padded one would leave the
                    // desktop, and GlideSelectionTo no-ops on that — so a selection already
                    // flush with an edge swallowed the press after playing its press fill.
                    SD.Rectangle padded = MarqueeEdge.Pad(pend, MarqueeEdge.PaddingStepPx, _vs);
                    if (padded != pend)
                        GlideSelectionTo(padded);
                    else
                        FlashMessage("The selection already reaches the edge of the screen.");
                }
            }
            return;
        }

        if (_pressedShutter)
        {
            _pressedShutter = false;
            // Released on the pill it was pressed on: take the element the pill labels.
            if (ShutterPillScreenRect() is SD.Rectangle shutter &&
                shutter.Contains(_currentScreen) &&
                _hoverPane is SD.Rectangle shot)
            {
                SelectedByPaneClick = true;
                Confirm(VirtualFromScreen(shot));
            }
            return;
        }

        // Ctrl+click retargets to the detected element. This has to settle at RELEASE, not
        // at press: staging the element on mouse-down consumed the press, so Ctrl+DRAG — the
        // documented per-drag override of "Adjust selection before capturing", and what the
        // Settings hint promises — could never start over a detected element, which is
        // almost anywhere on screen. A press that travelled is a drag and Ctrl means adjust;
        // a press that stayed put is a click and Ctrl means retarget.
        if (_pressedEdge != MarqueeSide.None)
        {
            MarqueeSide pressed = _pressedEdge;
            _pressedEdge = MarqueeSide.None;
            if (!HasTravelled(_currentScreen))
            {
                // A press that stayed put is the click-one-edge gesture.
                _movingPending = false;
                _dragging = false;
                _dragMoved = false;
                if (_pendingScreen is SD.Rectangle onEdge &&
                    MarqueeEdge.HitTest(onEdge, _currentScreen, EdgeToleranceAt(_currentScreen)) == pressed)
                {
                    SD.Rectangle snapped = MarqueeEdge.SnapEdge(onEdge, pressed, _snapEdgesX, _snapEdgesY);
                    if (snapped != onEdge)
                        GlideSelectionTo(snapped);
                    else
                        FlashMessage("Nothing on screen to line that edge up with.");
                }
                InvalidateAllSurfaces();
                return;
            }

            // It travelled, so it was a move of the selection body or a new marquee drawn
            // from just outside the border. Both are settled by the paths below.
        }

        // A Ctrl-held press that grabbed a handle, an edge or the selection body is an
        // adjustment, not a retarget: releasing it without travelling used to throw the
        // selection away and replace it with whatever sat under the pointer.
        if (_paneHover &&
            (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control &&
            _resizeHandle < 0 && !_movingPending &&
            !HasTravelled(_currentScreen))
        {
            _dragging = false;
            _dragMoved = false;
            _resizeHandle = -1;
            _movingPending = false;
            if (SnapRectAt(_currentScreen) is not SD.Rectangle element)
            {
                // The other three retargets pair success with a failure line — A, an edge
                // press, Pad edges. Ctrl+click is the least discoverable of the four, so
                // failing mute read as the modifier simply not being wired at all.
                FlashMessage("Nothing under the pointer to snap to.");
                InvalidateAllSurfaces();
                return;
            }
            if (_pendingScreen is null)
            {
                SetPendingRegion(element);
                InvalidateAllSurfaces();
            }
            else
            {
                GlideSelectionTo(element);
            }
            SelectedByPaneClick = true; // set last: GlideSelectionTo clears the provenance
            return;
        }

        if (_resizeHandle >= 0 || _movingPending)
        {
            _resizeHandle = -1;
            _movingPending = false;
            InvalidateAllSurfaces();
            return;
        }

        if (!_dragging)
            return;

        _dragging = false;
        if (_dragMoved)
        {
            var rect = DragRect();
            rect.Intersect(_vs);
            bool controlPressed = (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control;
            if (rect.Width >= MinConfirmDragPx && rect.Height >= MinConfirmDragPx &&
                !_options.ShouldAdjustSelection(controlPressed))
            {
                // Releasing takes the shot. Every region capture used to cost a second
                // gesture — drag, then aim at Done or reach for Enter — for an adjustment
                // most captures never wanted; the adjustable path is now the opt-in.
                Confirm(VirtualFromScreen(rect));
                return;
            }

            if (rect.Width > 0 && rect.Height > 0)
                SetPendingRegion(rect);
            InvalidateAllSurfaces();
        }
        else if (_paneHover && _pendingScreen is null && _hoverPane is SD.Rectangle pane)
        {
            // Bare click on the highlighted pane accepts it (CleanShot's scrolling flow).
            SelectedByPaneClick = true;
            Confirm(VirtualFromScreen(pane));
        }
    }

    // ----------------------------------------------------------- painting (per surface)

    /// <summary>
    /// True when PaintSurface's own background covers every pixel, so the surface does not
    /// need clearing first. Without a frozen snapshot it paints a SEMI-transparent dim that
    /// relies on the black underneath, so the Clear is still required there.
    /// </summary>
    internal bool HasFullSurfaceBackground => _frozen is not null;

    internal void PaintSurface(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
        SD.Size clientSize = monitorBounds.Size;
        bool cursorOnThisSurface = monitorBounds.Contains(_currentScreen);
        // THIS surface's monitor scale. Every pill measures and draws through it, so the
        // chrome is sized for the display it lands on and not for the primary.
        double s = WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds);

        // Advance the glide on the paint clock, not on a timer tick. Update() reads a
        // stopwatch, so calling it once per surface in a multi-monitor frame is harmless.
        SD.Rectangle tweenBefore = _hoverTween.Current;
        bool tweening = _hoverTween.IsActive && _hoverTween.Update();

        // Frozen desktop slice + uniform dim, baked once and blitted as one opaque copy.
        var dimmed = GetDimmedBackground(monitorBounds);
        if (dimmed is not null)
        {
            var dest = new SD.Rectangle(0, 0, clientSize.Width, clientSize.Height);
            // SourceCopy: the background covers the whole surface, so there is nothing to
            // blend with and a straight copy is a lot cheaper at full-monitor size.
            var previousCompositing = g.CompositingMode;
            g.CompositingMode = SD.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(dimmed, dest, 0, 0, dimmed.Width, dimmed.Height, SD.GraphicsUnit.Pixel);
            g.CompositingMode = previousCompositing;
        }
        else
        {
            // No frozen snapshot (capture failed): select over a plain dim.
            using var dim = new SD.SolidBrush(SD.Color.FromArgb(115, 0, 0, 0));
            g.FillRectangle(dim, 0, 0, clientSize.Width, clientSize.Height);
        }

        // The active selection (drawing / adjustable pending / hovered window) shows the
        // frozen pixels at full brightness with an accent border.
        SD.Rectangle? brightScreen = null;
        SD.Rectangle? labelScreen = null;
        bool showHandles = false;
        if (_dragging && _dragMoved)
        {
            brightScreen = DragRect();
        }
        else if (_pendingScreen is SD.Rectangle pending)
        {
            brightScreen = _pendingGlide.IsActive ? _pendingGlide.Current : pending;
            showHandles = true;
        }
        else if (_mode == SelectorMode.Window && _hoverWindow is not null)
        {
            brightScreen = _hoverWindow.Bounds;
        }
        else if (_paneHover && _hoverPane is SD.Rectangle hoverPane)
        {
            // Mid-glide the border rides the interpolated rect, but the readout stays on the
            // real one - spinning digits for 200 ms reads as a glitch, not as motion.
            brightScreen = tweening ? _hoverTween.Current : hoverPane;
            labelScreen = hoverPane;
        }

        if (brightScreen is SD.Rectangle bright)
        {
            var local = ToLocal(bright, monitorBounds);
            BrightenRegion(g, monitorBounds, local);
            // Scaled like the edge-press glow below, which starts at this same 2 px weight:
            // an unscaled base made hovering an edge pop the stroke a pixel wider at 150 %
            // before the tween had done anything.
            using (var pen = new SD.Pen(Accent, (float)(2d * Math.Max(1d, s))))
                g.DrawRectangle(pen, local);

            // The edge under the pointer thickens: the only thing on the selection that says
            // "clicking here moves this one edge onto the nearest boundary".
            if (showHandles && _hoverEdge != MarqueeSide.None && _edgeGlow > 0.01d)
            {
                float weight = (float)((2d + (4d * Math.Clamp(_edgeGlow, 0d, 1d))) * Math.Max(1d, s));
                using var edgePen = new SD.Pen(Accent, weight);
                switch (_hoverEdge)
                {
                    case MarqueeSide.Left:
                        g.DrawLine(edgePen, local.Left, local.Top, local.Left, local.Bottom);
                        break;
                    case MarqueeSide.Right:
                        g.DrawLine(edgePen, local.Right, local.Top, local.Right, local.Bottom);
                        break;
                    case MarqueeSide.Top:
                        g.DrawLine(edgePen, local.Left, local.Top, local.Right, local.Top);
                        break;
                    default:
                        g.DrawLine(edgePen, local.Left, local.Bottom, local.Right, local.Bottom);
                        break;
                }
            }

            if (_mode == SelectorMode.Window && _hoverWindow is not null && _pendingScreen is null && !_dragging)
            {
                // A hand-drawn marquee and a whole window looked identical — both a plain
                // accent border over undimmed pixels. The wash says "this is one object,
                // and it is the thing that will be captured".
                int washAlpha = (int)Math.Round(WindowWashAlpha * Math.Clamp(_hoverWash, 0d, 1d));
                if (washAlpha > 0)
                {
                    using var wash = new SD.SolidBrush(SD.Color.FromArgb(washAlpha, Accent));
                    g.FillRectangle(wash, local);
                }
                // One pill per window, not one per monitor the window overlaps: this block
                // runs on every surface, so a window spanning two displays used to draw its
                // name twice, each centred in that display's slice of it.
                if (_hoverAppName.Length > 0 && cursorOnThisSurface)
                {
                    SD.Size pill = SelectorChrome.MeasureLabel(_hoverAppName, s);
                    SelectorChrome.DrawLabel(g, clientSize, _hoverAppName,
                        local.Left + (local.Width - pill.Width) / 2,
                        local.Top + (local.Height - pill.Height) / 2, s,
                        // Rides the wash it sits on: landing solid over a tint still ramping
                        // is the same pop the wash exists to avoid.
                        Math.Clamp(_hoverWash, 0d, 1d));
                }
            }

            if (showHandles)
                DrawHandles(g, monitorBounds, bright);

            if (cursorOnThisSurface)
            {
                SD.Rectangle sized = labelScreen ?? bright;
                // The readout is placed off the rect it LABELS, never off the interpolated
                // border. `local` rides _hoverTween mid-retarget while ShutterPillScreenRect()
                // — the thing a press is tested against — always uses the settled element, so
                // deriving the position from `local` painted the shutter somewhere it could
                // not be clicked for the whole 200 ms glide.
                SD.Rectangle labelLocal = ToLocal(sized, monitorBounds);
                string sizeText = $"{sized.Width} × {sized.Height}";
                int pillGap = WinShot.Recording.RecordingMonitorDpi.Px(8, s);
                SD.Point at = new(labelLocal.Right + pillGap, labelLocal.Bottom + pillGap);
                if (_mode == SelectorMode.Window && _hoverWindow is not null && _pendingScreen is null && !_dragging)
                {
                    SD.Point atCursor = ToLocal(_currentScreen, monitorBounds);
                    SD.Size follow = SelectorChrome.ReadoutOffset(s);
                    at = new SD.Point(atCursor.X + follow.Width, atCursor.Y + follow.Height);
                }
                else if (_pendingScreen is not null)
                {
                    // The Cancel/Done bar owns the space below a pending region — on a narrow
                    // selection the pill would land on Cancel — so the readout rides the
                    // selection's top-right, flipped just inside when the region touches the
                    // monitor top.
                    SD.Size pill = SelectorChrome.MeasureLabel(sizeText, s);
                    int pillTop = labelLocal.Top - pill.Height - pillGap;
                    if (pillTop < 0) pillTop = labelLocal.Top + pillGap;
                    at = new SD.Point(Math.Max(0, labelLocal.Right - pill.Width), pillTop);
                }
                if (labelScreen is not null)
                    SelectorChrome.DrawShutterPill(g, clientSize, sizeText, at, _pillSwap, s);
                else
                    SelectorChrome.DrawLabel(g, clientSize, sizeText, at.X, at.Y, s);
            }
        }

        // Ask for the next frame. Self-sustaining for the 200 ms of the glide, then it stops
        // on its own because Update() clears IsActive once the stopwatch passes the duration.
        if (tweenBefore != _hoverTween.Current)
            InvalidateHoverRegion(tweenBefore, _hoverTween.Current);

        // Cancel / Done bar on the adjustable selection: below the region, flipped above when it
        // runs to the screen bottom, tucked inside the bottom when the region spans the full height.
        if (BarAnchor is SD.Rectangle pendBar)
        {
            var (bar, padButton, cancel, done) = ActionBarRects(pendBar);
            if (bar.IntersectsWith(monitorBounds))
                DrawActionBar(g, monitorBounds, bar, padButton, cancel, done);
        }

        // Opacity is the only gate. A `!_dragging` gate unpainted the pill on the mouse-DOWN,
        // one event before the 140 ms exit fade starts on the first move -- so the entrance
        // was answered by a hard cut and the fade animated a value nobody could see. Worse, a
        // press that never travelled (the bare element click, now the common gesture) cleared
        // _dragging on release and blinked the pill straight back at full strength.
        if (_hintOpacity > 0d && cursorOnThisSurface)
        {
            SelectorChrome.DrawHint(
                g, clientSize, _hintText, _hintOpacity, s, MessagePillTop(clientSize, monitorBounds));
        }

        // Crosshair + loupe only when drawing/idle in Area mode (not while adjusting a pending
        // rect, and never in Window mode where the window highlight is the affordance).
        bool inCrosshairContext = _mode == SelectorMode.Area && _pendingScreen is null && cursorOnThisSurface;
        if (inCrosshairContext)
        {
            // Crosshair guide lines honor the "Crosshair mode" setting; the magnifier/color
            // loupe honors "Show magnifier". Both default on when no settings are attached.
            if (CrosshairLinesVisible())
                SelectorChrome.DrawCrosshair(g, clientSize, ToLocal(_currentScreen, monitorBounds), s);

            // While dragging, the readout says how big the selection is; idle, where the
            // pointer is. The loupe carries it when the magnifier is on, and the crosshair
            // carries it on its own when the magnifier is off — either way the selector
            // always answers "where am I".
            SD.Size dragSize = _dragging && _dragMoved
                ? DragRect().Size
                : SD.Size.Empty;
            if (_magnifierOn)
            {
                FastSelectorLoupeRenderer.Draw(
                    g, clientSize, _vs, ToLocal(_currentScreen, monitorBounds), _currentScreen, _frozen, dragSize);
            }
            else if (dragSize.IsEmpty)
            {
                // Mid-drag the selection's own size pill already sits at the corner the
                // cursor is on; a second copy 16 px away is the same number twice.
                SelectorChrome.DrawCursorReadout(g, clientSize,
                    SelectorReadout.Text(_currentScreen, dragSize), ToLocal(_currentScreen, monitorBounds), s);
            }
        }
    }

    /// <summary>
    /// Returns this monitor's frozen slice with the selection dim baked in, building and
    /// caching it on first use. Lets the hot follow-paint be one opaque blit instead of a
    /// per-frame snapshot crop plus a full-screen software alpha fill.
    /// </summary>
    private SD.Bitmap? GetDimmedBackground(SD.Rectangle monitorBounds)
    {
        if (_frozen is null) return null;
        if (_dimmedCache.TryGetValue(monitorBounds, out var cached))
            return cached;

        // Format32bppRgb, not PArgb: the result is opaque, and an alpha channel would force
        // GDI+ to blend this full-monitor bitmap per pixel on every single paint.
        var bmp = new SD.Bitmap(monitorBounds.Width, monitorBounds.Height, SD.Imaging.PixelFormat.Format32bppRgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
            DrawFrozenSlice(g, monitorBounds);
            using var dim = new SD.SolidBrush(SD.Color.FromArgb(115, 0, 0, 0));
            g.FillRectangle(dim, 0, 0, monitorBounds.Width, monitorBounds.Height);
        }
        _dimmedCache[monitorBounds] = bmp;
        return bmp;
    }

    /// <summary>Paints this monitor's slice of the frozen snapshot at 1:1 (pure offset).</summary>
    private void DrawFrozenSlice(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        if (_frozen is null) return;
        var src = new SD.Rectangle(monitorBounds.X - _vs.X, monitorBounds.Y - _vs.Y, monitorBounds.Width, monitorBounds.Height);
        g.DrawImage(_frozen, new SD.Rectangle(0, 0, monitorBounds.Width, monitorBounds.Height), src, SD.GraphicsUnit.Pixel);
    }

    /// <summary>Re-draws the frozen slice clipped to a region so it shows undimmed.</summary>
    private void BrightenRegion(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle localRect)
    {
        if (_frozen is null) return;
        var clip = SD.Rectangle.Intersect(localRect, new SD.Rectangle(0, 0, monitorBounds.Width, monitorBounds.Height));
        if (clip.Width < 1 || clip.Height < 1) return;

        var state = g.Save();
        g.SetClip(clip);
        DrawFrozenSlice(g, monitorBounds);
        g.Restore(state);
    }

    /// <summary>Draws the 8 move/resize handles on the pending selection.</summary>
    private void DrawHandles(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle screenRect) =>
        SelectionHandles.Draw(g, ToLocal(screenRect, monitorBounds), _handleScale, _resizeHandle,
            WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds));

    private void DisposeFrozen()
    {
        _frozen?.Dispose();
        _frozen = null;
        foreach (var bmp in _dimmedCache.Values)
            bmp.Dispose();
        _dimmedCache.Clear();
    }

    private SD.Bitmap? CropFrozen(SD.Rectangle virtualRect)
    {
        if (_frozen is null) return null;
        var src = SD.Rectangle.Intersect(virtualRect, new SD.Rectangle(0, 0, _frozen.Width, _frozen.Height));
        if (src.Width < 1 || src.Height < 1) return null;

        var crop = new SD.Bitmap(src.Width, src.Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(crop);
        g.DrawImage(_frozen, new SD.Rectangle(0, 0, src.Width, src.Height), src, SD.GraphicsUnit.Pixel);
        return crop;
    }

    /// <summary>
    /// Cuts the shadowed wedges out of the corners of a window capture, when the setting is on
    /// and that window is rounded. A no-op for a dragged region, a square-cornered window, and
    /// on the live-grab fallback path, which never reaches this crop at all.
    /// </summary>
    private void TrimWindowShadow(IntPtr window, bool wholeWindowCaptured)
    {
        if (WinShot.Core.WindowShadowTrim.TrimWindowCapture(
                _capturedRegion, window,
                _settings?.Current.TrimWindowShadow ?? true,
                wholeWindowCaptured) is SD.Bitmap trimmed)
        {
            _capturedRegion!.Dispose();
            _capturedRegion = trimmed;
        }
    }

    /// <summary>Returns the region cropped from the frozen snapshot at confirm time and transfers
    /// ownership to the caller (null if freeze was unavailable). Null after the first call.</summary>
    public SD.Bitmap? TakeCapturedRegion()
    {
        var bmp = _capturedRegion;
        _capturedRegion = null;
        return bmp;
    }

    /// <summary>
    /// Resolves the window under the cursor by real z-order (WindowFromPoint) so a small
    /// foreground window wins over a larger background one; falls back to the first
    /// bounds-containing window when the topmost hwnd isn't in the cached list.
    /// </summary>
    private WindowInfo? ResolveWindow(SD.Point screenPoint)
    {
        IntPtr top = WindowEnumerator.TopLevelWindowFromPoint(screenPoint);
        if (top != IntPtr.Zero && top != Handle && !IsPaneHandle(top))
        {
            var match = _windows.FirstOrDefault(w => w.Handle == top);
            if (match is not null)
                return match;
        }

        return _windows.FirstOrDefault(w => w.Bounds.Contains(screenPoint));
    }

    /// <summary>
    /// Hover resolution is the FIRST rect in the snap list containing the cursor,
    /// nothing else. The list is EnumWindows + EnumChildWindows + client rects, deepest-first
    /// in z-order, built once before the overlay opened — so the answer is instant, identical
    /// on every pass, and never revised after the fact. Where an app draws its whole UI into
    /// one HWND (Chromium, Electron, WPF) the answer is the window, same as every other
    /// HWND-level selector — and the confidence of never being second-guessed is the feature.
    /// </summary>
    private void UpdateHoverElement(SD.Point point)
    {
        // A wheel-chosen rung is an explicit decision: keep it while the cursor stays inside.
        if (_wheelPinned is SD.Rectangle pinned && pinned.Contains(point))
            return;
        _wheelPinned = null;

        SD.Rectangle? snap = SnapRectAt(point);

        if (!_elementGranularity && snap is null)
        {
            // Scrolling-capture flow: fall back to the scrollable pane / window under the
            // cursor (pre-existing behavior, unrelated to element snapping).
            WindowInfo? win = ResolveWindow(point);
            snap = win is null
                ? null
                : WinShot.Scrolling.ScrollPaneDetector.QuickPaneRect(win.Handle, point) ?? win.Bounds;
        }

        if (snap is SD.Rectangle chosen)
        {
            BuildLadder(point, chosen);
            SetHoverPane(chosen);
        }
    }

    /// <summary>
    /// Collects the nesting of rects under the cursor - the resolved one plus every HWND rect
    /// containing the point - smallest first, so the wheel has somewhere to go in both
    /// directions. Rungs closer than the wobble tolerance are merged; stepping onto a rect
    /// eight pixels bigger is not a step a person can see.
    /// </summary>
    private void BuildLadder(SD.Point point, SD.Rectangle resolved)
    {
        var rungs = new List<SD.Rectangle> { resolved };
        foreach (var candidate in _snapRects)
        {
            if (candidate.Bounds.Contains(point))
                rungs.Add(candidate.Bounds);
        }

        var ladder = new List<SD.Rectangle>();
        foreach (var rung in rungs.OrderBy(r => (long)r.Width * r.Height))
        {
            if (ladder.Count == 0 || !IsWithin(ladder[^1], rung, WobbleTolerancePx))
                ladder.Add(rung);
        }

        _hoverLadder = ladder;
        _ladderIndex = ladder.FindIndex(r => IsWithin(r, resolved, WobbleTolerancePx));
        if (_ladderIndex < 0)
            _ladderIndex = 0;
    }

    /// <summary>
    /// The one place the hover highlight changes. Routing every tier through here is what
    /// keeps the glide honest: each new rect starts its slide from wherever the highlight
    /// currently sits, so a stale tier answer can never make it jump backwards.
    /// </summary>
    private void SetHoverPane(SD.Rectangle? rect)
    {
        if (rect == _hoverPane)
            return;

        if (_hoverPane is SD.Rectangle previous && rect is SD.Rectangle next)
        {
            _hoverTween.Retarget(previous, next);
            InvalidateHoverRegion(previous, next);
        }
        else
        {
            _hoverTween.Stop();
            InvalidateAllSurfaces();
        }
        _hoverPane = rect;
    }

    /// <summary>
    /// The one place the hovered WINDOW changes. Returns true when the target actually
    /// moved (and the old + new highlights were repainted); false is the common case on a
    /// cursor tick inside the same window, and repainting then is a whole-monitor redraw
    /// that shows nothing new.
    /// </summary>
    internal bool SetHoverWindow(WindowInfo? next)
    {
        if (next == _hoverWindow)
            return false;

        WindowInfo? previous = _hoverWindow;
        _hoverWindow = next;
        _hoverAppName = next is null ? string.Empty : AppNameFor(next);
        BeginHoverWash(next is not null, previous is not null);
        if (previous is not null && next is not null)
            InvalidateHoverRegion(previous.Bounds, next.Bounds);
        else if (previous is not null)
            InvalidateScreenRect(Inflated(previous.Bounds));
        else if (next is not null)
            InvalidateScreenRect(Inflated(next.Bounds));
        return true;
    }

    /// <summary>
    /// The friendly app name for a hovered window, resolved off the UI thread and cached for
    /// the session. <see cref="WindowEnumerator.AppNameFor"/> opens the target process and
    /// reads its PE version resource; this runs from the 8 ms cursor-follow tick, so sweeping
    /// the pointer across a row of windows used to stall the very thread that draws the wash,
    /// once per boundary crossing — and for an elevated window (Task Manager) it threw and
    /// swallowed a Win32Exception on the paint path every time. The title is shown at once and
    /// the friendlier name swaps in when it lands.
    /// </summary>
    private string AppNameFor(WindowInfo window)
    {
        if (_appNames.TryGetValue(window.Handle, out string? cached))
            return cached;

        IntPtr handle = window.Handle;
        string title = window.Title;

        // No context to come back on (a headless test) means no way to touch the cache or the
        // surfaces safely, so the title stands on its own — which is the fallback anyway.
        SynchronizationContext? ui = SynchronizationContext.Current;
        if (ui is null)
            return title;

        _ = Task.Run(() => WindowEnumerator.AppNameFor(handle, title))
            .ContinueWith(
                t => ui.Post(_ => OnAppNameResolved(handle, t.Result), null),
                TaskContinuationOptions.OnlyOnRanToCompletion);

        return title;
    }

    private void OnAppNameResolved(IntPtr handle, string name)
    {
        if (IsDisposed)
            return;
        _appNames[handle] = name;
        // Only repaint if the pointer is still on the window that asked for it.
        if (_hoverWindow is WindowInfo current && current.Handle == handle && _hoverAppName != name)
        {
            _hoverAppName = name;
            InvalidateScreenRect(Inflated(current.Bounds));
        }
    }

    /// <summary>Friendly app names already resolved this session, keyed by window handle.</summary>
    private readonly Dictionary<IntPtr, string> _appNames = new();

    /// <summary>
    /// Ramps the window-target wash in over <see cref="Motion.QuickMs"/> when the tint
    /// arrives from nothing. Retargeting straight from one window to another CARRIES the
    /// wash across at its current strength instead of restarting it at zero — that is what
    /// makes a retarget read as the tint moving rather than one rectangle blinking off while
    /// its neighbour fades up. Only the wash is painted, and only for the window under the
    /// pointer, so leaving has nothing to ramp: the tint goes with the rectangle it was on.
    ///
    /// This is the one asymmetric entrance/exit on the selector, and it only holds while the
    /// accent BORDER is also instant. Give the border a fade and this must get one too, or the
    /// wash will read as a pop against it.
    /// </summary>
    private void BeginHoverWash(bool arriving, bool wasHovering)
    {
        if (arriving && wasHovering && _hoverWashTween is null)
            return; // the tint moves with the target; it is already at full strength

        _hoverWashTween?.Dispose();
        _hoverWashTween = null;
        if (!arriving)
        {
            _hoverWash = 0d;
            return;
        }

        double from = wasHovering ? _hoverWash : 0d;
        _hoverWash = from;
        _hoverWashTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                _hoverWash = from + ((1d - from) * progress);
                if (_hoverWindow is WindowInfo w)
                    InvalidateScreenRect(Inflated(w.Bounds));
            },
            () => _hoverWashTween = null);
    }

    /// <summary>A highlight's ground plus everything drawn outside it: the border pen and
    /// its antialiasing, and -- on a framed selection -- the handle dots, their pop-in
    /// overshoot and the grabbed handle's focus ring. Those scale with the monitor now, so a
    /// fixed 4 px margin sliced the dots flat on anything above 100 %.</summary>
    private SD.Rectangle Inflated(SD.Rectangle rect)
    {
        int margin = SelectionHandles.OutsetAt(ScaleAt(new SD.Point(rect.Left, rect.Top)));
        rect.Inflate(margin, margin);
        return rect;
    }

    /// <summary>Edge-wise nearness: true when every side of the two rects is within
    /// <paramref name="tolerance"/> px.</summary>
    private static bool IsWithin(SD.Rectangle a, SD.Rectangle b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance &&
        Math.Abs(a.Right - b.Right) <= tolerance && Math.Abs(a.Bottom - b.Bottom) <= tolerance;

    /// <summary>Repaints only the ground the highlight covers across a glide step - the
    /// brightened interior changes too, so the whole union has to be redrawn, but that is
    /// still far less than the whole (possibly 4K) monitor.</summary>
    private void InvalidateHoverRegion(SD.Rectangle from, SD.Rectangle to)
    {
        var union = SD.Rectangle.Union(from, to);
        union.Inflate(4, 4); // border pen width + antialiasing
        InvalidateScreenRect(union);
        InvalidateScreenRect(new SD.Rectangle(union.Right, union.Bottom, LoupeBoxHalf, 64)); // size label
    }

    /// <summary>Innermost snappable rect under the point. The list is deepest-first, so the
    /// first hit IS the answer — no area comparison, no per-move enumeration.</summary>
    private SD.Rectangle? SnapRectAt(SD.Point point)
    {
        foreach (var candidate in _snapRects)
        {
            if (candidate.Bounds.Contains(point))
                return candidate.Bounds;
        }
        return null;
    }

    /// <summary>
    /// The dragged corner, pulled onto a real element edge when it comes within a few px of
    /// one. Hand-drawn control, but the result lines up with what is actually on screen -
    /// eyeballing the last three pixels is the tedious part of drawing a rectangle.
    /// </summary>
    private SD.Point SnappedCursor() => new(
        SnapCoordinate(_currentScreen.X, _snapEdgesX),
        SnapCoordinate(_currentScreen.Y, _snapEdgesY));

    internal static int SnapCoordinate(int value, int[] edges)
    {
        if (edges.Length == 0)
            return value;

        int at = Array.BinarySearch(edges, value);
        if (at >= 0)
            return value;

        at = ~at; // first edge greater than value
        int best = value;
        int bestGap = MarqueeSnapPx + 1;
        for (int i = at - 1; i <= at; i++)
        {
            if (i < 0 || i >= edges.Length)
                continue;
            int gap = Math.Abs(edges[i] - value);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = edges[i];
            }
        }
        return best;
    }

    private static int Distance(SD.Point a, SD.Point b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>Every window handle the selector owns, back to front - the set
    /// <see cref="SelectorForeground.KeepOnTop"/> re-raises together.</summary>
    private List<IntPtr> SurfaceHandles()
    {
        var handles = new List<IntPtr>(_panes.Count + 1);
        if (IsHandleCreated)
            handles.Add(Handle);
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed && pane.IsHandleCreated)
                handles.Add(pane.Handle);
        }
        return handles;
    }

    private bool IsPaneHandle(IntPtr handle)
    {
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed && pane.Handle == handle)
                return true;
        }
        return false;
    }

    /// <param name="window">
    /// The window the confirmation picked, when it was a window rather than a dragged region.
    /// Its handle is what the corner trim needs, and this is the last place that knows it.
    /// </param>
    private void Confirm(SD.Rectangle virtualRect, IntPtr window = default)
    {
        SD.Rectangle requested = virtualRect;
        virtualRect.Intersect(new SD.Rectangle(0, 0, _vs.Width, _vs.Height));
        if (virtualRect.Width < 1 || virtualRect.Height < 1) return;

        SelectedRegionPx = virtualRect;
        BareBackgroundRequested = (WF.Control.ModifierKeys & WF.Keys.Shift) == WF.Keys.Shift;
        // Crop the result from the frozen snapshot so the capture is exactly what was selected.
        _capturedRegion?.Dispose();
        _capturedRegion = CropFrozen(virtualRect);
        TrimWindowShadow(window, wholeWindowCaptured: virtualRect == requested);
        if (_settings is not null)
            _settings.Current.LastCaptureRegion = PreviousRegion.Format(
                new SD.Rectangle(virtualRect.X + _vs.X, virtualRect.Y + _vs.Y, virtualRect.Width, virtualRect.Height));

        Complete(WF.DialogResult.OK);
    }

    private void Complete(WF.DialogResult result)
    {
        DialogResult = result;
        StopFollowMotion();
        // A tween outliving the session holds the process-wide 1 ms clock.
        BeginHoverWash(arriving: false, wasHovering: false);
        _handleTween?.Dispose();
        _handleTween = null;
        _handleScale = 1d;
        _hintTween?.Dispose();
        _hintTween = null;
        _hintHold?.Dispose();
        _hintHold = null;
        _hintOpacity = 0d;
        _hintIsMessage = false;
        _barTween?.Dispose();
        _barTween = null;
        _pillTween?.Dispose();
        _pillTween = null;
        _pillHover = false;
        _pillSwap = 0d;
        _pendingGlideTween?.Dispose();
        _pendingGlideTween = null;
        _pendingGlide.Stop();
        _edgeTween?.Dispose();
        _edgeTween = null;
        _edgeGlow = 0d;
        _hoverEdge = MarqueeSide.None;
        _pressedEdge = MarqueeSide.None;
        _sessionEnded = true;
        // The hint counter reaches disk when the session that spent one ENDS, not at app
        // exit. WinShot is a tray app that usually dies with the Windows session rather than
        // leaving through the tray — and killing it is the documented pre-install step — so
        // App.OnExit's save routinely never ran and the five hint sessions restarted after
        // every reboot, which is the one counter whose whole job is to stop. Saved here
        // rather than at open so the Changed handlers (hotkey re-registration) fire with no
        // selector on screen, and fire-and-forget so nothing waits on a disk write.
        if (_hintSessionUnsaved && _settings is not null)
        {
            _hintSessionUnsaved = false;
            _ = _settings.SaveAsync();
        }
        Capture = false;
        DisposeFrozen(); // free the full snapshot now; _capturedRegion stays for the caller
        Park();
        _completion?.TrySetResult(result);
        _completion = null;
    }

    // ----------------------------------------------------------- handle math

    // The handle geometry itself lives in SelectionHandles, shared with the all-in-one
    // selector; these wrap it with this selector's clamp bounds.
    private SD.Rectangle ResizeRect(SD.Rectangle start, int handle, SD.Point cursor) =>
        SelectionHandles.Resize(start, handle, cursor, _vs);

    private SD.Rectangle MoveRect(SD.Rectangle start, int dx, int dy) =>
        SelectionHandles.Move(start, dx, dy, _vs);

    internal WF.Cursor CursorForCurrent()
    {
        if (_mode == SelectorMode.Window || _pillHover)
        {
            // Sized for the monitor the pointer is actually on: a custom HCURSOR is drawn at
            // its native pixel size, so a cursor built for the primary reads wrong the moment
            // it crosses onto a monitor at another DPI.
            return SelectorCursors.Camera(
                WinShot.Recording.RecordingMonitorDpi.ScaleFor(MonitorBoundsAt(_currentScreen)));
        }

        if (_pendingScreen is SD.Rectangle p)
        {
            // The Cancel/Done bar reads as buttons, not as selection ground.
            var (bar, _, _, _) = ActionBarRects(BarAnchor ?? p);
            if (bar.Contains(_currentScreen))
                return WF.Cursors.Hand;

            int h = _resizeHandle >= 0 ? _resizeHandle : SelectionHandles.HitTest(_currentScreen, p, ScaleAt(_currentScreen));
            if (SelectionHandles.CursorFor(h) is WF.Cursor sizing)
                return sizing;
            // The edge band is inside the move body, so SizeAll used to promise a move over
            // pixels that answer the click-to-snap gesture. The cursor names the gesture.
            if (!_movingPending && _hoverEdge != MarqueeSide.None)
            {
                return _hoverEdge is MarqueeSide.Left or MarqueeSide.Right
                    ? WF.Cursors.SizeWE
                    : WF.Cursors.SizeNS;
            }
            if (_movingPending || p.Contains(_currentScreen))
                return WF.Cursors.SizeAll;
        }
        return WF.Cursors.Cross;
    }

    private const int BarPad = 8;   // placement inset only; button geometry scales in ActionBarRects
    private const int BarGap = 12;

    /// <summary>What the padding cell says. Its keyboard equivalent is [ and ]. Spelled out
    /// rather than abbreviated: it sits between "Cancel" and "Done" on an owner-drawn overlay
    /// that can carry no tooltip, so the label is the only explanation it gets.</summary>
    private const string PadButtonLabel = "Pad edges";

    /// <summary>0..1 hover and press amounts for the bar's three cells (pad, cancel, done),
    /// eased over <see cref="Motion.QuickMs"/> so no fill on the bar snaps.</summary>
    private readonly double[] _barHover = new double[3];
    private readonly double[] _barPress = new double[3];
    private IDisposable? _barTween;

    /// <summary>
    /// Moves the bar's hover/press amounts toward the current state. Indexes match
    /// <see cref="ActionBarRects"/>: 0 = cancel, 1 = done, 2 = pad — the same numbers
    /// <see cref="_hoverBarButton"/> and <see cref="_pressedBarButton"/> already carry.
    /// </summary>
    private void AnimateBarFills()
    {
        double[] fromHover = (double[])_barHover.Clone();
        double[] fromPress = (double[])_barPress.Clone();
        int hoverTarget = _hoverBarButton;
        int pressTarget = _pressedBarButton;

        _barTween?.Dispose();
        _barTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                for (int i = 0; i < 3; i++)
                {
                    _barHover[i] = fromHover[i] + (((hoverTarget == i ? 1d : 0d) - fromHover[i]) * progress);
                    _barPress[i] = fromPress[i] + (((pressTarget == i ? 1d : 0d) - fromPress[i]) * progress);
                }
                if (BarAnchor is SD.Rectangle p)
                    InvalidateScreenRect(ActionBarRects(p).bar);
            },
            () => _barTween = null);
    }

    /// <summary>
    /// Screen rects for the Cancel/Done bar and its two buttons, given the pending selection.
    /// Centered on the region and placed BELOW it; flipped ABOVE when the region runs to the
    /// screen bottom; tucked INSIDE the region's bottom when it spans the full screen height.
    /// Pure function of the region (+ its monitor) so paint and hit-test always agree.
    /// </summary>
    /// <summary>Pixel-unit UI font sized for the monitor scale, so measure and draw agree
    /// regardless of which pane's DPI renders it (12px = the modal buttons' 9pt at 96).</summary>
    private static SD.Font ActionBarFont(double s) =>
        ThemePalette.UiFontPx(ThemePalette.PxForPt(ThemePalette.TextBodyPt, s));

    private static (SD.Rectangle bar, SD.Rectangle pad, SD.Rectangle cancel, SD.Rectangle done) ActionBarRects(SD.Rectangle r)
    {
        // Same proportions as the record modal's Cancel/Start pair, scaled to the
        // region's monitor: quiet regular-weight labels in roomy rounded buttons.
        SD.Rectangle m = WF.Screen.FromRectangle(r).Bounds;
        double s = WinShot.Recording.RecordingMonitorDpi.ScaleFor(m);
        using var font = ActionBarFont(s);
        int hpad = (int)Math.Round(16 * s);
        int pad = (int)Math.Round(6 * s);
        int gap = (int)Math.Round(8 * s);
        int btnH = (int)Math.Round(30 * s);
        int cancelW = WF.TextRenderer.MeasureText("Cancel", font).Width + hpad * 2;
        int doneW = Math.Max(cancelW, WF.TextRenderer.MeasureText("Done", font).Width + hpad * 2);
        // A word, not a glyph. This cell was the only icon-only control on the bar, the
        // overlay is owner-drawn so it can carry no tooltip, and its icon-font glyph was the
        // only Point-unit font here — Points already resolve against the monitor DPI, so
        // multiplying by the same scale as its pixel-unit neighbours sized it twice and it
        // grew against "Cancel" on every scaled display. A label in ActionBarFont says what
        // the button does and tracks `s` exactly like the two buttons beside it.
        int padW = WF.TextRenderer.MeasureText(PadButtonLabel, font).Width + hpad * 2;
        int barW = pad + padW + gap + cancelW + gap + doneW + pad;
        int barH = pad + btnH + pad;

        SD.Point origin = PlaceActionBar(r, m, barW, barH);
        var bar = new SD.Rectangle(origin.X, origin.Y, barW, barH);
        var padButton = new SD.Rectangle(origin.X + pad, origin.Y + pad, padW, btnH);
        var cancel = new SD.Rectangle(padButton.Right + gap, origin.Y + pad, cancelW, btnH);
        var done = new SD.Rectangle(cancel.Right + gap, origin.Y + pad, doneW, btnH);
        return (bar, padButton, cancel, done);
    }

    /// <summary>
    /// Top-left of the action bar for region <paramref name="r"/> within monitor <paramref name="m"/>:
    /// centered horizontally and placed below the region; flipped above when it would run off the
    /// monitor bottom; tucked inside the region's bottom when the region spans the full height
    /// (no room either side). Clamped to stay on-monitor. Pure — unit-testable without a screen.
    /// </summary>
    internal static SD.Point PlaceActionBar(SD.Rectangle r, SD.Rectangle m, int barW, int barH)
    {
        int x = r.Left + (r.Width - barW) / 2;
        int below = r.Bottom + BarGap;
        int above = r.Top - barH - BarGap;
        int y;
        if (below + barH <= m.Bottom - BarPad) y = below;            // room below the region
        else if (above >= m.Top + BarPad) y = above;                 // else above it
        else y = r.Bottom - barH - BarGap;                           // full-height region: inside the bottom
        x = Math.Clamp(x, m.Left + BarPad, Math.Max(m.Left + BarPad, m.Right - barW - BarPad));
        y = Math.Clamp(y, m.Top + BarPad, Math.Max(m.Top + BarPad, m.Bottom - barH - BarPad));
        return new SD.Point(x, y);
    }

    private void DrawActionBar(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle bar, SD.Rectangle padButton, SD.Rectangle cancel, SD.Rectangle done)
    {
        SD.Rectangle lb = ToLocal(bar, monitorBounds), lp = ToLocal(padButton, monitorBounds),
            lc = ToLocal(cancel, monitorBounds), ld = ToLocal(done, monitorBounds);
        double s = WinShot.Recording.RecordingMonitorDpi.ScaleFor(WF.Screen.FromRectangle(bar).Bounds);
        using var font = ActionBarFont(s);

        // Every cell's fill comes from one place: StateFill over a palette token, moved by a
        // 0..1 hover/press amount that eases over QuickMs. Neutral fills lift on hover and
        // the saturated one dips — a lighter blue under the white "Done" label reads worse
        // than the button at rest — and StateFill is the single place that decides which.
        // The pad cell used to mix two greys of its own (#3A3A3C idle, #303032 pressed) that
        // exist nowhere in ThemePalette, so one bar carried a fourth and fifth neutral shade.
        SD.Color cancelFill = DarkControlPalette.StateFill(ThemePalette.SurfaceAlt, _barHover[0], _barPress[0]);
        // White on the bare accent is 3.65:1, under the 4.5:1 floor; #0060DF clears it at
        // 5.62:1. Same call the all-in-one's SelectedFill made — one decision, both selectors.
        SD.Color doneFill = DarkControlPalette.StateFill(ThemePalette.AccentPressed, _barHover[1], _barPress[1]);
        SD.Color padFill = DarkControlPalette.StateFill(ThemePalette.SurfaceAlt, _barHover[2], _barPress[2]);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        int barRadius = (int)Math.Round(10 * s);
        int btnRadius = (int)Math.Round(6 * s);
        using (var path = GdiPaths.RoundedRect(lb, barRadius))
        {
            using (var bg = new SD.SolidBrush(SD.Color.FromArgb(245, ThemePalette.WindowBg)))
                g.FillPath(bg, path);
            using (var edge = new SD.Pen(ThemePalette.BorderStrong, 1))
                g.DrawPath(edge, path);
        }
        using (var pp = GdiPaths.RoundedRect(lp, btnRadius))
        using (var pb = new SD.SolidBrush(padFill))
            g.FillPath(pb, pp);
        using (var cp = GdiPaths.RoundedRect(lc, btnRadius))
        using (var cb = new SD.SolidBrush(cancelFill))
            g.FillPath(cb, cp);
        using (var dp = GdiPaths.RoundedRect(ld, btnRadius))
        using (var db = new SD.SolidBrush(doneFill))
            g.FillPath(db, dp);
        g.SmoothingMode = prev;

        const WF.TextFormatFlags center = WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(g, PadButtonLabel, font, lp, ThemePalette.TextPrimary, center);
        WF.TextRenderer.DrawText(g, "Cancel", font, lc, ThemePalette.TextPrimary, center);
        WF.TextRenderer.DrawText(g, "Done", font, ld, SD.Color.White, center);
    }

    // ----------------------------------------------------------- coordinate helpers

    private static SD.Rectangle ToLocal(SD.Rectangle screenRect, SD.Rectangle monitorBounds) =>
        new(screenRect.X - monitorBounds.X, screenRect.Y - monitorBounds.Y, screenRect.Width, screenRect.Height);

    private static SD.Point ToLocal(SD.Point screenPoint, SD.Rectangle monitorBounds) =>
        new(screenPoint.X - monitorBounds.X, screenPoint.Y - monitorBounds.Y);

    private SD.Rectangle VirtualFromScreen(SD.Rectangle screenRect)
    {
        var rect = new SD.Rectangle(screenRect.X - _vs.X, screenRect.Y - _vs.Y, screenRect.Width, screenRect.Height);
        rect.Intersect(new SD.Rectangle(0, 0, _vs.Width, _vs.Height));
        return rect;
    }

    /// <summary>
    /// The rectangle the in-progress drag currently describes. Shift constrains it to a
    /// square — the ratio math already lives in AllInOneDragLayout and is tested there, so
    /// a square is just the 1:1 case of it. Edge snapping still runs first; where the two
    /// disagree the square wins, because that is what the modifier asked for.
    /// </summary>
    private SD.Rectangle DragRect()
    {
        bool square = (WF.Control.ModifierKeys & WF.Keys.Shift) == WF.Keys.Shift;
        return square
            ? AllInOneDragLayout.CreatePixelRectangle(_dragStartScreen, SnappedCursor(), 1d)
            : Normalize(_dragStartScreen, SnappedCursor());
    }

    private static SD.Rectangle Normalize(SD.Point a, SD.Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new SD.Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>
    /// Test seam: where the pointer is. Tests drive selection gestures through the same
    /// handlers the surfaces call, and those read the REAL cursor — which would make every
    /// drag assertion depend on where the mouse happens to be sitting. Nothing but tests
    /// assigns this, and the hidden-desktop runner is the only place it is safe to.
    /// </summary>
    internal static Func<SD.Point>? CursorProvider;

    /// <summary>True physical cursor position — DPI-independent across monitors.</summary>
    private static SD.Point CursorScreen()
    {
        if (CursorProvider is Func<SD.Point> provider)
            return provider();
        return GetCursorPos(out POINT p) ? new SD.Point(p.X, p.Y) : WF.Cursor.Position;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// A non-primary monitor's overlay surface. Owns no state — it forwards input to and
    /// paints from the coordinator, so the selection is one logical thing spanning monitors.
    /// </summary>

    private IDisposable? _followMotionClock;

    /// <summary>
    /// The follow tick drives both the cursor-follow and the hover-highlight glide, so it runs
    /// under the high-resolution clock — at Windows' default ~15.6 ms tick an 8 ms timer
    /// silently halves its frame rate and coalesces under load.
    /// </summary>
    private void StartFollowMotion()
    {
        _followMotionClock ??= WinShot.Core.Motion.Acquire();
        _followTimer.Start();
    }

    private void StopFollowMotion()
    {
        _followTimer.Stop();
        _followMotionClock?.Dispose();
        _followMotionClock = null;
    }

    private sealed class SelectorPane : WF.Form
    {
        private readonly FastRegionSelectorDialog _owner;
        private readonly SD.Rectangle _bounds;

        public SelectorPane(FastRegionSelectorDialog owner, SD.Rectangle monitorBounds, bool freezeScreen)
        {
            _owner = owner;
            _bounds = monitorBounds;
            SelectorChrome.ConfigureSurface(this);
            SelectorChrome.ConfigurePresentation(this, freezeScreen);
            DoubleBuffered = true;
            SetStyle(PaintStyles, true);
            Bounds = monitorBounds;
            // No Opacity reset here: flipping a translucent Form back to Opacity=1.0
            // creates/recreates the window handle (~150-500 ms measured). The frozen swap
            // goes through CaptureExclusion.SetLayeredAlpha instead.
        }

        protected override bool ShowWithoutActivation => true;

        /// <summary>The physical monitor bounds this pane covers (used for dirty-region invalidation).</summary>
        public SD.Rectangle MonitorBounds => _bounds;

        protected override void OnMouseDown(WF.MouseEventArgs e)
        {
            _owner.HandleMouseDown(e);
            Capture = _owner.CapturingPointer;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(WF.MouseEventArgs e)
        {
            _owner.HandleMouseMove();
            Cursor = _owner.CursorForCurrent();
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(WF.MouseEventArgs e)
        {
            Capture = false;
            _owner.HandleMouseUp(e);
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(WF.MouseEventArgs e)
        {
            _owner.HandleMouseWheel(e);
            base.OnMouseWheel(e);
        }

        protected override void OnKeyDown(WF.KeyEventArgs e)
        {
            _owner.HandleKeyDown(e);
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(WF.KeyEventArgs e)
        {
            _owner.HandleKeyUp(e);
            base.OnKeyUp(e);
        }

        protected override void OnPaint(WF.PaintEventArgs e)
        {
            // Skip the clear once a frozen slice covers the surface — a redundant
            // full-monitor fill is expensive enough to show up in the freeze swap.
            if (!_owner.HasFullSurfaceBackground)
                e.Graphics.Clear(SD.Color.Black);
            _owner.PaintSurface(e.Graphics, _bounds);
            base.OnPaint(e);
        }
    }
}
