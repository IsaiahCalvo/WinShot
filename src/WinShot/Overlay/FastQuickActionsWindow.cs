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
    // CleanShot X-style two-card layout: a rounded thumbnail card floats on top,
    // a separate rounded action panel sits below it with a transparent gap between.
    private const int Margin = 14;              // transparent breathing room around both cards
    private const int CardGap = 12;             // vertical gap between thumbnail card and panel
    private const int PanelPad = 14;            // inner padding of the action panel
    private const int IconButtonSize = 30;      // the four cream corner icon buttons
    private const int OcrButtonSize = 26;       // the slim cream OCR control on the left edge
    private const int PillHeight = 36;          // the cream Copy / Save pills
    private const int PillGap = 10;             // vertical gap between the two pills
    private const int PillSideInset = 6;        // keep pills clear of the corner buttons
    private const int MaxThumbWidth = 320;
    private const int MaxThumbHeight = 180;
    private const int CardCornerRadius = 14;
    private const int PillCornerRadius = 9;
    private const int IconCornerRadius = 8;
    private const int WmNclbuttondown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    // Most chrome flows from the shared palette (mirrors Theme.xaml). The signature
    // CleanShot look is the light "cream" buttons over a translucent dark panel, so the
    // panel/cream tones are defined here on top of the palette.
    private static readonly SD.Color PanelFill = SD.Color.FromArgb(0xF2, 0x26, 0x26, 0x28);   // translucent dark panel
    private static readonly SD.Color CardFill = ThemePalette.ToolbarBg;                       // opaque thumbnail backing
    private static readonly SD.Color Border = ThemePalette.Border;
    private static readonly SD.Color BorderStrong = ThemePalette.BorderStrong;
    private static readonly SD.Color Cream = SD.Color.FromArgb(0xEC, 0xEA, 0xE3);             // the cream button face
    private static readonly SD.Color CreamHover = SD.Color.FromArgb(0xF6, 0xF4, 0xEE);
    private static readonly SD.Color CreamPressed = SD.Color.FromArgb(0xDA, 0xD7, 0xCD);
    private static readonly SD.Color CreamText = SD.Color.FromArgb(0x22, 0x22, 0x24);         // dark glyph/text on cream
    private static readonly SD.Font GlyphFont = ThemePalette.IconFont(11f);
    private static readonly SD.Font OcrGlyphFont = ThemePalette.IconFont(9f);
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
    private SD.Rectangle _thumbRect;
    private SD.Rectangle _panelRect;
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
        BackColor = ThemePalette.WindowBg;
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
        // Two separate floating cards with a transparent gap can't use the single-window
        // DWM rounded-corner trick — we always clip the window to the union of both cards
        // so the gap (and everything outside the cards) is genuinely transparent.
        _useRegionCorners = true;
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
                g.Clear(CardFill);
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
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(SD.Color.Transparent);

        DrawThumbnailCard(g);
        DrawPanel(g);

        for (int i = 0; i < _buttons.Count; i++)
            DrawButton(g, _buttons[i], i == _hoverButton, i == _pressedButton);
    }

    private void DrawThumbnailCard(SD.Graphics g)
    {
        // Opaque rounded card backing the thumbnail, with a soft hairline border.
        using (var path = RoundedRect(_thumbRect, CardCornerRadius))
        using (var fill = new SD.SolidBrush(CardFill))
            g.FillPath(fill, path);

        DrawThumbnail(g);

        using var borderPath = RoundedRect(InsetForBorder(_thumbRect), CardCornerRadius);
        using var pen = new SD.Pen(BorderStrong, 1);
        g.DrawPath(pen, borderPath);
    }

    private void DrawPanel(SD.Graphics g)
    {
        // Translucent dark rounded panel that hosts the cream controls.
        using (var path = RoundedRect(_panelRect, CardCornerRadius))
        using (var fill = new SD.SolidBrush(PanelFill))
            g.FillPath(fill, path);

        using var borderPath = RoundedRect(InsetForBorder(_panelRect), CardCornerRadius);
        using var pen = new SD.Pen(Border, 1);
        g.DrawPath(pen, borderPath);
    }

    private static SD.Rectangle InsetForBorder(SD.Rectangle r)
        => new(r.X, r.Y, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1));

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

        // Panel is wide enough for two corner icon buttons + breathing room and the pills,
        // and at least as wide as the thumbnail card so the two cards align.
        int minPanelWidth = IconButtonSize * 2 + PanelPad * 2 + 120;   // pills need room between the corners
        int panelWidth = Math.Max(thumbWidth, minPanelWidth);

        // The four corner icon buttons frame the two stacked pills in the centre, all sharing
        // one vertical band. The left column also has to fit the OCR control between the Pin
        // and Edit corners, so the band must clear: Pin + gap + OCR + gap + Edit.
        int pillsBlockHeight = PillHeight * 2 + PillGap;
        int leftColumnHeight = IconButtonSize * 2 + OcrButtonSize + CardGap * 2;
        int panelHeight = PanelPad * 2 + Math.Max(pillsBlockHeight, leftColumnHeight);

        int contentWidth = Math.Max(thumbWidth, panelWidth);

        ClientSize = new SD.Size(
            contentWidth + Margin * 2,
            thumbHeight + CardGap + panelHeight + Margin * 2);

        _thumbRect = new SD.Rectangle(
            (ClientSize.Width - thumbWidth) / 2,
            Margin,
            thumbWidth,
            thumbHeight);

        _panelRect = new SD.Rectangle(
            (ClientSize.Width - panelWidth) / 2,
            _thumbRect.Bottom + CardGap,
            panelWidth,
            panelHeight);

        BuildPanelButtons();
    }

    private void BuildPanelButtons()
    {
        int left = _panelRect.Left + PanelPad;
        int right = _panelRect.Right - PanelPad - IconButtonSize;
        int top = _panelRect.Top + PanelPad;
        int bottom = _panelRect.Bottom - PanelPad - IconButtonSize;

        // Four cream corner icon buttons (Pin / Close / Edit / Background).
        AddIconButton("\uE718", "Pin (P)", left, top, () => PinRequested?.Invoke(this));
        AddIconButton("\uE8BB", "Close (Esc)", right, top, Close);
        AddIconButton("\uE70F", "Edit (E)", left, bottom, () => EditRequested?.Invoke(this));
        AddIconButton("\uEB9F", "Background (B)", right, bottom, () => BackgroundRequested?.Invoke(this));

        // Two stacked cream pills (Copy / Save) centred between the corners.
        int pillsBlockHeight = PillHeight * 2 + PillGap;
        int pillLeft = left + IconButtonSize + PillSideInset;
        int pillRight = right - PillSideInset;
        int pillWidth = Math.Max(64, pillRight - pillLeft);
        int pillX = _panelRect.Left + (_panelRect.Width - pillWidth) / 2;
        int pillTop = _panelRect.Top + (_panelRect.Height - pillsBlockHeight) / 2;

        AddPillButton("\uE8C8", "Copy", "Copy (C)", pillX, pillTop, pillWidth, CopyAsync);
        AddPillButton("\uE74E", "Save", "Save (S)", pillX, pillTop + PillHeight + PillGap, pillWidth, SaveAsync);

        // OCR has no CleanShot corner equivalent, so it lives as a slim cream control on the
        // left edge, vertically centred between the Pin and Edit corner buttons (and stays on
        // the keyboard "O" shortcut). This strip is clear of the centred Copy/Save pills.
        int ocrX = left + (IconButtonSize - OcrButtonSize) / 2;
        int ocrY = _panelRect.Top + (_panelRect.Height - OcrButtonSize) / 2;
        _buttons.Add(new ActionButton(
            "\uE721",
            "OCR (O)",
            new SD.Rectangle(ocrX, ocrY, OcrButtonSize, OcrButtonSize),
            () => OcrRequested?.Invoke(this),
            OcrGlyphFont,
            ActionButtonShape.IconSquare,
            IconCornerRadius));
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

    private void AddPillButton(string glyph, string label, string tip, int x, int y, int width, Action action)
        => _buttons.Add(new ActionButton(
            glyph,
            tip,
            new SD.Rectangle(x, y, width, PillHeight),
            action,
            PillFont,
            ActionButtonShape.Pill,
            PillCornerRadius)
        {
            Label = label,
        });

    private void DrawThumbnail(SD.Graphics g)
    {
        if (_preview is null)
            return;

        // Clip the preview to the card's rounded corners so the image follows the card shape.
        using var clip = RoundedRect(_thumbRect, CardCornerRadius);
        var oldClip = g.Clip;
        var oldInterp = g.InterpolationMode;
        var oldOffset = g.PixelOffsetMode;
        try
        {
            g.SetClip(clip, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_preview, _thumbRect);
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
        // Every control is a light "cream" rounded shape with a dark glyph/label — the
        // signature CleanShot look. Hover brightens the cream, press darkens it slightly.
        SD.Color face = pressed ? CreamPressed : hot ? CreamHover : Cream;

        using (var path = RoundedRect(button.Bounds, button.CornerRadius))
        {
            using var fill = new SD.SolidBrush(face);
            g.FillPath(fill, path);

            using var pen = new SD.Pen(SD.Color.FromArgb(0x14, 0x00, 0x00, 0x00), 1);
            g.DrawPath(pen, path);
        }

        if (button.Shape == ActionButtonShape.Pill && button.Label is not null)
            DrawCenteredText(g, button.Label, button.Font, button.Bounds, CreamText);
        else
            TextRendererDrawGlyph(g, button.Glyph, button.Font, button.Bounds, CreamText);
    }

    private static void DrawCenteredText(SD.Graphics g, string text, SD.Font font, SD.Rectangle bounds, SD.Color color)
    {
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(g, text, font, bounds, color, flags);
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

            // Clip the window to the union of the two cards so the gap between them — and the
            // margin around them — is genuinely transparent (and click-through).
            using var thumbPath = RoundedRect(_thumbRect, CardCornerRadius);
            using var panelPath = RoundedRect(_panelRect, CardCornerRadius);
            var region = new SD.Region(thumbPath);
            region.Union(panelPath);

            Region?.Dispose();
            Region = region;
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

            // Copy is a pill, so confirm by swapping its label text rather than a glyph.
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
