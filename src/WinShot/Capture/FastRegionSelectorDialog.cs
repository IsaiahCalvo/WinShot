using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Native lightweight region/window selector.
///
/// DPI correctness: instead of one window stretched across the whole virtual desktop
/// (which Windows renders at a single monitor's scale and bitmap-stretches onto the
/// others, drifting the selection on mixed-DPI multi-monitor setups), this puts ONE
/// 1:1 overlay surface on EACH monitor. The primary monitor's surface is this Form
/// (also the coordinator); extra monitors get lightweight <see cref="SelectorPane"/>
/// children. All selection math is anchored to GetCursorPos, which returns true
/// physical pixels regardless of which monitor's scale the cursor is over, so the
/// painted selection always matches the captured pixels. On a single monitor there are
/// no panes and this behaves exactly like the previous single-window selector.
/// </summary>
public sealed class FastRegionSelectorDialog : WF.Form
{
    private const int DragThresholdPx = 4;
    private const int CrosshairGapPx = 10;
    // Single accent identity. The selection rectangle is filled with HoleKey, which each
    // surface's TransparencyKey turns into a full-brightness hole (CleanShot's punch-out).
    private static readonly SD.Color Accent = ThemePalette.Accent;
    private static readonly SD.Color HoleKey = SD.Color.Magenta;
    private static FastRegionSelectorDialog? _cached;

    private SD.Rectangle _vs = CaptureService.VirtualScreen;
    private SD.Rectangle _monitorBounds;   // this surface's monitor (physical px); primary monitor for the coordinator
    private SettingsService? _settings;
    private List<WindowInfo> _windows = new();
    private readonly List<SelectorPane> _panes = new();
    private SD.Point _dragStartScreen;     // physical screen px (GetCursorPos)
    private SD.Point _currentScreen;       // physical screen px (GetCursorPos)
    private bool _dragging;
    private bool _dragMoved;
    private WindowInfo? _hoverWindow;
    private SD.Rectangle? _pendingPx;
    private bool _prewarm;
    private Func<Task<List<WindowInfo>>> _windowsProvider;
    private bool _windowsLoadStarted;
    private TaskCompletionSource<WF.DialogResult>? _completion;

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
        Opacity = prewarm ? 0.01 : 0.45;

        Shown += (_, _) => StartWindowLoad();

