using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Lightweight all-in-one selector (Area/Window/Fullscreen/Record/OCR/Scroll toolbar) that
/// keeps the capture toolbar out of WPF.
///
/// DPI correctness mirrors <see cref="FastRegionSelectorDialog"/>: instead of one window
/// stretched across the whole virtual desktop (which Windows renders at a single monitor's
/// scale and bitmap-stretches onto the others, drifting the selection on mixed-DPI
/// multi-monitor setups), this puts ONE 1:1 overlay surface on EACH monitor. The primary
/// monitor's surface is this Form (also the coordinator and toolbar host); extra monitors get
/// lightweight <see cref="SelectorPane"/> children. All selection math is anchored to
/// GetCursorPos, which returns true physical pixels regardless of which monitor's scale the
/// cursor is over, so the painted selection always matches the captured pixels.
///
/// Screen-freeze: when enabled, the selector snapshots the whole desktop and each surface paints
/// its frozen slice. When disabled, native translucent surfaces leave the live desktop visible.
/// </summary>
public sealed class FastAllInOneSelectorDialog : WF.Form
{
    private const int DragThresholdPx = 4;
    private static readonly SD.Color Accent = ThemePalette.Accent;

    private SD.Rectangle _vs = CaptureService.VirtualScreen;
    private SD.Rectangle _monitorBounds;   // this surface's monitor (physical px); primary monitor for the coordinator
    private SettingsService? _settings;
    private SelectorOptions _options;
    private readonly ToolbarForm _toolbar;
    private List<WindowInfo> _windows = new();
    private readonly List<SelectorPane> _panes = new();
    private SD.Point _dragStartScreen;     // physical screen px (GetCursorPos)
    private SD.Point _currentScreen;       // physical screen px (GetCursorPos)
    private bool _dragging;
    private bool _dragMoved;
    private double? _dragRatio;
    private int _resizeHandle = -1;        // 0..7 while dragging a handle, else -1
    private bool _movingPending;           // dragging the pending rect body to move it
    private SD.Point _adjustAnchor;        // cursor (screen px) at the start of a move
    private SD.Rectangle _adjustStartRect; // pending rect (screen px) at the start of a move/resize
    /// <summary>0..1 growth of the pending selection handles (overshoots past 1).</summary>
    private double _handleScale = 1d;
    private IDisposable? _handleTween;
    private WindowInfo? _hoverWindow;
    private SD.Rectangle? _pendingPx;

    /// <summary>Display-only ease of <see cref="_pendingPx"/> while a button-driven size
    /// change is in flight. Same pattern the mode chip's glide already uses.</summary>
    private readonly RectangleTween _pendingGlide = new();
    private IDisposable? _pendingGlideTween;

    /// <summary>The pending rect as PAINTED — eased mid-glide, settled otherwise.</summary>
    private SD.Rectangle? DisplayPx => _pendingPx is SD.Rectangle p
        ? (_pendingGlide.IsActive ? _pendingGlide.Current : p)
        : null;
    private SD.Bitmap? _frozen;          // frozen virtual-desktop snapshot shown under the overlay
    // Per-monitor frozen-slice-with-dim baked once, so each follow-frame is a single opaque
    // blit instead of re-cropping the snapshot AND alpha-blending a full-screen dim every
    // tick. Same fix the region selector already carries; keyed by monitor.
    private readonly Dictionary<SD.Rectangle, SD.Bitmap> _dimmedCache = new();
    private SD.Bitmap? _capturedRegion;  // region cropped from _frozen at confirm; caller takes ownership
    private Func<Task<List<WindowInfo>>> _windowsProvider;
    private bool _windowsLoadStarted;
    private TaskCompletionSource<WF.DialogResult>? _completion;
    private readonly WF.Timer _followTimer;
    private bool _lastCtrlDown;
    // Session-effective loupe visibility: seeded from the "Show magnifier" setting each
    // ShowAsync, flipped by tapping Alt. The latch swallows key-repeat while Alt is held.
    private bool _magnifierOn;
    private bool _altLatched;

    public FastAllInOneSelectorDialog(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        _settings = settings;
        _windowsProvider = windowsProvider;
        _monitorBounds = PrimaryBounds();
        _toolbar = new ToolbarForm(this);

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

        // Post-show init runs from ShowAsync, not the Shown event: Shown fires only on a
        // form's FIRST display, so a pooled re-show would never run it.

        ResetForUse(windowsProvider, settings);
    }

    public SD.Rectangle? SelectedRegionPx { get; private set; }

    public AllInOneAction SelectedAction { get; private set; } = AllInOneAction.Capture;

    // DoubleBuffered + SetStyle are protected on Control, so each surface enables its own
    // flicker-free painting from inside its own constructor.
    private const WF.ControlStyles PaintStyles =
        WF.ControlStyles.AllPaintingInWmPaint |
        WF.ControlStyles.OptimizedDoubleBuffer |
        WF.ControlStyles.ResizeRedraw |
        WF.ControlStyles.UserPaint;

    private static SD.Rectangle PrimaryBounds() =>
        (WF.Screen.PrimaryScreen ?? WF.Screen.AllScreens[0]).Bounds;

    // One warm instance kept alive between captures — see FastRegionSelectorDialog.Rent.
    private static FastAllInOneSelectorDialog? _pooled;

    public static FastAllInOneSelectorDialog Rent(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        var pooled = _pooled;
        _pooled = null;
        if (pooled is not null && !pooled.IsDisposed && !pooled._toolbar.IsDisposed)
        {
            pooled.ResetForUse(windowsProvider, settings);
            return pooled;
        }
        return new FastAllInOneSelectorDialog(windowsProvider, settings);
    }

