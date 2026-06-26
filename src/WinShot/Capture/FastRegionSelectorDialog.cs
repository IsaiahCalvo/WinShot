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
/// math is anchored to GetCursorPos (true physical px). Screen-freeze: the whole desktop is
/// snapshotted on open and selection happens on that still image, cropped at confirm.
/// </summary>
public sealed class FastRegionSelectorDialog : WF.Form
{
    public enum SelectorMode { Area, Window }

    private const int DragThresholdPx = 4;
    private const int CrosshairGapPx = 10;
    private const int HandleHalf = 4;       // handle square is 2*HandleHalf px
    private const int HandleHitTol = 9;     // grab tolerance around a handle center
    private static readonly SD.Color Accent = ThemePalette.Accent;
    private static FastRegionSelectorDialog? _cached;

    private SD.Rectangle _vs = CaptureService.VirtualScreen;
    private SD.Rectangle _monitorBounds;
    private SettingsService? _settings;
    private SelectorMode _mode = SelectorMode.Area;
    private List<WindowInfo> _windows = new();
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
    private bool _prewarm;
    private Func<Task<List<WindowInfo>>> _windowsProvider;
    private bool _windowsLoadStarted;
    private TaskCompletionSource<WF.DialogResult>? _completion;
    // Polls the cursor while a selection is open so the crosshair/loupe follow continuously,
    // independent of whether idle hover WM_MOUSEMOVE reaches the overlay (it doesn't reliably
    // across multiple monitors / when the overlay isn't the foreground window). Runs ONLY while
    // shown — started in ShowAsync, stopped in Complete — so it adds no idle background work.
    private readonly WF.Timer _followTimer;
    private bool _lastCtrlDown;

    public FastRegionSelectorDialog(Task<List<WindowInfo>> windowsTask, SettingsService? settings)
        : this(() => windowsTask, settings)
    {
    }

