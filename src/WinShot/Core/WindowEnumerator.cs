using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WinShot.Core;

public record WindowInfo(IntPtr Handle, string Title, Rectangle Bounds);

/// <summary>One snappable rectangle: a top-level window, its client area, or a child control.</summary>
public readonly record struct SnapRect(IntPtr Handle, Rectangle Bounds, bool IsWindow);

/// <summary>
/// Enumerates visible top-level windows in z-order (front to back) with their
/// extended frame bounds in physical screen pixels. Used for window snapping.
/// </summary>
public static class WindowEnumerator
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private const int GaRoot = 2;

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExLayered = 0x00080000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;

    // Classes that enumerate as ordinary windows but are never a capture target.
    private static readonly string[] IgnoreClassNames = { "CEF-OSC-WIDGET" }; // NVIDIA overlay

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

            // Skip click-through overlays (layered + transparent, e.g. dictation HUDs, game
            // overlays): they can never receive input, so they are never a meaningful capture
            // target — and being TOPMOST they would otherwise shadow the real window
            // underneath in the selectors' bounds fallback (ResolveWindow).
            long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            if ((exStyle & WsExTransparent) != 0 && (exStyle & WsExLayered) != 0)
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

    /// <summary>
    /// Every snappable rectangle on screen, deepest-first: child controls before their
    /// parent, each window's client area (frame excluded) before the window itself, in
    /// z-order front to back. Hover detection is then a plain first-contains-point scan,
    /// which lands on the innermost element under the cursor.
    ///
    /// Port of ShareX's WindowsRectangleList (GPL-3, ShareX.ScreenCaptureLib) — same
    /// filters, same ordering, same containment dedupe. HWND-level only: apps that draw
    /// their whole UI into one window (Chromium, Electron, WPF) expose just the window
    /// and its client area here; the MSAA/UIA and pixel tiers refine those.
    /// </summary>
    public static List<SnapRect> GetSnapRectangles(HashSet<IntPtr>? exclude = null, int timeoutMs = 400)
    {
        var collected = new List<SnapRect>();
        var visited = new HashSet<IntPtr>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            EnumWindows((hwnd, _) => Collect(hwnd, null, collected, visited, exclude, clock, timeoutMs), IntPtr.Zero);
        }
        catch (Exception)
        {
            // A provider crashing mid-enumeration must never take the selector with it.
        }

        // Drop any control rect entirely covered by a deeper rect already accepted — an
        // inner control that exactly fills its parent would otherwise be unreachable.
        var result = new List<SnapRect>(collected.Count);
        foreach (var candidate in collected)
        {
            if (!candidate.IsWindow && result.Any(r => r.Bounds.Contains(candidate.Bounds)))
                continue;
            result.Add(candidate);
        }
        return result;
    }

    private static bool Collect(IntPtr hwnd, Rectangle? clip, List<SnapRect> collected,
        HashSet<IntPtr> visited, HashSet<IntPtr>? exclude, System.Diagnostics.Stopwatch clock, int timeoutMs)
    {
        if (clock.ElapsedMilliseconds > timeoutMs)
            return false;
        if (exclude is not null && exclude.Contains(hwnd))
            return true;
        if (!IsWindowVisible(hwnd))
            return true;

        Rectangle bounds;

        if (clip is not Rectangle parentBounds)
        {
            if (DwmGetWindowAttributeInt(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            if (IgnoreClassNames.Contains(ClassNameOf(hwnd), StringComparer.OrdinalIgnoreCase))
                return true;

            long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            // Non-activatable tool windows (tiling overlays, system auxiliaries) are never
            // the app the user means, and click-through layers can't be interacted with.
            if ((exStyle & WsExToolWindow) != 0 && (exStyle & WsExNoActivate) != 0)
                return true;
            if ((exStyle & WsExTransparent) != 0 && (exStyle & WsExLayered) != 0)
                return true;

            if (DwmGetWindowAttributeRect(hwnd, DwmwaExtendedFrameBounds, out Rect frame, Marshal.SizeOf<Rect>()) != 0 &&
                !GetWindowRect(hwnd, out frame))
            {
                return true;
            }
            bounds = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
        }
        else
        {
            if (!GetWindowRect(hwnd, out Rect wr))
                return true;
            bounds = Rectangle.Intersect(
                Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom), parentBounds);
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
            return true;

        // Children first so the finished list reads deepest-first.
        if (visited.Add(hwnd))
        {
            try
            {
                EnumChildWindows(hwnd, (child, _) =>
                    Collect(child, bounds, collected, visited, exclude, clock, timeoutMs), IntPtr.Zero);
            }
            catch (Exception)
            {
            }
        }

        if (clip is null && ClientRectOf(hwnd) is Rectangle client && client != bounds &&
            client.Width > 0 && client.Height > 0)
        {
            collected.Add(new SnapRect(hwnd, client, IsWindow: false));
        }

        collected.Add(new SnapRect(hwnd, bounds, IsWindow: clip is null));
        return true;
    }

    private static Rectangle? ClientRectOf(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out Rect c))
            return null;
        var origin = new PointStruct { X = c.Left, Y = c.Top };
        if (!ClientToScreen(hwnd, ref origin))
            return null;
        return new Rectangle(origin.X, origin.Y, c.Right - c.Left, c.Bottom - c.Top);
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int length = GetClassName(hwnd, sb, sb.Capacity);
        return length > 0 ? sb.ToString() : "";
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
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref PointStruct point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(PointStruct point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int attribute, out Rect value, int size);
}
