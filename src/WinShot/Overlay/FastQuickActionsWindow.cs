using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WinShot.Core;
using WinShot.History;
using SD = System.Drawing;
using WF = System.Windows.Forms;
using W = System.Windows;

namespace WinShot.Overlay;

public sealed class FastQuickActionsWindow : WF.Form
{
    // The idle surface is only the capture thumbnail. Hover reveals four restrained corner
    // actions and one centered primary Copy control; OCR and Background stay in the menu.
    private const int CsDropShadow = 0x00020000;
    private const int WmNclbuttondown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    private static readonly List<FastQuickActionsWindow> OpenWindows = new();
    private static readonly Stack<string> RecentlyClosed = new();

    // Not readonly: the menu's rotate/flip/resize rows replace the card's pixels in place.
    private SD.Bitmap _image;
    private readonly SettingsService _settings;
    // Media-file mode: the card fronts a finished recording (mp4/gif) instead of a
    // fresh bitmap. _image is just the poster frame; copy/save/drag act on the file.
    private readonly string? _mediaFilePath;
    private readonly bool _mediaCanEdit;
    private readonly Task? _releaseAfterTask;
    private readonly bool _requestMemoryCleanupOnClose;
    private readonly bool _loadPreview;
    private readonly QuickAccessOverlayVisuals _visuals;
    private readonly WF.Timer _tooltipTimer = new() { Interval = 300 };
    private readonly QuickActionsContextMenu _overflowMenu = new();
    private readonly List<ActionButton> _buttons = new();
    private QuickAccessTooltipWindow? _tooltipWindow;
    private WF.Timer? _exitMotionTimer;
    private WF.Timer? _stackMotionTimer;
    private IDisposable? _exitMotionClock;
    private IDisposable? _stackMotionClock;
    private WF.Timer? _autoCloseTimer;
    private long _autoCloseDueTick;
    private int _autoCloseRemainingMs;
    private SD.Rectangle _cardRect;
    private SD.Rectangle _thumbRect;   // fixed-ratio, center-cropped capture preview
    private int _cornerRadius;
    private int _dpi = 96;
    private readonly object _previewSync = new();
    private SD.Bitmap? _preview;
    private SD.Bitmap? _blurredPreview;
    private bool _previewClosed;
    private Task? _previewTask;
    private Task? _copyTask;
    private Task<string>? _dragFileTask;
    private string? _tempDragPath;
    private string? _historyPath;
    private int _imageVersion;
    private bool _hovering;
    private int _hoverButton = -1;
    private int _pressedButton = -1;
    private int _focusedButton = -1;
    private bool _dragArmed;
    private bool _closed;
    private bool _exitMotionStarted;
    private bool _allowImmediateClose;
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
        : this(image, settings, historyPath, historyPathTask, releaseAfterTask: null, themeOverride: null)
    {
    }

    internal static FastQuickActionsWindow CreateForTheme(
        SD.Bitmap image,
        SettingsService settings,
        QuickAccessOverlayTheme theme)
        => new(
            image,
            settings,
            historyPath: null,
            historyPathTask: null,
            releaseAfterTask: null,
            requestMemoryCleanupOnClose: false,
            loadPreview: true,
            themeOverride: theme);

    /// <summary>
    /// The screenshot card, fronting a finished recording file instead: same
    /// chrome, but Copy puts the file on the clipboard, Save copies the file,
    /// dragging drags the file, and Edit is only offered when a video editor
    /// applies. <paramref name="thumbnail"/> ownership transfers to the window.
    /// </summary>
    public static FastQuickActionsWindow CreateForMediaFile(
        string filePath,
        SD.Bitmap thumbnail,
        SettingsService settings,
        bool canEdit)
        => new(
            thumbnail,
            settings,
            historyPath: filePath,
            historyPathTask: null,
            releaseAfterTask: null,
            themeOverride: null,
            mediaFilePath: filePath,
            mediaCanEdit: canEdit);

