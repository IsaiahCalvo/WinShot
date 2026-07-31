using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace WinShot.Overlay;

public sealed class FastQuickActionsWindow : WF.Form
{
    // The idle surface is only the capture thumbnail. Hover reveals four corner actions and
    // one centered Copy pill; OCR and Background stay available in the context menu.
    private const int CsDropShadow = 0x00020000;
    private const int WmNclbuttondown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    private static readonly SD.Color CardFill = ThemePalette.WindowBg;                       // backing behind the thumbnail
    private static readonly SD.Color HoverScrim = SD.Color.FromArgb(112, 0, 0, 0);
    private static readonly SD.Color Cream = SD.Color.FromArgb(0xF8, 0xF8, 0xF6);
    private static readonly SD.Color CreamHover = SD.Color.White;
    private static readonly SD.Color CreamPressed = SD.Color.FromArgb(0xE3, 0xE3, 0xDF);
    private static readonly SD.Color CreamText = SD.Color.FromArgb(0x22, 0x22, 0x24);        // dark glyph/label on cream
    private static readonly List<FastQuickActionsWindow> OpenWindows = new();
    private static readonly Stack<string> RecentlyClosed = new();

    private readonly SD.Bitmap _image;
    private readonly SettingsService _settings;
    private readonly Task? _releaseAfterTask;
    private readonly bool _requestMemoryCleanupOnClose;
    private readonly bool _loadPreview;
    private readonly WF.ToolTip _toolTip = new() { InitialDelay = 300, ReshowDelay = 100 };
    private readonly WF.ContextMenuStrip _overflowMenu = new();
    private readonly List<ActionButton> _buttons = new();
    private WF.Timer? _autoCloseTimer;
    private long _autoCloseDueTick;
    private int _autoCloseRemainingMs;
    private SD.Rectangle _cardRect;
    private SD.Rectangle _thumbRect;   // the fitted thumbnail centered inside the card
    private int _cornerRadius;
    private int _dpi = 96;
    private SD.Bitmap? _preview;
    private SD.Bitmap? _blurredPreview;
    private Task? _previewTask;
    private Task? _copyTask;
    private Task<string>? _dragFileTask;
    private string? _tempDragPath;
    private string? _historyPath;
    private bool _hovering;
    private int _hoverButton = -1;
    private int _pressedButton = -1;
    private int _focusedButton = -1;
    private bool _dragArmed;
    private bool _closed;
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
        BackColor = CardFill;
        AccessibleName = "Quick Access capture preview";
        AccessibleDescription = "Captured image actions. Press Tab to move through actions or Shift+F10 for more actions.";
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

        BuildLayout(image, _dpi);
        PositionFromSettings();
        BuildOverflowMenu();

        MouseDown += OnOverlayMouseDown;
        MouseMove += OnOverlayMouseMove;
        MouseUp += OnOverlayMouseUp;
        MouseEnter += (_, _) => SetHovering(true);
        MouseLeave += (_, _) => SetHovering(false);
        KeyDown += OnOverlayKeyDown;
        Shown += (_, _) => QueuePreviewLoad();
        FormClosed += (_, _) =>
        {
            OpenWindows.Remove(this);
            StopAutoCloseTimer();
            _closed = true;
            if (_historyPath is not null)
                PushRecentlyClosed(_historyPath);
            DisposeImageWhenUnused();
        };
        OpenWindows.Add(this);

        if (historyPathTask is not null)
            _ = WatchHistoryPathAsync(historyPathTask);

