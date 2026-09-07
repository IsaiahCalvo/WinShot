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
    private const int DwmwaSystemBackdropType = 38;   // Win11 22H2+
    private const int DwmsbtMainWindow = 2;           // Mica

    // DWM COLORREF is 0x00BBGGRR. WindowBg #1C1C1E -> R=1C G=1C B=1E -> 0x001E1C1C.
    private const int CaptionColorRef = 0x001E1C1C;
    private const int BorderColorRef = 0x002B2B2B;

    /// <param name="micaBackdrop">Ask DWM for the Mica material behind the window. The
    /// caller must also make the WPF window background transparent — see <see cref="ApplyMica"/> —
    /// or WPF paints an opaque layer over it and nothing shows through.</param>
    public static void Apply(Window window, bool micaBackdrop = false)
    {
        if (window is null) return;

        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            ApplyToHandle(helper.Handle, micaBackdrop);
            return;
        }

        // Handle isn't created yet; apply once the HWND exists.
        window.SourceInitialized += (_, _) =>
            ApplyToHandle(new WindowInteropHelper(window).Handle, micaBackdrop);
    }

    /// <summary>
    /// Turns on Mica for a window: the DWM backdrop plus the two things WPF needs so the
    /// material is actually visible — a transparent window Background, and a null
    /// HwndTarget.BackgroundColor. Without the second one WPF composites an opaque black
    /// layer over the material and the window just looks flat again.
    /// </summary>
    public static void ApplyMica(Window window)
    {
        if (window is null) return;
        if (!CanUseMica(SystemParameters.HighContrast, Environment.OSVersion.Version.Build))
        {
            // Dark caption and border only, and the window keeps the XAML background it
            // declared. Clearing that first and hoping DWM paints the ground is what left
            // the settings window's gutters unpainted everywhere Mica does not exist.
            Apply(window);
            return;
        }

        window.Background = System.Windows.Media.Brushes.Transparent;
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                if (PresentationSource.FromVisual(window) is HwndSource source)
                    source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            }
            catch (Exception ex)
            {
                Log.Error("Could not clear the WPF composition background for Mica", ex);
            }
        };
        Apply(window, micaBackdrop: true);
    }

    /// <summary>
    /// Whether a window may trade its own opaque background for the system backdrop.
    /// DWMWA_SYSTEMBACKDROP_TYPE arrived in Windows 11 22H2 (build 22621) and WinShot
    /// supports back to Windows 10 20H1, where the attribute is rejected and a transparent
    /// window would render its gutters against an uninitialised DWM surface. High Contrast
    /// is excluded on every build: the palette pass sets a system background, and Mica would
    /// overwrite it with transparency moments later.
    /// </summary>
    internal static bool CanUseMica(bool highContrast, int osBuild) =>
        !highContrast && osBuild >= 22621;

    private static void ApplyToHandle(IntPtr hwnd, bool micaBackdrop = false)
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

            if (micaBackdrop)
            {
                // No version gate: on pre-22H2 the call returns an error we ignore, exactly
                // like the caption and border attributes above, and the window keeps the
                // opaque card fills it already draws.
                int backdrop = DwmsbtMainWindow;
                DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            }
        }
        catch
        {
            // DWM attribute unsupported on this OS build — native (light) chrome stays.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
