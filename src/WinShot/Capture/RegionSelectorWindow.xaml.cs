using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// Full-virtual-desktop overlay showing a frozen screenshot. Drag selects a
/// region; hovering snaps to a window, and a click (no drag) selects it.
/// All visuals are in WPF DIPs; results are mapped proportionally back to
/// bitmap pixels, which keeps the math exact regardless of monitor DPI.
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private readonly SD.Bitmap _shot;
    private readonly List<WindowInfo> _windows;
    private readonly SD.Rectangle _vs;
    private Point _dragStart;
    private bool _dragging;
    private WindowInfo? _hoverWindow;

    /// <summary>Selected region in bitmap pixel coordinates (origin = virtual screen top-left).</summary>
    public SD.Rectangle? SelectedRegionPx { get; private set; }

    public RegionSelectorWindow(SD.Bitmap shot, List<WindowInfo> windows)
    {
        InitializeComponent();
        _shot = shot;
        _windows = windows;
        _vs = CaptureService.VirtualScreen;
        ScreenshotImage.Source = CaptureService.ToBitmapSource(shot);

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
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
        base.OnKeyDown(e);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        DialogResult = false;
        base.OnMouseRightButtonUp(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Root);
        _dragging = true;
        WindowHighlight.Visibility = Visibility.Collapsed;
        CaptureMouse();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(Root);
        if (_dragging)
            ShowSelection(new Rect(_dragStart, pos));
        else
            HitTestWindow(pos);
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var rectDip = new Rect(_dragStart, e.GetPosition(Root));
        if (rectDip.Width < 4 && rectDip.Height < 4)
        {
            if (_hoverWindow is not null)
            {
                SelectedRegionPx = ScreenToBitmap(_hoverWindow.Bounds);
                DialogResult = true;
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

        var px = DipToBitmap(rectDip);
        if (px.Width > 0 && px.Height > 0)
        {
            SelectedRegionPx = px;
            DialogResult = true;
        }
    }

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
    }

    private void HitTestWindow(Point posDip)
    {
        int sx = (int)(posDip.X / Root.ActualWidth * _shot.Width) + _vs.X;
        int sy = (int)(posDip.Y / Root.ActualHeight * _shot.Height) + _vs.Y;
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
        r.Intersect(new SD.Rectangle(0, 0, _shot.Width, _shot.Height));
        return r;
    }

    private SD.Rectangle DipToBitmap(Rect dip)
    {
        double sx = _shot.Width / Root.ActualWidth;
        double sy = _shot.Height / Root.ActualHeight;
        var r = new SD.Rectangle(
            (int)Math.Round(dip.X * sx),
            (int)Math.Round(dip.Y * sy),
            (int)Math.Round(dip.Width * sx),
            (int)Math.Round(dip.Height * sy));
        r.Intersect(new SD.Rectangle(0, 0, _shot.Width, _shot.Height));
        return r;
    }

    private Rect BitmapToDip(SD.Rectangle px)
    {
        double sx = Root.ActualWidth / _shot.Width;
        double sy = Root.ActualHeight / _shot.Height;
        return new Rect(px.X * sx, px.Y * sy, px.Width * sx, px.Height * sy);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