    public FastRegionSelectorDialog(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
        : this(windowsProvider, settings, prewarm: false)
    {
    }

    private FastRegionSelectorDialog(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings, bool prewarm)
    {
        _settings = settings;
        _prewarm = prewarm;
        _windowsProvider = windowsProvider;
        _monitorBounds = PrimaryBounds();

        ConfigureSurface(this);
        DoubleBuffered = true;
        SetStyle(PaintStyles, true);
        Bounds = _monitorBounds;
        Opacity = prewarm ? 0.01 : 1.0;

        _followTimer = new WF.Timer { Interval = 15 };
        _followTimer.Tick += OnFollowTick;

        Shown += (_, _) => StartWindowLoad();

        ResetForUse(windowsProvider, settings, prewarm);
    }

    public SD.Rectangle? SelectedRegionPx { get; private set; }

    /// <summary>Shared surface setup for the coordinator Form and every pane.</summary>
    private static void ConfigureSurface(WF.Form form)
    {
        form.AutoScaleMode = WF.AutoScaleMode.None;
        form.BackColor = SD.Color.Black;
        form.Cursor = WF.Cursors.Cross;
        form.FormBorderStyle = WF.FormBorderStyle.None;
        form.KeyPreview = true;
        form.ShowInTaskbar = false;
        form.StartPosition = WF.FormStartPosition.Manual;
        form.TopMost = true;
    }

    // DoubleBuffered + SetStyle are protected on Control, so each surface enables its own
    // flicker-free painting from inside its own constructor.
    private const WF.ControlStyles PaintStyles =
        WF.ControlStyles.AllPaintingInWmPaint |
        WF.ControlStyles.OptimizedDoubleBuffer |
        WF.ControlStyles.ResizeRedraw |
        WF.ControlStyles.UserPaint;

    private static SD.Rectangle PrimaryBounds() =>
        (WF.Screen.PrimaryScreen ?? WF.Screen.AllScreens[0]).Bounds;

    public static void Prewarm()
    {
        try
        {
            if (_cached is { IsDisposed: false })
                return;

            var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()),
                settings: null,
                prewarm: true);

            selector.Show();
            WF.Application.DoEvents();
            selector.Hide();
            _cached = selector;
        }
        catch (Exception ex)
        {
            Log.Error("Fast selector prewarm failed", ex);
        }
    }

    public static FastRegionSelectorDialog Rent(Task<List<WindowInfo>> windowsTask, SettingsService? settings) =>
        Rent(() => windowsTask, settings);

    public static FastRegionSelectorDialog Rent(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        var selector = Interlocked.Exchange(ref _cached, null);
        if (selector is { IsDisposed: false })
        {
            selector.ResetForUse(windowsProvider, settings, prewarm: false);
            return selector;
        }

        return new FastRegionSelectorDialog(windowsProvider, settings);
    }

    public static void Return(FastRegionSelectorDialog selector)
    {
        if (selector.IsDisposed)
            return;

        if (ReferenceEquals(_cached, selector))
            _cached = null;
        selector._settings = null;
        selector._windows = new List<WindowInfo>();
        selector._hoverWindow = null;
        selector._pendingScreen = null;
        selector._dragging = false;
        selector._dragMoved = false;
        selector._resizeHandle = -1;
        selector._movingPending = false;
        selector.Capture = false;
        selector.DisposePanes();
        selector.DisposeFrozen();
        selector._capturedRegion?.Dispose();
        selector._capturedRegion = null;
        selector.Hide();
        selector.Dispose();
    }

    public Task<WF.DialogResult> ShowAsync(SelectorMode mode = SelectorMode.Area)
    {
        _mode = mode;
        _completion = new TaskCompletionSource<WF.DialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        Bounds = _monitorBounds;
        CaptureFrozen();
        CreatePanes();

        Show();
        foreach (var pane in _panes)
            pane.Show();

        Activate();
        Focus();
        _lastCtrlDown = false;
        _currentScreen = CursorScreen();
        _lastFollowScreen = _currentScreen;
        if (!_prewarm)
            _followTimer.Start();
        return _completion.Task;
    }

    private void ResetForUse(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings, bool prewarm)
    {
        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        _settings = settings;
        _prewarm = prewarm;
        _mode = SelectorMode.Area;
        _windowsProvider = windowsProvider;
        _windowsLoadStarted = false;
        _windows = new List<WindowInfo>();
        _dragging = false;
        _dragMoved = false;
        _pendingScreen = null;
        _resizeHandle = -1;
        _movingPending = false;
        _hoverWindow = null;
        SelectedRegionPx = null;
        DisposeFrozen();
        _capturedRegion?.Dispose();
        _capturedRegion = null;
        DialogResult = WF.DialogResult.None;
        Bounds = _monitorBounds;
        Capture = false;
        Opacity = prewarm ? 0.01 : 1.0;
        // Seed at the real cursor so the first paint draws the crosshair/loupe at the
        // pointer instead of a corner until the first mouse-move arrives.
        _currentScreen = CursorScreen();
        _completion = null;
    }

    // ----------------------------------------------------------- per-monitor panes

    private void CreatePanes()
    {
        DisposePanes();
        if (_prewarm)
            return;

        foreach (var screen in WF.Screen.AllScreens)
        {
            if (screen.Bounds == _monitorBounds)
                continue; // the coordinator Form already covers the primary monitor

            var pane = new SelectorPane(this, screen.Bounds);
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
        if (_prewarm) return;
        SD.Point p = CursorScreen();
        bool ctrl = (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control;
        if (p == _currentScreen && ctrl == _lastCtrlDown)
            return;
        _lastCtrlDown = ctrl;
        HandleMouseMove();
    }

    /// <summary>Whether the full-bleed crosshair guide lines should be drawn, per the
    /// Screenshots "Crosshair mode" setting (always / only while Ctrl is held / never).</summary>
    private bool CrosshairLinesVisible()
    {
        string mode = _settings?.Current.CrosshairMode ?? "always";
        return mode switch
        {
            "never" => false,
            "command" => (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control,
            _ => true,
        };
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
            var rect = Normalize(_dragStartScreen, _currentScreen);
            rect.Intersect(_vs);
            if (rect.Width > 0 && rect.Height > 0)
                _pendingScreen = rect;
            InvalidateAllSurfaces();
        }
    }

    // ----------------------------------------------------------- painting (per surface)

    internal void PaintSurface(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
        SD.Size clientSize = monitorBounds.Size;
        bool cursorOnThisSurface = monitorBounds.Contains(_currentScreen);

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
        bool showHandles = false;
        if (_dragging && _dragMoved)
        {
            brightScreen = Normalize(_dragStartScreen, _currentScreen);
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
                DrawLabel(g, clientSize, $"{bright.Width} × {bright.Height}", at.X, at.Y);
            }
        }

        // Crosshair + loupe only when drawing/idle in Area mode (not while adjusting a pending
        // rect, and never in Window mode where the window highlight is the affordance).
        bool inCrosshairContext = _mode == SelectorMode.Area && _pendingScreen is null && !_prewarm && cursorOnThisSurface;
        if (inCrosshairContext)
        {
            // Crosshair guide lines honor the "Crosshair mode" setting; the magnifier/color
            // loupe honors "Show magnifier". Both default on when no settings are attached.
            if (CrosshairLinesVisible())
                DrawCrosshair(g, clientSize, ToLocal(_currentScreen, monitorBounds));

            if (_settings?.Current.ShowMagnifier ?? true)
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

    private void CaptureFrozen()
    {
        DisposeFrozen();
        if (_prewarm) return;
        try
        {
            _frozen = CaptureService.CaptureVirtualDesktop();
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

    // ----------------------------------------------------------- drawing helpers

    private static SD.Drawing2D.GraphicsPath RoundedRect(SD.Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new SD.Drawing2D.GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return path;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawCrosshair(SD.Graphics g, SD.Size clientSize, SD.Point cursor)
    {
        var guides = FastSelectorGuideLayout.Calculate(clientSize, cursor, CrosshairGapPx);
        if (!guides.IsVisible)
            return;

        using var shadow = new SD.Pen(SD.Color.FromArgb(120, 0, 0, 0), 3);
        using var pen = new SD.Pen(SD.Color.FromArgb(210, 255, 255, 255), 1);
        DrawGuideLines(g, shadow, guides);
        DrawGuideLines(g, pen, guides);
    }

    private static void DrawGuideLines(SD.Graphics g, SD.Pen pen, FastSelectorGuideLines guides)
    {
        g.DrawLine(pen, guides.LeftStart, guides.LeftEnd);
        g.DrawLine(pen, guides.RightStart, guides.RightEnd);
        g.DrawLine(pen, guides.TopStart, guides.TopEnd);
        g.DrawLine(pen, guides.BottomStart, guides.BottomEnd);
    }

    private void DrawLabel(SD.Graphics g, SD.Size clientSize, string text, int x, int y)
    {
        using var font = ThemePalette.UiFont(9f, SD.FontStyle.Bold);
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        int w = size.Width + 16;
        int h = size.Height + 8;
        int left = Math.Clamp(x, 0, Math.Max(0, clientSize.Width - w));
        int top = Math.Clamp(y, 0, Math.Max(0, clientSize.Height - h));
        var bg = new SD.Rectangle(left, top, w, h);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = RoundedRect(bg, 6))
        using (var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(235, 0x1C, 0x1C, 0x1E)))
            g.FillPath(bgBrush, path);
        g.SmoothingMode = prev;

        WF.TextRenderer.DrawText(g, text, font, bg, ThemePalette.TextPrimary,
            WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.NoPadding);
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

        public SelectorPane(FastRegionSelectorDialog owner, SD.Rectangle monitorBounds)
        {
            _owner = owner;
            _bounds = monitorBounds;
            ConfigureSurface(this);
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
