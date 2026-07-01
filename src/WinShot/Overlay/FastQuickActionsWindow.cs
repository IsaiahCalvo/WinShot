using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Overlay;

public sealed class FastQuickActionsWindow : WF.Form
{
    // CleanShot X-style ONE-card overlay: the captured thumbnail floats bottom-right. On
    // hover the thumbnail blurs and the action buttons (Pin/Close/Copy/Save/Edit/Background)
    // fade in on top; on mouse-out the clean thumbnail returns.
    private const int Margin = 12;             // transparent breathing room around the card
    private const int CardPad = 12;            // inset for the corner icon buttons
    private const int IconButtonSize = 30;     // the four corner icon buttons
    private const int PillHeight = 34;         // the cream Copy / Save pills
    private const int PillGap = 8;
    private const int MinCardWidth = 232;      // keep room for corners + centered pills
    private const int MinCardHeight = 152;
    private const int MaxCardWidth = 340;
    private const int MaxCardHeight = 200;
    private const int CardCornerRadius = 14;
    private const int PillCornerRadius = 9;
    private const int IconCornerRadius = 8;
    private const int WmNclbuttondown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    private static readonly SD.Color CardFill = ThemePalette.WindowBg;                       // backing behind the thumbnail
    private static readonly SD.Color Border = ThemePalette.Border;
    private static readonly SD.Color BorderStrong = ThemePalette.BorderStrong;
    private static readonly SD.Color HoverScrim = SD.Color.FromArgb(96, 0, 0, 0);            // darkens the blur so cream pops
    private static readonly SD.Color Cream = SD.Color.FromArgb(0xEC, 0xEA, 0xE3);            // the cream button face
    private static readonly SD.Color CreamHover = SD.Color.FromArgb(0xF6, 0xF4, 0xEE);
    private static readonly SD.Color CreamPressed = SD.Color.FromArgb(0xDA, 0xD7, 0xCD);
    private static readonly SD.Color CreamText = SD.Color.FromArgb(0x22, 0x22, 0x24);        // dark glyph/label on cream
    private static readonly SD.Font GlyphFont = ThemePalette.IconFont(11f);
    private static readonly SD.Font CloseGlyphFont = ThemePalette.IconFont(9f);
    private static readonly SD.Font PillFont = ThemePalette.UiFont(11.5f, SD.FontStyle.Bold);
    private static readonly List<FastQuickActionsWindow> OpenWindows = new();
    private static readonly Stack<string> RecentlyClosed = new();

