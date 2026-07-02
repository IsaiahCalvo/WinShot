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
/// Screen-freeze: at open the selector snapshots the whole desktop, and each surface paints its
/// frozen slice, dims it, and re-brightens the selection — so nothing moves under the cursor
/// while you drag (CleanShot's screen-freeze). The snapshot is disposed on close.
/// </summary>
public sealed class FastAllInOneSelectorDialog : WF.Form
{
    private const int DragThresholdPx = 4;
    private const int CrosshairGapPx = 10;
    private static readonly SD.Color Accent = ThemePalette.Accent;

    private SD.Rectangle _vs = CaptureService.VirtualScreen;
    private SD.Rectangle _monitorBounds;   // this surface's monitor (physical px); primary monitor for the coordinator
    private SettingsService? _settings;
    private readonly ToolbarForm _toolbar;
    private List<WindowInfo> _windows = new();
    private readonly List<SelectorPane> _panes = new();
    private SD.Point _dragStartScreen;     // physical screen px (GetCursorPos)
    private SD.Point _currentScreen;       // physical screen px (GetCursorPos)
    private bool _dragging;
    private bool _dragMoved;
    private double? _dragRatio;
    private WindowInfo? _hoverWindow;
    private SD.Rectangle? _pendingPx;
    private SD.Bitmap? _frozen;          // frozen virtual-desktop snapshot shown under the overlay
    private SD.Bitmap? _capturedRegion;  // region cropped from _frozen at confirm; caller takes ownership
    private Func<Task<List<WindowInfo>>> _windowsProvider;
    private bool _windowsLoadStarted;
    private TaskCompletionSource<WF.DialogResult>? _completion;

    public FastAllInOneSelectorDialog(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        _settings = settings;
        _windowsProvider = windowsProvider;
        _monitorBounds = PrimaryBounds();
        _toolbar = new ToolbarForm(this);

        ConfigureSurface(this);
        DoubleBuffered = true;
        SetStyle(PaintStyles, true);
        Bounds = _monitorBounds;
        Opacity = 1.0;

        Shown += (_, _) =>
        {
            StartWindowLoad();
            BeginInvoke(new Action(() =>
            {
                ShowToolbar();
                BeginInvoke(new Action(TryRestoreLastRegion));
            }));
        };

        ResetForUse(windowsProvider, settings);
    }

    public SD.Rectangle? SelectedRegionPx { get; private set; }

    public AllInOneAction SelectedAction { get; private set; } = AllInOneAction.Capture;

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

    public static FastAllInOneSelectorDialog Rent(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        return new FastAllInOneSelectorDialog(windowsProvider, settings);
    }

    public static void Return(FastAllInOneSelectorDialog selector)
    {
        if (selector.IsDisposed)
            return;

        selector._settings = null;
        selector._windows = new List<WindowInfo>();
        selector._hoverWindow = null;
        selector._pendingPx = null;
        selector._dragging = false;
        selector._dragMoved = false;
        selector._dragRatio = null;
        selector.Capture = false;
        selector.DisposePanes();
        selector.DisposeFrozen();
        selector._capturedRegion?.Dispose();
        selector._capturedRegion = null;
        selector.Opacity = 0.01;
        selector.Hide();
        if (!selector._toolbar.IsDisposed)
            selector._toolbar.Hide();

        selector.Dispose();
    }

    public async Task<WF.DialogResult> ShowAsync()
    {
        _completion = new TaskCompletionSource<WF.DialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        Bounds = _monitorBounds;
        // Snapshot off the UI thread — see FastRegionSelectorDialog.ShowAsync.
        await CaptureFrozenAsync();
        CreatePanes();

        Show();
        foreach (var pane in _panes)
            pane.Show();

        Activate();
        Focus();
        return await _completion.Task;
    }

    private void ResetForUse(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        _settings = settings;
        _windowsProvider = windowsProvider;
        _windowsLoadStarted = false;
        _windows = new List<WindowInfo>();
        _dragging = false;
        _dragMoved = false;
        _dragRatio = null;
        _hoverWindow = null;
        _pendingPx = null;
        SelectedRegionPx = null;
        SelectedAction = AllInOneAction.Capture;
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
        if (!_toolbar.IsDisposed)
        {
            _toolbar.Opacity = 1;
            _toolbar.UpdateMode(0);
            _toolbar.SetSize(1, 1);
        }
    }

    // ----------------------------------------------------------- per-monitor panes

    private void CreatePanes()
    {
        DisposePanes();

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
            Log.Error("Failed to load all-in-one selector window list", ex);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposePanes();
            DisposeFrozen();
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
        _dragRatio = _toolbar.AspectRatio;
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

            var virtualRect = VirtualFromScreen(MakeDragRect(_currentScreen));
            _toolbar.SetSize(virtualRect.Width, virtualRect.Height);
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

        var virtualRect = VirtualFromScreen(MakeDragRect(_currentScreen));
        if (virtualRect.Width > 0 && virtualRect.Height > 0)
            Confirm(virtualRect);
    }

