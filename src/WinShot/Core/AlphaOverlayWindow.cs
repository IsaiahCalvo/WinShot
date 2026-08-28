using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Core;

/// <summary>
/// Click-through, never-activating overlay window with true per-pixel alpha
/// (UpdateLayeredWindow). Subclasses render each frame into a 32bpp ARGB bitmap
/// and call <see cref="Present"/>; the desktop shows through wherever the bitmap
/// is transparent, and anti-aliased edges blend correctly. This replaces the old
/// magenta-TransparencyKey overlays, whose semi-transparent pixels blended with
/// the magenta backdrop into opaque pink — visible in every recording.
/// </summary>
public abstract class AlphaOverlayWindow : WF.Form
{
    protected AlphaOverlayWindow()
    {
        ClientSize = new SD.Size(1, 1);
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override WF.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    /// <summary>
    /// Pushes <paramref name="frame"/> (32bpp ARGB) to the screen with its top-left
    /// at <paramref name="screenTopLeft"/>, resizing/moving the window to match.
    /// </summary>
    protected void Present(SD.Bitmap frame, SD.Point screenTopLeft)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            hBitmap = frame.GetHbitmap(SD.Color.FromArgb(0));
            previous = SelectObject(memDc, hBitmap);
            var size = new NativeSize(frame.Width, frame.Height);
            var source = new NativePoint(0, 0);
            var destination = new NativePoint(screenTopLeft.X, screenTopLeft.Y);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };
            UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (previous != IntPtr.Zero)
                SelectObject(memDc, previous);
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Clears the overlay to fully transparent (nothing on screen).</summary>
    protected void PresentEmpty()
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        using var clear = new SD.Bitmap(1, 1, PixelFormat.Format32bppArgb);
        Present(clear, Location);
    }

    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const int UlwAlpha = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref NativePoint pptDst,
        ref NativeSize psize,
        IntPtr hdcSrc,
        ref NativePoint pptSrc,
        int crKey,
        ref BlendFunction pblend,
        int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);
}
