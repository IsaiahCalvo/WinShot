using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Overlay;

public sealed class FastQuickActionsWindow : WF.Form
{
    private const int Pad = 12;
    private const int ButtonSize = 30;
    private const int ButtonGap = 6;
    private const int MaxThumbWidth = 320;
    private const int MaxThumbHeight = 180;
    private const int CornerRadius = 14;
    private const int WmNclbuttondown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    // All colors flow from the one shared palette (mirrors Theme.xaml) so the overlay
    // matches the rest of the app instead of hardcoding its own grays.
    private static readonly SD.Color Surface = ThemePalette.ToolbarBg;
    private static readonly SD.Color SurfaceAlt = ThemePalette.Surface;
    private static readonly SD.Color SurfaceHover = ThemePalette.SurfaceHover;
    private static readonly SD.Color Border = ThemePalette.Border;
    private static readonly SD.Color TextColor = ThemePalette.TextPrimary;
    private static readonly SD.Color TextDim = ThemePalette.TextSecondary;
    private static readonly SD.Color Accent = ThemePalette.Accent;
    private static readonly SD.Font GlyphFont = ThemePalette.IconFont(11f);
    private static readonly SD.Font CloseGlyphFont = ThemePalette.IconFont(9f);
    private static readonly List<FastQuickActionsWindow> OpenWindows = new();
    private static readonly Stack<string> RecentlyClosed = new();

    private readonly SD.Bitmap _image;
    private readonly SettingsService _settings;
    private readonly Task? _releaseAfterTask;
    private readonly bool _requestMemoryCleanupOnClose;
    private readonly bool _loadPreview;
    private readonly WF.ToolTip _toolTip = new() { InitialDelay = 300, ReshowDelay = 100 };
    private readonly List<ActionButton> _buttons = new();
    private SD.Rectangle _thumbRect;
    private SD.Bitmap? _preview;
    private Task? _previewTask;
    private Task? _copyTask;
    private Task<string>? _dragFileTask;
    private string? _tempDragPath;
    private string? _historyPath;
    private int _hoverButton = -1;
    private int _pressedButton = -1;
    private bool _dragArmed;
    private bool _closed;
    private bool _useRegionCorners;
    private bool _regionUpdateQueued;
    private SD.Point _dragStart;

    public event Action<FastQuickActionsWindow>? EditRequested;
    public event Action<FastQuickActionsWindow>? PinRequested;
    public event Action<FastQuickActionsWindow>? OcrRequested;
    public event Action<FastQuickActionsWindow>? BackgroundRequested;

    public FastQuickActionsWindow(
        SD.Bitmap image,
        SettingsService settings,
        string? historyPath = null,
        Task<string?>? historyPathTask = null)
        : this(image, settings, historyPath, historyPathTask, releaseAfterTask: null)
    {
    }