    internal bool IsDragging => _dragging;

    internal void SetMode(int mode, AllInOneAction action)
    {
        SelectedAction = action;
        _toolbar.UpdateMode(mode);
    }

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

    internal void ApplyExactSize(int width, int height)
    {
        if (width < 1 || height < 1)
            return;

        SD.Point anchor = _pendingPx?.Location ?? new SD.Point(
            Math.Max(0, (_vs.Width - width) / 2),
            Math.Max(0, (_vs.Height - height) / 2));
        var rect = new SD.Rectangle(anchor.X, anchor.Y, width, height);
        rect.Intersect(new SD.Rectangle(0, 0, _vs.Width, _vs.Height));
        if (rect.Width < 1 || rect.Height < 1)
            return;

        _pendingPx = rect;
        _hoverWindow = null;
        _toolbar.SetSize(rect.Width, rect.Height);
        InvalidateAllSurfaces();
        Focus();
    }

    internal void Cancel()
    {
        Complete(WF.DialogResult.Cancel);
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

        // Frozen desktop slice for this monitor, then a uniform dim over it.
        DrawFrozenSlice(g, monitorBounds);
        using (var dim = new SD.SolidBrush(SD.Color.FromArgb(115, 0, 0, 0)))
            g.FillRectangle(dim, 0, 0, clientSize.Width, clientSize.Height);

        // The active selection (or hovered window) shows the frozen pixels at full brightness.
        SD.Rectangle? brightScreen = null;
        if (_dragging && _dragMoved)
            brightScreen = MakeDragRect(_currentScreen);
        else if (_pendingPx is SD.Rectangle pending)
            brightScreen = ScreenFromVirtual(pending);
        else if (_hoverWindow is not null)
            brightScreen = _hoverWindow.Bounds;

        if (brightScreen is SD.Rectangle bright)
        {
            var local = ToLocal(bright, monitorBounds);
            BrightenRegion(g, monitorBounds, local);
            using (var pen = new SD.Pen(Accent, 2))
                g.DrawRectangle(pen, local);
            if (cursorOnThisSurface)
            {
                var px = _hoverWindow is not null && !_dragging && _pendingPx is null
                    ? VirtualFromScreen(_hoverWindow.Bounds)
                    : VirtualFromScreen(bright);
                SD.Point at = _dragging || _pendingPx is not null
                    ? new SD.Point(local.Right + 8, local.Bottom + 8)
                    : new SD.Point(ToLocal(_currentScreen, monitorBounds).X + 14, ToLocal(_currentScreen, monitorBounds).Y + 18);
                DrawLabel(g, clientSize, $"{px.Width} × {px.Height}", at.X, at.Y);
            }
        }

        if (cursorOnThisSurface)
            DrawCrosshair(g, clientSize, ToLocal(_currentScreen, monitorBounds));

        if (cursorOnThisSurface)
        {
            FastSelectorLoupeRenderer.Draw(
                g, clientSize, _vs, ToLocal(_currentScreen, monitorBounds), _currentScreen, _frozen);
        }
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
        if (!PreviousRegion.TryParse(_settings.Current.LastCaptureRegion, out SD.Rectangle screenRect)) return;
        var px = VirtualFromScreen(screenRect);
        if (px.Width < 1 || px.Height < 1) return;
        _pendingPx = px;
        _toolbar.SetSize(px.Width, px.Height);
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
        Capture = false;
        DisposePanes();
        DisposeFrozen(); // free the full snapshot now; _capturedRegion stays for the caller
        Hide();
        if (!_toolbar.IsDisposed)
            _toolbar.Hide();
        _completion?.TrySetResult(result);
        _completion = null;
    }

    private void ClearPending()
    {
        _pendingPx = null;
        _hoverWindow = null;
        InvalidateAllSurfaces();
    }

    private SD.Rectangle MakeDragRect(SD.Point screenPoint) =>
        AllInOneDragLayout.CreatePixelRectangle(_dragStartScreen, screenPoint, _dragRatio);

    // ----------------------------------------------------------- drawing helpers

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
        using (var path = GdiPaths.RoundedRect(bg, 6))
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
        private readonly FastAllInOneSelectorDialog _owner;
        private readonly SD.Rectangle _bounds;

