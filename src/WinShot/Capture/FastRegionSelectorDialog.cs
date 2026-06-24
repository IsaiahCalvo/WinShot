using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Native lightweight region/window selector for simple capture flows. It keeps
/// WPF out of the hot path; all-in-one still uses RegionSelectorWindow.
/// </summary>
public sealed class FastRegionSelectorDialog : WF.Form
{
    private const int DragThresholdPx = 4;
    private const int CrosshairGapPx = 10;
    // Single accent identity (was #4DA3FF). The selection rectangle is filled with
    // HoleKey, which the form's TransparencyKey turns into a full-brightness hole so the
    // chosen region is NOT dimmed like the rest of the screen (CleanShot's punch-out).
    private static readonly SD.Color Accent = ThemePalette.Accent;
    private static readonly SD.Color HoleKey = SD.Color.Magenta;
    private static FastRegionSelectorDialog? _cached;

    private SD.Rectangle _vs = CaptureService.VirtualScreen;
    private SettingsService? _settings;
    private List<WindowInfo> _windows = new();
    private SD.Point _dragStartScreen;
    private SD.Point _currentScreen;
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

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = SD.Color.Black;
        // Pixels painted in HoleKey become fully transparent (full-brightness hole),
        // while everything else stays dimmed by Opacity below.
        TransparencyKey = HoleKey;
        Bounds = _vs;
        Cursor = WF.Cursors.Cross;
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        Opacity = prewarm ? 0.01 : 0.45;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        Shown += (_, _) => StartWindowLoad();

