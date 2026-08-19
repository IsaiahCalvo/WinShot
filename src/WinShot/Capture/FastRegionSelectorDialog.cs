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
    private const int HandleHalf = 4;       // handle square is 2*HandleHalf px
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
    /// <summary>How the current highlight was resolved. A COARSE rect (whole window or client
    /// area) keeps getting re-examined as the cursor moves; a FINE one is left alone until the
    /// cursor leaves it, which is what stops the highlight churning inside a single element.</summary>
    private enum HoverTier { None, Coarse, Fine }
    private HoverTier _hoverTier = HoverTier.None;
    /// <summary>Which tier produced the pending highlight, for the one log line per change.</summary>
    private string _hoverSource = "none";
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
    private bool _elementLookupBusy;
    private SD.Point _elementLookupPoint;
    private SD.Point _lastVisualPoint = new(-9999, -9999);
    /// <summary>Where the pixel scan last came back with nothing usable. Re-running it a few
    /// px away would fail again for the same reason, so it is not retried until the cursor has
    /// genuinely moved on.</summary>
    private SD.Point _scanFailedAt = new(-9999, -9999);
    /// <summary>Cursor travel required before the pixel scan is worth repeating, and the
    /// larger distance required after it has already failed once nearby.</summary>
    private const int ScanIntervalPx = 12;
    private const int ScanRetryAfterFailurePx = 48;
    /// <summary>How far the cursor may have travelled while the accessibility walk ran before
    /// its answer counts as describing somewhere the cursor no longer is. Tight enough that a
    /// stale rect never lands, loose enough to survive a fast flick across a window.</summary>
    private const int StaleAnswerPx = 160;
    /// <summary>Pane seams per window, found once from the frozen pixels. This is what finds a
    /// resizable sidebar or a header strip in apps whose accessibility tree describes neither.</summary>
    private readonly Dictionary<IntPtr, PaneGridDetector.Grid> _paneGrids = new();
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

    public FastRegionSelectorDialog(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        _settings = settings;
        _windowsProvider = windowsProvider;
        _monitorBounds = PrimaryBounds();

        SelectorChrome.ConfigureSurface(this);
        DoubleBuffered = true;
        SetStyle(PaintStyles, true);
        Bounds = _monitorBounds;
        Opacity = 1.0;

        _followTimer = new WF.Timer { Interval = 15 };
        _followTimer.Tick += OnFollowTick;

        Shown += (_, _) => StartWindowLoad();

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

    public static FastRegionSelectorDialog Rent(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        return new FastRegionSelectorDialog(windowsProvider, settings);
    }

    public static void Return(FastRegionSelectorDialog selector)
    {
        if (selector.IsDisposed)
            return;

        selector.DisposePanes();
        selector.DisposeFrozen();
        selector._capturedRegion?.Dispose();
        selector._capturedRegion = null;
        selector.Hide();
        selector.Dispose();
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
        SelectorChrome.ConfigurePresentation(this, _options.FreezeScreen);
        // Snapshot off the UI thread: WGC can take seconds before its fallback kicks in
        // when the GPU capture path is contended, and the app (tray menu, settings,
        // queued hotkeys) must stay responsive while the freeze frame is grabbed.
        if (_options.FreezeScreen)
            await CaptureFrozenAsync();
        else
            DisposeFrozen();
        // Snapshot every snappable HWND rect BEFORE our own overlay windows go up, so the
        // list can never contain the selector itself. Cheap (a few ms) and done once - hover
        // detection is then a pure in-memory scan with no per-move syscalls.
        _snapRects = _paneHover
            ? await Task.Run(() => WindowEnumerator.GetSnapRectangles())
            : new List<SnapRect>();
        _snapEdgesX = _snapRects.SelectMany(r => new[] { r.Bounds.Left, r.Bounds.Right })
            .Distinct().OrderBy(x => x).ToArray();
        _snapEdgesY = _snapRects.SelectMany(r => new[] { r.Bounds.Top, r.Bounds.Bottom })
            .Distinct().OrderBy(y => y).ToArray();
        CreatePanes();

        Show();
        foreach (var pane in _panes)
            pane.Show();

        Activate();
        Focus();
        SelectorForeground.Restore(this);
        _lastCtrlDown = false;
        _currentScreen = CursorScreen();
        _lastFollowScreen = _currentScreen;
        if (_options.NeedsCursorFollow || _paneHover)
            _followTimer.Start();
        return await _completion.Task;
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
        _paneGrids.Clear();
        _dragging = false;
        _dragMoved = false;
        _pendingScreen = null;
        _resizeHandle = -1;
        _movingPending = false;
        _hoverWindow = null;
        _paneHover = false;
        _elementGranularity = false;
        _hoverPane = null;
        _hoverTween.Stop();
        _hoverTier = HoverTier.None;
        _scanFailedAt = new SD.Point(-9999, -9999);
        _lastVisualPoint = new SD.Point(-9999, -9999);
        _hoverLadder = new List<SD.Rectangle>();
        _ladderIndex = -1;
        _elementLookupBusy = false;
        SelectedByPaneClick = false;
        SelectedRegionPx = null;
        DisposeFrozen();
        _capturedRegion?.Dispose();
        _capturedRegion = null;
        DialogResult = WF.DialogResult.None;
        Bounds = _monitorBounds;
        Capture = false;
        Opacity = 1.0;
        // Seed at the real cursor so the first paint draws the crosshair/loupe at the
        // pointer instead of a corner until the first mouse-move arrives.
        _currentScreen = CursorScreen();
        _completion = null;
    }

    // ----------------------------------------------------------- per-monitor panes

    private void CreatePanes()
    {
        DisposePanes();

        foreach (var screen in WF.Screen.AllScreens)
        {
            if (screen.Bounds == _monitorBounds)
                continue; // the coordinator Form already covers the primary monitor

            var pane = new SelectorPane(this, screen.Bounds, _options.FreezeScreen);
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
            // Cancel / Done bar wins over move/resize, so a click on it always commits or cancels.
            var (barRect, cancelRect, doneRect) = ActionBarRects(pending);
            if (barRect.Contains(screen))
            {
                if (doneRect.Contains(screen)) Confirm(VirtualFromScreen(pending));
                else if (cancelRect.Contains(screen)) Complete(WF.DialogResult.Cancel);
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
        // Tier Fine: an explicit choice must not be second-guessed by the next mouse twitch.
        SetHoverPane(_hoverLadder[next], HoverTier.Fine);
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
            g.DrawImage(dimmed, dest, 0, 0, dimmed.Width, dimmed.Height, SD.GraphicsUnit.Pixel);
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
                SD.Point at = new(local.Right + 8, local.Bottom + 8);
                if (_mode == SelectorMode.Window && _hoverWindow is not null && _pendingScreen is null && !_dragging)
                    at = new SD.Point(ToLocal(_currentScreen, monitorBounds).X + 14, ToLocal(_currentScreen, monitorBounds).Y + 18);
                SD.Rectangle sized = labelScreen ?? bright;
                SelectorChrome.DrawLabel(g, clientSize, $"{sized.Width} × {sized.Height}", at.X, at.Y);
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

            if (_options.ShowMagnifier)
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

        var bmp = new SD.Bitmap(monitorBounds.Width, monitorBounds.Height, SD.Imaging.PixelFormat.Format32bppPArgb);
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
        using var fill = new SD.SolidBrush(SD.Color.White);
        using var pen = new SD.Pen(Accent, 1.5f);
        foreach (SD.Point pt in HandlePoints(screenRect))
        {
            SD.Point l = ToLocal(pt, monitorBounds);
            var sq = new SD.Rectangle(l.X - HandleHalf, l.Y - HandleHalf, HandleHalf * 2, HandleHalf * 2);
            g.FillRectangle(fill, sq);
            g.DrawRectangle(pen, sq);
        }
        g.SmoothingMode = prev;
    }

    private async Task CaptureFrozenAsync()
    {
        DisposeFrozen();
        try
        {
            _frozen = await Task.Run(CaptureService.CaptureVirtualDesktop);
        }
        catch (Exception ex)
        {
            Log.Error("Screen-freeze capture failed; selecting over a plain dim instead", ex);
            _frozen = null;
        }
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
    /// Resolves the highlight for a cursor position and commits it EXACTLY ONCE. The tiers used
    /// to publish in order — HWND rect first, pixel scan a moment later — which showed as the
    /// highlight ballooning out to the whole window and then collapsing onto the element under
    /// the cursor. Every synchronous tier is now consulted before anything is drawn, so a move
    /// produces a single glide to a single answer.
    /// </summary>
    private void UpdateHoverElement(SD.Point point)
    {
        // Settled on a real element and still inside it: nothing can improve, so don't spend
        // the pixel scan and don't disturb the highlight. A coarse rect stays under review.
        if (_hoverTier == HoverTier.Fine && _hoverPane is SD.Rectangle settled && settled.Contains(point))
            return;

        SD.Rectangle? snap = SnapRectAt(point);

        if (!_elementGranularity)
        {
            if (snap is null)
            {
                WindowInfo? win = ResolveWindow(point);
                snap = win is null
                    ? null
                    : WinShot.Scrolling.ScrollPaneDetector.QuickPaneRect(win.Handle, point) ?? win.Bounds;
            }
            SetHoverPane(snap, HoverTier.Coarse);
            return;
        }

        // Ask the app first, and ask it on EVERY move. This costs the UI thread almost
        // nothing — the walk happens on a worker — and it is the tier that actually knows
        // what a button or a footer is. It used to sit below the throttle guard, so backing
        // the pixel scan off silently muted the accessibility tier along with it.
        KickElementLookup(point);

        // The pixel scan is the expensive one: segmenting a megapixel crop costs several ms of
        // UI thread, and at follow-tick rate that is most of the frame budget. It runs on
        // cursor travel rather than on ticks, and backs off hard where it has drawn a blank.
        if (Distance(point, _lastVisualPoint) < ScanIntervalPx ||
            Distance(point, _scanFailedAt) < ScanRetryAfterFailurePx)
        {
            return;
        }
        _lastVisualPoint = point;

        WindowInfo? seed = ResolveWindow(point);
        SD.Rectangle searchBounds = snap ?? seed?.Bounds ?? SD.Rectangle.Empty;
        SD.Rectangle? visual = null;
        if (_frozen is not null && searchBounds.Width > 0)
        {
            var vp = new SD.Point(point.X - _vs.X, point.Y - _vs.Y);
            visual = VisualElementDetector.Find(_frozen, vp, VirtualFromScreen(searchBounds));
            if (visual is SD.Rectangle v)
            {
                var mapped = new SD.Rectangle(v.X + _vs.X, v.Y + _vs.Y, v.Width, v.Height);
                // The segmenter seeds outward from the cursor, so it can return a block the
                // cursor is not actually in. Highlighting that would be a lie, and the cursor
                // would then sit outside the highlight and force a rescan on every move.
                visual = mapped.Contains(point) ? mapped : null;
            }
        }
        _scanFailedAt = visual is null ? point : new SD.Point(-9999, -9999);

        SD.Rectangle? pane = PaneAt(seed, point);
        SD.Rectangle? resolved = visual ?? pane ?? snap ??
            (seed is null
                ? null
                : WinShot.Scrolling.ScrollPaneDetector.QuickPaneRect(seed.Handle, point) ?? seed.Bounds);

        if (resolved is SD.Rectangle chosen)
        {
            _hoverSource = visual is not null ? "visual" : pane is not null ? "pane"
                : snap is not null ? "hwnd" : "seed";
            BuildLadder(point, chosen, null, pane);
            SetHoverPane(chosen, visual is not null || pane is not null ? HoverTier.Fine : HoverTier.Coarse);
        }
    }

    /// <summary>
    /// Collects the nesting of rects under the cursor - the resolved one plus every HWND rect
    /// containing the point - smallest first, so the wheel has somewhere to go in both
    /// directions. Rungs closer than the wobble tolerance are merged; stepping onto a rect
    /// eight pixels bigger is not a step a person can see.
    /// </summary>
    private void BuildLadder(SD.Point point, SD.Rectangle resolved,
        IReadOnlyList<AxNode>? chain = null, SD.Rectangle? pane = null)
    {
        var rungs = new List<SD.Rectangle> { resolved };
        if (pane is SD.Rectangle paneRect)
            rungs.Add(paneRect); // the sidebar / header / content pane the cursor is in
        if (chain is not null)
        {
            // Every level the app itself reports — the button, the row, the toolbar it sits in,
            // the region around that — so the wheel steps through real structure rather than
            // through whatever sizes happened to fall out of a pixel scan.
            foreach (var node in chain)
            {
                if (node.Rect.Contains(point) && !IsWholePane(node.Rect, point))
                    rungs.Add(node.Rect);
            }
        }
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

    private void KickElementLookup(SD.Point point)
    {
        // The only tier that can't answer in time to join the one-step decision above: a
        // cross-process UIA/MSAA walk, up to 450 ms. It runs on a worker and is allowed to
        // replace the highlight only with something strictly tighter that still contains the
        // cursor, so at worst it tightens a rect — it can never balloon one back out.
        if (_elementLookupBusy)
            return; // one in flight; the next mouse move re-kicks with the fresh point
        _elementLookupBusy = true;
        _elementLookupPoint = point;
        WindowInfo? win = ResolveWindow(point);
        IntPtr root = win?.Handle ?? IntPtr.Zero;
        Task.Run(() =>
        {
            try
            {
                RunElementLookup(root, point);
            }
            finally
            {
                // Clear the in-flight flag HERE, not inside the marshalled callback. If the
                // walk threw, or the selector closed before BeginInvoke could run, the flag
                // stayed set and every later lookup was skipped for the rest of the session —
                // one answer at the start and silence after.
                _elementLookupBusy = false;
            }
        });
    }

    private void RunElementLookup(IntPtr root, SD.Point point)
    {
            IReadOnlyList<AxNode> chain = WinShot.Scrolling.ScrollPaneDetector.ElementChainFromPoint(
                root, point, TimeSpan.FromMilliseconds(450));
            SD.Rectangle? rect = PreferredRung(chain);
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || !_paneHover)
                        return;
                    // The accessibility tree KNOWS where the element ends; the pixel scan is
                    // inferring it from colour. So the tree wins outright rather than only when
                    // it happens to be smaller — except for the one answer it gets wrong often
                    // enough to matter, a rect so large it is really the whole pane wearing an
                    // element's name. Stale answers for a point the cursor has left are dropped.
                    if (rect is SD.Rectangle r && Distance(CursorScreen(), point) < StaleAnswerPx &&
                        r != _hoverPane && r.Contains(point) && !IsWholePane(r, point))
                    {
                        _hoverSource = "element";
                        BuildLadder(point, r, chain);
                        SetHoverPane(r, HoverTier.Fine);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // Selector torn down mid-lookup — nothing to update.
            }
    }

    /// <summary>
    /// The one place the hover highlight changes. Routing every tier through here is what
    /// keeps the glide honest: each new rect starts its slide from wherever the highlight
    /// currently sits, so a stale tier answer can never make it jump backwards.
    /// </summary>
    private void SetHoverPane(SD.Rectangle? rect, HoverTier tier)
    {
        _hoverTier = rect is null ? HoverTier.None : tier;
        if (rect == _hoverPane)
            return;

        // The pixel tier re-segments on every few px of cursor travel and its answer breathes
        // by a handful of pixels each time. Restarting the glide for that reads as a permanent
        // shiver, so a near-identical rect is treated as the same rect.
        if (rect is SD.Rectangle candidate && _hoverPane is SD.Rectangle settled &&
            IsWithin(candidate, settled, WobbleTolerancePx))
        {
            return;
        }
        Log.Info($"Hover: {_hoverSource} {(rect is SD.Rectangle r ? r.ToString() : "none")}");

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
    /// The layout pane under the cursor — the sidebar, the header strip, the content area —
    /// found from the seams in the frozen pixels. Built once per window and cached, because a
    /// frozen screen's layout cannot change while the overlay is up.
    /// </summary>
    private SD.Rectangle? PaneAt(WindowInfo? window, SD.Point point)
    {
        if (window is null || _frozen is null)
            return null;

        if (!_paneGrids.TryGetValue(window.Handle, out var grid))
        {
            try
            {
                grid = PaneGridDetector.Build(_frozen, VirtualFromScreen(window.Bounds));
            }
            catch (Exception ex)
            {
                Log.Error("Pane seam scan failed (non-fatal)", ex);
                grid = new PaneGridDetector.Grid(Array.Empty<int>(), Array.Empty<int>());
            }
            _paneGrids[window.Handle] = grid;
        }

        var virtualPoint = new SD.Point(point.X - _vs.X, point.Y - _vs.Y);
        if (PaneGridDetector.PaneAt(grid, virtualPoint, VirtualFromScreen(window.Bounds)) is not SD.Rectangle found)
            return null;
        return new SD.Rectangle(found.X + _vs.X, found.Y + _vs.Y, found.Width, found.Height);
    }

    /// <summary>
    /// Which level of the app's own nesting to highlight by default.
    ///
    /// Innermost is the wrong answer: hit-testing lands on the text run inside a button, so
    /// taking the deepest hit highlights a word and not the control. Prefer instead the
    /// smallest node the app calls a THING — a button, a link, a row, a cell, a field, a tab.
    /// Failing that, the smallest region — a toolbar, a list, a header or footer group. Only
    /// if the app offered nothing but raw text and window wrappers does the innermost rect win.
    /// The wheel reaches every other level, so this only has to be right by default.
    /// </summary>
    internal static SD.Rectangle? PreferredRung(IReadOnlyList<AxNode> chain)
    {
        if (chain.Count == 0)
            return null;

        // chain is sorted smallest-first, so the first match at each tier is the tightest.
        foreach (var node in chain)
        {
            if (node.IsPrimary)
                return node.Rect;
        }
        foreach (var node in chain)
        {
            if (node.IsRegion)
                return node.Rect;
        }
        foreach (var node in chain)
        {
            if (!node.IsPassThrough)
                return node.Rect;
        }
        return chain[0].Rect;
    }

    /// <summary>Whether a candidate is really the whole window dressed up as an element -
    /// the one shape the accessibility tree reports confidently and uselessly.</summary>
    private bool IsWholePane(SD.Rectangle candidate, SD.Point point)
    {
        if (ResolveWindow(point) is not WindowInfo win || win.Bounds.Width <= 0)
            return false;
        return (long)candidate.Width * candidate.Height * 100 >=
               (long)win.Bounds.Width * win.Bounds.Height * 55;
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
        _followTimer.Stop();
        Capture = false;
        DisposePanes();
        DisposeFrozen(); // free the full snapshot now; _capturedRegion stays for the caller
        Hide();
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

    private const int BarBtnH = 30;
    private const int BarPad = 8;
    private const int BarInnerGap = 8;
    private const int BarGap = 12;

    /// <summary>
    /// Screen rects for the Cancel/Done bar and its two buttons, given the pending selection.
    /// Centered on the region and placed BELOW it; flipped ABOVE when the region runs to the
    /// screen bottom; tucked INSIDE the region's bottom when it spans the full screen height.
    /// Pure function of the region (+ its monitor) so paint and hit-test always agree.
    /// </summary>
    private static (SD.Rectangle bar, SD.Rectangle cancel, SD.Rectangle done) ActionBarRects(SD.Rectangle r)
    {
        using var font = ThemePalette.UiFont(9.5f, SD.FontStyle.Bold);
        int cancelW = WF.TextRenderer.MeasureText("Cancel", font).Width + 28;
        int doneW = WF.TextRenderer.MeasureText("Done", font).Width + 28;
        int barW = BarPad + cancelW + BarInnerGap + doneW + BarPad;
        int barH = BarPad + BarBtnH + BarPad;

        SD.Rectangle m = WF.Screen.FromRectangle(r).Bounds;
        SD.Point origin = PlaceActionBar(r, m, barW, barH);
        var bar = new SD.Rectangle(origin.X, origin.Y, barW, barH);
        var cancel = new SD.Rectangle(origin.X + BarPad, origin.Y + BarPad, cancelW, BarBtnH);
        var done = new SD.Rectangle(cancel.Right + BarInnerGap, origin.Y + BarPad, doneW, BarBtnH);
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
        using var font = ThemePalette.UiFont(9.5f, SD.FontStyle.Bold);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(lb, 8))
        using (var bg = new SD.SolidBrush(SD.Color.FromArgb(245, 0x1C, 0x1C, 0x1E)))
            g.FillPath(bg, path);
        using (var cp = GdiPaths.RoundedRect(lc, 6))
        using (var cb = new SD.SolidBrush(SD.Color.FromArgb(255, 0x3A, 0x3A, 0x3C)))
            g.FillPath(cb, cp);
        using (var dp = GdiPaths.RoundedRect(ld, 6))
        using (var db = new SD.SolidBrush(Accent))
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
            Opacity = 1.0;
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

        protected override void OnPaint(WF.PaintEventArgs e)
        {
            e.Graphics.Clear(SD.Color.Black);
            _owner.PaintSurface(e.Graphics, _bounds);
            base.OnPaint(e);
        }
    }
}
