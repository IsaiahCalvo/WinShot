using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Lightweight all-in-one selector that keeps the capture toolbar out of WPF.
/// </summary>
public sealed class FastAllInOneSelectorDialog : WF.Form
{
    private const int DragThresholdPx = 4;
    private const int CrosshairGapPx = 10;
    private static readonly SD.Color Blue = SD.Color.FromArgb(255, 0x4D, 0xA3, 0xFF);
    private static readonly SD.Color ToolbarBack = SD.Color.FromArgb(43, 43, 43);
    private static readonly SD.Color ButtonHot = SD.Color.FromArgb(79, 79, 79);
    private static readonly SD.Color ButtonSelected = SD.Color.FromArgb(45, 125, 255);
    private static FastAllInOneSelectorDialog? _cached;

    private SD.Rectangle _vs = CaptureService.VirtualScreen;
    private SettingsService? _settings;
    private readonly ToolbarForm _toolbar;
    private List<WindowInfo> _windows = new();
    private SD.Point _dragStartScreen;
    private SD.Point _currentScreen;
    private bool _dragging;
    private bool _dragMoved;
    private double? _dragRatio;
    private WindowInfo? _hoverWindow;
    private SD.Rectangle? _pendingPx;
    private bool _prewarm;
    private Func<Task<List<WindowInfo>>> _windowsProvider;
    private bool _windowsLoadStarted;
    private TaskCompletionSource<WF.DialogResult>? _completion;

    public FastAllInOneSelectorDialog(Task<List<WindowInfo>> windowsTask, SettingsService? settings)
        : this(() => windowsTask, settings)
    {
    }