        ResetForUse(windowsProvider, settings, prewarm);
    }

    public SD.Rectangle? SelectedRegionPx { get; private set; }

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
        selector.Hide();
        selector.Dispose();
    }

    public Task<WF.DialogResult> ShowAsync()
    {
        _completion = new TaskCompletionSource<WF.DialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Show();
        Activate();
        Focus();
        return _completion.Task;
    }

    private void ResetForUse(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings, bool prewarm)
    {
        _vs = CaptureService.VirtualScreen;
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
        Bounds = _vs;
        Capture = false;
        Opacity = prewarm ? 0.01 : 0.45;
        // Seed at the real cursor so the first paint draws the crosshair/loupe at the
        // pointer instead of the top-left corner until the first mouse-move arrives.
        _currentScreen = WF.Cursor.Position;
        _completion = null;
    }

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

    protected override void OnKeyDown(WF.KeyEventArgs e)
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

        _dragStartScreen = PointToScreen(e.Location);
        _currentScreen = _dragStartScreen;
        _dragging = true;
        _dragMoved = false;
        _hoverWindow = null;
        Capture = true;
        RefreshOverlay();
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(WF.MouseEventArgs e)
    {
        _currentScreen = PointToScreen(e.Location);

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

            RefreshOverlay();
        }
        else if (_pendingPx is null)
        {
            HitTestWindow(_currentScreen);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left)
            return;

        Capture = false;
        if (!_dragging) return;
        _dragging = false;
        _currentScreen = PointToScreen(e.Location);

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

        base.OnMouseUp(e);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        e.Graphics.Clear(SD.Color.Black);
        DrawOverlay(e.Graphics);
        base.OnPaint(e);
    }

    private void DrawOverlay(SD.Graphics g)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
        DrawCrosshair(g, PointToClient(_currentScreen));

        if (_hoverWindow is not null && !_dragging && _pendingPx is null)
            DrawRect(g, ClientFromScreen(_hoverWindow.Bounds), Accent);

        if (_dragging && _dragMoved)
        {
            var screenRect = Normalize(_dragStartScreen, _currentScreen);
            var clientRect = ClientFromScreen(screenRect);
            DrawSelection(g, clientRect);
            DrawLabel(g, $"{screenRect.Width} × {screenRect.Height}", clientRect.Right + 8, clientRect.Bottom + 8);
        }
        else if (_pendingPx is SD.Rectangle pending)
        {
            var clientRect = ClientFromScreen(ScreenFromVirtual(pending));
            DrawSelection(g, clientRect);
            DrawLabel(g, $"{pending.Width} × {pending.Height}", clientRect.Right + 8, clientRect.Bottom + 8);
        }
        else if (_hoverWindow is not null)
        {
            var px = VirtualFromScreen(_hoverWindow.Bounds);
            DrawLabel(g, $"{px.Width} x {px.Height}", PointToClient(_currentScreen).X + 14, PointToClient(_currentScreen).Y + 18);
        }

        if (!_prewarm)
        {
            FastSelectorLoupeRenderer.Draw(
                g,
                ClientSize,
                _vs,
                PointToClient(_currentScreen),
                _currentScreen);
        }
    }

    private void HitTestWindow(SD.Point screenPoint)
    {
        var hover = ResolveWindow(screenPoint);
        if (ReferenceEquals(hover, _hoverWindow)) return;
        _hoverWindow = hover;
        RefreshOverlay();
    }

    /// <summary>
    /// Resolves the window under the cursor by real z-order (WindowFromPoint) so a
    /// small foreground window wins over a larger background one. Falls back to the
    /// first bounds-containing window when the topmost hwnd isn't in the cached list
    /// (e.g. it's the selector overlay itself, or an excluded/untitled window).
    /// </summary>
    private WindowInfo? ResolveWindow(SD.Point screenPoint)
    {
        IntPtr top = WindowEnumerator.TopLevelWindowFromPoint(screenPoint);
        if (top != IntPtr.Zero && top != Handle)
        {
            var match = _windows.FirstOrDefault(w => w.Handle == top);
            if (match is not null)
                return match;
        }

        return _windows.FirstOrDefault(w => w.Bounds.Contains(screenPoint));
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
        Hide();
        _completion?.TrySetResult(result);
        _completion = null;
    }

    private void ClearPending()
    {
        _pendingPx = null;
        _hoverWindow = null;
        RefreshOverlay();
    }

    private void RefreshOverlay()
    {
        Invalidate();
    }

    private static void DrawRect(SD.Graphics g, SD.Rectangle rect, SD.Color stroke)
    {
        using var pen = new SD.Pen(stroke, 2);
        g.DrawRectangle(pen, rect);
    }

    /// <summary>Fills the selection with the transparency-key color (so it shows the live
    /// screen at full brightness, undimmed) and strokes it with the accent border.</summary>
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

    private void DrawCrosshair(SD.Graphics g, SD.Point cursor)
    {
        var guides = FastSelectorGuideLayout.Calculate(ClientSize, cursor, CrosshairGapPx);
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

    private void DrawLabel(SD.Graphics g, string text, int x, int y)
    {
        using var font = ThemePalette.UiFont(9f, SD.FontStyle.Bold);
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        int w = size.Width + 16;
        int h = size.Height + 8;
        int left = Math.Clamp(x, 0, Math.Max(0, ClientRectangle.Width - w));
        int top = Math.Clamp(y, 0, Math.Max(0, ClientRectangle.Height - h));
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

    private SD.Rectangle VirtualFromScreen(SD.Rectangle screenRect)
    {
        var rect = new SD.Rectangle(screenRect.X - _vs.X, screenRect.Y - _vs.Y, screenRect.Width, screenRect.Height);
        rect.Intersect(new SD.Rectangle(0, 0, _vs.Width, _vs.Height));
        return rect;
    }

    private SD.Rectangle ScreenFromVirtual(SD.Rectangle virtualRect) =>
        new(virtualRect.X + _vs.X, virtualRect.Y + _vs.Y, virtualRect.Width, virtualRect.Height);

    private SD.Rectangle ClientFromScreen(SD.Rectangle screenRect)
    {
        SD.Point topLeft = PointToClient(screenRect.Location);
        return new SD.Rectangle(topLeft.X, topLeft.Y, screenRect.Width, screenRect.Height);
    }

    private static SD.Rectangle Normalize(SD.Point a, SD.Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new SD.Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

}