    private FastQuickActionsWindow(
        SD.Bitmap image,
        SettingsService settings,
        string? historyPath,
        Task<string?>? historyPathTask,
        Task? releaseAfterTask,
        bool requestMemoryCleanupOnClose = true,
        bool loadPreview = true,
        QuickAccessOverlayTheme? themeOverride = null,
        string? mediaFilePath = null,
        bool mediaCanEdit = true)
    {
        _image = image;
        _settings = settings;
        _mediaFilePath = mediaFilePath;
        _mediaCanEdit = mediaCanEdit;
        _historyPath = historyPath;
        _releaseAfterTask = releaseAfterTask;
        _requestMemoryCleanupOnClose = requestMemoryCleanupOnClose;
        _loadPreview = loadPreview;
        _visuals = QuickAccessOverlayThemePalette.For(themeOverride ?? QuickAccessOverlayThemePalette.Current);

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = _visuals.CardFill;
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
        _tooltipTimer.Tick += (_, _) => ShowActionTooltip();

        MouseDown += OnOverlayMouseDown;
        MouseMove += OnOverlayMouseMove;
        MouseUp += OnOverlayMouseUp;
        MouseEnter += (_, _) => SetHovering(true);
        MouseLeave += (_, _) => SetHovering(false);
        KeyDown += OnOverlayKeyDown;
        Shown += (_, _) => QueuePreviewLoad();
        FormClosed += (_, _) =>
        {
            _closed = true;
            ClosePreviewPipeline();
            OpenWindows.Remove(this);
            StopAutoCloseTimer();
            HideActionTooltip();
            // Restore-last reopens captures as images, so a media file never qualifies.
            if (_historyPath is not null && _mediaFilePath is null)
                PushRecentlyClosed(_historyPath);
            DisposeImageWhenUnused();
            ReflowOpenWindowsAnimated();
        };
        OpenWindows.Add(this);

        if (historyPathTask is not null)
            _ = WatchHistoryPathAsync(historyPathTask);

        StartAutoCloseTimer();
    }

    protected override bool ShowWithoutActivation => true;