    public FastAllInOneSelectorDialog(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
        : this(windowsProvider, settings, prewarm: false)
    {
    }

    private FastAllInOneSelectorDialog(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings, bool prewarm)
    {
        _settings = settings;
        _prewarm = prewarm;
        _windowsProvider = windowsProvider;
        _toolbar = new ToolbarForm(this);

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = SD.Color.Black;
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

        Shown += (_, _) =>
        {
            StartWindowLoad();
            if (_prewarm)
            {
                _toolbar.Opacity = 0;
                ShowToolbar();
            }
            else
            {
                BeginInvoke(new Action(() =>
                {
                    ShowToolbar();
                    BeginInvoke(new Action(TryRestoreLastRegion));
                }));
            }
        };

        ResetForUse(windowsProvider, settings, prewarm);
    }

    public SD.Rectangle? SelectedRegionPx { get; private set; }

    public AllInOneAction SelectedAction { get; private set; } = AllInOneAction.Capture;

    public static void Prewarm()
    {
        try
        {
            if (_cached is { IsDisposed: false })
                return;

            var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()),
                settings: null,
                prewarm: true);

            selector.Show();
            WF.Application.DoEvents();
            selector.Hide();
            selector._toolbar.Hide();
            _cached = selector;
        }
        catch (Exception ex)
        {
            Log.Error("Fast all-in-one selector prewarm failed", ex);
        }
    }

    public static FastAllInOneSelectorDialog Rent(Task<List<WindowInfo>> windowsTask, SettingsService? settings) =>
        Rent(() => windowsTask, settings);

    public static FastAllInOneSelectorDialog Rent(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        var selector = Interlocked.Exchange(ref _cached, null);
        if (selector is { IsDisposed: false })
        {
            selector.ResetForUse(windowsProvider, settings, prewarm: false);
            return selector;
        }

        return new FastAllInOneSelectorDialog(windowsProvider, settings);
    }

    public static void Return(FastAllInOneSelectorDialog selector)
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
        selector._dragRatio = null;
        selector.Capture = false;
        selector.Opacity = 0.01;
        selector.Hide();
        if (!selector._toolbar.IsDisposed)
            selector._toolbar.Hide();

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
        _dragRatio = null;
        _hoverWindow = null;
        _pendingPx = null;
        SelectedRegionPx = null;
        SelectedAction = AllInOneAction.Capture;
        DialogResult = WF.DialogResult.None;
        Bounds = _vs;
        Capture = false;
        Opacity = prewarm ? 0.01 : 0.45;
        _completion = null;
        if (!_toolbar.IsDisposed)
        {
            _toolbar.Opacity = prewarm ? 0 : 1;
            _toolbar.UpdateMode(0);
            _toolbar.SetSize(1, 1);
        }
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
            Log.Error("Failed to load all-in-one selector window list", ex);
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
        _dragRatio = _toolbar.AspectRatio;
        _hoverWindow = null;
        Capture = true;
        Invalidate();
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

            var virtualRect = VirtualFromScreen(MakeDragRect(_currentScreen));
            _toolbar.SetSize(virtualRect.Width, virtualRect.Height);
            Invalidate();
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

        var virtualRect = VirtualFromScreen(MakeDragRect(_currentScreen));
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_toolbar.IsDisposed)
            _toolbar.Dispose();
        base.Dispose(disposing);
    }

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
        _toolbar.PositionWithin(_vs);
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
        Invalidate();
        Focus();
    }

    internal void Cancel()
    {
        Complete(WF.DialogResult.Cancel);
    }

    private void DrawOverlay(SD.Graphics g)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
        DrawCrosshair(g, PointToClient(_currentScreen));

        if (_hoverWindow is not null && !_dragging && _pendingPx is null)
            DrawRect(g, ClientFromScreen(_hoverWindow.Bounds), Blue);

        if (_dragging && _dragMoved)
        {
            var screenRect = MakeDragRect(_currentScreen);
            var clientRect = ClientFromScreen(screenRect);
            DrawRect(g, clientRect, SD.Color.White);
            DrawLabel(g, $"{screenRect.Width} x {screenRect.Height}", clientRect.Right + 8, clientRect.Bottom + 8);
        }
        else if (_pendingPx is SD.Rectangle pending)
        {
            var clientRect = ClientFromScreen(ScreenFromVirtual(pending));
            DrawRect(g, clientRect, SD.Color.White);
            DrawLabel(g, $"{pending.Width} x {pending.Height}", clientRect.Right + 8, clientRect.Bottom + 8);
        }
        else if (_hoverWindow is not null)
        {
            var px = VirtualFromScreen(_hoverWindow.Bounds);
            var cursor = PointToClient(_currentScreen);
            DrawLabel(g, $"{px.Width} x {px.Height}", cursor.X + 14, cursor.Y + 18);
            _toolbar.SetSize(px.Width, px.Height);
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
        var hover = _windows.FirstOrDefault(w => w.Bounds.Contains(screenPoint));
        if (ReferenceEquals(hover, _hoverWindow)) return;
        _hoverWindow = hover;
        Invalidate();
    }

    private void TryRestoreLastRegion()
    {
        if (_settings is null) return;
        if (!PreviousRegion.TryParse(_settings.Current.LastCaptureRegion, out SD.Rectangle screenRect)) return;
        var px = VirtualFromScreen(screenRect);
        if (px.Width < 1 || px.Height < 1) return;
        _pendingPx = px;
        _toolbar.SetSize(px.Width, px.Height);
        Invalidate();
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
        if (!_toolbar.IsDisposed)
            _toolbar.Hide();
        _completion?.TrySetResult(result);
        _completion = null;
    }

    private void ClearPending()
    {
        _pendingPx = null;
        _hoverWindow = null;
        Invalidate();
    }

    private SD.Rectangle MakeDragRect(SD.Point screenPoint) =>
        AllInOneDragLayout.CreatePixelRectangle(_dragStartScreen, screenPoint, _dragRatio);

    private static void DrawRect(SD.Graphics g, SD.Rectangle rect, SD.Color stroke)
    {
        using var pen = new SD.Pen(stroke, 2);
        g.DrawRectangle(pen, rect);
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
        using var font = new SD.Font("Consolas", 9f);
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        int left = Math.Clamp(x, 0, Math.Max(0, ClientRectangle.Width - size.Width - 12));
        int top = Math.Clamp(y, 0, Math.Max(0, ClientRectangle.Height - size.Height - 8));
        var bg = new SD.Rectangle(left, top, size.Width + 12, size.Height + 6);
        using var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(30, 30, 30));
        g.FillRectangle(bgBrush, bg);
        WF.TextRenderer.DrawText(g, text, font, new SD.Point(left + 6, top + 3), SD.Color.White);
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

    private sealed class ToolbarForm : WF.Form
    {
        private readonly FastAllInOneSelectorDialog _owner;
        private readonly WF.Button[] _modeButtons;
        private readonly WF.TextBox _widthBox;
        private readonly WF.TextBox _heightBox;
        private readonly WF.CheckBox _lockBox;

        public ToolbarForm(FastAllInOneSelectorDialog owner)
        {
            _owner = owner;

            AutoScaleMode = WF.AutoScaleMode.None;
            BackColor = ToolbarBack;
            FormBorderStyle = WF.FormBorderStyle.None;
            KeyPreview = true;
            ShowInTaskbar = false;
            StartPosition = WF.FormStartPosition.Manual;
            TopMost = true;

            var panel = new WF.FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
                BackColor = ToolbarBack,
                FlowDirection = WF.FlowDirection.LeftToRight,
                Padding = new WF.Padding(12, 6, 12, 6),
                WrapContents = false,
            };

            _modeButtons =
            [
                ModeButton("Area", 0, AllInOneAction.Capture),
                ModeButton("Window", 1, AllInOneAction.Capture),
                ModeButton("Record", 2, AllInOneAction.Record),
                ModeButton("OCR", 3, AllInOneAction.Ocr),
                ModeButton("Scroll", 4, AllInOneAction.Scroll),
            ];

            foreach (var button in _modeButtons)
                panel.Controls.Add(button);

            var fullscreen = ToolButton("Fullscreen");
            fullscreen.Click += (_, _) => _owner.ConfirmFullscreen();
            panel.Controls.Add(fullscreen);
            panel.Controls.Add(Separator());

            _widthBox = SizeBox();
            _heightBox = SizeBox();
            panel.Controls.Add(_widthBox);
            panel.Controls.Add(Label("x"));
            panel.Controls.Add(_heightBox);

            _lockBox = new WF.CheckBox
            {
                Appearance = WF.Appearance.Button,
                AutoSize = true,
                BackColor = ToolbarBack,
                FlatStyle = WF.FlatStyle.Flat,
                ForeColor = SD.Color.White,
                Margin = new WF.Padding(6, 0, 0, 0),
                Padding = new WF.Padding(8, 5, 8, 5),
                Text = "Lock",
                TextAlign = SD.ContentAlignment.MiddleCenter,
            };
            _lockBox.FlatAppearance.BorderSize = 0;
            _lockBox.CheckedChanged += (_, _) =>
            {
                _lockBox.BackColor = _lockBox.Checked ? ButtonSelected : ToolbarBack;
            };
            panel.Controls.Add(_lockBox);

            Controls.Add(panel);
            ClientSize = panel.PreferredSize;
            IntPtr regionHandle = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 20, 20);
            Region = SD.Region.FromHrgn(regionHandle);
            DeleteObject(regionHandle);
            UpdateMode(0);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == WF.Keys.Escape)
                    _owner.Cancel();
            };
        }

        public double? AspectRatio
        {
            get
            {
                if (!_lockBox.Checked)
                    return null;
                if (!int.TryParse(_widthBox.Text, out int w) || !int.TryParse(_heightBox.Text, out int h))
                    return null;
                if (w < 1 || h < 1)
                    return null;
                return (double)w / h;
            }
        }

        public void PositionWithin(SD.Rectangle virtualScreen)
        {
            Left = virtualScreen.X + Math.Max(0, (virtualScreen.Width - Width) / 2);
            Top = virtualScreen.Y + 18;
        }

        public void SetSize(int width, int height)
        {
            _widthBox.Text = Math.Max(1, width).ToString();
            _heightBox.Text = Math.Max(1, height).ToString();
        }

        public void UpdateMode(int selectedMode)
        {
            for (int i = 0; i < _modeButtons.Length; i++)
                _modeButtons[i].BackColor = i == selectedMode ? ButtonSelected : ToolbarBack;
        }

        private WF.Button ModeButton(string text, int mode, AllInOneAction action)
        {
            var button = ToolButton(text);
            button.Click += (_, _) => _owner.SetMode(mode, action);
            return button;
        }

        private static WF.Button ToolButton(string text)
        {
            var button = new WF.Button
            {
                AutoSize = true,
                BackColor = ToolbarBack,
                Cursor = WF.Cursors.Hand,
                FlatStyle = WF.FlatStyle.Flat,
                ForeColor = SD.Color.White,
                Margin = new WF.Padding(2, 0, 2, 0),
                Padding = new WF.Padding(10, 5, 10, 5),
                Text = text,
                UseVisualStyleBackColor = false,
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ButtonHot;
            return button;
        }

        private WF.TextBox SizeBox()
        {
            var box = new WF.TextBox
            {
                BackColor = SD.Color.FromArgb(58, 58, 58),
                BorderStyle = WF.BorderStyle.FixedSingle,
                ForeColor = SD.Color.White,
                Margin = new WF.Padding(2, 2, 2, 0),
                TextAlign = WF.HorizontalAlignment.Center,
                Width = 48,
            };
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode != WF.Keys.Enter)
                    return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (int.TryParse(_widthBox.Text, out int w) && int.TryParse(_heightBox.Text, out int h))
                    _owner.ApplyExactSize(w, h);
            };
            return box;
        }

        private static WF.Control Label(string text) =>
            new WF.Label
            {
                AutoSize = true,
                ForeColor = SD.Color.FromArgb(170, 170, 170),
                Margin = new WF.Padding(4, 6, 4, 0),
                Text = text,
            };

        private static WF.Control Separator() =>
            new WF.Label
            {
                AutoSize = false,
                BackColor = SD.Color.FromArgb(90, 90, 90),
                Margin = new WF.Padding(8, 2, 8, 2),
                Width = 1,
                Height = 24,
            };

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);
    }
}
