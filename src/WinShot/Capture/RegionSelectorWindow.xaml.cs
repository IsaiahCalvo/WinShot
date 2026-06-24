using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// Full-virtual-desktop overlay used to select a screen region. It can either
/// show a frozen screenshot or a live transparent overlay; hovering snaps to a
/// window, and a click (no drag) selects it.
/// All visuals are in WPF DIPs; results are mapped proportionally back to
/// bitmap pixels, which keeps the math exact regardless of monitor DPI.
/// A magnifier loupe follows the cursor for pixel-precise selection.
/// In all-in-one mode a top-center toolbar picks what to do with the region
/// (capture / record / OCR / scroll), offers exact-size entry with an aspect
/// lock, and restores the previously captured region as a pending selection
/// that Enter or a double-click confirms.
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private const double LoupeSizeDip = 120;  // loupe circle diameter
    private const double LoupeZoom = 8;       // magnification factor
    private const double DragThresholdDip = 4;

    private readonly SD.Bitmap? _shot;
    private List<WindowInfo> _windows;
    private readonly SD.Rectangle _vs;
    private readonly SettingsService? _settings;
    private readonly bool _allInOne;
    private readonly BitmapSource? _previewSource;
    private readonly int _bitmapWidth;
    private readonly int _bitmapHeight;
    private readonly double _imageUnitX;  // image DIUs per bitmap pixel (horizontal)
    private readonly double _imageUnitY;

    private Point _dragStart;
    private bool _dragging;
    private bool _dragMoved;
    private double? _dragRatio;           // W:H locked at drag start when aspect lock is on
    private WindowInfo? _hoverWindow;
    private SD.Rectangle? _pendingPx;     // adjustable, not-yet-confirmed selection (bitmap px)

    /// <summary>Selected region in bitmap pixel coordinates (origin = virtual screen top-left).</summary>
    public SD.Rectangle? SelectedRegionPx { get; private set; }

    /// <summary>What the caller should do with the region; only changes from Capture in all-in-one mode.</summary>
    public AllInOneAction SelectedAction { get; private set; } = AllInOneAction.Capture;

    public static void Prewarm()
    {
        var window = new RegionSelectorWindow(
            Task.FromResult(new List<WindowInfo>()),
            settings: null,
            allInOne: true)
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            Opacity = 0,
        };
        window.Show();
        window.Close();
    }

    public RegionSelectorWindow(SD.Bitmap shot, List<WindowInfo> windows)
        : this(shot, windows, null, allInOne: false)
    {
    }

    public RegionSelectorWindow(Task<List<WindowInfo>> windowsTask, SettingsService? settings, bool allInOne)
        : this(
            shot: null,
            source: null,
            windows: new List<WindowInfo>(),
            settings,
            allInOne,
            liveOverlay: true)
    {
        _ = LoadWindowsAsync(windowsTask);
    }

    public RegionSelectorWindow(SD.Bitmap shot, List<WindowInfo> windows, SettingsService? settings, bool allInOne)
        : this(shot, CaptureService.ToBitmapSource(shot), windows, settings, allInOne)
    {
    }

    public RegionSelectorWindow(
        SD.Bitmap shot,
        BitmapSource source,
        List<WindowInfo> windows,
        SettingsService? settings,
        bool allInOne)
        : this(shot, source, windows, settings, allInOne, liveOverlay: false)
    {
    }

    private RegionSelectorWindow(
        SD.Bitmap? shot,
        BitmapSource? source,
        List<WindowInfo> windows,
        SettingsService? settings,
        bool allInOne,
        bool liveOverlay = true)
    {
        InitializeComponent();
        _shot = shot;
        _windows = windows;
        _settings = settings;
        _allInOne = allInOne;
        _vs = CaptureService.VirtualScreen;
        _bitmapWidth = shot?.Width ?? _vs.Width;
        _bitmapHeight = shot?.Height ?? _vs.Height;

        ScreenshotImage.Source = source;
        ScreenshotImage.Visibility = liveOverlay ? Visibility.Collapsed : Visibility.Visible;
        _previewSource = source;
        _imageUnitX = source is null ? 1 : source.Width / _bitmapWidth;
        _imageUnitY = source is null ? 1 : source.Height / _bitmapHeight;

        if (allInOne)
            Toolbar.Visibility = Visibility.Visible;

        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, HwndTopmost, _vs.X, _vs.Y, _vs.Width, _vs.Height, SwpShowWindow);
        };
        Loaded += (_, _) =>
        {
            UpdateDim(null);
            Activate();
            Focus();
            if (_allInOne && _settings is not null)
                Dispatcher.InvokeAsync(TryRestoreLastRegion, DispatcherPriority.Loaded);
        };
        Closed += (_, _) =>
        {
            ScreenshotImage.Source = null;
            LoupeBrush.ImageSource = null;
            DimPath.Data = null;
            Root.Children.Clear();
            Content = null;
            _windows = new List<WindowInfo>();
            MemoryCleanup.Request();
        };
    }

    public RegionSelectorWindow(
        SD.Bitmap shot,
        BitmapSource source,
        Task<List<WindowInfo>> windowsTask,
        SettingsService? settings,
        bool allInOne)
        : this(shot, source, new List<WindowInfo>(), settings, allInOne)
    {
        _ = LoadWindowsAsync(windowsTask);
    }

    private async Task LoadWindowsAsync(Task<List<WindowInfo>> windowsTask)
    {
        try
        {
            var windows = await windowsTask.ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => _windows = windows);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load selector window list", ex);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
        else if (e.Key == Key.Return && _pendingPx is SD.Rectangle pending)
        {
            e.Handled = true;
            Confirm(pending, SelectedAction);
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        DialogResult = false;
        base.OnMouseRightButtonUp(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Toolbar.IsVisible && Toolbar.IsMouseOver)
        {
            // Clicks on the toolbar surface must not start a drag.
            base.OnMouseLeftButtonDown(e);
            return;
        }

        if (e.ClickCount == 2 && _pendingPx is SD.Rectangle pending)
        {
            Confirm(pending, SelectedAction);
            return;
        }

        Keyboard.Focus(this); // pull focus out of the size boxes so Enter/Escape reach the window
        _dragStart = e.GetPosition(Root);
        _dragging = true;
        _dragMoved = false;
        _dragRatio = null;
        if (_allInOne && AspectLock.IsChecked == true &&
            int.TryParse(WBox.Text, out int rw) && int.TryParse(HBox.Text, out int rh) && rw > 0 && rh > 0)
        {
            _dragRatio = (double)rw / rh;
        }
        WindowHighlight.Visibility = Visibility.Collapsed;
        CaptureMouse();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(Root);
        bool overToolbar = Toolbar.IsVisible && Toolbar.IsMouseOver;

        if (_dragging)
        {
            if (!_dragMoved &&
                Math.Abs(pos.X - _dragStart.X) < DragThresholdDip &&
                Math.Abs(pos.Y - _dragStart.Y) < DragThresholdDip)
            {
                // Still within click jitter; don't disturb pending-selection visuals.
                UpdateLoupe(pos, overToolbar);
                base.OnMouseMove(e);
                return;
            }
            if (!_dragMoved)
            {
                _dragMoved = true;
                _pendingPx = null; // a new drag replaces the pending selection
            }
            ShowSelection(MakeDragRect(pos));
        }
        else if (_pendingPx is null && !overToolbar)
        {
            HitTestWindow(pos);
        }

        UpdateLoupe(pos, overToolbar);
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var pos = e.GetPosition(Root);

        if (!_dragMoved)
        {
            // A click, not a drag.
            if (_pendingPx is SD.Rectangle pending)
            {
                // Click inside keeps the pending selection (Enter/double-click confirms);
                // click outside dismisses it.
                if (!BitmapToDip(pending).Contains(pos))
                    ClearPending();
                return;
            }
            if (_hoverWindow is not null)
            {
                Confirm(ScreenToBitmap(_hoverWindow.Bounds), SelectedAction);
            }
            else
            {
                // Click on empty space: reset and keep selecting.
                SelectionRect.Visibility = Visibility.Collapsed;
                SizeLabel.Visibility = Visibility.Collapsed;
                UpdateDim(null);
            }
            return;
        }

        var px = DipToBitmap(MakeDragRect(pos));
        if (px.Width > 0 && px.Height > 0)
            Confirm(px, SelectedAction);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        Loupe.Visibility = Visibility.Collapsed;
        base.OnMouseLeave(e);
    }

    /// <summary>Builds the drag rectangle, constrained to the locked aspect ratio when active.</summary>
    private Rect MakeDragRect(Point pos) =>
        AllInOneDragLayout.CreateDipRect(_dragStart, pos, _dragRatio);

    private void ShowSelection(Rect rect)
    {
        Canvas.SetLeft(SelectionRect, rect.X);
        Canvas.SetTop(SelectionRect, rect.Y);
        SelectionRect.Width = rect.Width;
        SelectionRect.Height = rect.Height;
        SelectionRect.Visibility = Visibility.Visible;
        UpdateDim(rect);

        var px = DipToBitmap(rect);
        ShowSizeLabel($"{px.Width} × {px.Height}", rect.Right + 8, rect.Bottom + 8);
        if (_allInOne)
            SetSizeBoxes(px.Width, px.Height);
    }

    private void HitTestWindow(Point posDip)
    {
        int sx = (int)(posDip.X / Root.ActualWidth * _bitmapWidth) + _vs.X;
        int sy = (int)(posDip.Y / Root.ActualHeight * _bitmapHeight) + _vs.Y;
        _hoverWindow = _windows.FirstOrDefault(w => w.Bounds.Contains(sx, sy));

        if (_hoverWindow is null)
        {
            WindowHighlight.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            UpdateDim(null);
            return;
        }

        var px = ScreenToBitmap(_hoverWindow.Bounds);
        var dip = BitmapToDip(px);
        Canvas.SetLeft(WindowHighlight, dip.X);
        Canvas.SetTop(WindowHighlight, dip.Y);
        WindowHighlight.Width = dip.Width;
        WindowHighlight.Height = dip.Height;
        WindowHighlight.Visibility = Visibility.Visible;
        UpdateDim(dip);
        ShowSizeLabel($"{px.Width} × {px.Height}", posDip.X + 14, posDip.Y + 18);
    }

    // ---- Magnifier loupe -------------------------------------------------

    /// <summary>
    /// Re-aims the loupe at the bitmap pixel under the cursor. Only the brush
    /// viewbox and canvas position change per move — no bitmap copies.
    /// </summary>
    private void UpdateLoupe(Point pos, bool hide)
    {
        if (_shot is null || _previewSource is null || hide ||
            Root.ActualWidth < 1 || Root.ActualHeight < 1 ||
            pos.X < 0 || pos.Y < 0 || pos.X >= Root.ActualWidth || pos.Y >= Root.ActualHeight)
        {
            Loupe.Visibility = Visibility.Collapsed;
            return;
        }

        int px = Math.Clamp((int)(pos.X / Root.ActualWidth * _bitmapWidth), 0, _bitmapWidth - 1);
        int py = Math.Clamp((int)(pos.Y / Root.ActualHeight * _bitmapHeight), 0, _bitmapHeight - 1);

        double vbW = LoupeSizeDip / LoupeZoom * _imageUnitX;
        double vbH = LoupeSizeDip / LoupeZoom * _imageUnitY;
        double cx = (px + 0.5) * _imageUnitX;
        double cy = (py + 0.5) * _imageUnitY;
        LoupeBrush.ImageSource ??= _previewSource;
        LoupeBrush.Viewbox = new Rect(cx - vbW / 2, cy - vbH / 2, vbW, vbH);
        LoupeCoords.Text = $"{px + _vs.X}, {py + _vs.Y}";

        // Offset below the cursor-side size label so the two never overlap.
        double lx = pos.X + 24;
        double ly = pos.Y + 52;
        if (lx + LoupeSizeDip + 8 > Root.ActualWidth) lx = pos.X - LoupeSizeDip - 24;
        if (ly + LoupeSizeDip + 50 > Root.ActualHeight) ly = pos.Y - LoupeSizeDip - 56;
        Canvas.SetLeft(Loupe, lx);
        Canvas.SetTop(Loupe, ly);
        Loupe.Visibility = Visibility.Visible;
    }

    // ---- All-in-one toolbar ----------------------------------------------

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        SelectedAction =
            ReferenceEquals(sender, ModeRecord) ? AllInOneAction.Record :
            ReferenceEquals(sender, ModeOcr) ? AllInOneAction.Ocr :
            ReferenceEquals(sender, ModeScroll) ? AllInOneAction.Scroll :
            AllInOneAction.Capture; // Area / Window
    }

    private void OnFullscreen(object sender, RoutedEventArgs e)
        => Confirm(new SD.Rectangle(0, 0, _bitmapWidth, _bitmapHeight), AllInOneAction.Capture);

    private void OnSizeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return) return;
        e.Handled = true;
        if (!int.TryParse(WBox.Text, out int w) || !int.TryParse(HBox.Text, out int h) || w < 1 || h < 1)
            return;

        // Resize anchored at the current selection's top-left; center when there is none.
        SD.Point anchor = _pendingPx?.Location ?? new SD.Point(
            Math.Max(0, (_bitmapWidth - w) / 2),
            Math.Max(0, (_bitmapHeight - h) / 2));
        var px = new SD.Rectangle(anchor.X, anchor.Y, w, h);
        px.Intersect(new SD.Rectangle(0, 0, _bitmapWidth, _bitmapHeight));
        if (px.Width < 1 || px.Height < 1) return;

        _pendingPx = px;
        ShowPending();
        Keyboard.Focus(this); // so the next Enter (or a double-click) confirms
    }

    // ---- Pending selection (memory / exact size) ---------------------------

    private void TryRestoreLastRegion()
    {
        if (_settings is null) return;
        if (!PreviousRegion.TryParse(_settings.Current.LastCaptureRegion, out SD.Rectangle screenRect)) return;
        var px = ScreenToBitmap(screenRect);
        if (px.Width < 1 || px.Height < 1) return;
        _pendingPx = px;
        ShowPending();
    }

    private void ShowPending()
    {
        if (_pendingPx is not SD.Rectangle px) return;
        var dip = BitmapToDip(px);
        Canvas.SetLeft(SelectionRect, dip.X);
        Canvas.SetTop(SelectionRect, dip.Y);
        SelectionRect.Width = dip.Width;
        SelectionRect.Height = dip.Height;
        SelectionRect.Visibility = Visibility.Visible;
        WindowHighlight.Visibility = Visibility.Collapsed;
        UpdateDim(dip);
        ShowSizeLabel($"{px.Width} × {px.Height}", dip.Right + 8, dip.Bottom + 8);
        SetSizeBoxes(px.Width, px.Height);
    }

    private void ClearPending()
    {
        _pendingPx = null;
        SelectionRect.Visibility = Visibility.Collapsed;
        SizeLabel.Visibility = Visibility.Collapsed;
        UpdateDim(null);
    }

    private void SetSizeBoxes(int w, int h)
    {
        WBox.Text = w.ToString();
        HBox.Text = h.ToString();
    }

    /// <summary>Finalizes the selection, persisting it (in screen coords) when settings were provided.</summary>
    private void Confirm(SD.Rectangle px, AllInOneAction action)
    {
        if (px.Width < 1 || px.Height < 1) return;
        SelectedRegionPx = px;
        SelectedAction = action;
        if (_settings is not null)
            _settings.Current.LastCaptureRegion = PreviousRegion.Format(
                new SD.Rectangle(px.X + _vs.X, px.Y + _vs.Y, px.Width, px.Height));
        DialogResult = true;
    }

    // ---- Shared visuals / mapping ------------------------------------------

    private void ShowSizeLabel(string text, double x, double y)
    {
        SizeText.Text = text;
        SizeLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(SizeLabel, Math.Clamp(x, 0, Math.Max(0, Root.ActualWidth - 110)));
        Canvas.SetTop(SizeLabel, Math.Clamp(y, 0, Math.Max(0, Root.ActualHeight - 30)));
    }

    private void UpdateDim(Rect? holeDip)
    {
        var full = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        DimPath.Data = holeDip is Rect hole
            ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole))
            : full;
    }

    private SD.Rectangle ScreenToBitmap(SD.Rectangle screenRect)
    {
        var r = new SD.Rectangle(screenRect.X - _vs.X, screenRect.Y - _vs.Y, screenRect.Width, screenRect.Height);
        r.Intersect(new SD.Rectangle(0, 0, _bitmapWidth, _bitmapHeight));
        return r;
    }

    private SD.Rectangle DipToBitmap(Rect dip)
    {
        double sx = _bitmapWidth / Root.ActualWidth;
        double sy = _bitmapHeight / Root.ActualHeight;
        var r = new SD.Rectangle(
            (int)Math.Round(dip.X * sx),
            (int)Math.Round(dip.Y * sy),
            (int)Math.Round(dip.Width * sx),
            (int)Math.Round(dip.Height * sy));
        r.Intersect(new SD.Rectangle(0, 0, _bitmapWidth, _bitmapHeight));
        return r;
    }

    private Rect BitmapToDip(SD.Rectangle px)
    {
        double sx = Root.ActualWidth / _bitmapWidth;
        double sy = Root.ActualHeight / _bitmapHeight;
        return new Rect(px.X * sx, px.Y * sy, px.Width * sx, px.Height * sy);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
