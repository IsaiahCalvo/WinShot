using System.Runtime.InteropServices;
using System.Text;

namespace WinShot.Core;

/// <summary>
/// Shows/hides the desktop icons by toggling the shell's SHELLDLL_DefView window
/// (child of Progman, or of a WorkerW when a wallpaper slideshow re-parents it).
/// Never throws; failures are logged.
/// </summary>
public static class DesktopIcons
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    /// <summary>True when the icons window is visible (or cannot be found — assume visible).</summary>
    public static bool Visible
    {
        get
        {
            try
            {
                IntPtr defView = FindDefView();
                return defView == IntPtr.Zero || IsWindowVisible(defView);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to query desktop icon visibility", ex);
                return true;
            }
        }
    }

    public static void Show() => SetVisible(true);

    public static void Hide() => SetVisible(false);

    public static void Toggle() => SetVisible(!Visible);

    private static void SetVisible(bool visible)
    {
        try
        {
            IntPtr defView = FindDefView();
            if (defView == IntPtr.Zero)
            {
                Log.Error("Desktop icons window (SHELLDLL_DefView) not found");
                return;
            }
            ShowWindow(defView, visible ? SW_SHOW : SW_HIDE);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to change desktop icon visibility", ex);
        }
    }

    private static IntPtr FindDefView()
    {
        // Normal layout: Progman -> SHELLDLL_DefView.
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                return defView;
        }

        // Wallpaper slideshow / some shells re-parent the icons under a WorkerW window.
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var className = new StringBuilder(64);
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() == "WorkerW")
            {
                IntPtr defView = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    found = defView;
                    return false; // stop enumerating
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