        public SelectorPane(FastAllInOneSelectorDialog owner, SD.Rectangle monitorBounds)
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
        // CleanShot palette: a near-black bar (form-level Opacity gives the soft translucency),
        // white line-art glyphs over dim captions, the single app accent for the active mode.
        private static readonly SD.Color BarFill = SD.Color.FromArgb(0x1C, 0x1C, 0x1E);
        private static readonly SD.Color BarBorder = SD.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        private static readonly SD.Color HoverFill = SD.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
        private static readonly SD.Color SelectedFill = ThemePalette.Accent;
        private static readonly SD.Color GlyphColor = SD.Color.FromArgb(0xF2, 0xF2, 0xF4);
        private static readonly SD.Color LabelColor = SD.Color.FromArgb(0xC8, 0xC8, 0xCC);
        private static readonly SD.Color FieldFill = SD.Color.FromArgb(0x33, 0x33, 0x36);

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

        private readonly FastAllInOneSelectorDialog _owner;
        private readonly SD.Font _glyphFont = ThemePalette.IconFont(15f);
        private readonly SD.Font _caretFont = ThemePalette.IconFont(7f);
        private readonly SD.Font _ocrFont = ThemePalette.UiFont(13f, SD.FontStyle.Bold);
        private readonly SD.Font _labelFont = ThemePalette.UiFont(7.5f);
        private readonly SD.Font _fieldFont = ThemePalette.UiFont(9.5f);
        private readonly List<BarButton> _buttons = new();
        private readonly WF.TextBox _widthBox;
        private readonly WF.TextBox _heightBox;
        private readonly WF.ToolTip _toolTip = new() { InitialDelay = 350, ReshowDelay = 120 };

        private SD.Rectangle _mainBar;       // client-space bounds of the mode bar
        private SD.Rectangle _sizeBar;       // client-space bounds of the size bar
        private SD.Rectangle _expandRect;    // aspect-lock toggle inside the size bar
        private SD.Rectangle _cropRect;      // confirm/crop button inside the size bar
        private SD.Rectangle _xLabelRect;    // the "×" between the size fields
        private int _selectedMode;
        private int _hoverIndex = -1;
        private bool _aspectLocked;

        public ToolbarForm(FastAllInOneSelectorDialog owner)
        {
            _owner = owner;

            AutoScaleMode = WF.AutoScaleMode.None;
            BackColor = SD.Color.FromArgb(0x1C, 0x1C, 0x1E);
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
            _buttons.Add(new BarButton("Timer", "", 3, AllInOneAction.Capture, isMode: true));
            _buttons.Add(new BarButton("OCR", "Aa", 4, AllInOneAction.Ocr, isMode: true) { IsTextGlyph = true });
            _buttons.Add(new BarButton("Recording", "", 5, AllInOneAction.Record, isMode: true));

            _widthBox = SizeBox();
            _heightBox = SizeBox();
            Controls.Add(_widthBox);
            Controls.Add(_heightBox);

            BuildLayout();

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

        /// <summary>Locked aspect ratio when the expand toggle is on (drives drag/exact-size).</summary>
        public double? AspectRatio
        {
            get
            {
                if (!_aspectLocked)
                    return null;
                if (!int.TryParse(_widthBox.Text, out int w) || !int.TryParse(_heightBox.Text, out int h))
                    return null;
                if (w < 1 || h < 1)
                    return null;
                return (double)w / h;
            }
        }

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
            _selectedMode = selectedMode;
            Invalidate(_mainBar);
        }

        // ---------------------------------------------------------------- layout

        private void BuildLayout()
        {
            int x = BarPadX;
            foreach (var b in _buttons)
            {
                b.Bounds = new SD.Rectangle(x, 0, ButtonWidth, BarHeight);
                x += ButtonWidth + ButtonGap;
            }

            int mainWidth = x - ButtonGap + BarPadX;
            _mainBar = new SD.Rectangle(0, 0, mainWidth, BarHeight);

            // Size bar: [ width ] × [ height ]  [expand]  [crop ▾]
            const int fieldW = 48;
            const int iconW = 34;
            int innerPad = 12;
            // A single-line TextBox sizes its own height to the font; center on that real height.
            int fieldH = _widthBox.PreferredHeight;
            int sizeBarLeft = _mainBar.Right + BarGap;
            int sx = sizeBarLeft + innerPad;
            int fieldY = (BarHeight - fieldH) / 2;

            _widthBox.SetBounds(sx, fieldY, fieldW, fieldH);
            sx += fieldW;
            _xLabelRect = new SD.Rectangle(sx, 0, 16, BarHeight);
            sx += 16;
            _heightBox.SetBounds(sx, fieldY, fieldW, fieldH);
            sx += fieldW + 8;

            _expandRect = new SD.Rectangle(sx, (BarHeight - 40) / 2, iconW, 40);
            sx += iconW + 4;
            _cropRect = new SD.Rectangle(sx, (BarHeight - 40) / 2, iconW + 12, 40);
            sx += iconW + 12 + innerPad;

            _sizeBar = new SD.Rectangle(sizeBarLeft, 0, sx - sizeBarLeft, BarHeight);

            ClientSize = new SD.Size(_sizeBar.Right, BarHeight);
            ApplyRegion();
        }