        ResetForUse(windowsProvider, settings, prewarm);
    }

    public SD.Rectangle? SelectedRegionPx { get; private set; }

    /// <summary>Shared surface setup for the coordinator Form and every pane.</summary>
    private static void ConfigureSurface(WF.Form form)
    {
        form.AutoScaleMode = WF.AutoScaleMode.None;
        form.BackColor = SD.Color.Black;
        // Pixels painted in HoleKey become a full-brightness hole; everything else is
        // dimmed by Opacity. Each surface is wholly inside one monitor, so it is 1:1.
        form.TransparencyKey = HoleKey;
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
        selector._pendingPx = null;
        selector._dragging = false;
        selector._dragMoved = false;
        selector.Capture = false;
        selector.DisposePanes();
        selector.Hide();
        selector.Dispose();
    }

    public Task<WF.DialogResult> ShowAsync()
    {
        _completion = new TaskCompletionSource<WF.DialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        Bounds = _monitorBounds;
        CreatePanes();

        Show();
        foreach (var pane in _panes)
            pane.Show();

        Activate();
        Focus();
        return _completion.Task;
    }

    private void ResetForUse(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings, bool prewarm)
    {
        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        _settings = settings;
        _prewarm = prewarm;
        _windowsProvider = windowsProvider;
        _windowsLoadStarted = false;
        _windows = new List<WindowInfo>();
        _dragging = false;
        _dragMoved = false;
        _hoverWindow = null;
        _pendingPx = null;
        SelectedRegionPx = null;
        DialogResult = WF.DialogResult.None;
        Bounds = _monitorBounds;
        Capture = false;
        Opacity = prewarm ? 0.01 : 0.45;
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
        Capture = _dragging;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(WF.MouseEventArgs e)
    {
        HandleMouseMove();
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
    }

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

        if (e.Clicks >= 2 && _pendingPx is SD.Rectangle pending)
        {
            Confirm(pending);
            return;
        }

        _dragStartScreen = screen;
        _currentScreen = screen;
        _dragging = true;
        _dragMoved = false;
        _hoverWindow = null;
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

            if (!_dragMoved)
            {
                _dragMoved = true;
                _pendingPx = null;
            }

            InvalidateAllSurfaces();
        }
        else if (_pendingPx is null)
        {
            HitTestWindow(_currentScreen);
        }
        else
        {
            InvalidateAllSurfaces();
        }
    }

    internal void HandleMouseUp(WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left)
            return;

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

        var screenRect = Normalize(_dragStartScreen, _currentScreen);
        var virtualRect = VirtualFromScreen(screenRect);
        if (virtualRect.Width > 0 && virtualRect.Height > 0)
            Confirm(virtualRect);
    }

    internal bool IsDragging => _dragging;

    // ----------------------------------------------------------- painting (per surface)

    /// <summary>
    /// Paints one monitor surface. <paramref name="monitorBounds"/> is that monitor's
    /// physical bounds; the surface is 1:1, so screen->local is a pure subtraction.
    /// </summary>
    internal void PaintSurface(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;

        bool cursorOnThisSurface = monitorBounds.Contains(_currentScreen);
        SD.Size clientSize = monitorBounds.Size;

        if (cursorOnThisSurface)
            DrawCrosshair(g, clientSize, ToLocal(_currentScreen, monitorBounds));

        if (_hoverWindow is not null && !_dragging && _pendingPx is null)
            DrawRect(g, ToLocal(_hoverWindow.Bounds, monitorBounds), Accent);

        if (_dragging && _dragMoved)
        {
            var screenRect = Normalize(_dragStartScreen, _currentScreen);
            DrawSelection(g, ToLocal(screenRect, monitorBounds));
            if (cursorOnThisSurface)
            {
                var local = ToLocal(screenRect, monitorBounds);
                DrawLabel(g, clientSize, $"{screenRect.Width} × {screenRect.Height}", local.Right + 8, local.Bottom + 8);
            }
        }
        else if (_pendingPx is SD.Rectangle pending)
        {
            var screenRect = ScreenFromVirtual(pending);
            DrawSelection(g, ToLocal(screenRect, monitorBounds));
            if (cursorOnThisSurface)
            {
                var local = ToLocal(screenRect, monitorBounds);
                DrawLabel(g, clientSize, $"{pending.Width} × {pending.Height}", local.Right + 8, local.Bottom + 8);
            }
        }
        else if (_hoverWindow is not null && cursorOnThisSurface)
        {
            var px = VirtualFromScreen(_hoverWindow.Bounds);
            SD.Point lc = ToLocal(_currentScreen, monitorBounds);
            DrawLabel(g, clientSize, $"{px.Width} × {px.Height}", lc.X + 14, lc.Y + 18);
        }

        if (!_prewarm && cursorOnThisSurface)
        {
            FastSelectorLoupeRenderer.Draw(
                g,
                clientSize,
                _vs,
                ToLocal(_currentScreen, monitorBounds),
                _currentScreen);
        }
    }

    private void HitTestWindow(SD.Point screenPoint)
    {
        var hover = ResolveWindow(screenPoint);
        if (ReferenceEquals(hover, _hoverWindow)) return;
        _hoverWindow = hover;
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
        if (_settings is not null)
            _settings.Current.LastCaptureRegion = PreviousRegion.Format(
                new SD.Rectangle(virtualRect.X + _vs.X, virtualRect.Y + _vs.Y, virtualRect.Width, virtualRect.Height));

        Complete(WF.DialogResult.OK);
    }

    private void Complete(WF.DialogResult result)
    {
        DialogResult = result;
        Capture = false;
        DisposePanes();
        Hide();
        _completion?.TrySetResult(result);
        _completion = null;
    }

    private void ClearPending()
    {
        _pendingPx = null;
        _hoverWindow = null;
        InvalidateAllSurfaces();
    }

    // ----------------------------------------------------------- drawing helpers

    private static void DrawRect(SD.Graphics g, SD.Rectangle rect, SD.Color stroke)
    {
        using var pen = new SD.Pen(stroke, 2);
        g.DrawRectangle(pen, rect);
    }

    /// <summary>Fills the selection with the transparency-key color (full-brightness hole)
    /// and strokes it with the accent border.</summary>
    private void DrawSelection(SD.Graphics g, SD.Rectangle rect)
    {
        if (rect.Width < 1 || rect.Height < 1)
            return;

        using (var hole = new SD.SolidBrush(HoleKey))
            g.FillRectangle(hole, rect);

        using var pen = new SD.Pen(Accent, 2);
        g.DrawRectangle(pen, rect);
    }

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

    private static SD.Rectangle Normalize(SD.Point a, SD.Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new SD.Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>True physical cursor position — DPI-independent across monitors, the
    /// anchor that keeps selection math correct on mixed-DPI setups.</summary>
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
            Opacity = 0.45;
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnMouseDown(WF.MouseEventArgs e)
        {
            _owner.HandleMouseDown(e);
            Capture = _owner.IsDragging;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(WF.MouseEventArgs e)
        {
            _owner.HandleMouseMove();
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