    private readonly SD.Bitmap _image;
    private readonly SettingsService _settings;
    private readonly Task? _releaseAfterTask;
    private readonly bool _requestMemoryCleanupOnClose;
    private readonly bool _loadPreview;
    private readonly WF.ToolTip _toolTip = new() { InitialDelay = 300, ReshowDelay = 100 };
    private readonly List<ActionButton> _buttons = new();
    private SD.Rectangle _cardRect;
    private SD.Rectangle _thumbRect;   // the fitted thumbnail centered inside the card
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
        MouseEnter += (_, _) => SetHovering(true);
        MouseLeave += (_, _) => SetHovering(false);
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
        QueueRegionUpdate();
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
            using var bitmap = new SD.Bitmap(MaxCardWidth, MaxCardHeight, PixelFormat.Format32bppPArgb);
            using (var g = SD.Graphics.FromImage(bitmap))
                g.Clear(CardFill);
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
                PixelFormat.Format32bppPArgb);
            window.DrawToBitmap(render, new SD.Rectangle(0, 0, render.Width, render.Height));
            WF.Application.DoEvents();
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Fast quick actions prewarm failed", ex);
        }
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

    private void SetHovering(bool hovering)
    {
        if (_hovering == hovering) return;
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

        // The card: rounded dark backing + the thumbnail (sharp when idle, blurred on hover).
        using (var fill = new SD.SolidBrush(CardFill))
        using (var cardPath = GdiPaths.RoundedRect(_cardRect, CardCornerRadius))
            g.FillPath(fill, cardPath);

        DrawThumbnail(g, _hovering);

        if (_hovering)
        {
            using (var scrim = new SD.SolidBrush(HoverScrim))
            using (var cardPath = GdiPaths.RoundedRect(_cardRect, CardCornerRadius))
                g.FillPath(scrim, cardPath);

            for (int i = 0; i < _buttons.Count; i++)
                DrawButton(g, _buttons[i], i == _hoverButton, i == _pressedButton);
        }

        using var borderPath = GdiPaths.RoundedRect(InsetForBorder(_cardRect), CardCornerRadius);
        using var pen = new SD.Pen(BorderStrong, 1);
        g.DrawPath(pen, borderPath);
    }

    private static SD.Rectangle InsetForBorder(SD.Rectangle r)
        => new(r.X, r.Y, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preview?.Dispose();
            _blurredPreview?.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildLayout(SD.Bitmap image)
    {
        var size = CaptureService.GetBitmapSize(image);
        double scale = Math.Min(1.0, Math.Min(MaxCardWidth / (double)size.Width, MaxCardHeight / (double)size.Height));
        int thumbWidth = Math.Max(1, (int)Math.Round(size.Width * scale));
        int thumbHeight = Math.Max(1, (int)Math.Round(size.Height * scale));

        int cardWidth = Math.Clamp(thumbWidth, MinCardWidth, MaxCardWidth);
        int cardHeight = Math.Clamp(thumbHeight, MinCardHeight, MaxCardHeight);

        ClientSize = new SD.Size(cardWidth + Margin * 2, cardHeight + Margin * 2);
        _cardRect = new SD.Rectangle(Margin, Margin, cardWidth, cardHeight);

        // Thumbnail fitted (contained) inside the card, centered.
        double fit = Math.Min(cardWidth / (double)thumbWidth, cardHeight / (double)thumbHeight);
        int fw = Math.Max(1, (int)Math.Round(thumbWidth * fit));
        int fh = Math.Max(1, (int)Math.Round(thumbHeight * fit));
        _thumbRect = new SD.Rectangle(
            _cardRect.X + (cardWidth - fw) / 2,
            _cardRect.Y + (cardHeight - fh) / 2,
            fw, fh);

        BuildButtons();
    }

    private void BuildButtons()
    {
        _buttons.Clear();
        int left = _cardRect.Left + CardPad;
        int right = _cardRect.Right - CardPad - IconButtonSize;
        int top = _cardRect.Top + CardPad;
        int bottom = _cardRect.Bottom - CardPad - IconButtonSize;

        // Four corner icon buttons.
        AddIconButton("", "Pin (P)", left, top, () => PinRequested?.Invoke(this));
        AddIconButton("", "Close (Esc)", right, top, Close);
        AddIconButton("", "Edit (E)", left, bottom, () => EditRequested?.Invoke(this));
        AddIconButton("", "Background (B)", right, bottom, () => BackgroundRequested?.Invoke(this));

        // Two stacked cream pills (Copy / Save) centered between the corner columns.
        int pillLeft = left + IconButtonSize + 10;
        int pillRight = right - 10;
        int pillWidth = Math.Max(96, pillRight - pillLeft);
        int pillX = _cardRect.Left + (_cardRect.Width - pillWidth) / 2;
        int block = PillHeight * 2 + PillGap;
        int pillTop = _cardRect.Top + (_cardRect.Height - block) / 2;

        AddPillButton("Copy", "Copy (C)", pillX, pillTop, pillWidth, CopyAsync);
        AddPillButton("Save", "Save (S)", pillX, pillTop + PillHeight + PillGap, pillWidth, SaveAsync);
    }

    private void AddIconButton(string glyph, string tip, int x, int y, Action action)
        => _buttons.Add(new ActionButton(
            glyph,
            tip,
            new SD.Rectangle(x, y, IconButtonSize, IconButtonSize),
            action,
            tip.StartsWith("Close", StringComparison.Ordinal) ? CloseGlyphFont : GlyphFont,
            ActionButtonShape.IconSquare,
            IconCornerRadius));

    private void AddPillButton(string label, string tip, int x, int y, int width, Action action)
        => _buttons.Add(new ActionButton(
            label,
            tip,
            new SD.Rectangle(x, y, width, PillHeight),
            action,
            PillFont,
            ActionButtonShape.Pill,
            PillCornerRadius)
        {
            Label = label,
        });

    private void DrawThumbnail(SD.Graphics g, bool blurred)
    {
        SD.Bitmap? bmp = blurred ? (_blurredPreview ?? _preview) : _preview;
        if (bmp is null)
            return;

        using var clip = GdiPaths.RoundedRect(_cardRect, CardCornerRadius);
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

        using (var path = GdiPaths.RoundedRect(button.Bounds, button.CornerRadius))
        {
            using var fill = new SD.SolidBrush(face);
            g.FillPath(fill, path);
            using var pen = new SD.Pen(SD.Color.FromArgb(0x18, 0x00, 0x00, 0x00), 1);
            g.DrawPath(pen, path);
        }

        string text = button.Shape == ActionButtonShape.Pill && button.Label is not null ? button.Label : button.Glyph;
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(g, text, button.Font, button.Bounds, CreamText, flags);
    }

    private void SetPressed(int index)
    {
        if (_pressedButton == index) return;
        int old = _pressedButton;
        _pressedButton = index;
        if (old >= 0 && old < _buttons.Count) Invalidate(_buttons[old].Bounds);
        if (index >= 0) Invalidate(_buttons[index].Bounds);
    }

    private void PositionBottomRight()
    {
        // Pop on the monitor the user just acted on (under the cursor).
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
            using var cardPath = GdiPaths.RoundedRect(_cardRect, CardCornerRadius);
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

    private async void CopyAsync()
    {
        try
        {
            _copyTask = CaptureService.CopyToClipboardAsync(_image);
            await _copyTask;
            if (_closed || IsDisposed) return;

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

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private enum ActionButtonShape
    {
        IconSquare,
        Pill,
    }

    private sealed class ActionButton(
        string glyph,
        string tip,
        SD.Rectangle bounds,
        Action action,
        SD.Font font,
        ActionButtonShape shape = ActionButtonShape.IconSquare,
        int cornerRadius = IconCornerRadius)
    {
        public string Glyph { get; set; } = glyph;
        public string Tip { get; } = tip;
        public SD.Rectangle Bounds { get; } = bounds;
        public Action Action { get; } = action;
        public SD.Font Font { get; } = font;
        public ActionButtonShape Shape { get; } = shape;
        public int CornerRadius { get; } = cornerRadius;
        public string? Label { get; set; }
    }
}
