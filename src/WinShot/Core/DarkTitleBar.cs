using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinShot.Core;

/// <summary>
/// Paints a WPF window's native title bar dark (Win11 immersive dark mode + caption
/// color) so windows that keep native chrome — Editor, Settings, History, dialogs —
/// stop showing a bright system title bar above a dark charcoal body. No-op on
/// builds that don't support the DWM attribute (the call simply returns an error
/// code we ignore), so it is safe to call on every titled window.
/// </summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20; // Win10 1809+
    private const int DwmwaCaptionColor = 35;         // Win11 22H2+
    private const int DwmwaBorderColor = 34;          // Win11 22H2+

    // DWM COLORREF is 0x00BBGGRR. WindowBg #1C1C1E -> R=1C G=1C B=1E -> 0x001E1C1C.
    private const int CaptionColorRef = 0x001E1C1C;
    private const int BorderColorRef = 0x002B2B2B;

    public static void Apply(Window window)
    {
        if (window is null) return;

        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            ApplyToHandle(helper.Handle);
            return;
        }

        // Handle isn't created yet; apply once the HWND exists.
        window.SourceInitialized += (_, _) => ApplyToHandle(new WindowInteropHelper(window).Handle);
    }

    private static void ApplyToHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));

            int caption = CaptionColorRef;
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));

            int border = BorderColorRef;
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch
        {
            // DWM attribute unsupported on this OS build — native (light) chrome stays.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