    private FastQuickActionsWindow(
        SD.Bitmap image,
        SettingsService settings,
        string? historyPath,
        Task<string?>? historyPathTask,
        Task? releaseAfterTask,
        bool requestMemoryCleanupOnClose = true,
        bool loadPreview = true)
    {
        _image = image;
        _settings = settings;
        _historyPath = historyPath;
        _releaseAfterTask = releaseAfterTask;
        _requestMemoryCleanupOnClose = requestMemoryCleanupOnClose;
        _loadPreview = loadPreview;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Surface;
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

        BuildLayout(image);
        PositionBottomRight();

        MouseDown += OnOverlayMouseDown;
        MouseMove += OnOverlayMouseMove;
        MouseUp += OnOverlayMouseUp;
        MouseLeave += (_, _) => { SetHover(-1); SetPressed(-1); };
        KeyDown += OnOverlayKeyDown;
        Shown += (_, _) => QueuePreviewLoad();
        FormClosed += (_, _) =>
        {
            OpenWindows.Remove(this);
            _closed = true;
            if (_historyPath is not null)
                PushRecentlyClosed(_historyPath);
            DisposeImageWhenUnused();
        };
        OpenWindows.Add(this);

        if (historyPathTask is not null)
            _ = WatchHistoryPathAsync(historyPathTask);

        int seconds = settings.Current.OverlayAutoCloseSeconds;
        if (seconds > 0)
        {
            var timer = new WF.Timer { Interval = seconds * 1000 };
            timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); Close(); };
            timer.Start();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _useRegionCorners = !TryApplySystemRoundedCorners(Handle);
    }

    public static FastQuickActionsWindow CreateWithDeferredImageRelease(
        SD.Bitmap image,
        SettingsService settings,
        Task<string?> historyPathTask,
        Task releaseAfterTask)
        => new(image, settings, historyPath: null, historyPathTask, releaseAfterTask);

    public static void Prewarm(SettingsService settings)
    {
        try
        {
            using var bitmap = new SD.Bitmap(MaxThumbWidth, MaxThumbHeight, SD.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = SD.Graphics.FromImage(bitmap))
                g.Clear(SurfaceAlt);
            using var preview = CreatePreviewBitmap(bitmap, MaxThumbWidth, MaxThumbHeight);
            using var window = new FastQuickActionsWindow(
                (SD.Bitmap)bitmap.Clone(),
                settings,
                historyPath: null,
                historyPathTask: null,
                releaseAfterTask: null,
                requestMemoryCleanupOnClose: false,
                loadPreview: false)
            {
                Opacity = 0,
                ShowInTaskbar = false,
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-32000, -32000),
            };
            window.Show();
            using var render = new SD.Bitmap(
                Math.Max(1, window.Width),
                Math.Max(1, window.Height),
                SD.Imaging.PixelFormat.Format32bppPArgb);
            window.DrawToBitmap(render, new SD.Rectangle(0, 0, render.Width, render.Height));
            WF.Application.DoEvents();
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Fast quick actions prewarm failed", ex);
        }
    }

    public static void TrackFirstShown(WF.Form form, string metricName)
    {
        var sw = Stopwatch.StartNew();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (handler is not null)
                form.Shown -= handler;
            Log.Info($"Perf {metricName} first show: {sw.ElapsedMilliseconds} ms");
        };
        form.Shown += handler;
    }

    public static string? PopRecentlyClosed()
    {
        lock (RecentlyClosed)
        {
            while (RecentlyClosed.Count > 0)
            {
                string path = RecentlyClosed.Pop();
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    public SD.Bitmap CloneImage() => CaptureService.CloneBitmap(_image);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        QueueRegionUpdate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        QueueRegionUpdate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            QueueRegionUpdate();
            QueuePreviewLoad();
        }
    }

    private void QueuePreviewLoad()
    {
        if (!_loadPreview || _closed || IsDisposed)
            return;

        BeginInvoke(new Action(() =>
        {
            if (!_closed && !IsDisposed)
                _previewTask ??= LoadPreviewAsync();
        }));
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Surface);

        DrawThumbnail(e.Graphics);
        for (int i = 0; i < _buttons.Count; i++)
            DrawButton(e.Graphics, _buttons[i], i == _hoverButton, i == _pressedButton);

        using var path = RoundedRect(new SD.Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var pen = new SD.Pen(Border, 1);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preview?.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildLayout(SD.Bitmap image)
    {
        var size = CaptureService.GetBitmapSize(image);
        double scale = Math.Min(1.0, Math.Min(MaxThumbWidth / (double)size.Width, MaxThumbHeight / (double)size.Height));
        int thumbWidth = Math.Max(1, (int)Math.Round(size.Width * scale));
        int thumbHeight = Math.Max(1, (int)Math.Round(size.Height * scale));
        int buttonCount = 6;
        int buttonRowWidth = buttonCount * ButtonSize + (buttonCount - 1) * ButtonGap;
        int contentWidth = Math.Max(thumbWidth, buttonRowWidth);

        ClientSize = new SD.Size(contentWidth + Pad * 2, thumbHeight + ButtonSize + Pad * 3);
        _thumbRect = new SD.Rectangle((ClientSize.Width - thumbWidth) / 2, Pad, thumbWidth, thumbHeight);

        int x = (ClientSize.Width - buttonRowWidth) / 2;
        int y = _thumbRect.Bottom + Pad;
        AddButton("\uE718", "Pin (P)", x, y, () => PinRequested?.Invoke(this));
        x += ButtonSize + ButtonGap;
        AddButton("\uE8C8", "Copy (C)", x, y, CopyAsync);
        x += ButtonSize + ButtonGap;
        AddButton("\uE70F", "Edit (E)", x, y, () => EditRequested?.Invoke(this));
        x += ButtonSize + ButtonGap;
        AddButton("\uE74E", "Save (S)", x, y, SaveAsync);
        x += ButtonSize + ButtonGap;
        AddButton("\uE721", "OCR (O)", x, y, () => OcrRequested?.Invoke(this));
        x += ButtonSize + ButtonGap;
        AddButton("\uEB9F", "Background (B)", x, y, () => BackgroundRequested?.Invoke(this));

        _buttons.Add(new ActionButton(
            "\uE8BB",
            "Close (Esc)",
            new SD.Rectangle(ClientSize.Width - 25, 3, 22, 22),
            Close,
            CloseGlyphFont));
    }

    private void AddButton(string glyph, string tip, int x, int y, Action action)
        => _buttons.Add(new ActionButton(glyph, tip, new SD.Rectangle(x, y, ButtonSize, ButtonSize), action, GlyphFont));

    private void DrawThumbnail(SD.Graphics g)
    {
        if (_preview is not null)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_preview, _thumbRect);
            return;
        }

        using var brush = new SD.SolidBrush(SurfaceAlt);
        using var pen = new SD.Pen(Border);
        g.FillRectangle(brush, _thumbRect);
        g.DrawRectangle(pen, _thumbRect.X, _thumbRect.Y, Math.Max(0, _thumbRect.Width - 1), Math.Max(0, _thumbRect.Height - 1));
    }

    private static void DrawButton(SD.Graphics g, ActionButton button, bool hot, bool pressed)
    {
        // Rest: glyph only (dim white). Hover: subtle white circle + bright glyph.
        // Pressed: solid accent-blue circle + white glyph — the one accent identity.
        if (pressed)
        {
            using var accent = new SD.SolidBrush(Accent);
            g.FillEllipse(accent, button.Bounds);
        }
        else if (hot)
        {
            using var hover = new SD.SolidBrush(ThemePalette.HoverFill);
            g.FillEllipse(hover, button.Bounds);
        }

        SD.Color glyphColor = pressed ? SD.Color.White : hot ? TextColor : TextDim;
        TextRendererDrawGlyph(g, button.Glyph, button.Font, button.Bounds, glyphColor);
    }

    private static void TextRendererDrawGlyph(SD.Graphics g, string glyph, SD.Font font, SD.Rectangle bounds, SD.Color color)
    {
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(g, glyph, font, bounds, color, flags);
    }

    private void SetPressed(int index)
    {
        if (_pressedButton == index) return;
        int old = _pressedButton;
        _pressedButton = index;
        if (old >= 0) Invalidate(_buttons[old].Bounds);
        if (index >= 0) Invalidate(_buttons[index].Bounds);
    }

    private void PositionBottomRight()
    {
        // Pop on the monitor the user just acted on (under the cursor), not always the
        // primary one — matches where the capture happened, like the pin window does.
        var area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        int offset = OpenWindows
            .Where(w => !ReferenceEquals(w, this) && w.Visible)
            .Sum(w => w.Height + 12);
        Location = new SD.Point(
            area.Right - Width - 16,
            area.Bottom - Height - 16 - offset);
    }

    private void QueueRegionUpdate()
    {
        if (!_useRegionCorners || !Visible || Width <= 0 || Height <= 0 || _regionUpdateQueued)
            return;

        _regionUpdateQueued = true;
        BeginInvoke(new Action(() =>
        {
            _regionUpdateQueued = false;
            if (!_useRegionCorners || IsDisposed || Width <= 0 || Height <= 0)
                return;

            Region?.Dispose();
            using var path = RoundedRect(new SD.Rectangle(0, 0, Width, Height), CornerRadius);
            Region = new SD.Region(path);
        }));
    }

    private async Task WatchHistoryPathAsync(Task<string?> task)
    {
        try
        {
            string? path = await task.ConfigureAwait(false);
            if (path is null) return;

            if (_closed || IsDisposed)
            {
                PushRecentlyClosed(path);
                return;
            }

            BeginInvoke(new Action(() =>
            {
                _historyPath = path;
                if (_closed)
                    PushRecentlyClosed(path);
            }));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to attach history path to overlay", ex);
        }
    }

    private static void PushRecentlyClosed(string path)
    {
        lock (RecentlyClosed)
            RecentlyClosed.Push(path);
    }

    private void DisposeImageWhenUnused()
    {
        Task? pending = PendingImageUseTask();
        if (pending is null || pending.IsCompleted)
        {
            _image.Dispose();
            if (_requestMemoryCleanupOnClose)
                MemoryCleanup.Request();
            return;
        }

        _ = DisposeImageAfterAsync(pending, _image, _requestMemoryCleanupOnClose);
    }

    private Task? PendingImageUseTask()
    {
        var tasks = new List<Task>(3);
        if (_previewTask is { IsCompleted: false } previewTask) tasks.Add(previewTask);
        if (_releaseAfterTask is { IsCompleted: false } releaseTask) tasks.Add(releaseTask);
        if (_copyTask is { IsCompleted: false } copyTask) tasks.Add(copyTask);

        return tasks.Count switch
        {
            0 => null,
            1 => tasks[0],
            _ => Task.WhenAll(tasks),
        };
    }

    private static async Task DisposeImageAfterAsync(Task pending, SD.Bitmap image, bool requestCleanup)
    {
        try { await pending.ConfigureAwait(false); }
        catch { }
        image.Dispose();
        if (requestCleanup)
            MemoryCleanup.Request();
    }

    private async Task LoadPreviewAsync()
    {
        try
        {
            var preview = await Task.Run(() => CreatePreviewBitmap(_image, _thumbRect.Width, _thumbRect.Height)).ConfigureAwait(false);
            if (_closed || IsDisposed)
            {
                preview.Dispose();
                return;
            }

            BeginInvoke(new Action(() =>
            {
                if (_closed || IsDisposed)
                {
                    preview.Dispose();
                    return;
                }

                var old = _preview;
                _preview = preview;
                old?.Dispose();
                Invalidate(_thumbRect);
            }));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load fast overlay thumbnail", ex);
        }
    }

    private static SD.Bitmap CreatePreviewBitmap(SD.Bitmap image, int width, int height)
    {
        var preview = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppPArgb);
        lock (image)
        {
            using var g = SD.Graphics.FromImage(preview);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.Low;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(image, new SD.Rectangle(0, 0, width, height));
        }
        return preview;
    }

    private void OnOverlayKeyDown(object? sender, WF.KeyEventArgs e)
    {
        if (e.Modifiers != WF.Keys.None) return;
        switch (e.KeyCode)
        {
            case WF.Keys.C: CopyAsync(); e.Handled = true; break;
            case WF.Keys.S: SaveAsync(); e.Handled = true; break;
            case WF.Keys.E: EditRequested?.Invoke(this); e.Handled = true; break;
            case WF.Keys.P: PinRequested?.Invoke(this); e.Handled = true; break;
            case WF.Keys.O: OcrRequested?.Invoke(this); e.Handled = true; break;
            case WF.Keys.B: BackgroundRequested?.Invoke(this); e.Handled = true; break;
            case WF.Keys.Escape: Close(); e.Handled = true; break;
        }
    }

    private void OnOverlayMouseDown(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left) return;

        int buttonIndex = HitTestButton(e.Location);
        if (buttonIndex >= 0)
        {
            SetPressed(buttonIndex);
            return;
        }

        if (_thumbRect.Contains(e.Location))
        {
            _dragArmed = true;
            _dragStart = e.Location;
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, WmNclbuttondown, HtCaption, IntPtr.Zero);
    }

    private async void OnOverlayMouseMove(object? sender, WF.MouseEventArgs e)
    {
        SetHover(HitTestButton(e.Location));

        if (!_dragArmed || e.Button != WF.MouseButtons.Left) return;
        if (Math.Abs(e.X - _dragStart.X) < WF.SystemInformation.DragSize.Width / 2 &&
            Math.Abs(e.Y - _dragStart.Y) < WF.SystemInformation.DragSize.Height / 2)
            return;

        _dragArmed = false;
        try
        {
            string path = await EnsureDragFileAsync();
            if (_closed || IsDisposed) return;

            var data = new WF.DataObject();
            data.SetData(WF.DataFormats.FileDrop, new[] { path });
            DoDragDrop(data, WF.DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            _dragFileTask = null;
            Log.Error("Thumbnail drag-out failed", ex);
        }
    }

    private void OnOverlayMouseUp(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left) return;
        _dragArmed = false;
        SetPressed(-1);

        int buttonIndex = HitTestButton(e.Location);
        if (buttonIndex >= 0)
            _buttons[buttonIndex].Action();
    }

    private int HitTestButton(SD.Point point)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            if (_buttons[i].Bounds.Contains(point))
                return i;
        }
        return -1;
    }

    private void SetHover(int index)
    {
        if (_hoverButton == index) return;

        int old = _hoverButton;
        _hoverButton = index;
        if (old >= 0) Invalidate(_buttons[old].Bounds);
        if (index >= 0)
        {
            Invalidate(_buttons[index].Bounds);
            _toolTip.SetToolTip(this, _buttons[index].Tip);
        }
        else
        {
            _toolTip.SetToolTip(this, null);
        }
    }

    private Task<string> EnsureDragFileAsync()
    {
        if (_historyPath is not null && File.Exists(_historyPath))
            return Task.FromResult(_historyPath);
        if (_tempDragPath is not null && File.Exists(_tempDragPath))
            return Task.FromResult(_tempDragPath);

        string dir = TempFileJanitor.WinShotTempDirectory;
        string path = FileNamer.NextUniquePath(_settings, dir, "png");
        _dragFileTask ??= CreateDragFileAsync(dir, path);
        return _dragFileTask;
    }

    private async Task<string> CreateDragFileAsync(string dir, string path)
    {
        var copy = CaptureService.CloneBitmap(_image);
        await Task.Run(() =>
        {
            using (copy)
            {
                Directory.CreateDirectory(dir);
                TempFileJanitor.DeleteOldFiles(dir, DateTimeOffset.UtcNow, TimeSpan.FromDays(1), maxFilesToDelete: 50);
                ImageSaver.Save(copy, path);
            }
        });
        _tempDragPath = path;
        return path;
    }

    private async void CopyAsync()
    {
        try
        {
            _copyTask = CaptureService.CopyToClipboardAsync(_image);
            await _copyTask;
            if (_closed || IsDisposed) return;

            int index = _buttons.FindIndex(b => b.Tip.StartsWith("Copy", StringComparison.Ordinal));
            if (index < 0) return;

            _buttons[index].Glyph = "\uE73E";
            Invalidate(_buttons[index].Bounds);
            var timer = new WF.Timer { Interval = 1200 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (_closed || IsDisposed) return;
                _buttons[index].Glyph = "\uE8C8";
                Invalidate(_buttons[index].Bounds);
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Copy to clipboard failed", ex);
        }
    }

    private async void SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(_settings.Current.SaveFolder);
            using var dialog = new WF.SaveFileDialog
            {
                FileName = FileNamer.Next(_settings, _settings.Current.ImageFormat),
                InitialDirectory = _settings.Current.SaveFolder,
                Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image|*.webp",
                FilterIndex = _settings.Current.ImageFormat switch
                {
                    "jpg" => 2,
                    "webp" => 3,
                    _ => 1,
                },
            };
            if (dialog.ShowDialog(this) != WF.DialogResult.OK)
                return;

            var copy = CaptureService.CloneBitmap(_image);
            await Task.Run(() =>
            {
                using (copy)
                    ImageSaver.Save(copy, dialog.FileName);
            });
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
        }
    }

    private static GraphicsPath RoundedRect(SD.Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return path;

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static bool TryApplySystemRoundedCorners(IntPtr handle)
    {
        if (Environment.OSVersion.Version.Build < 22000)
            return false;

        try
        {
            int preference = 2;
            return DwmSetWindowAttribute(
                handle,
                33,
                ref preference,
                sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    private sealed class ActionButton(
        string glyph,
        string tip,
        SD.Rectangle bounds,
        Action action,
        SD.Font font)
    {
        public string Glyph { get; set; } = glyph;
        public string Tip { get; } = tip;
        public SD.Rectangle Bounds { get; } = bounds;
        public Action Action { get; } = action;
        public SD.Font Font { get; } = font;
    }
}