        StartAutoCloseTimer();
    }

    protected override bool ShowWithoutActivation => true;

    protected override WF.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    protected override WF.AccessibleObject CreateAccessibilityInstance()
        => new OverlayAccessibleObject(this);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int handleDpi = GetDpiForWindow(Handle);
        if (handleDpi > 0 && handleDpi != _dpi)
        {
            _dpi = handleDpi;
            BuildLayout(_image, _dpi);
            PositionFromSettings();
        }
        QueueRegionUpdate();
    }

    public static FastQuickActionsWindow CreateWithDeferredImageRelease(
        SD.Bitmap image,
        SettingsService settings,
        Task<string?> historyPathTask,
        Task releaseAfterTask)
        => new(image, settings, historyPath: null, historyPathTask, releaseAfterTask);

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

    private void SetHovering(bool hovering)
    {
        if (_hovering == hovering) return;
        if (hovering)
        {
            foreach (var window in OpenWindows.Where(w => !ReferenceEquals(w, this)))
                window.SetHovering(false);
            PauseAutoCloseTimer();
        }
        else if (!_overflowMenu.Visible)
        {
            ResumeAutoCloseTimer();
        }
        _hovering = hovering;
        if (!hovering)
        {
            SetHover(-1);
            SetPressed(-1);
        }
        Invalidate();
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(SD.Color.Transparent);

        // Idle is only the rounded thumbnail card. The native window class supplies the soft shadow.
        using (var fill = new SD.SolidBrush(CardFill))
        using (var cardPath = GdiPaths.RoundedRect(_cardRect, _cornerRadius))
            g.FillPath(fill, cardPath);

        DrawThumbnail(g, _hovering);

        if (_hovering)
        {
            using (var scrim = new SD.SolidBrush(HoverScrim))
            using (var cardPath = GdiPaths.RoundedRect(_cardRect, _cornerRadius))
                g.FillPath(scrim, cardPath);

            for (int i = 0; i < _buttons.Count; i++)
            {
                DrawButton(g, _buttons[i], i == _hoverButton, i == _pressedButton);
                if (i == _focusedButton)
                    DrawFocus(g, _buttons[i]);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            OpenWindows.Remove(this);
            _closed = true;
            _preview?.Dispose();
            _blurredPreview?.Dispose();
            StopAutoCloseTimer();
            _toolTip.Dispose();
            _overflowMenu.Dispose();
            foreach (var button in _buttons)
                button.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildLayout(SD.Bitmap image, int dpi)
    {
        var size = CaptureService.GetBitmapSize(image);
        var layout = QuickAccessOverlayLayout.Calculate(size, _settings.Current.OverlaySizePercent, dpi);
        ClientSize = layout.ClientSize;
        _cardRect = layout.CardBounds;
        _thumbRect = layout.ThumbnailBounds;
        _cornerRadius = layout.CornerRadius;

        BuildButtons(layout);
    }

    private void BuildButtons(QuickAccessOverlayLayout layout)
    {
        foreach (var button in _buttons)
            button.Dispose();
        _buttons.Clear();
        int left = _cardRect.Left + layout.ControlPadding;
        int right = _cardRect.Right - layout.ControlPadding - layout.IconSize;
        int top = _cardRect.Top + layout.ControlPadding;
        int bottom = _cardRect.Bottom - layout.ControlPadding - layout.IconSize;
        // Cloud is intentionally absent. Save occupies its former bottom-right corner.
        AddIconButton("", "Pin to the screen", "Pin", left, top, layout.IconSize, () => PinRequested?.Invoke(this));
        AddIconButton("", "Close (Ctrl+W)", "Close", right, top, layout.IconSize, Close);
        AddIconButton("", "Open Annotate tool (Ctrl+E)", "Annotate", left, bottom, layout.IconSize, () => EditRequested?.Invoke(this));
        AddIconButton("", "Save (Ctrl+S)", "Save", right, bottom, layout.IconSize, SaveFromInput, CreateSaveIcon(layout.IconSize));

        int pillX = _cardRect.Left + (_cardRect.Width - layout.PillWidth) / 2;
        int pillTop = _cardRect.Top + (_cardRect.Height - layout.PillHeight) / 2;

        AddPillButton("Copy", "Copy (Ctrl+C)", pillX, pillTop, layout.PillWidth, layout.PillHeight, () => CopyAsync(keepOpen: IsAltDown()));
        _focusedButton = Math.Clamp(_focusedButton, -1, _buttons.Count - 1);
    }

    private void AddIconButton(
        string glyph,
        string tip,
        string name,
        int x,
        int y,
        int size,
        Action action,
        SD.Bitmap? icon = null)
        => _buttons.Add(new ActionButton(
            glyph,
            tip,
            name,
            new SD.Rectangle(x, y, size, size),
            action,
            ThemePalette.IconFont(tip.StartsWith("Close", StringComparison.Ordinal) ? 8.5f : 10.5f),
            ActionButtonShape.Circle,
            size / 2)
        {
            Icon = icon,
        });

    private void AddPillButton(string label, string tip, int x, int y, int width, int height, Action action)
        => _buttons.Add(new ActionButton(
            label,
            tip,
            label,
            new SD.Rectangle(x, y, width, height),
            action,
            ThemePalette.UiFont(9.5f, SD.FontStyle.Bold),
            ActionButtonShape.Pill,
            height / 2)
        {
            Label = label,
        });

    private void DrawThumbnail(SD.Graphics g, bool blurred)
    {
        SD.Bitmap? bmp = blurred ? (_blurredPreview ?? _preview) : _preview;
        if (bmp is null)
            return;

        using var clip = GdiPaths.RoundedRect(_cardRect, _cornerRadius);
        var oldClip = g.Clip;
        var oldInterp = g.InterpolationMode;
        var oldOffset = g.PixelOffsetMode;
        try
        {
            g.SetClip(clip, CombineMode.Intersect);
            g.InterpolationMode = blurred ? InterpolationMode.HighQualityBilinear : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bmp, _thumbRect);
        }
        finally
        {
            g.Clip = oldClip;
            g.InterpolationMode = oldInterp;
            g.PixelOffsetMode = oldOffset;
        }
    }

    private static void DrawButton(SD.Graphics g, ActionButton button, bool hot, bool pressed)
    {
        SD.Color face = pressed ? CreamPressed : hot ? CreamHover : Cream;

        using (var path = button.Shape == ActionButtonShape.Circle
            ? CirclePath(button.Bounds)
            : GdiPaths.RoundedRect(button.Bounds, button.CornerRadius))
        {
            using var fill = new SD.SolidBrush(face);
            g.FillPath(fill, path);
            using var pen = new SD.Pen(SD.Color.FromArgb(0x18, 0x00, 0x00, 0x00), 1);
            g.DrawPath(pen, path);
        }

        if (button.Icon is not null)
        {
            int x = button.Bounds.Left + (button.Bounds.Width - button.Icon.Width) / 2;
            int y = button.Bounds.Top + (button.Bounds.Height - button.Icon.Height) / 2;
            g.DrawImageUnscaled(button.Icon, x, y);
            return;
        }

        string text = button.Shape == ActionButtonShape.Pill && button.Label is not null ? button.Label : button.Glyph;
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(g, text, button.Font, button.Bounds, CreamText, flags);
    }

    private static SD.Bitmap? CreateSaveIcon(int buttonSize)
    {
        try
        {
            using Stream? stream = typeof(FastQuickActionsWindow).Assembly.GetManifestResourceStream(
                "WinShot.Assets.download-window-svgrepo-com.svg");
            if (stream is null)
                return null;

            XDocument document = XDocument.Load(stream);
            XNamespace svg = "http://www.w3.org/2000/svg";
            string? pathData = document.Root?.Element(svg + "path")?.Attribute("d")?.Value;
            if (string.IsNullOrWhiteSpace(pathData))
                return null;

            WM.Geometry geometry = WM.Geometry.Parse(pathData);
            W.Rect bounds = geometry.Bounds;
            int pixelSize = Math.Max(10, (int)Math.Round(buttonSize * 0.64));
            double inset = Math.Max(1, pixelSize * 0.06);
            double scale = Math.Min(
                (pixelSize - inset * 2) / bounds.Width,
                (pixelSize - inset * 2) / bounds.Height);
            var transform = new WM.MatrixTransform(new WM.Matrix(
                scale,
                0,
                0,
                scale,
                (pixelSize - bounds.Width * scale) / 2 - bounds.X * scale,
                (pixelSize - bounds.Height * scale) / 2 - bounds.Y * scale));

            var visual = new WM.DrawingVisual();
            using (WM.DrawingContext context = visual.RenderOpen())
            {
                context.PushTransform(transform);
                context.DrawGeometry(
                    new WM.SolidColorBrush(WM.Color.FromArgb(CreamText.A, CreamText.R, CreamText.G, CreamText.B)),
                    null,
                    geometry);
                context.Pop();
            }

            var rendered = new WMI.RenderTargetBitmap(
                pixelSize,
                pixelSize,
                96,
                96,
                WM.PixelFormats.Pbgra32);
            rendered.Render(visual);

            var bitmap = new SD.Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppPArgb);
            BitmapData data = bitmap.LockBits(
                new SD.Rectangle(0, 0, pixelSize, pixelSize),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                rendered.CopyPixels(
                    new W.Int32Rect(0, 0, pixelSize, pixelSize),
                    data.Scan0,
                    data.Stride * data.Height,
                    data.Stride);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("Save icon SVG could not be rendered", ex);
            return null;
        }
    }

    private static GraphicsPath CirclePath(SD.Rectangle bounds)
    {
        var path = new GraphicsPath();
        path.AddEllipse(bounds);
        return path;
    }

    private static void DrawFocus(SD.Graphics g, ActionButton button)
    {
        var bounds = SD.Rectangle.Inflate(button.Bounds, -2, -2);
        using var pen = new SD.Pen(SD.Color.FromArgb(210, 36, 99, 180), 1) { DashStyle = DashStyle.Dot };
        if (button.Shape == ActionButtonShape.Circle)
            g.DrawEllipse(pen, bounds);
        else
        {
            using var path = GdiPaths.RoundedRect(bounds, Math.Max(1, button.CornerRadius - 2));
            g.DrawPath(pen, path);
        }
    }

    private void SetPressed(int index)
    {
        if (_pressedButton == index) return;
        int old = _pressedButton;
        _pressedButton = index;
        if (old >= 0 && old < _buttons.Count) Invalidate(_buttons[old].Bounds);
        if (index >= 0) Invalidate(_buttons[index].Bounds);
    }

    private void PositionFromSettings()
    {
        var screen = _settings.Current.OverlayMoveToActiveScreen
            ? WF.Screen.FromPoint(WF.Cursor.Position)
            : WF.Screen.PrimaryScreen ?? WF.Screen.FromPoint(WF.Cursor.Position);
        var area = screen.WorkingArea;
        int offset = OpenWindows
            .Where(w => !ReferenceEquals(w, this) && w.Visible && area.IntersectsWith(w.Bounds))
            .Sum(w => w.Height + 12);
        Location = QuickAccessOverlayLayout.Place(
            area,
            Size,
            _settings.Current.OverlayPosition,
            offset,
            _dpi);
    }

    private void BuildOverflowMenu()
    {
        _overflowMenu.AccessibleName = "More capture actions";
        _overflowMenu.Items.Add("Recognize text (OCR)", null, (_, _) => OcrRequested?.Invoke(this));
        _overflowMenu.Items.Add("Add background", null, (_, _) => BackgroundRequested?.Invoke(this));
        _overflowMenu.Opening += (_, _) =>
        {
            SetHovering(true);
            PauseAutoCloseTimer();
        };
        _overflowMenu.Closed += (_, _) =>
        {
            if (!ClientRectangle.Contains(PointToClient(WF.Cursor.Position)))
            {
                if (_hovering)
                    SetHovering(false);
                else
                    ResumeAutoCloseTimer();
            }
            else
            {
                SetHovering(true);
                PauseAutoCloseTimer();
            }
        };
    }

    private void ShowOverflowMenu()
    {
        SetHovering(true);
        _overflowMenu.Show(this, new SD.Point(Width / 2, Height / 2));
    }

    private void StartAutoCloseTimer()
    {
        int seconds = Math.Max(0, _settings.Current.OverlayAutoCloseSeconds);
        if (!_settings.Current.OverlayAutoClose || seconds <= 0)
            return;
        _autoCloseRemainingMs = (int)Math.Min(int.MaxValue, seconds * 1000L);
        ResumeAutoCloseTimer();
    }

    private void PauseAutoCloseTimer()
    {
        if (_autoCloseTimer is null || !_autoCloseTimer.Enabled)
            return;
        _autoCloseRemainingMs = (int)Math.Clamp(_autoCloseDueTick - Environment.TickCount64, 1, int.MaxValue);
        _autoCloseTimer.Stop();
    }

    private void ResumeAutoCloseTimer()
    {
        if (_autoCloseRemainingMs <= 0 || _closed || IsDisposed)
            return;
        _autoCloseTimer ??= CreateAutoCloseTimer();
        _autoCloseTimer.Interval = Math.Max(1, _autoCloseRemainingMs);
        _autoCloseDueTick = Environment.TickCount64 + _autoCloseRemainingMs;
        _autoCloseTimer.Start();
    }

    private WF.Timer CreateAutoCloseTimer()
    {
        var timer = new WF.Timer();
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            _autoCloseRemainingMs = 0;
            await RunAutoCloseActionAsync();
        };
        return timer;
    }

    private void StopAutoCloseTimer()
    {
        _autoCloseTimer?.Stop();
        _autoCloseTimer?.Dispose();
        _autoCloseTimer = null;
        _autoCloseRemainingMs = 0;
    }

    private async Task RunAutoCloseActionAsync()
    {
        switch (QuickAccessOverlayLayout.NormalizeAutoCloseAction(_settings.Current.OverlayAutoCloseAction))
        {
            case "copy-close":
                await CopyCoreAsync(keepOpen: false);
                break;
            case "close":
                Close();
                break;
            default:
                await SaveCoreAsync(askForDestination: false, closeAfterSave: true);
                break;
        }
    }

    private void QueueRegionUpdate()
    {
        if (!Visible || Width <= 0 || Height <= 0 || _regionUpdateQueued)
            return;

        _regionUpdateQueued = true;
        BeginInvoke(new Action(() =>
        {
            _regionUpdateQueued = false;
            if (IsDisposed || Width <= 0 || Height <= 0)
                return;

            // Clip the window to the rounded card so the margin around it is transparent
            // (and click-through), and so MouseEnter/Leave fire on the card's real shape.
            using var cardPath = GdiPaths.RoundedRect(_cardRect, _cornerRadius);
            Region?.Dispose();
            Region = new SD.Region(cardPath);
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
            int w = _thumbRect.Width, h = _thumbRect.Height;
            var (preview, blurred) = await Task.Run(() =>
            {
                var p = CreatePreviewBitmap(_image, w, h);
                var b = CreateBlurred(p);
                return (p, b);
            }).ConfigureAwait(false);

            if (_closed || IsDisposed)
            {
                preview.Dispose();
                blurred.Dispose();
                return;
            }

            BeginInvoke(new Action(() =>
            {
                if (_closed || IsDisposed)
                {
                    preview.Dispose();
                    blurred.Dispose();
                    return;
                }

                var oldP = _preview;
                var oldB = _blurredPreview;
                _preview = preview;
                _blurredPreview = blurred;
                oldP?.Dispose();
                oldB?.Dispose();
                Invalidate();
            }));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load fast overlay thumbnail", ex);
        }
    }

    private static SD.Bitmap CreatePreviewBitmap(SD.Bitmap image, int width, int height)
    {
        var preview = new SD.Bitmap(width, height, PixelFormat.Format32bppPArgb);
        lock (image)
        {
            using var g = SD.Graphics.FromImage(preview);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(image, new SD.Rectangle(0, 0, width, height));
        }
        return preview;
    }

    /// <summary>Cheap frosted blur: downscale to ~1/12 then upscale with bilinear smoothing.</summary>
    private static SD.Bitmap CreateBlurred(SD.Bitmap src)
    {
        int dw = Math.Max(1, src.Width / 12);
        int dh = Math.Max(1, src.Height / 12);
        using var small = new SD.Bitmap(dw, dh, PixelFormat.Format32bppPArgb);
        using (var g = SD.Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new SD.Rectangle(0, 0, dw, dh));
        }

        var blurred = new SD.Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
        using (var g = SD.Graphics.FromImage(blurred))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, new SD.Rectangle(0, 0, src.Width, src.Height));
        }
        return blurred;
    }

    private void OnOverlayKeyDown(object? sender, WF.KeyEventArgs e)
    {
        bool ctrl = e.Control;
        bool alt = e.Alt;
        if (e.KeyCode == WF.Keys.Tab && !ctrl && !alt)
        {
            SetHovering(true);
            int direction = e.Shift ? -1 : 1;
            _focusedButton = (_focusedButton + direction + _buttons.Count) % _buttons.Count;
            Invalidate();
            AccessibilityNotifyClients(WF.AccessibleEvents.Focus, _focusedButton);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if ((e.KeyCode == WF.Keys.Enter || e.KeyCode == WF.Keys.Space) && e.Modifiers == WF.Keys.None && _focusedButton >= 0)
        {
            _buttons[_focusedButton].Action();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if ((e.KeyCode == WF.Keys.Apps || (e.KeyCode == WF.Keys.F10 && e.Shift)) && !ctrl && !alt)
        {
            ShowOverflowMenu();
            e.Handled = true;
            return;
        }

        switch (e.KeyCode)
        {
            case WF.Keys.C when ctrl:
                CopyAsync(keepOpen: alt);
                e.Handled = true;
                break;
            case WF.Keys.C when e.Modifiers == WF.Keys.None:
                CopyAsync(keepOpen: true); // preserve the legacy single-key action
                e.Handled = true;
                break;
            case WF.Keys.S when ctrl || e.Modifiers == WF.Keys.None:
                SaveFromInput();
                e.Handled = true;
                break;
            case WF.Keys.E when ctrl || e.Modifiers == WF.Keys.None:
                EditRequested?.Invoke(this);
                e.Handled = true;
                break;
            case WF.Keys.W when ctrl:
            case WF.Keys.Escape when e.Modifiers == WF.Keys.None:
                Close();
                e.Handled = true;
                break;
            case WF.Keys.P when e.Modifiers == WF.Keys.None:
                PinRequested?.Invoke(this);
                e.Handled = true;
                break;
            case WF.Keys.O when e.Modifiers == WF.Keys.None:
                OcrRequested?.Invoke(this);
                e.Handled = true;
                break;
            case WF.Keys.B when e.Modifiers == WF.Keys.None:
                BackgroundRequested?.Invoke(this);
                e.Handled = true;
                break;
        }
    }

    private void OnOverlayMouseDown(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Right)
        {
            ShowOverflowMenu();
            return;
        }
        if (e.Button != WF.MouseButtons.Left) return;

        Activate();
        Focus();

        int buttonIndex = _hovering ? HitTestButton(e.Location) : -1;
        if (buttonIndex >= 0)
        {
            SetPressed(buttonIndex);
            return;
        }

        if (_cardRect.Contains(e.Location))
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
        if (!_hovering)
            SetHovering(true);
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

            bool keepOpen = IsAltDown();
            var data = new WF.DataObject();
            data.SetData(WF.DataFormats.FileDrop, new[] { path });
            var result = DoDragDrop(data, WF.DragDropEffects.Copy);
            if (result != WF.DragDropEffects.None && _settings.Current.OverlayCloseAfterDragging && !keepOpen)
                Close();
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

        int buttonIndex = _hovering ? HitTestButton(e.Location) : -1;
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
        if (old >= 0 && old < _buttons.Count) Invalidate(_buttons[old].Bounds);
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

    private async void CopyAsync(bool keepOpen)
        => await CopyCoreAsync(keepOpen);

    private async Task CopyCoreAsync(bool keepOpen)
    {
        try
        {
            _copyTask = CaptureService.CopyToClipboardAsync(_image);
            await _copyTask;
            if (_closed || IsDisposed) return;

            if (!keepOpen)
            {
                Close();
                return;
            }

            int index = _buttons.FindIndex(b => b.Tip.StartsWith("Copy", StringComparison.Ordinal));
            if (index < 0) return;

            _buttons[index].Label = "Copied";
            Invalidate(_buttons[index].Bounds);
            var timer = new WF.Timer { Interval = 1200 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (_closed || IsDisposed) return;
                _buttons[index].Label = "Copy";
                Invalidate(_buttons[index].Bounds);
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Copy to clipboard failed", ex);
        }
    }

    private void SaveFromInput()
        => _ = SaveCoreAsync(
            askForDestination: IsAltDown() || QuickAccessOverlayLayout.NormalizeSaveBehavior(_settings.Current.OverlaySaveButtonBehavior) == "ask",
            closeAfterSave: true);

    private async Task SaveCoreAsync(bool askForDestination, bool closeAfterSave)
    {
        try
        {
            Directory.CreateDirectory(_settings.Current.SaveFolder);
            string path;
            if (askForDestination)
            {
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
                path = dialog.FileName;
            }
            else
            {
                path = FileNamer.NextUniquePath(_settings, _settings.Current.SaveFolder, _settings.Current.ImageFormat);
            }

            var copy = CaptureService.CloneBitmap(_image);
            await Task.Run(() =>
            {
                using (copy)
                    ImageSaver.Save(copy, path);
            });
            if (closeAfterSave && !_closed && !IsDisposed)
                Close();
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
        }
    }

    private static bool IsAltDown() => (WF.Control.ModifierKeys & WF.Keys.Alt) == WF.Keys.Alt;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);

    private enum ActionButtonShape
    {
        Circle,
        Pill,
    }

    private sealed class ActionButton(
        string glyph,
        string tip,
        string accessibleName,
        SD.Rectangle bounds,
        Action action,
        SD.Font font,
        ActionButtonShape shape = ActionButtonShape.Circle,
        int cornerRadius = 11) : IDisposable
    {
        public string Glyph { get; set; } = glyph;
        public string Tip { get; } = tip;
        public string AccessibleName { get; } = accessibleName;
        public SD.Rectangle Bounds { get; } = bounds;
        public Action Action { get; } = action;
        public SD.Font Font { get; } = font;
        public ActionButtonShape Shape { get; } = shape;
        public int CornerRadius { get; } = cornerRadius;
        public string? Label { get; set; }
        public SD.Bitmap? Icon { get; init; }

        public void Dispose()
        {
            Font.Dispose();
            Icon?.Dispose();
        }
    }

    private sealed class OverlayAccessibleObject(FastQuickActionsWindow owner)
        : WF.Control.ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner._buttons.Count;

        public override WF.AccessibleObject? GetChild(int index) =>
            index >= 0 && index < owner._buttons.Count
                ? new ActionAccessibleObject(owner, index)
                : null;

        public override WF.AccessibleObject? GetFocused() =>
            owner._focusedButton >= 0 ? GetChild(owner._focusedButton) : base.GetFocused();
    }

    private sealed class ActionAccessibleObject(FastQuickActionsWindow owner, int index) : WF.AccessibleObject
    {
        private ActionButton Button => owner._buttons[index];

        public override string? Name
        {
            get => Button.AccessibleName;
            set { }
        }

        public override string? Help => Button.Tip;
        public override string? DefaultAction => "Press";
        public override WF.AccessibleRole Role => WF.AccessibleRole.PushButton;
        public override WF.AccessibleStates State =>
            WF.AccessibleStates.Focusable |
            (owner._focusedButton == index ? WF.AccessibleStates.Focused : WF.AccessibleStates.None);
        public override SD.Rectangle Bounds => owner.RectangleToScreen(Button.Bounds);
        public override WF.AccessibleObject? Parent => owner.AccessibilityObject;

        public override void DoDefaultAction()
        {
            if (owner.IsDisposed)
                return;
            if (owner.InvokeRequired)
                owner.BeginInvoke(Button.Action);
            else
                Button.Action();
        }
    }
}
