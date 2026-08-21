using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// The thumbnail that follows the cursor during a drag. WPF has no built-in drag image, and the
/// shell's IDragSourceHelper is unusable here (neither WPF's nor WinForms' DataObject implements
/// the COM SetData it calls back into — both return E_NOTIMPL), so this is a plain click-through
/// topmost window repositioned from the drag source's GiveFeedback event.
/// </summary>
internal sealed class DragPreview : IDisposable
{
    /// <summary>Longest edge of the thumbnail, in logical pixels.</summary>
    private const int MaxEdge = 180;

    /// <summary>Kept below-right of the hotspot so the cursor and its drop badge stay readable.</summary>
    private const int CursorOffsetX = 12;
    private const int CursorOffsetY = 12;

    private readonly Window _window;
    private bool _closed;

    public DragPreview(SD.Bitmap image)
    {
        double scale = Math.Min(1.0, (double)MaxEdge / Math.Max(image.Width, image.Height));

        var source = CaptureService.ToBitmapSource(image);
        source.Freeze();

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            IsHitTestVisible = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Opacity = 0.75,
            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = new Image
                {
                    Source = source,
                    Width = Math.Max(1, Math.Round(image.Width * scale)),
                    Height = Math.Max(1, Math.Round(image.Height * scale)),
                    Stretch = Stretch.Fill,
                },
            },
        };

        _window.Show();

        // Click-through and never-activate, so the preview can't eat the drag or steal focus.
        IntPtr handle = new WindowInteropHelper(_window).Handle;
        nint exStyle = GetWindowLongPtr(handle, GwlExStyle);
        SetWindowLongPtr(handle, GwlExStyle, exStyle | WsExTransparent | WsExNoActivate | WsExToolWindow);

        MoveToCursor();
    }

    /// <summary>Snaps the preview to the current cursor position, in physical pixels (DPI-agnostic).</summary>
    public void MoveToCursor()
    {
        if (_closed || !GetCursorPos(out PointL cursor)) return;

        IntPtr handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HwndTopmost, cursor.X + CursorOffsetX, cursor.Y + CursorOffsetY, 0, 0,
            SwpNoSize | SwpNoActivate);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _window.Close();
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointL point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr handle, int index, nint value);
}