    protected override void OnFormClosing(WF.FormClosingEventArgs e)
    {
        bool systemClose = e.CloseReason is
            WF.CloseReason.ApplicationExitCall or
            WF.CloseReason.TaskManagerClosing or
            WF.CloseReason.WindowsShutDown;
        if (!_allowImmediateClose &&
            !systemClose &&
            W.SystemParameters.ClientAreaAnimation &&
            Visible &&
            IsHandleCreated &&
            !IsDisposed)
        {
            e.Cancel = true;
            BeginExitMotion();
        }

        base.OnFormClosing(e);
    }

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
        => new(image, settings, historyPath: null, historyPathTask, releaseAfterTask, themeOverride: null);

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
        if (!_loadPreview || _closed || IsDisposed || PreviewPipelineClosed())
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!_closed && !IsDisposed && !PreviewPipelineClosed())
                    _previewTask ??= LoadPreviewAsync();
            }));
        }
        catch (InvalidOperationException) when (_closed || IsDisposed || PreviewPipelineClosed())
        {
            // A Shown/Visible callback raced the native handle being torn down.
        }
    }

    private void SetHovering(bool hovering)
    {
        if (_hovering == hovering) return;
        if (hovering)
        {
            foreach (var window in OpenWindows.ToArray().Where(w => !ReferenceEquals(w, this)))
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
        using (var fill = new SD.SolidBrush(_visuals.CardFill))
        using (var cardPath = GdiPaths.RoundedRect(_cardRect, _cornerRadius))
            g.FillPath(fill, cardPath);

        DrawThumbnail(g, _hovering);

        if (_hovering)
        {
            using (var scrim = new SD.SolidBrush(_visuals.HoverTint))
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
            bool removed = OpenWindows.Remove(this);
            _closed = true;
            ClosePreviewPipeline();
            StopExitMotionTimer();
            StopStackMotionTimer();
            StopAutoCloseTimer();
            HideActionTooltip();
            _tooltipTimer.Dispose();
            _overflowMenu.Dispose();
            foreach (var button in _buttons)
                button.Dispose();
            if (removed)
                ReflowOpenWindowsAnimated();
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
        // Preserve the user's verified action placement while using the selected HTML design.
        AddIconButton("quick-access-pin.svg", "Pin to the screen", "Pin", "Pin", left, top, layout.IconSize, () => PinRequested?.Invoke(this));
        AddIconButton("quick-access-close.svg", "Close (Ctrl+W)", "Close", "Close", right, top, layout.IconSize, Close);
        if (_mediaFilePath is null)
            AddIconButton("quick-access-edit.svg", "Open Annotate tool (Ctrl+E)", "Annotate", "Edit", left, bottom, layout.IconSize, () => EditRequested?.Invoke(this));
        else if (_mediaCanEdit)
            AddIconButton("quick-access-edit.svg", "Open video editor (Ctrl+E)", "Edit video", "Edit", left, bottom, layout.IconSize, () => EditRequested?.Invoke(this));
        AddIconButton("quick-access-save.svg", "Save (Ctrl+S)", "Save", "Save", right, bottom, layout.IconSize, SaveFromInput);

        int pillX = _cardRect.Left + (_cardRect.Width - layout.PillWidth) / 2;
        int pillTop = _cardRect.Top + (_cardRect.Height - layout.PillHeight) / 2;

        AddCopyButton("Copy", "Copy (Ctrl+C)", pillX, pillTop, layout.PillWidth, layout.PillHeight, () => CopyAsync(keepOpen: IsAltDown()));
        _focusedButton = Math.Clamp(_focusedButton, -1, _buttons.Count - 1);
    }

    private void AddIconButton(
        string assetName,
        string tip,
        string name,
        string tooltipText,
        int x,
        int y,
        int size,
        Action action)
        => _buttons.Add(new ActionButton(
            tip,
            name,
            tooltipText,
            new SD.Rectangle(x, y, size, size),
            action,
            ActionButtonShape.Circle,
            size / 2,
            CreateSvgIcon(assetName, Math.Max(14, (int)Math.Round(size * 0.56)), _visuals.Glyph)));

    private void AddCopyButton(string label, string tip, int x, int y, int width, int height, Action action)
        => _buttons.Add(new ActionButton(
            tip,
            "Copy",
            "Copy",
            new SD.Rectangle(x, y, width, height),
            action,
            ActionButtonShape.Pill,
            height / 2,
            CreateSvgIcon("quick-access-copy.svg", Math.Max(14, (int)Math.Round(height * 0.52)), _visuals.Glyph))
        {
            Label = label,
            // 25px in the 512px HTML reference scales to roughly 9px on this 192px card.
            LabelFont = ThemePalette.UiFont(7f),
        });

    private void DrawThumbnail(SD.Graphics g, bool blurred)
    {
        lock (_previewSync)
        {
            SD.Bitmap? bmp = blurred ? (_blurredPreview ?? _preview) : _preview;
            if (_previewClosed || bmp is null)
                return;

            using var clip = GdiPaths.RoundedRect(_cardRect, _cornerRadius);
            // Save/Restore instead of get/set Clip: the Clip getter allocates a NEW native
            // Region per call, which leaked one per paint in this hot path.
            GraphicsState state = g.Save();
            try
            {
                g.SetClip(clip, CombineMode.Intersect);
                g.InterpolationMode = blurred ? InterpolationMode.HighQualityBilinear : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(bmp, _thumbRect);
            }
            finally
            {
                g.Restore(state);
            }
        }
    }

    private void DrawButton(SD.Graphics g, ActionButton button, bool hot, bool pressed)
    {
        SD.Color face = pressed ? _visuals.ControlPressed : hot ? _visuals.ControlHover : _visuals.ControlFace;

        var shadowBounds = button.Bounds;
        shadowBounds.Offset(0, Math.Max(1, button.Bounds.Height / 18));
        using (var shadowPath = CreateButtonPath(shadowBounds, button.Shape, button.CornerRadius))
        using (var shadow = new SD.SolidBrush(_visuals.ControlShadow))
            g.FillPath(shadow, shadowPath);

        using (var path = CreateButtonPath(button.Bounds, button.Shape, button.CornerRadius))
        {
            using var fill = new SD.SolidBrush(face);
            g.FillPath(fill, path);
            using var pen = new SD.Pen(_visuals.ControlBorder, 1);
            g.DrawPath(pen, path);
        }

        if (button.Label is not null && button.LabelFont is not null)
        {
            var flags = WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding;
            SD.Size labelSize = WF.TextRenderer.MeasureText(g, button.Label, button.LabelFont, SD.Size.Empty, flags);
            int iconWidth = button.Icon?.Width ?? 0;
            int gap = button.Icon is null ? 0 : Math.Max(4, button.Bounds.Height / 7);
            int contentWidth = iconWidth + gap + labelSize.Width;
            int contentX = button.Bounds.Left + (button.Bounds.Width - contentWidth) / 2;

            if (button.Icon is not null)
            {
                int iconY = button.Bounds.Top + (button.Bounds.Height - button.Icon.Height) / 2;
                g.DrawImageUnscaled(button.Icon, contentX, iconY);
            }

            var labelBounds = new SD.Rectangle(
                contentX + iconWidth + gap,
                button.Bounds.Top,
                labelSize.Width,
                button.Bounds.Height);
            WF.TextRenderer.DrawText(
                g,
                button.Label,
                button.LabelFont,
                labelBounds,
                _visuals.Glyph,
                flags | WF.TextFormatFlags.VerticalCenter);
            return;
        }

        if (button.Icon is not null)
        {
            int x = button.Bounds.Left + (button.Bounds.Width - button.Icon.Width) / 2;
            int y = button.Bounds.Top + (button.Bounds.Height - button.Icon.Height) / 2;
            g.DrawImageUnscaled(button.Icon, x, y);
        }
    }

    private static GraphicsPath CreateButtonPath(SD.Rectangle bounds, ActionButtonShape shape, int cornerRadius)
    {
        if (shape == ActionButtonShape.Pill)
            return GdiPaths.RoundedRect(bounds, cornerRadius);

        var path = new GraphicsPath();
        path.AddEllipse(bounds);
        return path;
    }

    // Icon rasterization + process-wide cache moved to WinShot.Core.SvgIcons so the pin
    // toolbar shares the exact same assets and rendering; behavior here is unchanged.
    private static SD.Bitmap? CreateSvgIcon(string assetName, int pixelSize, SD.Color color)
        => SvgIcons.Get(assetName, pixelSize, color);

    private void DrawFocus(SD.Graphics g, ActionButton button)
    {
        var bounds = SD.Rectangle.Inflate(button.Bounds, -2, -2);
        using var pen = new SD.Pen(_visuals.FocusRing, 1) { DashStyle = DashStyle.Dot };
        using var path = CreateButtonPath(bounds, button.Shape, Math.Max(1, button.CornerRadius - 2));
        g.DrawPath(pen, path);
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
        // Snapshot: a window closing mid-enumeration removes itself from OpenWindows.
        int offset = OpenWindows.ToArray()
            .Where(w => !ReferenceEquals(w, this) && w.Visible && area.IntersectsWith(w.Bounds))
            .Sum(w => w.Height + 12);
        Location = QuickAccessOverlayLayout.Place(
            area,
            Size,
            _settings.Current.OverlayPosition,
            offset,
            _dpi);
    }

    private void BeginExitMotion()
    {
        if (_exitMotionStarted || _closed || IsDisposed)
            return;

        _exitMotionStarted = true;
        StopStackMotionTimer();
        StopAutoCloseTimer();
        HideActionTooltip();
        if (_overflowMenu.Visible)
            _overflowMenu.Close();

        SD.Point start = Location;
        SD.Rectangle workingArea = WF.Screen.FromRectangle(Bounds).WorkingArea;
        SD.Point target = QuickAccessOverlayMotion.ExitTarget(
            workingArea,
            Bounds,
            _settings.Current.OverlayPosition);
        double startingOpacity = Opacity;
        long started = Stopwatch.GetTimestamp();

        _exitMotionTimer = new WF.Timer { Interval = QuickAccessOverlayMotion.FrameIntervalMs };
        _exitMotionClock = WinShot.Core.Motion.Acquire();
        _exitMotionTimer.Tick += (_, _) =>
        {
            if (IsDisposed)
            {
                StopExitMotionTimer();
                return;
            }

            double progress = Math.Clamp(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds / QuickAccessOverlayMotion.ExitDurationMs,
                0d,
                1d);
            MoveWithoutActivation(QuickAccessOverlayMotion.Interpolate(
                start,
                target,
                QuickAccessOverlayMotion.EaseInOutSine(progress)));
            Opacity = Math.Max(0.02d, startingOpacity * QuickAccessOverlayMotion.ExitOpacity(progress));
            if (progress < 1d)
                return;

            StopExitMotionTimer();
            Hide();
            _allowImmediateClose = true;
            Close();
        };
        _exitMotionTimer.Start();
    }

    private static void ReflowOpenWindowsAnimated()
    {
        var offsets = new Dictionary<SD.Rectangle, int>();
        long started = Stopwatch.GetTimestamp();
        foreach (FastQuickActionsWindow window in OpenWindows.ToArray().Where(w =>
                     w.Visible && !w.IsDisposed && !w._exitMotionStarted))
        {
            SD.Rectangle workingArea = WF.Screen.FromRectangle(window.Bounds).WorkingArea;
            offsets.TryGetValue(workingArea, out int offset);
            SD.Point target = QuickAccessOverlayLayout.Place(
                workingArea,
                window.Size,
                window._settings.Current.OverlayPosition,
                offset,
                window._dpi);
            window.BeginStackMotion(target, started);
            offsets[workingArea] = offset + window.Height + 12;
        }
    }

    private void BeginStackMotion(SD.Point target, long started)
    {
        StopStackMotionTimer();
        if (_exitMotionStarted || _closed || IsDisposed || Location == target)
            return;
        if (!W.SystemParameters.ClientAreaAnimation)
        {
            Location = target;
            return;
        }

        SD.Point start = Location;
        _stackMotionTimer = new WF.Timer { Interval = QuickAccessOverlayMotion.FrameIntervalMs };
        _stackMotionClock = WinShot.Core.Motion.Acquire();
        _stackMotionTimer.Tick += (_, _) =>
        {
            if (_exitMotionStarted || _closed || IsDisposed)
            {
                StopStackMotionTimer();
                return;
            }

            double progress = Math.Clamp(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds / QuickAccessOverlayMotion.RestackDurationMs,
                0d,
                1d);
            MoveWithoutActivation(QuickAccessOverlayMotion.Interpolate(
                start,
                target,
                QuickAccessOverlayMotion.EaseOutCubic(progress)));
            if (progress >= 1d)
            {
                MoveWithoutActivation(target);
                StopStackMotionTimer();
            }
        };
        _stackMotionTimer.Start();
    }

    private void StopExitMotionTimer()
    {
        _exitMotionTimer?.Stop();
        _exitMotionTimer?.Dispose();
        _exitMotionTimer = null;
        _exitMotionClock?.Dispose();
        _exitMotionClock = null;
    }

    private void StopStackMotionTimer()
    {
        _stackMotionTimer?.Stop();
        _stackMotionTimer?.Dispose();
        _stackMotionTimer = null;
        _stackMotionClock?.Dispose();
        _stackMotionClock = null;
    }

    private void MoveWithoutActivation(SD.Point point)
    {
        const uint flags = 0x0001 | 0x0004 | 0x0010 | 0x0200; // NOSIZE, NOZORDER, NOACTIVATE, NOOWNERZORDER
        if (!IsHandleCreated ||
            !SetWindowPos(Handle, IntPtr.Zero, point.X, point.Y, 0, 0, flags))
        {
            Location = point;
        }
    }

    private void BuildOverflowMenu()
    {
        _overflowMenu.AccessibleName = "More capture actions";
        AddMenuRows(_overflowMenu, QuickActionsMenu.Rows(
            mediaFile: _mediaFilePath is not null,
            canEdit: _mediaCanEdit,
            canShare: TryIsShareSupported(),
            canBlip: TryIsBlipInstalled()));
        // The pre-show measurement can be a few pixels off; correct once the real size
        // is known, so the menu never ends up sitting over the thumbnail.
        _overflowMenu.Opened += (_, _) =>
        {
            SD.Point corrected = OverflowMenuOrigin(_overflowMenu.Size);
            if (_overflowMenu.Location != corrected)
                _overflowMenu.Location = corrected;
        };
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

    /// <summary>
    /// Opens the menu beside the card, on the card's own monitor. Letting WinForms place
    /// it from a card-relative point put it on the primary monitor when the card sat on a
    /// screen above this one, and over the thumbnail besides.
    /// </summary>
    private void ShowOverflowMenu()
    {
        SetHovering(true);
        _overflowMenu.Show(OverflowMenuOrigin(_overflowMenu.GetPreferredSize(SD.Size.Empty)),
            WF.ToolStripDropDownDirection.BelowRight);
    }

    private SD.Point OverflowMenuOrigin(SD.Size menuSize) =>
        QuickActionsMenu.MenuOrigin(
            RectangleToScreen(_cardRect),
            menuSize,
            WF.Screen.FromRectangle(RectangleToScreen(_cardRect)).WorkingArea);

    private static bool TryIsShareSupported()
    {
        try
        {
            return HistoryShareService.IsWindowsShareSupported();
        }
        catch (Exception ex)
        {
            // The share contract is unavailable on some Server SKUs; the row just drops out.
            Log.Error("Windows share support probe failed", ex);
            return false;
        }
    }

    private void AddMenuRows(WF.ToolStrip menu, IEnumerable<QuickActionsMenu.Row> rows)
    {
        foreach (var row in rows)
        {
            if (row.Id == QuickActionsMenu.Separator)
            {
                menu.Items.Add(new WF.ToolStripSeparator());
                continue;
            }

            string id = row.Id;
            var item = new QuickActionMenuItem(id, row.Text, () => RunMenuAction(id));
            if (row.Shortcut.Length > 0)
            {
                item.ShortcutKeyDisplayString = row.Shortcut;
                item.ShowShortcutKeys = true;
            }
            if (row.Children is { Count: > 0 } children)
            {
                // Its own QuickActionsContextMenu so the submenu gets the same rounded
                // chrome and row drawing as the menu it hangs off.
                var submenu = new QuickActionsContextMenu();
                AddMenuRows(submenu, children);
                item.DropDown = submenu;
            }
            menu.Items.Add(item);
        }
    }

    private static bool TryIsBlipInstalled()
    {
        try
        {
            return HistoryShareService.IsBlipInstalled();
        }
        catch (Exception ex)
        {
            Log.Error("Blip install probe failed", ex);
            return false;
        }
    }

    private void RunMenuAction(string id)
    {
        try
        {
            switch (id)
            {
                case QuickActionsMenu.Annotate:
                    EditRequested?.Invoke(this);
                    return;
                case QuickActionsMenu.Pin:
                    PinRequested?.Invoke(this);
                    return;
                case QuickActionsMenu.ExtractText:
                    OcrRequested?.Invoke(this);
                    return;
                case QuickActionsMenu.Background:
                    BackgroundRequested?.Invoke(this);
                    return;
                case QuickActionsMenu.Resize:
                    ResizeImage();
                    return;
                case QuickActionsMenu.Print:
                    QuickActionsMenu.PrintImage(this, _image);
                    return;
                case QuickActionsMenu.Save:
                    _ = SaveCoreAsync(askForDestination: false, closeAfterSave: true);
                    return;
                case QuickActionsMenu.SaveAs:
                    _ = SaveCoreAsync(askForDestination: true, closeAfterSave: true);
                    return;
                case QuickActionsMenu.Open:
                    OpenMediaFile();
                    return;
                case QuickActionsMenu.OpenWith:
                    _ = WithFileAsync(QuickActionsMenu.ShowOpenWith, closeAfter: true);
                    return;
                case QuickActionsMenu.ShowInFolder:
                    _ = WithFileAsync(QuickActionsMenu.ShowInExplorer, closeAfter: true);
                    return;
                case QuickActionsMenu.MoveToRecycleBin:
                    MoveToRecycleBin();
                    return;
                case QuickActionsMenu.ShareWindows:
                    _ = ShareAsync();
                    return;
                case QuickActionsMenu.ShareBlip:
                    _ = ShareWithBlipAsync();
                    return;
                case QuickActionsMenu.Close:
                    Close();
                    return;
                default:
                    if (QuickActionsMenu.TransformFor(id) is { } transform)
                        TransformImage(transform);
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Quick-actions menu action '{id}' failed", ex);
        }
    }

    /// <summary>
    /// Rotate/flip the card's bitmap in place. <see cref="SD.Image.RotateFlip"/> swaps
    /// the dimensions on a quarter turn, so only the preview has to be re-rendered.
    /// The card stays open, and the row keeps an undo button until the card closes.
    /// </summary>
    private async void TransformImage(SD.RotateFlipType transform)
    {
        if (!await WaitForRenderIdleAsync()) return;
        _image.RotateFlip(transform);
        OnImageChanged();
    }

    /// <summary>
    /// The thumbnail renders on a background thread reading <see cref="_image"/> directly,
    /// and GDI+ locks an image for the duration of an operation — editing it mid-render
    /// throws "Object is currently in use elsewhere". Every pixel edit waits the in-flight
    /// render out first. The wait is awaited rather than blocked: that render finishes by
    /// posting its bitmaps back to this thread, so blocking here would deadlock it.
    /// </summary>
    private async Task<bool> WaitForRenderIdleAsync()
    {
        while (_previewTask is { IsCompleted: false } pending)
        {
            await pending;
            if (_closed || IsDisposed) return false;
        }
        return !_closed && !IsDisposed;
    }

    private void ReplaceImage(SD.Bitmap replacement)
    {
        // Callers reach here only after WaitForRenderIdleAsync, so nothing is reading _image.
        SD.Bitmap previous = _image;
        _image = replacement;
        previous.Dispose();
        OnImageChanged();
    }


    /// <summary>
    /// Re-renders the thumbnail and drops every copy of the old pixels: the temp
    /// drag file, and the history file the card was written to on capture. Without
    /// this a drag-out or a Show in folder after a rotate hands back the old image.
    /// </summary>
    private void OnImageChanged()
    {
        _imageVersion++;
        _previewTask = LoadPreviewAsync();
        Invalidate();

        _dragFileTask = null;
        string? stale = _tempDragPath;
        _tempDragPath = null;
        string? history = _historyPath;
        var copy = CaptureService.CloneBitmap(_image);
        _ = Task.Run(() =>
        {
            using (copy)
            {
                try
                {
                    if (stale is not null && File.Exists(stale))
                        File.Delete(stale);
                    if (history is not null && File.Exists(history))
                        ImageSaver.Save(copy, history);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to rewrite the capture file after an image edit", ex);
                }
            }
        });
    }

    private async void ResizeImage()
    {
        using var dialog = new ResizeImageDialog(_image.Size);
        if (dialog.ShowDialog(this) != WF.DialogResult.OK || dialog.Result is not { } size)
            return;
        if (size == _image.Size)
            return;
        if (!await WaitForRenderIdleAsync()) return;
        ReplaceImage(QuickActionsMenu.Resized(_image, size.Width, size.Height));
    }

    /// <summary>Runs a shell action that needs a real file, saving one first if the capture has none.</summary>
    private async Task WithFileAsync(Action<string> action, bool closeAfter)
    {
        try
        {
            string path = _mediaFilePath ?? await EnsureDragFileAsync();
            action(path);
            if (closeAfter && !_closed && !IsDisposed)
                Close();
        }
        catch (Exception ex)
        {
            Log.Error("Quick-actions file action failed", ex);
        }
    }

    private async Task ShareWithBlipAsync()
    {
        try
        {
            string path = _mediaFilePath ?? await EnsureDragFileAsync();
            if (_closed || IsDisposed) return;
            switch (HistoryShareService.ShareWithBlip(new[] { path }))
            {
                case BlipShareResult.Opened:
                    Close();
                    return;
                case BlipShareResult.NeedsRestart:
                    ShowShareToast("Blip can't receive shares", "Restart Blip and try again.");
                    return;
                default:
                    ShowShareToast("Blip share failed", "The capture was not handed to Blip.");
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Blip share failed", ex);
        }
    }

    /// <summary>Reuses the OCR toast — it is the app's generic anchored dark notice.</summary>
    private void ShowShareToast(string title, string message)
    {
        try
        {
            SD.Rectangle card = RectangleToScreen(_cardRect);
            new Ocr.OcrToastWindow(title, message, new SD.Point(card.Left, card.Top), null).Show();
        }
        catch (Exception ex)
        {
            Log.Error("Share toast failed", ex);
        }
    }

    private async Task ShareAsync()
    {
        try
        {
            string path = _mediaFilePath ?? await EnsureDragFileAsync();
            if (_closed || IsDisposed) return;
            HistoryShareService.ShowWindowsShare(
                Handle,
                new[] { path },
                ex => Log.Error("Windows share failed", ex));
        }
        catch (Exception ex)
        {
            Log.Error("Share failed", ex);
        }
    }

    /// <summary>
    /// Trashes the capture's file and closes the card. A capture that was never
    /// written to disk has nothing to trash, so this is just a discard.
    /// </summary>
    private void MoveToRecycleBin()
    {
        string? path = _mediaFilePath ?? _historyPath;
        if (QuickActionsMenu.FileReady(path))
        {
            try
            {
                QuickActionsMenu.SendToRecycleBin(path!);
                _historyPath = _mediaFilePath is null ? null : _historyPath;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to recycle {path}", ex);
                return;
            }
        }
        _allowImmediateClose = true;
        Close();
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
        string action = QuickAccessOverlayLayout.NormalizeAutoCloseAction(_settings.Current.OverlayAutoCloseAction);
        if (_mediaFilePath is not null && action != "copy-close")
        {
            Close(); // the recording is already saved; auto-"save" would just duplicate it
            return;
        }

        switch (action)
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

            try
            {
                BeginInvoke(new Action(() =>
                {
                    _historyPath = path;
                    if (_closed)
                        PushRecentlyClosed(path);
                }));
            }
            catch (InvalidOperationException)
            {
                // The window was disposed between the check above and the BeginInvoke —
                // behave like the closed fast path so restore-last still finds the capture.
                PushRecentlyClosed(path);
            }
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

    private bool PreviewPipelineClosed()
    {
        lock (_previewSync)
            return _previewClosed;
    }

    private void ClosePreviewPipeline()
    {
        SD.Bitmap? preview;
        SD.Bitmap? blurred;
        lock (_previewSync)
        {
            _previewClosed = true;
            preview = _preview;
            blurred = _blurredPreview;
            _preview = null;
            _blurredPreview = null;
        }

        DisposePreviewPair(preview, blurred);
    }

    private void InstallPreviewBitmaps(SD.Bitmap preview, SD.Bitmap blurred)
    {
        SD.Bitmap? oldPreview = null;
        SD.Bitmap? oldBlurred = null;
        bool accepted;
        lock (_previewSync)
        {
            accepted = !_previewClosed && !_closed && !IsDisposed;
            if (accepted)
            {
                oldPreview = _preview;
                oldBlurred = _blurredPreview;
                _preview = preview;
                _blurredPreview = blurred;
            }
        }

        if (!accepted)
        {
            DisposePreviewPair(preview, blurred);
            return;
        }

        DisposePreviewPair(oldPreview, oldBlurred);
        if (!_closed && !IsDisposed)
            Invalidate();
    }

    private static void DisposePreviewPair(SD.Bitmap? preview, SD.Bitmap? blurred)
    {
        preview?.Dispose();
        if (!ReferenceEquals(blurred, preview))
            blurred?.Dispose();
    }

    private async Task LoadPreviewAsync()
    {
        SD.Bitmap? preview = null;
        SD.Bitmap? blurred = null;
        try
        {
            int w = _thumbRect.Width, h = _thumbRect.Height;
            // A rotate/flip/resize starts a fresh render; drop whatever the previous
            // one produces so it cannot repaint the card with the old pixels.
            int version = _imageVersion;
            SD.Bitmap source = _image;
            (preview, blurred) = await Task.Run(() =>
            {
                var p = CreatePreviewBitmap(source, w, h);
                var b = CreateBlurred(p);
                return (p, b);
            }).ConfigureAwait(false);

            if (_closed || IsDisposed || PreviewPipelineClosed() || version != _imageVersion)
            {
                return;
            }

            Invoke(new Action(() => InstallPreviewBitmaps(preview, blurred)));
            preview = null;
            blurred = null;
        }
        catch (Exception) when (_closed || IsDisposed || PreviewPipelineClosed())
        {
            // Closing the window can destroy its handle while the background render completes.
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load fast overlay thumbnail", ex);
        }
        finally
        {
            DisposePreviewPair(preview, blurred);
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
            double scale = Math.Max(width / (double)Math.Max(1, image.Width), height / (double)Math.Max(1, image.Height));
            float sourceWidth = (float)(width / scale);
            float sourceHeight = (float)(height / scale);
            float sourceX = (image.Width - sourceWidth) / 2f;
            float sourceY = (image.Height - sourceHeight) / 2f;
            g.DrawImage(
                image,
                new SD.Rectangle(0, 0, width, height),
                sourceX,
                sourceY,
                sourceWidth,
                sourceHeight,
                SD.GraphicsUnit.Pixel);
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

        _tooltipTimer.Stop();
        HideActionTooltip();
        int old = _hoverButton;
        _hoverButton = index;
        if (old >= 0 && old < _buttons.Count) Invalidate(_buttons[old].Bounds);
        if (index >= 0)
        {
            Invalidate(_buttons[index].Bounds);
            _tooltipTimer.Start();
        }
    }

    private void ShowActionTooltip()
    {
        _tooltipTimer.Stop();
        if (_closed || IsDisposed || !Visible || !_hovering || _hoverButton < 0 || _hoverButton >= _buttons.Count)
            return;

        ActionButton button = _buttons[_hoverButton];
        var tooltip = new QuickAccessTooltipWindow(button.TooltipText, _visuals, _dpi);
        _tooltipWindow = tooltip;
        tooltip.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_tooltipWindow, tooltip))
                _tooltipWindow = null;
        };
        tooltip.ShowNear(this, RectangleToScreen(button.Bounds), RectangleToScreen(_cardRect));
    }

    private void HideActionTooltip()
    {
        _tooltipTimer.Stop();
        QuickAccessTooltipWindow? tooltip = _tooltipWindow;
        _tooltipWindow = null;
        if (tooltip is null || tooltip.IsDisposed)
            return;

        tooltip.Close();
        tooltip.Dispose();
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
            if (_mediaFilePath is not null)
            {
                // Copy the recording as a real file so Outlook/Slack paste an attachment.
                var files = new System.Collections.Specialized.StringCollection { _mediaFilePath };
                WF.Clipboard.SetFileDropList(files);
            }
            else
            {
                _copyTask = CaptureService.CopyToClipboardAsync(_image);
                await _copyTask;
            }
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
            if (_mediaFilePath is not null)
            {
                // The recording is already saved; Save here means "save a copy as…".
                string extension = Path.GetExtension(_mediaFilePath).TrimStart('.');
                using var mediaDialog = new WF.SaveFileDialog
                {
                    FileName = Path.GetFileName(_mediaFilePath),
                    InitialDirectory = _settings.Current.SaveFolder,
                    Filter = $"{extension.ToUpperInvariant()} file|*.{extension}|All files|*.*",
                };
                if (mediaDialog.ShowDialog(this) != WF.DialogResult.OK)
                    return;
                string source = _mediaFilePath;
                string destination = mediaDialog.FileName;
                await Task.Run(() => File.Copy(source, destination, overwrite: true));
                if (closeAfterSave && !_closed && !IsDisposed)
                    Close();
                return;
            }
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

    private void OpenMediaFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_mediaFilePath!) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open {_mediaFilePath}", ex);
        }
    }

    private static bool IsAltDown() => (WF.Control.ModifierKeys & WF.Keys.Alt) == WF.Keys.Alt;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    private enum ActionButtonShape
    {
        Circle,
        Pill,
    }

    private sealed class ActionButton(
        string tip,
        string accessibleName,
        string tooltipText,
        SD.Rectangle bounds,
        Action action,
        ActionButtonShape shape,
        int cornerRadius,
        SD.Bitmap? icon)
        : IDisposable
    {
        public string Tip { get; } = tip;
        public string AccessibleName { get; } = accessibleName;
        public string TooltipText { get; } = tooltipText;
        public SD.Rectangle Bounds { get; } = bounds;
        public Action Action { get; } = action;
        public ActionButtonShape Shape { get; } = shape;
        public int CornerRadius { get; } = cornerRadius;
        public string? Label { get; set; }
        public SD.Font? LabelFont { get; init; }
        public SD.Bitmap? Icon { get; } = icon;

        public void Dispose()
        {
            LabelFont?.Dispose();
            // Icon is NOT disposed: glyphs are shared out of the process-wide IconCache.
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