        private void ApplyRegion()
        {
            // The host window is shaped to the two bars only, so the desktop shows through the gap.
            IntPtr main = CreateRoundRectRgn(
                _mainBar.Left, _mainBar.Top, _mainBar.Right + 1, _mainBar.Bottom + 1,
                CornerRadius * 2, CornerRadius * 2);
            IntPtr size = CreateRoundRectRgn(
                _sizeBar.Left, _sizeBar.Top, _sizeBar.Right + 1, _sizeBar.Bottom + 1,
                CornerRadius * 2, CornerRadius * 2);
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
            g.Clear(SD.Color.FromArgb(0x1C, 0x1C, 0x1E));

            PaintBar(g, _mainBar);
            PaintBar(g, _sizeBar);

            for (int i = 0; i < _buttons.Count; i++)
                PaintButton(g, _buttons[i], i == _hoverIndex);

            PaintSizeBar(g);
            base.OnPaint(e);
        }

        private static void PaintBar(SD.Graphics g, SD.Rectangle bar)
        {
            using var path = GdiPaths.RoundedRect(new SD.Rectangle(bar.X, bar.Y, bar.Width - 1, bar.Height - 1), CornerRadius);
            using var fill = new SD.SolidBrush(BarFill);
            using var pen = new SD.Pen(BarBorder, 1);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        private void PaintButton(SD.Graphics g, BarButton b, bool hot)
        {
            bool selected = b.IsMode && b.Mode == _selectedMode;
            var inner = SD.Rectangle.Inflate(b.Bounds, -4, -8);
            if (selected || hot)
            {
                using var path = GdiPaths.RoundedRect(inner, 9);
                using var brush = new SD.SolidBrush(selected ? SelectedFill : HoverFill);
                g.FillPath(brush, path);
            }

            SD.Color glyphColor = selected ? SD.Color.White : GlyphColor;
            var glyphRect = new SD.Rectangle(b.Bounds.X, GlyphTop, b.Bounds.Width, GlyphHeight);
            WF.TextRenderer.DrawText(g, b.Glyph, b.IsTextGlyph ? _ocrFont : _glyphFont, glyphRect, glyphColor,
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);

            var labelRect = new SD.Rectangle(b.Bounds.X, LabelTop, b.Bounds.Width, LabelHeight);
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

            // Expand / aspect-lock toggle (diagonal arrows). Accent-filled while locked.
            bool expandHot = _hoverIndex == HoverExpand;
            if (_aspectLocked || expandHot)
            {
                using var path = GdiPaths.RoundedRect(_expandRect, 8);
                using var brush = new SD.SolidBrush(_aspectLocked ? SelectedFill : HoverFill);
                g.FillPath(brush, path);
            }
            WF.TextRenderer.DrawText(g, "", _glyphFont, _expandRect,
                _aspectLocked ? SD.Color.White : GlyphColor,
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);

            // Crop / confirm with dropdown caret.
            bool cropHot = _hoverIndex == HoverCrop;
            if (cropHot)
            {
                using var path = GdiPaths.RoundedRect(_cropRect, 8);
                using var brush = new SD.SolidBrush(HoverFill);
                g.FillPath(brush, path);
            }
            var cropGlyphRect = new SD.Rectangle(_cropRect.X, _cropRect.Y, _cropRect.Width - 12, _cropRect.Height);
            WF.TextRenderer.DrawText(g, "", _glyphFont, cropGlyphRect, GlyphColor,
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
            var caretRect = new SD.Rectangle(_cropRect.Right - 14, _cropRect.Y, 12, _cropRect.Height);
            WF.TextRenderer.DrawText(g, "", _caretFont, caretRect, LabelColor,
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
            Cursor = index == -1 ? WF.Cursors.Default : WF.Cursors.Hand;
            _toolTip.SetToolTip(this,
                index is >= 0 and < HoverExpand ? _buttons[index].Label
                : index == HoverExpand ? "Lock aspect ratio"
                : index == HoverCrop ? "Capture region"
                : null);
            Invalidate();
        }

        private void OnBarMouseClick(object? sender, WF.MouseEventArgs e)
        {
            if (e.Button != WF.MouseButtons.Left)
                return;

            int hit = HitTest(e.Location);
            if (hit == HoverExpand)
            {
                _aspectLocked = !_aspectLocked;
                Invalidate();
                return;
            }
            if (hit == HoverCrop)
            {
                _owner.Cancel();   // dismiss the bar; the live region/drag drives the actual capture
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
                Font = ThemePalette.UiFont(9.5f, SD.FontStyle.Bold),
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
                _glyphFont.Dispose();
                _caretFont.Dispose();
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
