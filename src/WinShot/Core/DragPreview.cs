using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// The thumbnail that follows the cursor during a drag. WPF has no built-in drag image, and the
/// shell's IDragSourceHelper is unusable here (neither WPF's nor WinForms' DataObject implements
/// the COM SetData it calls back into — both return E_NOTIMPL), so this is a click-through topmost
/// window repositioned from the drag source's GiveFeedback event.
///
/// Built for smoothness: one instance is created per editor and reused (Show/Hide, never
/// create/close), it is fully opaque so WPF keeps the hardware render path — AllowsTransparency
/// would force software rendering — and moves go straight to SetWindowPos in physical pixels.
/// </summary>
internal sealed class DragPreview : IDisposable
{
    /// <summary>Longest edge of the thumbnail, in logical pixels.</summary>
    private const int MaxEdge = 180;

    /// <summary>Kept below-right of the hotspot so the cursor and its drop badge stay readable.</summary>
    private const int CursorOffsetX = 12;
    private const int CursorOffsetY = 12;

    private readonly Window _window;
    private readonly Image _image;
    private IntPtr _handle;
    private bool _visible;
    private bool _disposed;

    public DragPreview()
    {
        _image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality); // already pre-scaled

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            IsHitTestVisible = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = Brushes.Black,
            Content = new Border
            {
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Child = _image,
            },
        };

        // Realize the HWND up front so the first drag doesn't pay for window creation.
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        nint exStyle = GetWindowLongPtr(_handle, GwlExStyle);
        SetWindowLongPtr(_handle, GwlExStyle, exStyle | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>
    /// Opaque, pre-scaled, frozen thumbnail of <paramref name="image"/>, ready to hand to
    /// <see cref="Show"/>. Cheap enough to build on mouse-down, before a drag is certain.
    /// </summary>
    public static BitmapSource CreateThumbnail(SD.Bitmap image, double dpiScale)
    {
        double dpi = dpiScale > 0 ? dpiScale : 1.0;
        int max = Math.Max(1, (int)Math.Round(MaxEdge * dpi));
        double scale = Math.Min(1.0, (double)max / Math.Max(image.Width, image.Height));
        int w = Math.Max(1, (int)Math.Round(image.Width * scale));
        int h = Math.Max(1, (int)Math.Round(image.Height * scale));

        using var thumb = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(thumb))
        {
            g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
            // The preview window is opaque, so flatten transparency onto white rather than black.
            g.Clear(SD.Color.White);
            g.DrawImage(image, new SD.Rectangle(0, 0, w, h));
        }

        var source = CaptureService.ToBitmapSource(thumb);
        source.Freeze();
        return source;
    }

    /// <summary>Shows the preview at the cursor. <paramref name="dpiScale"/> keeps it crisp on HiDPI.</summary>
    public void Show(BitmapSource thumbnail, double dpiScale)
    {
        if (_disposed) return;

        double dpi = dpiScale > 0 ? dpiScale : 1.0;
        _image.Source = thumbnail;
        _image.Width = thumbnail.PixelWidth / dpi;
        _image.Height = thumbnail.PixelHeight / dpi;

        MoveToCursor(activate: true);
        if (!_visible)
        {
            _window.Show();
            _visible = true;
        }
        else
        {
            ShowWindow(_handle, SwShowNoActivate);
        }
    }

    /// <summary>Snaps the preview to the cursor, in physical pixels so mixed-DPI setups don't drift.</summary>
    public void MoveToCursor() => MoveToCursor(activate: false);

    private void MoveToCursor(bool activate)
    {
        if (_disposed || _handle == IntPtr.Zero || !GetCursorPos(out PointL cursor)) return;

        // Re-asserting topmost on every move churns the z-order for nothing; only do it on show.
        uint flags = SwpNoSize | SwpNoActivate | (activate ? 0 : SwpNoZOrder);
        SetWindowPos(_handle, activate ? HwndTopmost : IntPtr.Zero,
            cursor.X + CursorOffsetX, cursor.Y + CursorOffsetY, 0, 0, flags);
    }

    public void Hide()
    {
        if (_disposed || !_visible) return;
        ShowWindow(_handle, SwHide);
        _image.Source = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle = IntPtr.Zero;
        _window.Close();
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointL point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr handle, int index, nint value);
}