    /// <summary>
    /// Invisible and click-through, but still composited — so the next open is an alpha flip
    /// instead of ~130 ms of ShowWindow per full-screen surface. See <see cref="SelectorSurfaces"/>.
    /// </summary>
    internal void Park()
    {
        SelectorSurfaces.Park(this);
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed)
                SelectorSurfaces.Park(pane);
        }

        if (!Visible) Show();
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed && !pane.Visible)
                pane.Show();
        }
    }

    public static void Return(FastAllInOneSelectorDialog selector)
    {
        if (selector.IsDisposed)
            return;

        selector.DisposeFrozen();
        selector._capturedRegion?.Dispose();
        selector._capturedRegion = null;
        selector.Park();
        if (!selector._toolbar.IsDisposed)
            selector._toolbar.Hide();

        var displaced = _pooled;
        _pooled = selector;
        if (displaced is not null && displaced != selector && !displaced.IsDisposed)
        {
            displaced.DisposePanes();
            displaced.Dispose();
        }
    }

    public async Task<WF.DialogResult> ShowAsync()
    {
        _completion = new TaskCompletionSource<WF.DialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        Bounds = _monitorBounds;
        _options = SelectorOptions.ForAllInOne(_settings?.Current);
        // Open instantly over the live desktop; the freeze snapshot bakes in when it
        // lands — see FastRegionSelectorDialog.ShowAsync.
        SelectorChrome.ConfigurePresentation(this, freezeScreen: false);
        DisposeFrozen();
        CreatePanes();
        // Warm (pooled) surfaces may still be opaque from a previous frozen swap.
        SelectorSurfaces.Unpark(this, SelectorChrome.LiveAlpha);
        foreach (var pane in _panes)
            SelectorSurfaces.Unpark(pane, SelectorChrome.LiveAlpha);

        Show();
        foreach (var pane in _panes)
            pane.Show();

        StartWindowLoad();
        BeginInvoke(new Action(() =>
        {
            ShowToolbar();
            BeginInvoke(new Action(() =>
            {
                TryRestoreLastRegion();
                Activate();
                Focus();
                SelectorForeground.Restore(this);
            }));
        }));

        Activate();
        Focus();
        SelectorForeground.Restore(this);
        _lastCtrlDown = false;
        _magnifierOn = _options.ShowMagnifier;
        _altLatched = false;
        if (_options.NeedsCursorFollow)
            StartFollowMotion();
        if (_options.FreezeScreen)
            _ = FreezeWhileShownAsync(_completion);
        return await _completion.Task;
    }

    /// <summary>Deferred freeze — see FastRegionSelectorDialog.FreezeWhileShownAsync,
    /// including why a stale (pooled, cancelled) session must only ever dispose its own
    /// LOCAL snapshot and never the shared _frozen the live session paints from.</summary>
    private async Task FreezeWhileShownAsync(TaskCompletionSource<WF.DialogResult> session)
    {
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

        // Bake the dimmed slices BEFORE the first frozen paint so no surface is opaque
        // over a half-swapped state — see FastRegionSelectorDialog.FreezeWhileShownAsync.
        GetDimmedBackground(_monitorBounds);
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed)
                GetDimmedBackground(pane.MonitorBounds);
        }

        // Paint each surface before going opaque — see
        // FastRegionSelectorDialog.FreezeWhileShownAsync for why this ordering matters.
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
    }

    private void ResetForUse(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        _settings = settings;
        _options = SelectorOptions.ForAllInOne(settings?.Current);
        _windowsProvider = windowsProvider;
        _windowsLoadStarted = false;
        _windows = new List<WindowInfo>();
        _dragging = false;
        _dragMoved = false;
        _dragRatio = null;
        _selectedMode = ModeRegion;
        _resizeHandle = -1;
        _movingPending = false;
        _handleTween?.Dispose();
        _handleTween = null;
        _handleScale = 1d;
        _hoverWindow = null;
        _pendingPx = null;
        _pendingGlideTween?.Dispose();
        _pendingGlideTween = null;
        _pendingGlide.Stop();
        SelectedRegionPx = null;
        SelectedAction = AllInOneAction.Capture;
        DisposeFrozen();
        _capturedRegion?.Dispose();
        _capturedRegion = null;
        DialogResult = WF.DialogResult.None;
        Bounds = _monitorBounds;
        Capture = false;
        // Seed at the real cursor so the first paint draws the crosshair/loupe at the
        // pointer instead of a corner until the first mouse-move arrives.
        _currentScreen = CursorScreen();
        _lastFollowScreen = _currentScreen;
        _lastCtrlDown = false;
        _magnifierOn = _options.ShowMagnifier;
        _altLatched = false;
        _completion = null;
        if (!_toolbar.IsDisposed)
        {
            _toolbar.Opacity = 1;
            _toolbar.ResetMode(0);
            _toolbar.SetSize(1, 1);
            _toolbar.ResetAspectLock(); // pooled instance: a stale lock made new drags snap to the OLD session's ratio
            _toolbar.RefreshPendingState();
        }
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

    /// <summary>Repaints the coordinator surface and every pane (the selection can span monitors).</summary>
    private void InvalidateAllSurfaces()
    {
        Invalidate();
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed)
                pane.Invalidate();
        }
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
            Log.Error("Failed to load all-in-one selector window list", ex);
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

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        // No Clear once a frozen slice covers the whole surface: a redundant full-monitor
        // fill is expensive enough at 4K to show up in the freeze swap.
        if (!HasFullSurfaceBackground)
            e.Graphics.Clear(SD.Color.Black);
        PaintSurface(e.Graphics, _monitorBounds);
        base.OnPaint(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposePanes();
            DisposeFrozen();
            _handleTween?.Dispose();
            _handleTween = null;
            _pendingGlideTween?.Dispose();
            _pendingGlideTween = null;
            _followTimer.Dispose();
            _capturedRegion?.Dispose();
            _capturedRegion = null;
            if (!_toolbar.IsDisposed)
                _toolbar.Dispose();
        }
        base.Dispose(disposing);
    }

    // Called by panes so all input funnels through the one coordinator state.
    internal void HandleKeyDown(WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Escape)
        {
            Complete(WF.DialogResult.Cancel);
        }
        else if (e.KeyCode == WF.Keys.Enter && _pendingPx is SD.Rectangle pending)
        {
            e.Handled = true;
            Confirm(pending);
        }
        else if (_pendingPx is SD.Rectangle nudged && SelectionNudge.Handles(e.KeyCode))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            SD.Rectangle screen = ScreenFromVirtual(nudged);
            if (SelectionNudge.Apply(screen, e.KeyCode, e.Shift, e.Control, _vs) is SD.Rectangle moved &&
                moved != screen)
            {
                SetPending(VirtualFromScreen(moved));
            }
        }
        else if (e.KeyCode == WF.Keys.Menu)
        {
            if (!_altLatched)
            {
                _altLatched = true;
                _magnifierOn = !_magnifierOn;
                if (_magnifierOn)
                    StartFollowMotion(); // the setting may be off, so the follow tick may not be running
                InvalidateAllSurfaces();
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

    internal void HandleMouseDown(WF.MouseEventArgs e)
    {
        SD.Point screen = CursorScreen();

        if (e.Button == WF.MouseButtons.Right)
        {
            Complete(WF.DialogResult.Cancel);
            return;
        }

        if (e.Button != WF.MouseButtons.Left)
            return;

        // A press ends any glide in flight, so the rect that answers the hit-test below is
        // always the rect on screen.
        StopPendingGlide();
        InvalidateAllSurfaces();

        if (e.Clicks >= 2 && _pendingPx is SD.Rectangle doubleClicked)
        {
            Confirm(doubleClicked);
            return;
        }

        if (_pendingPx is SD.Rectangle pending)
        {
            // The restored (or typed) region is a real object you can grab. Before this a
            // press anywhere threw it away and started a fresh marquee, so remembering the
            // last region only ever gave you a rectangle to accept or redraw.
            SD.Rectangle pendingScreen = ScreenFromVirtual(pending);
            int handle = SelectionHandles.HitTest(screen, pendingScreen);
            if (handle >= 0)
            {
                _resizeHandle = handle;
                _adjustStartRect = pendingScreen;
                InvalidateAllSurfaces();
                return;
            }

            if (pendingScreen.Contains(screen))
            {
                _movingPending = true;
                _adjustAnchor = screen;
                _adjustStartRect = pendingScreen;
                return;
            }
        }

        _dragStartScreen = screen;
        _currentScreen = screen;
        _dragging = true;
        _dragMoved = false;
        _dragRatio = _toolbar.AspectRatio;
        _hoverWindow = null;
        InvalidateAllSurfaces();
    }

    internal void HandleMouseMove()
    {
        _currentScreen = CursorScreen();

        if (_resizeHandle >= 0)
        {
            SetPending(VirtualFromScreen(SelectionHandles.Resize(
                _adjustStartRect, _resizeHandle, _currentScreen, _vs)));
            return;
        }

        if (_movingPending)
        {
            SetPending(VirtualFromScreen(SelectionHandles.Move(
                _adjustStartRect,
                _currentScreen.X - _adjustAnchor.X,
                _currentScreen.Y - _adjustAnchor.Y,
                _vs)));
            return;
        }

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
                _pendingPx = null;
                _toolbar.RefreshPendingState();
            }

            var virtualRect = VirtualFromScreen(MakeDragRect(_currentScreen));
            _toolbar.SetSize(virtualRect.Width, virtualRect.Height);
            InvalidateAllSurfaces();
        }
        else if (_pendingPx is null)
        {
            if (_selectedMode != ModeRegion)
                HitTestWindow(_currentScreen);
            // HitTestWindow repaints only when the hovered WINDOW changes, but the
            // loupe/crosshair/size chrome is anchored to the cursor — without this the
            // chrome froze at the last window-boundary crossing while the cursor moved.
            InvalidateFollowRegion(_lastFollowScreen, _currentScreen);
            _lastFollowScreen = _currentScreen;
        }
        else
        {
            InvalidateAllSurfaces();
        }
    }

    private SD.Point _lastFollowScreen; // last cursor pos the crosshair/loupe were painted at

    // Half-extents of the regions the idle crosshair/loupe occupy around the cursor
    // (mirrors FastRegionSelectorDialog).
    private const int CrosshairBandHalf = 16;
    private const int LoupeBoxHalf = 250;

    /// <summary>Repaints just the crosshair bands + loupe boxes at the old and new cursor
    /// positions, on whichever surfaces they touch — proportional to the chrome, not the
    /// (possibly 4K) monitor. Port of FastRegionSelectorDialog.InvalidateFollowRegion.</summary>
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

    private SD.Rectangle MonitorBoundsAt(SD.Point screen)
    {
        if (_monitorBounds.Contains(screen)) return _monitorBounds;
        foreach (var pane in _panes)
            if (!pane.IsDisposed && pane.MonitorBounds.Contains(screen))
                return pane.MonitorBounds;
        return WF.Screen.FromPoint(screen).Bounds;
    }

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

    private void OnFollowTick(object? sender, EventArgs e)
    {
        try
        {
            SD.Point cursor = CursorScreen();
            // See FastRegionSelectorDialog.OnFollowTick: keeps a self-raising window (crash
            // dialog, always-on-top utility) from covering the overlay and eating clicks.
            SelectorForeground.KeepOnTop(SurfaceHandles(), cursor);
            bool controlPressed = (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control;
            if (cursor == _currentScreen && controlPressed == _lastCtrlDown)
                return;

            _lastCtrlDown = controlPressed;
            HandleMouseMove();
        }
        catch (Exception ex)
        {
            Log.Error("All-in-one selector follow-tick failed (non-fatal)", ex);
        }
    }

    internal void HandleMouseUp(WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left)
            return;

        if (_resizeHandle >= 0 || _movingPending)
        {
            _resizeHandle = -1;
            _movingPending = false;
            InvalidateAllSurfaces();
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        _currentScreen = CursorScreen();

        if (!_dragMoved)
        {
            if (_pendingPx is SD.Rectangle pending)
            {
                if (!ScreenFromVirtual(pending).Contains(_currentScreen))
                    ClearPending();
                return;
            }

            if (_hoverWindow is not null)
                Confirm(VirtualFromScreen(_hoverWindow.Bounds));
            else
                ClearPending();
            return;
        }

        var virtualRect = VirtualFromScreen(MakeDragRect(_currentScreen));
        if (virtualRect.Width > 0 && virtualRect.Height > 0)
            Confirm(virtualRect);
    }

    internal bool IsDragging => _dragging;

    /// <summary>The pointer keeps reporting to the pressed surface while a marquee, a handle
    /// or the pending rect body is being dragged.</summary>
    internal bool CapturingPointer => _dragging || _resizeHandle >= 0 || _movingPending;

    /// <summary>The adjustable selection in virtual pixels, for tests.</summary>
    internal SD.Rectangle? PendingRegion => _pendingPx;

    /// <summary>Applies an adjusted selection and keeps the size fields in step with it.</summary>
    private void SetPending(SD.Rectangle virtualRect)
    {
        if (virtualRect.Width < 1 || virtualRect.Height < 1)
            return;
        _pendingPx = virtualRect;
        _toolbar.SetSize(virtualRect.Width, virtualRect.Height);
        InvalidateAllSurfaces();
    }

    /// <summary>
    /// Pops the pending selection handles in over QuickMs. Eight dots arriving at full size
    /// in one frame reads as a glitch; growing them reads as the region becoming grabbable —
    /// which, until they existed here, it was not.
    /// </summary>
    private void BeginHandlePop()
    {
        _handleTween?.Dispose();
        _handleScale = 0d;
        _handleTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutBack,
            progress =>
            {
                _handleScale = progress;
                if (_pendingPx is SD.Rectangle p)
                {
                    SD.Rectangle rect = ScreenFromVirtual(p);
                    rect.Inflate(SelectionHandles.HitTolerance, SelectionHandles.HitTolerance);
                    InvalidateScreenRect(rect);
                }
            },
            () => _handleTween = null);
    }

    internal WF.Cursor CursorForCurrent()
    {
        if (_pendingPx is SD.Rectangle pending)
        {
            SD.Rectangle screen = ScreenFromVirtual(pending);
            int handle = _resizeHandle >= 0 ? _resizeHandle : SelectionHandles.HitTest(_currentScreen, screen);
            if (SelectionHandles.CursorFor(handle) is WF.Cursor sizing)
                return sizing;
            if (_movingPending || screen.Contains(_currentScreen))
                return WF.Cursors.SizeAll;
        }
        return WF.Cursors.Cross;
    }

    internal void SetMode(int mode, AllInOneAction action)
    {
        SelectedAction = action;
        _selectedMode = mode;
        if (mode == ModeRegion && _hoverWindow is not null)
        {
            // Region mode means "draw a rectangle". Leaving the window highlight lit made
            // the Region and Window buttons decorative — either one hovered windows.
            _hoverWindow = null;
            InvalidateAllSurfaces();
        }
        _toolbar.UpdateMode(mode);
    }

    /// <summary>Toolbar mode ids. Fullscreen is not a mode — it confirms immediately.</summary>
    internal const int ModeRegion = 0;

    private int _selectedMode = ModeRegion;

    private void ShowToolbar()
    {
        if (_toolbar.IsDisposed)
            return;
        _toolbar.Show(this);
        // The toolbar lives over the primary surface (where the cursor starts).
        _toolbar.PositionWithin(_monitorBounds);
    }

    internal void ConfirmFullscreen() =>
        Confirm(new SD.Rectangle(0, 0, _vs.Width, _vs.Height));

    /// <summary>
    /// Expand to full size: the size-bar glyph that always looked like this now does it. It
    /// SELECTS the whole desktop rather than capturing it, so the region is still yours to
    /// trim before you confirm.
    /// </summary>
    internal void SelectWholeScreen()
    {
        // Through the ratio, not around it: expanding straight to the desktop's own shape
        // left the accent badge under the crop glyph still claiming 16:9 over a selection
        // that was not 16:9.
        if (_toolbar.AspectRatio is double ratio && ratio > 0)
            ApplyRatioAt(_vs.Width, ratio);
        else
            ApplyExactSize(_vs.Width, _vs.Height, glide: true);
    }

    /// <summary>
    /// Reshapes the pending region to a newly chosen ratio, keeping its top-left and width.
    /// Picking 16:9 and watching the selection you already framed stay 4:3 until you redrew
    /// it would make the menu look broken.
    ///
    /// A ratio taller than the desktop scales its WIDTH down instead of losing the height:
    /// clipping the height would hand back a rectangle that is not the ratio the badge under
    /// the crop glyph says is in force, which is the same "looks broken" this method exists
    /// to prevent.
    /// </summary>
    internal void ApplyAspectRatio(double? ratio)
    {
        if (ratio is not double value || value <= 0 || _pendingPx is not SD.Rectangle pending)
            return;
        ApplyRatioAt(pending.Width, value);
    }

    /// <summary>The largest rectangle of <paramref name="ratio"/> that fits the desktop and is
    /// no wider than <paramref name="width"/>.</summary>
    private void ApplyRatioAt(int width, double ratio)
    {
        width = Math.Min(width, _vs.Width);
        int height = AspectRatioPresets.HeightFor(width, ratio);
        if (height > _vs.Height)
        {
            height = _vs.Height;
            width = Math.Max(1, (int)Math.Round(height * ratio));
        }
        ApplyExactSize(width, height, glide: true);
    }

    /// <summary>
    /// Resizes the pending region to an exact size, keeping its top-left where it can and
    /// SLIDING it back inside the desktop where it cannot.
    ///
    /// Anchoring at the old top-left and then clipping is what made "expand to full size"
    /// select only the leftover below-and-right of whatever was already framed, and made a
    /// tall aspect preset on a low selection come out at some other ratio entirely. The
    /// requested size wins; only a size that genuinely exceeds the desktop is clipped.
    /// </summary>
    internal void ApplyExactSize(int width, int height) => ApplyExactSize(width, height, glide: false);

    /// <param name="glide">True for the button-driven changes (expand to full size, an aspect
    /// preset), which move the rect on their own and must be seen to move. The typed width and
    /// height fields stay instant: a size you are typing should track the keystroke.</param>
    private void ApplyExactSize(int width, int height, bool glide)
    {
        if (width < 1 || height < 1)
            return;

        width = Math.Min(width, _vs.Width);
        height = Math.Min(height, _vs.Height);
        SD.Point anchor = _pendingPx?.Location ?? new SD.Point(
            Math.Max(0, (_vs.Width - width) / 2),
            Math.Max(0, (_vs.Height - height) / 2));
        var rect = new SD.Rectangle(
            Math.Clamp(anchor.X, 0, Math.Max(0, _vs.Width - width)),
            Math.Clamp(anchor.Y, 0, Math.Max(0, _vs.Height - height)),
            width,
            height);
        if (rect.Width < 1 || rect.Height < 1)
            return;

        SD.Rectangle? from = _pendingPx;
        _pendingPx = rect;
        _hoverWindow = null;
        _toolbar.SetSize(rect.Width, rect.Height);
        _toolbar.RefreshPendingState();
        BeginHandlePop();
        // "Select the whole screen" and the ratio presets move the selection across the whole
        // desktop; assigning the rect and repainting teleported it while only the eight
        // handles animated. The glide is display-only — _pendingPx is the destination from
        // frame one, so Enter and the confirm button still commit the final rectangle.
        if (from is SD.Rectangle start && start != rect && glide)
            GlidePendingTo(start, rect);
        else
            StopPendingGlide();
        InvalidateAllSurfaces();
        Focus();
    }

    /// <summary>
    /// Display-only ease of the pending rect from one shape to another, in virtual px.
    /// <see cref="DisplayPx"/> is what paint reads; every hit-test reads the destination, and
    /// a press settles the glide so the two can never disagree for longer than a frame.
    /// </summary>
    private void GlidePendingTo(SD.Rectangle from, SD.Rectangle to)
    {
        _pendingGlideTween?.Dispose();
        _pendingGlide.Retarget(from, to);
        _pendingGlideTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                _pendingGlide.Update((float)progress);
                InvalidateAllSurfaces();
            },
            () =>
            {
                _pendingGlideTween = null;
                _pendingGlide.Stop();
                InvalidateAllSurfaces();
            });
    }

    /// <summary>Ends a glide early, leaving the rect at its destination.</summary>
    private void StopPendingGlide()
    {
        if (_pendingGlideTween is null && !_pendingGlide.IsActive)
            return;
        _pendingGlideTween?.Dispose();
        _pendingGlideTween = null;
        _pendingGlide.Stop();
    }

    internal void Cancel()
    {
        Complete(WF.DialogResult.Cancel);
    }

    /// <summary>Commits the pending region from the toolbar's confirm button; no-op when
    /// there is nothing pending (the button paints disabled then).</summary>
    internal void ConfirmPending()
    {
        if (_pendingPx is SD.Rectangle pending)
            Confirm(pending);
    }

    // ----------------------------------------------------------- painting (per surface)

    /// <summary>
    /// Paints one monitor surface. <paramref name="monitorBounds"/> is that monitor's
    /// physical bounds; the surface is 1:1, so screen->local is a pure subtraction.
    /// </summary>
    internal void PaintSurface(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
        SD.Size clientSize = monitorBounds.Size;
        bool cursorOnThisSurface = monitorBounds.Contains(_currentScreen);
        // THIS surface's monitor scale — see SelectorChrome.PillFont for why the chrome is
        // measured and drawn in pixels off it rather than in points.
        double s = WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds);

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
            // No frozen snapshot yet (or the capture failed): select over a plain dim.
            using var dim = new SD.SolidBrush(SD.Color.FromArgb(115, 0, 0, 0));
            g.FillRectangle(dim, 0, 0, clientSize.Width, clientSize.Height);
        }

        // The active selection (or hovered window) shows the frozen pixels at full brightness.
        SD.Rectangle? brightScreen = null;
        if (_dragging && _dragMoved)
            brightScreen = MakeDragRect(_currentScreen);
        else if (DisplayPx is SD.Rectangle pending)
            brightScreen = ScreenFromVirtual(pending);
        else if (_hoverWindow is not null)
            brightScreen = _hoverWindow.Bounds;

        if (brightScreen is SD.Rectangle bright)
        {
            var local = ToLocal(bright, monitorBounds);
            BrightenRegion(g, monitorBounds, local);
            using (var pen = new SD.Pen(Accent, 2))
                g.DrawRectangle(pen, local);
            if (_pendingPx is not null && !_dragging)
                SelectionHandles.Draw(g, local, _handleScale, _resizeHandle);
            if (cursorOnThisSurface)
            {
                // The readout labels the SETTLED rect, never the interpolated one: digits
                // spinning for 140 ms read as a glitch rather than as motion.
                var px = _hoverWindow is not null && !_dragging && _pendingPx is null
                    ? VirtualFromScreen(_hoverWindow.Bounds)
                    : _pendingPx is SD.Rectangle settled && !_dragging
                        ? settled
                        : VirtualFromScreen(bright);
                SD.Size follow = SelectorChrome.ReadoutOffset(s);
                int pillGap = WinShot.Recording.RecordingMonitorDpi.Px(8, s);
                SD.Point at = _dragging || _pendingPx is not null
                    ? new SD.Point(local.Right + pillGap, local.Bottom + pillGap)
                    : new SD.Point(
                        ToLocal(_currentScreen, monitorBounds).X + follow.Width,
                        ToLocal(_currentScreen, monitorBounds).Y + follow.Height);
                SelectorChrome.DrawLabel(g, clientSize, $"{px.Width} × {px.Height}", at.X, at.Y, s);
            }
        }

        bool controlPressed = (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control;
        if (cursorOnThisSurface && _options.IsCrosshairVisible(controlPressed))
            SelectorChrome.DrawCrosshair(g, clientSize, ToLocal(_currentScreen, monitorBounds), s);

        SD.Size dragSize = _dragging && _dragMoved
            ? MakeDragRect(_currentScreen).Size
            : SD.Size.Empty;
        if (cursorOnThisSurface && _magnifierOn)
        {
            FastSelectorLoupeRenderer.Draw(
                g, clientSize, _vs, ToLocal(_currentScreen, monitorBounds), _currentScreen, _frozen, dragSize);
        }
        else if (cursorOnThisSurface && dragSize.IsEmpty && _pendingPx is null && _hoverWindow is null)
        {
            // With the magnifier off nothing else on screen says where the pointer is.
            SelectorChrome.DrawCursorReadout(g, clientSize,
                SelectorReadout.Text(_currentScreen, dragSize), ToLocal(_currentScreen, monitorBounds), s);
        }
    }

    /// <summary>
    /// True when PaintSurface's own background covers every pixel, so the surface does not
    /// need clearing first. Without a frozen snapshot it paints a SEMI-transparent dim that
    /// relies on the black underneath, so the Clear is still required there.
    /// </summary>
    internal bool HasFullSurfaceBackground => _frozen is not null;

    /// <summary>
    /// This monitor's frozen slice with the selection dim baked in, built and cached on
    /// first use. Lets the hot follow-paint be one opaque blit instead of a per-frame
    /// snapshot crop plus a full-screen software alpha fill.
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
    /// ownership to the caller (null if freeze was unavailable). Lets a capture flow use exactly
    /// what was selected instead of re-grabbing the live screen. Null after the first call.</summary>
    public SD.Bitmap? TakeCapturedRegion()
    {
        var bmp = _capturedRegion;
        _capturedRegion = null;
        return bmp;
    }

    private void HitTestWindow(SD.Point screenPoint)
    {
        var hover = ResolveWindow(screenPoint);
        if (ReferenceEquals(hover, _hoverWindow)) return;
        _hoverWindow = hover;
        if (_hoverWindow is not null)
        {
            var px = VirtualFromScreen(_hoverWindow.Bounds);
            _toolbar.SetSize(px.Width, px.Height);
        }
        InvalidateAllSurfaces();
    }

    /// <summary>
    /// Resolves the window under the cursor by real z-order (WindowFromPoint) so a small
    /// foreground window wins over a larger background one; falls back to the first
    /// bounds-containing window when the topmost hwnd isn't in the cached list.
    /// </summary>
    private WindowInfo? ResolveWindow(SD.Point screenPoint)
    {
        IntPtr top = WindowEnumerator.TopLevelWindowFromPoint(screenPoint);
        if (top != IntPtr.Zero && top != Handle && top != _toolbar.Handle && !IsPaneHandle(top))
        {
            var match = _windows.FirstOrDefault(w => w.Handle == top);
            if (match is not null)
                return match;
        }

        return _windows.FirstOrDefault(w => w.Bounds.Contains(screenPoint));
    }

    /// <summary>Every window handle the selector owns, back to front - the toolbar last so
    /// it stays above the panes when <see cref="SelectorForeground.KeepOnTop"/> re-raises them.</summary>
    private List<IntPtr> SurfaceHandles()
    {
        var handles = new List<IntPtr>(_panes.Count + 2);
        if (IsHandleCreated)
            handles.Add(Handle);
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed && pane.IsHandleCreated)
                handles.Add(pane.Handle);
        }
        if (!_toolbar.IsDisposed && _toolbar.IsHandleCreated)
            handles.Add(_toolbar.Handle);
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

    private void TryRestoreLastRegion()
    {
        if (_settings is null) return;
        if (!_options.TryGetRememberedRegion(_settings.Current.LastCaptureRegion, _vs, out SD.Rectangle px)) return;
        if (px.Width < 1 || px.Height < 1) return;
        _pendingPx = px;
        _toolbar.SetSize(px.Width, px.Height);
        _toolbar.RefreshPendingState();
        BeginHandlePop();
        InvalidateAllSurfaces();
    }

    private void Confirm(SD.Rectangle virtualRect)
    {
        virtualRect.Intersect(new SD.Rectangle(0, 0, _vs.Width, _vs.Height));
        if (virtualRect.Width < 1 || virtualRect.Height < 1) return;

        SelectedRegionPx = virtualRect;
        // Crop the result from the frozen snapshot so a freeze-aware caller gets exactly what was
        // selected. (The all-in-one flow re-captures live for Record/Scroll; the crop is available
        // via TakeCapturedRegion for parity with the region selector.)
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
        // A tween outliving the session holds the process-wide 1 ms clock.
        _handleTween?.Dispose();
        _handleTween = null;
        _handleScale = 1d;
        _pendingGlideTween?.Dispose();
        _pendingGlideTween = null;
        _pendingGlide.Stop();
        Capture = false;
        DisposeFrozen(); // free the full snapshot now; _capturedRegion stays for the caller
        Park();
        if (!_toolbar.IsDisposed)
        {
            _toolbar.StopMotion();
            _toolbar.Hide();
        }
        _completion?.TrySetResult(result);
        _completion = null;
    }

    private void ClearPending()
    {
        _pendingPx = null;
        _hoverWindow = null;
        _toolbar.RefreshPendingState();
        InvalidateAllSurfaces();
    }

    private SD.Rectangle MakeDragRect(SD.Point screenPoint) =>
        AllInOneDragLayout.CreatePixelRectangle(_dragStartScreen, screenPoint, _dragRatio);

    // ----------------------------------------------------------- coordinate helpers

    /// <summary>Screen-physical rect -> local pixels on a 1:1 surface (pure offset).</summary>
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

    private SD.Rectangle ScreenFromVirtual(SD.Rectangle virtualRect) =>
        new(virtualRect.X + _vs.X, virtualRect.Y + _vs.Y, virtualRect.Width, virtualRect.Height);

    /// <summary>Test seam — see FastRegionSelectorDialog.CursorProvider.</summary>
    internal static Func<SD.Point>? CursorProvider;

    /// <summary>True physical cursor position — DPI-independent across monitors, the
    /// anchor that keeps selection math correct on mixed-DPI setups.</summary>
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
        private readonly FastAllInOneSelectorDialog _owner;
        private readonly SD.Rectangle _bounds;

        public SelectorPane(FastAllInOneSelectorDialog owner, SD.Rectangle monitorBounds, bool freezeScreen)
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

        /// <summary>The physical monitor bounds this pane covers (pane-reuse matching).</summary>
        public SD.Rectangle MonitorBounds => _bounds;

        protected override bool ShowWithoutActivation => true;

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
            if (!_owner.HasFullSurfaceBackground)
                e.Graphics.Clear(SD.Color.Black);
            _owner.PaintSurface(e.Graphics, _bounds);
            base.OnPaint(e);
        }
    }

    /// <summary>
    /// CleanShot-style all-in-one capture bar. One borderless translucent host paints TWO
    /// separate rounded dark bars: a main bar of icon-over-label mode buttons (Area, Fullscreen,
    /// Window, Scrolling, Timer, OCR, Recording) and, to its right, a smaller bar holding the two
    /// size fields, an expand/aspect-lock toggle, and a crop/confirm button. Everything except the
    /// two real size text boxes is owner-drawn so there are no WinForms focus rectangles and the
    /// bars get true rounded corners. The window's Region is the union of the two bar shapes, so the
    /// frozen desktop shows through the gap between them, matching CleanShot.
    /// </summary>
    private sealed class ToolbarForm : WF.Form
    {
        // CleanShot look, WinShot palette: every color aliases a ThemePalette token so the
        // bar cannot drift from the rest of the app when the palette is tuned.
        private static readonly SD.Color BarFill = ThemePalette.WindowBg;
        private static readonly SD.Color BarBorder = ThemePalette.BorderStrong;
        private static readonly SD.Color HoverFill = ThemePalette.HoverFill;
        /// <summary>
        /// Every accent chip on this bar that carries small white text — the selected mode
        /// chip and the ratio badge. White on the bare accent measures 3.65:1, under the
        /// 4.5:1 floor for text this small; the pressed accent is the same identity a step
        /// darker and clears it at 5.62:1.
        /// </summary>
        private static readonly SD.Color SelectedFill = ThemePalette.AccentPressed;
        private static readonly SD.Color GlyphColor = ThemePalette.TextPrimary;
        private static readonly SD.Color LabelColor = ThemePalette.TextSecondary;
        private static readonly SD.Color FieldFill = SD.Color.FromArgb(0x3A, 0x3A, 0x3A);

        // Design units (96-DPI logical px). Point-based fonts render at the monitor's
        // DPI, so the pixel layout must scale with them via U() or the bar renders
        // cramped/clipped on 125%/150% displays.
        private const int BarHeight = 64;
        private const int CornerRadius = 14;
        private const int ButtonWidth = 64;
        private const int ButtonGap = 2;
        private const int BarPadX = 6;
        private const int BarGap = 12;       // transparent gap between the two bars
        private const int GlyphTop = 12;
        private const int GlyphHeight = 24;
        private const int LabelTop = 38;
        private const int LabelHeight = 16;

        private readonly double _u = WinShot.Recording.RecordingMonitorDpi.ScaleFor(PrimaryBounds());

        private int U(int logical) => (int)Math.Round(logical * _u);

        private readonly FastAllInOneSelectorDialog _owner;
        private readonly SD.Font _glyphFont = ThemePalette.IconFont(ThemePalette.GlyphActionPt);
        private readonly SD.Font _tipFont = ThemePalette.UiFont(ThemePalette.TextCaptionPt);
        private readonly SD.Font _ocrFont = ThemePalette.UiFont(ThemePalette.GlyphTextPt, SD.FontStyle.Bold);
        private readonly SD.Font _labelFont = ThemePalette.UiFont(ThemePalette.TextMicroPt);
        private readonly SD.Font _fieldFont = ThemePalette.UiFont(ThemePalette.TextBodyLgPt);
        private readonly List<BarButton> _buttons = new();
        private readonly WF.TextBox _widthBox;
        private readonly WF.TextBox _heightBox;
        private readonly WF.ToolTip _toolTip = new() { InitialDelay = 350, ReshowDelay = 120, OwnerDraw = true };

        private SD.Rectangle _mainBar;       // client-space bounds of the mode bar
        private SD.Rectangle _sizeBar;       // client-space bounds of the size bar
        private SD.Rectangle _ratioRect;     // aspect-ratio menu inside the size bar
        private SD.Rectangle _expandRect;    // expand-to-full-size inside the size bar
        private SD.Rectangle _cropRect;      // confirm button inside the size bar
        private SD.Rectangle _xLabelRect;    // the "×" between the size fields
        private int _selectedMode;
        private int _hoverIndex = -1;
        private int _ratioIndex;             // index into AspectRatioPresets.All; 0 = Freeform
        private double _badgeFade = 1d;      // 0..1 ramp on the ratio badge after a change

        /// <summary>The preset the BADGE is drawn from. It lags <see cref="_ratioIndex"/>
        /// while the badge fades out, so picking Freeform eases the badge and its accent
        /// chip away instead of dropping both in one frame — the entrance already eased,
        /// and an asymmetric pair is the one state on this bar that popped.</summary>
        private int _badgeIndex;
        private IDisposable? _badgeTween;

        public ToolbarForm(FastAllInOneSelectorDialog owner)
        {
            _owner = owner;

            AutoScaleMode = WF.AutoScaleMode.None;
            BackColor = BarFill;
            DoubleBuffered = true;
            FormBorderStyle = WF.FormBorderStyle.None;
            KeyPreview = true;
            ShowInTaskbar = false;
            StartPosition = WF.FormStartPosition.Manual;
            TopMost = true;
            SetStyle(
                WF.ControlStyles.AllPaintingInWmPaint |
                WF.ControlStyles.OptimizedDoubleBuffer |
                WF.ControlStyles.ResizeRedraw |
                WF.ControlStyles.UserPaint,
                true);

            // Mode buttons — icon (or "Aa" for OCR) over a small caption, in reference order.
            // Fullscreen confirms immediately; the rest select a mode that drives SelectedAction.
            _buttons.Add(new BarButton("Region", "", 0, AllInOneAction.Capture, isMode: true));
            _buttons.Add(new BarButton("Fullscreen", "", -1, AllInOneAction.Capture, isMode: false));
            _buttons.Add(new BarButton("Window", "", 1, AllInOneAction.Capture, isMode: true));
            _buttons.Add(new BarButton("Scrolling", "", 2, AllInOneAction.Scroll, isMode: true));
            _buttons.Add(new BarButton("Timer", "", 3, AllInOneAction.Timer, isMode: true));
            _buttons.Add(new BarButton("OCR", "Aa", 4, AllInOneAction.Ocr, isMode: true) { IsTextGlyph = true });
            _buttons.Add(new BarButton("Recording", "", 5, AllInOneAction.Record, isMode: true));

            _widthBox = SizeBox();
            _heightBox = SizeBox();
            Controls.Add(_widthBox);
            Controls.Add(_heightBox);

            BuildLayout();

            // The stock tooltip is a light system balloon — the one un-themed element over
            // an otherwise fully owner-drawn dark bar — so it is drawn in the palette too.
            _toolTip.Popup += (_, e) => e.ToolTipSize = SD.Size.Add(
                WF.TextRenderer.MeasureText(_toolTip.GetToolTip(e.AssociatedControl), _tipFont),
                new SD.Size(12, 8));
            _toolTip.Draw += (_, e) =>
            {
                using var bg = new SD.SolidBrush(SD.Color.FromArgb(0x2B, 0x2B, 0x2B));
                e.Graphics.FillRectangle(bg, e.Bounds);
                using var edge = new SD.Pen(BarBorder, 1);
                e.Graphics.DrawRectangle(edge,
                    new SD.Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1));
                WF.TextRenderer.DrawText(e.Graphics, e.ToolTipText, _tipFont, e.Bounds, GlyphColor,
                    WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter);
            };

            MouseMove += OnBarMouseMove;
            MouseLeave += (_, _) => SetHover(-1);
            MouseClick += OnBarMouseClick;
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == WF.Keys.Escape)
                    _owner.Cancel();
            };
        }

        protected override bool ShowWithoutActivation => true;

        /// <summary>
        /// The chosen preset ratio, which constrains the live drag. Freeform returns null.
        /// The width and height fields still set an exact size with Enter — a ratio picked by
        /// name beats a ratio inferred from two numbers someone had to know first.
        /// </summary>
        public double? AspectRatio => AspectRatioPresets.At(_ratioIndex).Ratio;

        public void PositionWithin(SD.Rectangle monitorBounds)
        {
            Left = monitorBounds.X + Math.Max(0, (monitorBounds.Width - Width) / 2);
            Top = monitorBounds.Y + 18;
        }

        public void SetSize(int width, int height)
        {
            _widthBox.Text = Math.Max(1, width).ToString();
            _heightBox.Text = Math.Max(1, height).ToString();
        }

        public void UpdateMode(int selectedMode)
        {
            if (_selectedMode == selectedMode)
                return;

            // The accent chip slides from the old button to the new one instead of
            // teleporting; with seven cells in a row the jump was the only thing on the bar
            // that moved without saying where it came from.
            SD.Rectangle from = ChipBounds(_selectedMode);
            _selectedMode = selectedMode;
            SD.Rectangle to = ChipBounds(_selectedMode);
            _chipTween?.Dispose();
            _chipTween = null;
            if (from.IsEmpty || to.IsEmpty)
            {
                _chipGlide.Stop();
                Invalidate(_mainBar);
                return;
            }

            _chipGlide.Retarget(from, to);
            _chipTween = WinShot.Core.Motion.Run(
                WinShot.Core.Motion.SwapMs,
                WinShot.Core.Motion.EaseOutCubic,
                progress =>
                {
                    _chipGlide.Update((float)progress);
                    Invalidate(_mainBar);
                },
                () =>
                {
                    _chipTween = null;
                    _chipGlide.Stop();
                    Invalidate(_mainBar);
                });
        }

        /// <summary>
        /// Drops the bar's in-flight tweens when the session ends. Both of them hold the
        /// process-wide 1 ms clock, and the bar is pooled — so without this, changing the
        /// mode or the ratio and immediately pressing Escape left a tween repainting a
        /// hidden window on a 1 ms timer for the rest of its duration.
        /// </summary>
        public void StopMotion()
        {
            _chipTween?.Dispose();
            _chipTween = null;
            _chipGlide.Stop();
            _badgeTween?.Dispose();
            _badgeTween = null;
            _badgeFade = 1d;
            _badgeIndex = _ratioIndex;
        }

        /// <summary>Jumps to a mode with no glide — a pooled reuse is not a mode change the
        /// user made, and the toolbar is hidden while it happens.</summary>
        public void ResetMode(int selectedMode)
        {
            _chipTween?.Dispose();
            _chipTween = null;
            _chipGlide.Stop();
            _selectedMode = selectedMode;
            Invalidate(_mainBar);
        }

        /// <summary>The accent chip's client-space bounds for a mode, or empty when no
        /// button owns that mode (Fullscreen, and the initial unset state).</summary>
        private SD.Rectangle ChipBounds(int mode)
        {
            foreach (var button in _buttons)
            {
                if (button.IsMode && button.Mode == mode)
                    return SD.Rectangle.Inflate(button.Bounds, -4, -8);
            }
            return SD.Rectangle.Empty;
        }

        private readonly RectangleTween _chipGlide = new();
        private IDisposable? _chipTween;

        /// <summary>Repaints the confirm button when the pending region appears/disappears.</summary>
        public void RefreshPendingState() => Invalidate(_sizeBar);

        /// <summary>Clears the chosen ratio between pooled sessions — a stale one made every
        /// new drag snap to the previous session's shape.</summary>
        public void ResetAspectLock()
        {
            _badgeTween?.Dispose();
            _badgeTween = null;
            _badgeFade = 1d;
            _badgeIndex = 0;
            if (_ratioIndex == 0)
                return;
            _ratioIndex = 0;
            Invalidate();
        }

        /// <summary>The ratio list, opened from the crop cell.</summary>
        private void ShowRatioMenu()
        {
            var menu = new WF.ContextMenuStrip
            {
                Renderer = new DarkDropDown.DarkMenuRenderer(),
                ShowImageMargin = false,
                // WinForms only runs the renderer's check pass when a margin exists, so with
                // both margins off the Checked flags below were dead code and reopening the
                // menu could not say which ratio was in force.
                ShowCheckMargin = true,
            };
            for (int i = 0; i < AspectRatioPresets.All.Count; i++)
            {
                int index = i;
                var item = new WF.ToolStripMenuItem(AspectRatioPresets.All[i].Label)
                {
                    Checked = index == _ratioIndex,
                };
                item.Click += (_, _) => ChooseRatio(index);
                menu.Items.Add(item);
            }
            // Deferred, never inline: WinForms raises Closed from inside
            // ToolStripDropDown.SetVisibleCore(false) and keeps touching the strip after the
            // handler returns — SetWindowLong(Handle) on a disposed ContextMenuStrip threw
            // ObjectDisposedException out of the framework's own click handling, aborting the
            // hide sequence (mouse capture and the modal menu filter never released) on a
            // topmost, ShowWithoutActivation toolbar, and App's ThreadException handler only
            // logged it. Picking a ratio looked like it worked and left the menu machinery
            // half torn down.
            menu.Closed += (_, _) => menu.BeginInvoke(new Action(menu.Dispose));
            menu.Show(this, new SD.Point(_ratioRect.Left, _ratioRect.Bottom + U(4)));
        }

        private void ChooseRatio(int index)
        {
            if (_ratioIndex == index)
                return;
            _ratioIndex = index;
            _owner.ApplyAspectRatio(AspectRatioPresets.At(index).Ratio);

            // The badge and its accent chip fade rather than blinking — they are the only
            // thing on the bar that says what shape the next drag will take. Freeform fades
            // them OUT over the outgoing preset, which is why the badge paints from
            // _badgeIndex: entrance and exit are the same 140 ms either way, and a tween
            // that ran for a badge nobody could see is gone.
            _badgeTween?.Dispose();
            _badgeTween = null;
            bool chosen = AspectRatioPresets.At(index).Ratio is not null;
            bool hadBadge = AspectRatioPresets.At(_badgeIndex).Ratio is not null;
            if (!chosen && !hadBadge)
            {
                _badgeIndex = index;
                _badgeFade = 1d;
                Invalidate();
                return;
            }
            if (chosen)
                _badgeIndex = index;
            _badgeFade = chosen ? 0d : 1d;
            _badgeTween = WinShot.Core.Motion.Run(
                WinShot.Core.Motion.QuickMs,
                WinShot.Core.Motion.EaseOutCubic,
                progress => { _badgeFade = chosen ? progress : 1d - progress; Invalidate(_sizeBar); },
                () =>
                {
                    _badgeTween = null;
                    if (!chosen)
                        _badgeIndex = index; // faded out; stop holding the outgoing preset
                });
            Invalidate();
        }

        /// <summary>The chosen ratio index, for tests.</summary>
        internal int RatioIndex => _ratioIndex;

        // ---------------------------------------------------------------- layout

        private void BuildLayout()
        {
            int barHeight = U(BarHeight);
            int x = U(BarPadX);
            foreach (var b in _buttons)
            {
                b.Bounds = new SD.Rectangle(x, 0, U(ButtonWidth), barHeight);
                x += U(ButtonWidth) + U(ButtonGap);
            }

            int mainWidth = x - U(ButtonGap) + U(BarPadX);
            _mainBar = new SD.Rectangle(0, 0, mainWidth, barHeight);

            // Size bar: [ width ] × [ height ]  [expand]  [confirm]
            int fieldW = U(48);
            int iconW = U(34);
            int innerPad = U(12);
            // A single-line TextBox sizes its own height to the font; center on that real height.
            int fieldH = _widthBox.PreferredHeight;
            int sizeBarLeft = _mainBar.Right + U(BarGap);
            int sx = sizeBarLeft + innerPad;
            int fieldY = (barHeight - fieldH) / 2;

            _widthBox.SetBounds(sx, fieldY, fieldW, fieldH);
            sx += fieldW;
            _xLabelRect = new SD.Rectangle(sx, 0, U(16), barHeight);
            sx += U(16);
            _heightBox.SetBounds(sx, fieldY, fieldW, fieldH);
            sx += fieldW + U(8);

            _ratioRect = new SD.Rectangle(sx, (barHeight - U(40)) / 2, iconW, U(40));
            sx += iconW + U(4);
            _expandRect = new SD.Rectangle(sx, (barHeight - U(40)) / 2, iconW, U(40));
            sx += iconW + U(4);
            _cropRect = new SD.Rectangle(sx, (barHeight - U(40)) / 2, iconW, U(40));
            sx += iconW + innerPad;

            _sizeBar = new SD.Rectangle(sizeBarLeft, 0, sx - sizeBarLeft, barHeight);

            ClientSize = new SD.Size(_sizeBar.Right, barHeight);
            ApplyRegion();
        }

        private void ApplyRegion()
        {
            // The host window is shaped to the two bars only, so the desktop shows through the gap.
            IntPtr main = CreateRoundRectRgn(
                _mainBar.Left, _mainBar.Top, _mainBar.Right + 1, _mainBar.Bottom + 1,
                U(CornerRadius) * 2, U(CornerRadius) * 2);
            IntPtr size = CreateRoundRectRgn(
                _sizeBar.Left, _sizeBar.Top, _sizeBar.Right + 1, _sizeBar.Bottom + 1,
                U(CornerRadius) * 2, U(CornerRadius) * 2);
            CombineRgn(main, main, size, RgnOr);
            Region?.Dispose();
            Region = SD.Region.FromHrgn(main);
            DeleteObject(main);
            DeleteObject(size);
        }

        // ---------------------------------------------------------------- painting

        protected override void OnPaint(WF.PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BarFill);

            PaintBar(g, _mainBar);
            PaintBar(g, _sizeBar);
            PaintSelectedChip(g);

            for (int i = 0; i < _buttons.Count; i++)
                PaintButton(g, _buttons[i], i == _hoverIndex);

            PaintSizeBar(g);
            base.OnPaint(e);
        }

        private void PaintBar(SD.Graphics g, SD.Rectangle bar)
        {
            using var path = GdiPaths.RoundedRect(new SD.Rectangle(bar.X, bar.Y, bar.Width - 1, bar.Height - 1), U(CornerRadius));
            using var fill = new SD.SolidBrush(BarFill);
            using var pen = new SD.Pen(BarBorder, 1);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        /// <summary>Paints the sliding accent chip once, under every button.</summary>
        private void PaintSelectedChip(SD.Graphics g)
        {
            SD.Rectangle chip = _chipGlide.IsActive ? _chipGlide.Current : ChipBounds(_selectedMode);
            if (chip.IsEmpty)
                return;
            using var path = GdiPaths.RoundedRect(chip, 9);
            using var brush = new SD.SolidBrush(SelectedFill);
            g.FillPath(brush, path);
        }

        private void PaintButton(SD.Graphics g, BarButton b, bool hot)
        {
            bool selected = b.IsMode && b.Mode == _selectedMode;
            var inner = SD.Rectangle.Inflate(b.Bounds, -4, -8);
            if (hot && !selected)
            {
                using var path = GdiPaths.RoundedRect(inner, 9);
                using var brush = new SD.SolidBrush(HoverFill);
                g.FillPath(brush, path);
            }

            SD.Color glyphColor = selected ? SD.Color.White : GlyphColor;
            var glyphRect = new SD.Rectangle(b.Bounds.X, U(GlyphTop), b.Bounds.Width, U(GlyphHeight));
            WF.TextRenderer.DrawText(g, b.Glyph, b.IsTextGlyph ? _ocrFont : _glyphFont, glyphRect, glyphColor,
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);

            var labelRect = new SD.Rectangle(b.Bounds.X, U(LabelTop), b.Bounds.Width, U(LabelHeight));
            WF.TextRenderer.DrawText(g, b.Label, _labelFont, labelRect,
                selected ? SD.Color.White : LabelColor,
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
        }

        private void PaintSizeBar(SD.Graphics g)
        {
            // Soft rounded wells behind the two size fields, plus the "×" separator.
            DrawFieldWell(g, _widthBox.Bounds);
            DrawFieldWell(g, _heightBox.Bounds);
            WF.TextRenderer.DrawText(g, "×", _fieldFont, _xLabelRect, LabelColor,
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);

            // Aspect-ratio menu: crop glyph with the chosen ratio as a badge under it.
            AspectRatioPreset preset = AspectRatioPresets.At(_badgeIndex);
            double badgeFade = Math.Clamp(_badgeFade, 0d, 1d);
            bool ratioChosen = preset.Ratio is not null && badgeFade > 0d;
            if (_hoverIndex == HoverRatio)
            {
                using var path = GdiPaths.RoundedRect(_ratioRect, U(8));
                using var brush = new SD.SolidBrush(HoverFill);
                g.FillPath(brush, path);
            }
            if (ratioChosen)
            {
                using var path = GdiPaths.RoundedRect(_ratioRect, U(8));
                using var brush = new SD.SolidBrush(
                    SD.Color.FromArgb((int)Math.Round(SelectedFill.A * badgeFade), SelectedFill));
                g.FillPath(brush, path);
            }
            var ratioGlyphRect = _ratioRect;
            if (ratioChosen)
                ratioGlyphRect.Height -= U(11); // make room for the badge under the glyph
            // U+E7A8, not the crop glyph: with Region selected and a ratio chosen the bar was
            // showing two identical accent-filled crop icons a few cells apart.
            WF.TextRenderer.DrawText(g, "", _glyphFont, ratioGlyphRect,
                ratioChosen
                    ? WinShot.Core.ThemePalette.Over(
                        SD.Color.FromArgb((int)Math.Round(255 * badgeFade), SD.Color.White), GlyphColor)
                    : GlyphColor,
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
            if (ratioChosen)
            {
                int badgeAlpha = (int)Math.Round(255 * badgeFade);
                var badgeRect = new SD.Rectangle(
                    _ratioRect.X, _ratioRect.Bottom - U(13), _ratioRect.Width, U(11));
                // GDI+ DrawString, not TextRenderer: GDI drops the alpha byte, so the ramp
                // this fade exists for was popping the badge in at full strength.
                using (var badgeBrush = new SD.SolidBrush(SD.Color.FromArgb(badgeAlpha, SD.Color.White)))
                using (var badgeFormat = new SD.StringFormat(SD.StringFormat.GenericTypographic)
                {
                    Alignment = SD.StringAlignment.Center,
                    LineAlignment = SD.StringAlignment.Center,
                    FormatFlags = SD.StringFormatFlags.NoWrap,
                })
                {
                    var prevHint = g.TextRenderingHint;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.DrawString(preset.Badge, _labelFont, badgeBrush, badgeRect, badgeFormat);
                    g.TextRenderingHint = prevHint;
                }
            }

            // Expand to full size (diagonal arrows) — it finally does what it looks like.
            if (_hoverIndex == HoverExpand)
            {
                using var path = GdiPaths.RoundedRect(_expandRect, U(8));
                using var brush = new SD.SolidBrush(HoverFill);
                g.FillPath(brush, path);
            }
            WF.TextRenderer.DrawText(g, "", _glyphFont, _expandRect, GlyphColor,
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);

            // Confirm button: commits the pending region, dimmed while there is nothing to
            // commit. No caret — there is no menu behind this button.
            bool cropEnabled = _owner._pendingPx is not null;
            bool cropHot = cropEnabled && _hoverIndex == HoverCrop;
            if (cropHot)
            {
                using var path = GdiPaths.RoundedRect(_cropRect, 8);
                using var brush = new SD.SolidBrush(HoverFill);
                g.FillPath(brush, path);
            }
            var cropGlyphRect = _cropRect;
            // A check, not a second crop glyph: the crop glyph is the ratio menu now.
            WF.TextRenderer.DrawText(g, "", _glyphFont, cropGlyphRect,
                cropEnabled ? GlyphColor : SD.Color.FromArgb(0x60, GlyphColor),
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
        }

        private static void DrawFieldWell(SD.Graphics g, SD.Rectangle field)
        {
            var well = SD.Rectangle.Inflate(field, 3, 3);
            using var path = GdiPaths.RoundedRect(well, 7);
            using var brush = new SD.SolidBrush(FieldFill);
            g.FillPath(brush, path);
        }

        // ---------------------------------------------------------------- input

        private const int HoverExpand = 1000;
        private const int HoverCrop = 1001;
        private const int HoverRatio = 1002;

        private void OnBarMouseMove(object? sender, WF.MouseEventArgs e)
        {
            int hit = HitTest(e.Location);
            SetHover(hit);
        }

        private void SetHover(int index)
        {
            if (_hoverIndex == index)
                return;
            _hoverIndex = index;
            bool cropDisabled = index == HoverCrop && _owner._pendingPx is null;
            Cursor = index == -1 || cropDisabled ? WF.Cursors.Default : WF.Cursors.Hand;
            // Tooltips only for the two caption-less controls; the mode buttons already
            // carry visible captions under their glyphs.
            _toolTip.SetToolTip(this,
                index == HoverRatio ? "Aspect ratio"
                : index == HoverExpand ? "Select the whole screen"
                : index == HoverCrop ? (cropDisabled ? "Capture selected region (draw or restore one first)" : "Capture selected region")
                : null);
            Invalidate();
        }

        private void OnBarMouseClick(object? sender, WF.MouseEventArgs e)
        {
            if (e.Button != WF.MouseButtons.Left)
                return;

            int hit = HitTest(e.Location);
            if (hit == HoverRatio)
            {
                ShowRatioMenu();
                return;
            }
            if (hit == HoverExpand)
            {
                _owner.SelectWholeScreen();
                return;
            }
            if (hit == HoverCrop)
            {
                _owner.ConfirmPending();   // commits the pending region; inert when there is none
                return;
            }
            if (hit < 0 || hit >= _buttons.Count)
                return;

            var b = _buttons[hit];
            if (b.IsMode)
                _owner.SetMode(b.Mode, b.Action);
            else
                _owner.ConfirmFullscreen();
        }

        private int HitTest(SD.Point p)
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                if (_buttons[i].Bounds.Contains(p))
                    return i;
            }
            if (_ratioRect.Contains(p))
                return HoverRatio;
            if (_expandRect.Contains(p))
                return HoverExpand;
            if (_cropRect.Contains(p))
                return HoverCrop;
            return -1;
        }

        // ---------------------------------------------------------------- helpers

        private WF.TextBox SizeBox()
        {
            var box = new WF.TextBox
            {
                BackColor = FieldFill,
                BorderStyle = WF.BorderStyle.None,
                Font = ThemePalette.UiFont(ThemePalette.TextBodyLgPt, SD.FontStyle.Bold),
                ForeColor = SD.Color.White,
                TextAlign = WF.HorizontalAlignment.Center,
            };
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == WF.Keys.Escape)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    _owner.Cancel();
                    return;
                }
                if (e.KeyCode != WF.Keys.Enter)
                    return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (int.TryParse(_widthBox.Text, out int w) && int.TryParse(_heightBox.Text, out int h))
                    _owner.ApplyExactSize(w, h);
            };
            return box;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _chipTween?.Dispose();
                _chipTween = null;
                _badgeTween?.Dispose();
                _badgeTween = null;
                _glyphFont.Dispose();
                _tipFont.Dispose();
                _ocrFont.Dispose();
                _labelFont.Dispose();
                _fieldFont.Dispose();
                _toolTip.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class BarButton(string label, string glyph, int mode, AllInOneAction action, bool isMode)
        {
            public string Label { get; } = label;
            public string Glyph { get; } = glyph;
            public int Mode { get; } = mode;
            public AllInOneAction Action { get; } = action;
            public bool IsMode { get; } = isMode;
            public bool IsTextGlyph { get; init; }
            public SD.Rectangle Bounds { get; set; }
        }

        private const int RgnOr = 2;

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr dst, IntPtr src1, IntPtr src2, int mode);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);
    }
}
