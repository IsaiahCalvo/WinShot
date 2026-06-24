using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace WinShot.Core;

public record WindowInfo(IntPtr Handle, string Title, Rectangle Bounds);

/// <summary>
/// Enumerates visible top-level windows in z-order (front to back) with their
/// extended frame bounds in physical screen pixels. Used for window snapping.
/// </summary>
public static class WindowEnumerator
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private const int GaRoot = 2;

    /// <summary>
    /// Returns the top-level (root) window handle under <paramref name="screenPoint"/>
    /// using the real z-order (WindowFromPoint), or <see cref="IntPtr.Zero"/> if none.
    /// Unlike a bounds scan, this correctly resolves a small foreground window sitting
    /// on top of a larger background one.
    /// </summary>
    public static IntPtr TopLevelWindowFromPoint(Point screenPoint)
    {
        IntPtr hwnd = WindowFromPoint(new PointStruct { X = screenPoint.X, Y = screenPoint.Y });
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr root = GetAncestor(hwnd, GaRoot);
        return root != IntPtr.Zero ? root : hwnd;
    }

    public static List<WindowInfo> GetTopLevelWindows(HashSet<IntPtr>? exclude = null)
    {
        var windows = new List<WindowInfo>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (exclude is not null && exclude.Contains(hwnd)) return true;

            // Skip UWP/ghost windows that are "cloaked" (invisible but enumerable).
            if (DwmGetWindowAttributeInt(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            int length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (title == "Program Manager") return true;

            if (DwmGetWindowAttributeRect(hwnd, DwmwaExtendedFrameBounds, out Rect rect, Marshal.SizeOf<Rect>()) != 0)
            {
                if (!GetWindowRect(hwnd, out rect)) return true;
            }

            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width < 30 || bounds.Height < 30) return true;

            windows.Add(new WindowInfo(hwnd, title, bounds));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(PointStruct point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int attribute, out Rect value, int size);
}
