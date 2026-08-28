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
    private const int HandleHalf = 5;       // handle circle is 2*HandleHalf px
    private const int HandleHitTol = 9;     // grab tolerance around a handle center
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
        if (!Visible) Show();
        foreach (var pane in _panes)
            if (!pane.Visible) pane.Show();
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
        _resizeHandle = -1;
        _movingPending = false;
        _hoverWindow = null;
        _paneHover = false;
        _elementGranularity = false;
        _hoverPane = null;
        _hoverTween.Stop();
        _hoverLadder = new List<SD.Rectangle>();
        _ladderIndex = -1;
        _wheelPinned = null;
        SelectedByPaneClick = false;
        SelectedRegionPx = null;
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

    internal void HandleKeyDown(WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Escape)
        {
            Complete(WF.DialogResult.Cancel);
        }
        else if (e.KeyCode == WF.Keys.Enter && _pendingScreen is SD.Rectangle pending)
        {
            e.Handled = true;
            Confirm(VirtualFromScreen(pending));
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

        // Area mode.
        if (_pendingScreen is SD.Rectangle pending)
        {
            // Cancel / Done bar wins over move/resize. The press only arms the button —
            // the action fires on mouse-up over the same button, standard button feel.
            var (barRect, cancelRect, doneRect) = ActionBarRects(pending);
            if (barRect.Contains(screen))
            {
                _pressedBarButton = doneRect.Contains(screen) ? 1 : cancelRect.Contains(screen) ? 0 : -1;
                InvalidateScreenRect(barRect);
                return; // consume any click on the bar (don't start a new marquee under it)
            }

            if (e.Clicks >= 2 && pending.Contains(screen))
            {
                Confirm(VirtualFromScreen(pending));
                return;
            }

            int handle = HitTestHandle(screen, pending);
            if (handle >= 0)
            {
                _resizeHandle = handle;
                _adjustStartRect = pending;
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

    internal void HandleMouseMove()
    {
        _currentScreen = CursorScreen();

        if (_dragging)
        {
            if (!_dragMoved &&
                Math.Abs(_currentScreen.X - _dragStartScreen.X) < DragThresholdPx &&
                Math.Abs(_currentScreen.Y - _dragStartScreen.Y) < DragThresholdPx)
            {
                return;
            }

            _dragMoved = true;
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
        if (_pendingScreen is SD.Rectangle barPending)
        {
            var (barRect, cancelRect, doneRect) = ActionBarRects(barPending);
            int hover = doneRect.Contains(_currentScreen) ? 1
                : cancelRect.Contains(_currentScreen) ? 0 : -1;
            if (hover != _hoverBarButton)
            {
                _hoverBarButton = hover;
                InvalidateScreenRect(barRect);
            }
        }

        if (_mode == SelectorMode.Window)
        {
            // Window highlight tracks the cursor.
            _hoverWindow = ResolveWindow(_currentScreen);
            InvalidateAllSurfaces();
            return;
        }

        // Element detection is always on, ShareX-style: the rect under the cursor lights up
        // the moment the overlay opens, no modifier to discover. Click it to take it;
        // click-drag instead and the marquee wins — a drawn rect is never second-guessed.
        if (_paneHover && _pendingScreen is null && !_dragging)
        {
            UpdateHoverElement(_currentScreen);
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
        if (e.Button != WF.MouseButtons.Left)
            return;

        _currentScreen = CursorScreen();

        if (_mode == SelectorMode.Window)
        {
            if (_hoverWindow is not null)
                Confirm(VirtualFromScreen(_hoverWindow.Bounds));
            return;
        }

        if (_pressedBarButton >= 0)
        {
            int pressed = _pressedBarButton;
            _pressedBarButton = -1;
            if (_pendingScreen is SD.Rectangle pend)
            {
                var (bar, cancel, done) = ActionBarRects(pend);
                InvalidateScreenRect(bar);
                if (pressed == 1 && done.Contains(_currentScreen)) Confirm(VirtualFromScreen(pend));
                else if (pressed == 0 && cancel.Contains(_currentScreen)) Complete(WF.DialogResult.Cancel);
            }
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
            // Don't capture yet — present an adjustable selection (move/resize, then Enter or
            // double-click to confirm), matching CleanShot's behavior.
            var rect = Normalize(_dragStartScreen, SnappedCursor());
            rect.Intersect(_vs);
            if (rect.Width > 0 && rect.Height > 0)
                _pendingScreen = rect;
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
            brightScreen = Normalize(_dragStartScreen, SnappedCursor());
        }
        else if (_pendingScreen is SD.Rectangle pending)
        {
            brightScreen = pending;
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
            using (var pen = new SD.Pen(Accent, 2))
                g.DrawRectangle(pen, local);

            if (showHandles)
                DrawHandles(g, monitorBounds, bright);

            if (cursorOnThisSurface)
            {
                SD.Rectangle sized = labelScreen ?? bright;
                string sizeText = $"{sized.Width} × {sized.Height}";
                SD.Point at = new(local.Right + 8, local.Bottom + 8);
                if (_mode == SelectorMode.Window && _hoverWindow is not null && _pendingScreen is null && !_dragging)
                {
                    at = new SD.Point(ToLocal(_currentScreen, monitorBounds).X + 14, ToLocal(_currentScreen, monitorBounds).Y + 18);
                }
                else if (_pendingScreen is not null)
                {
                    // The Cancel/Done bar owns the space below a pending region — on a narrow
                    // selection the pill would land on Cancel — so the readout rides the
                    // selection's top-right, flipped just inside when the region touches the
                    // monitor top.
                    SD.Size pill = SelectorChrome.MeasureLabel(sizeText);
                    int pillTop = local.Top - pill.Height - 8;
                    if (pillTop < 0) pillTop = local.Top + 8;
                    at = new SD.Point(Math.Max(0, local.Right - pill.Width), pillTop);
                }
                SelectorChrome.DrawLabel(g, clientSize, sizeText, at.X, at.Y);
            }
        }

        // Ask for the next frame. Self-sustaining for the 200 ms of the glide, then it stops
        // on its own because Update() clears IsActive once the stopwatch passes the duration.
        if (tweenBefore != _hoverTween.Current)
            InvalidateHoverRegion(tweenBefore, _hoverTween.Current);

        // Cancel / Done bar on the adjustable selection: below the region, flipped above when it
        // runs to the screen bottom, tucked inside the bottom when the region spans the full height.
        if (_pendingScreen is SD.Rectangle pendBar)
        {
            var (bar, cancel, done) = ActionBarRects(pendBar);
            if (bar.IntersectsWith(monitorBounds))
                DrawActionBar(g, monitorBounds, bar, cancel, done);
        }

        // Crosshair + loupe only when drawing/idle in Area mode (not while adjusting a pending
        // rect, and never in Window mode where the window highlight is the affordance).
        bool inCrosshairContext = _mode == SelectorMode.Area && _pendingScreen is null && cursorOnThisSurface;
        if (inCrosshairContext)
        {
            // Crosshair guide lines honor the "Crosshair mode" setting; the magnifier/color
            // loupe honors "Show magnifier". Both default on when no settings are attached.
            if (CrosshairLinesVisible())
                SelectorChrome.DrawCrosshair(g, clientSize, ToLocal(_currentScreen, monitorBounds));

            if (_magnifierOn)
                FastSelectorLoupeRenderer.Draw(
                    g, clientSize, _vs, ToLocal(_currentScreen, monitorBounds), _currentScreen, _frozen);
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
    private void DrawHandles(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle screenRect)
    {
        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using var shadow = new SD.SolidBrush(SD.Color.FromArgb(60, 0, 0, 0));
        using var fill = new SD.SolidBrush(SD.Color.White);
        using var pen = new SD.Pen(Accent, 1.5f);
        foreach (SD.Point pt in HandlePoints(screenRect))
        {
            SD.Point l = ToLocal(pt, monitorBounds);
            var dot = new SD.Rectangle(l.X - HandleHalf, l.Y - HandleHalf, HandleHalf * 2, HandleHalf * 2);
            var under = dot;
            under.Offset(0, 1);
            g.FillEllipse(shadow, under);
            g.FillEllipse(fill, dot);
            g.DrawEllipse(pen, dot);
        }
        g.SmoothingMode = prev;
    }

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
    /// ShareX-exact hover resolution: the FIRST rect in the snap list containing the cursor,
    /// nothing else. The list is EnumWindows + EnumChildWindows + client rects, deepest-first
    /// in z-order, built once before the overlay opened — so the answer is instant, identical
    /// on every pass, and never revised after the fact. Where an app draws its whole UI into
    /// one HWND (Chromium, Electron, WPF) the answer is the window; that is what ShareX gives
    /// too, and the confidence of never being second-guessed is the feature.
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

    private void Confirm(SD.Rectangle virtualRect)
    {
        virtualRect.Intersect(new SD.Rectangle(0, 0, _vs.Width, _vs.Height));
        if (virtualRect.Width < 1 || virtualRect.Height < 1) return;

        SelectedRegionPx = virtualRect;
        // Crop the result from the frozen snapshot so the capture is exactly what was selected.
        _capturedRegion?.Dispose();
        _capturedRegion = CropFrozen(virtualRect);
        if (_settings is not null)
            _settings.Current.LastCaptureRegion = PreviousRegion.Format(
                new SD.Rectangle(virtualRect.X + _vs.X, virtualRect.Y + _vs.Y, virtualRect.Width, virtualRect.Height));

        Complete(WF.DialogResult.OK);
    }

    private void Complete(WF.DialogResult result)
    {
        DialogResult = result;
        StopFollowMotion();
        Capture = false;
        DisposeFrozen(); // free the full snapshot now; _capturedRegion stays for the caller
        Park();
        _completion?.TrySetResult(result);
        _completion = null;
    }

    // ----------------------------------------------------------- handle math

    /// <summary>The 8 handle anchor points (screen px): TL, Top, TR, Right, BR, Bottom, BL, Left.</summary>
    private static SD.Point[] HandlePoints(SD.Rectangle r)
    {
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        return
        [
            new(r.Left, r.Top), new(cx, r.Top), new(r.Right, r.Top),
            new(r.Right, cy), new(r.Right, r.Bottom),
            new(cx, r.Bottom), new(r.Left, r.Bottom), new(r.Left, cy),
        ];
    }

    private static int HitTestHandle(SD.Point cursor, SD.Rectangle r)
    {
        SD.Point[] pts = HandlePoints(r);
        for (int i = 0; i < pts.Length; i++)
        {
            if (Math.Abs(cursor.X - pts[i].X) <= HandleHitTol && Math.Abs(cursor.Y - pts[i].Y) <= HandleHitTol)
                return i;
        }
        return -1;
    }

    private SD.Rectangle ResizeRect(SD.Rectangle start, int handle, SD.Point cursor)
    {
        int l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        switch (handle)
        {
            case 0: l = cursor.X; t = cursor.Y; break;            // TL
            case 1: t = cursor.Y; break;                          // Top
            case 2: r = cursor.X; t = cursor.Y; break;            // TR
            case 3: r = cursor.X; break;                          // Right
            case 4: r = cursor.X; b = cursor.Y; break;            // BR
            case 5: b = cursor.Y; break;                          // Bottom
            case 6: l = cursor.X; b = cursor.Y; break;            // BL
            case 7: l = cursor.X; break;                          // Left
        }
        var rect = SD.Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
        rect.Intersect(_vs);
        return rect;
    }

    private SD.Rectangle MoveRect(SD.Rectangle start, int dx, int dy)
    {
        var rect = new SD.Rectangle(start.X + dx, start.Y + dy, start.Width, start.Height);
        if (rect.Left < _vs.Left) rect.X = _vs.Left;
        if (rect.Top < _vs.Top) rect.Y = _vs.Top;
        if (rect.Right > _vs.Right) rect.X = _vs.Right - rect.Width;
        if (rect.Bottom > _vs.Bottom) rect.Y = _vs.Bottom - rect.Height;
        return rect;
    }

    internal WF.Cursor CursorForCurrent()
    {
        if (_mode == SelectorMode.Window)
            return WF.Cursors.Hand;

        if (_pendingScreen is SD.Rectangle p)
        {
            // The Cancel/Done bar reads as buttons, not as selection ground.
            var (bar, _, _) = ActionBarRects(p);
            if (bar.Contains(_currentScreen))
                return WF.Cursors.Hand;

            int h = _resizeHandle >= 0 ? _resizeHandle : HitTestHandle(_currentScreen, p);
            switch (h)
            {
                case 0: case 4: return WF.Cursors.SizeNWSE;
                case 2: case 6: return WF.Cursors.SizeNESW;
                case 1: case 5: return WF.Cursors.SizeNS;
                case 3: case 7: return WF.Cursors.SizeWE;
            }
            if (_movingPending || p.Contains(_currentScreen))
                return WF.Cursors.SizeAll;
        }
        return WF.Cursors.Cross;
    }

    private const int BarPad = 8;   // placement inset only; button geometry scales in ActionBarRects
    private const int BarGap = 12;

    /// <summary>
    /// Screen rects for the Cancel/Done bar and its two buttons, given the pending selection.
    /// Centered on the region and placed BELOW it; flipped ABOVE when the region runs to the
    /// screen bottom; tucked INSIDE the region's bottom when it spans the full screen height.
    /// Pure function of the region (+ its monitor) so paint and hit-test always agree.
    /// </summary>
    /// <summary>Pixel-unit UI font sized for the monitor scale, so measure and draw agree
    /// regardless of which pane's DPI renders it (12px = the modal buttons' 9pt at 96).</summary>
    private static SD.Font ActionBarFont(double s) =>
        new("Segoe UI", Math.Max(9, (int)Math.Round(12 * s)), SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);

    private static (SD.Rectangle bar, SD.Rectangle cancel, SD.Rectangle done) ActionBarRects(SD.Rectangle r)
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
        int barW = pad + cancelW + gap + doneW + pad;
        int barH = pad + btnH + pad;

        SD.Point origin = PlaceActionBar(r, m, barW, barH);
        var bar = new SD.Rectangle(origin.X, origin.Y, barW, barH);
        var cancel = new SD.Rectangle(origin.X + pad, origin.Y + pad, cancelW, btnH);
        var done = new SD.Rectangle(cancel.Right + gap, origin.Y + pad, doneW, btnH);
        return (bar, cancel, done);
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

    private void DrawActionBar(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle bar, SD.Rectangle cancel, SD.Rectangle done)
    {
        SD.Rectangle lb = ToLocal(bar, monitorBounds), lc = ToLocal(cancel, monitorBounds), ld = ToLocal(done, monitorBounds);
        double s = WinShot.Recording.RecordingMonitorDpi.ScaleFor(WF.Screen.FromRectangle(bar).Bounds);
        using var font = ActionBarFont(s);

        // Hover lifts, press darkens — the same interaction language as the toolbar/card.
        SD.Color cancelFill = _pressedBarButton == 0 ? SD.Color.FromArgb(0x30, 0x30, 0x32)
            : _hoverBarButton == 0 ? ThemePalette.SurfaceHover
            : SD.Color.FromArgb(0x3A, 0x3A, 0x3C);
        SD.Color doneFill = _pressedBarButton == 1 ? SD.Color.FromArgb(0x0A, 0x6F, 0xD6)
            : _hoverBarButton == 1 ? ThemePalette.AccentHover
            : Accent;

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
        using (var cp = GdiPaths.RoundedRect(lc, btnRadius))
        using (var cb = new SD.SolidBrush(cancelFill))
            g.FillPath(cb, cp);
        using (var dp = GdiPaths.RoundedRect(ld, btnRadius))
        using (var db = new SD.SolidBrush(doneFill))
            g.FillPath(db, dp);
        g.SmoothingMode = prev;

        const WF.TextFormatFlags center = WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.NoPadding;
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

    private static SD.Rectangle Normalize(SD.Point a, SD.Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new SD.Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>True physical cursor position — DPI-independent across monitors.</summary>
    private static SD.Point CursorScreen()
    {
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
