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

    // Window classes that enumerate exactly like an ordinary top-level app but are never
    // something a user points at. "CEF-OSC-WIDGET" is NVIDIA's GeForce overlay: it is a
    // real visible top-level window, so nothing below filters it and it has to be named.
    private static readonly string[] IgnoreClassNames = { "CEF-OSC-WIDGET" };

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
    /// The shape of this list — deepest-first, cloaked and non-interactive windows filtered
    /// out, a window's client area listed before its frame — matches what ShareX's region
    /// selector produces, because that is what the Win32 window model makes correct. ShareX
    /// (GPL-3) was studied for behaviour only; no ShareX code is included here. See
    /// THIRD_PARTY_NOTICES.md.
    ///
    /// HWND-level only: apps that draw their whole UI into one window (Chromium, Electron,
    /// WPF) expose just the window and its client area here; the MSAA/UIA and pixel tiers
    /// refine those.
    /// </summary>
    public static List<SnapRect> GetSnapRectangles(HashSet<IntPtr>? exclude = null, int timeoutMs = 400)
    {
        var scan = new SnapScan(exclude, timeoutMs);

        try
        {
            EnumWindows((hwnd, _) => scan.CollectTopLevel(hwnd), IntPtr.Zero);
        }
        catch (Exception)
        {
            // A provider crashing mid-enumeration must never take the selector with it.
        }

        return DropCoveredControls(scan.Rects);
    }

    /// <summary>
    /// A control rect is dropped when something already accepted covers it completely. The
    /// list is built deepest-first, so "already accepted" means "deeper", and a control that
    /// exactly fills its parent would otherwise sit behind it and be unreachable to the
    /// first-contains-point hover scan. Window rects are never dropped — the ladder always
    /// needs a rung for the window itself.
    /// </summary>
    private static List<SnapRect> DropCoveredControls(List<SnapRect> rects)
    {
        var kept = new List<SnapRect>(rects.Count);
        foreach (var rect in rects)
        {
            if (!rect.IsWindow && kept.Any(k => k.Bounds.Contains(rect.Bounds)))
                continue;
            kept.Add(rect);
        }
        return kept;
    }

    /// <summary>
    /// One pass of the snap-rect walk. Top-level windows and child controls are collected by
    /// two separate entry points because they answer to different rules: a window is filtered
    /// on its class and extended styles and measured by its DWM frame, a control is neither
    /// filtered nor measured that way and is always clipped to its parent. The whole walk
    /// shares one elapsed-time budget and one descended-into set.
    /// </summary>
    private sealed class SnapScan
    {
        public readonly List<SnapRect> Rects = new();

        private readonly HashSet<IntPtr> _descended = new();
        private readonly HashSet<IntPtr>? _exclude;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly int _budgetMs;

        public SnapScan(HashSet<IntPtr>? exclude, int budgetMs)
        {
            _exclude = exclude;
            _budgetMs = budgetMs;
        }

        /// <summary>Top-level window. False stops the enumeration; true means "carry on".</summary>
        public bool CollectTopLevel(IntPtr hwnd)
        {
            if (_clock.ElapsedMilliseconds > _budgetMs)
                return false;
            if (Skip(hwnd) || !IsCapturableWindow(hwnd))
                return true;
            if (FrameBoundsOf(hwnd) is not Rectangle frame || frame.Width <= 0 || frame.Height <= 0)
                return true;

            Descend(hwnd, frame);

            // The client area is a rung of its own wherever the frame adds chrome around it.
            if (ClientRectOf(hwnd) is Rectangle client && client != frame &&
                client.Width > 0 && client.Height > 0)
            {
                Rects.Add(new SnapRect(hwnd, client, IsWindow: false));
            }

            Rects.Add(new SnapRect(hwnd, frame, IsWindow: true));
            return true;
        }

        /// <summary>Child control, clipped to the parent rect it was found under.</summary>
        private bool CollectChild(IntPtr hwnd, Rectangle parent)
        {
            if (_clock.ElapsedMilliseconds > _budgetMs)
                return false;
            if (Skip(hwnd))
                return true;
            if (!GetWindowRect(hwnd, out Rect r))
                return true;

            var bounds = Rectangle.Intersect(
                Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), parent);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return true;

            Descend(hwnd, bounds);
            Rects.Add(new SnapRect(hwnd, bounds, IsWindow: false));
            return true;
        }

        /// <summary>
        /// Emits everything below <paramref name="hwnd"/> before <paramref name="hwnd"/> itself,
        /// which is what leaves the finished list reading deepest-first. Each handle is descended
        /// into at most once, so a window reachable by two routes can't loop the walk.
        /// </summary>
        private void Descend(IntPtr hwnd, Rectangle bounds)
        {
            if (!_descended.Add(hwnd))
                return;
            try
            {
                EnumChildWindows(hwnd, (child, _) => CollectChild(child, bounds), IntPtr.Zero);
            }
            catch (Exception)
            {
            }
        }

        private bool Skip(IntPtr hwnd) =>
            (_exclude is not null && _exclude.Contains(hwnd)) || !IsWindowVisible(hwnd);
    }

    /// <summary>
    /// Whether a visible top-level window can be what the user is pointing at. Cloaked windows
    /// (UWP ghosts) are enumerable but not on screen. Two extended-style pairs also disqualify
    /// one outright: WS_EX_TOOLWINDOW with WS_EX_NOACTIVATE can never take focus, and
    /// WS_EX_TRANSPARENT with WS_EX_LAYERED can never take a click. Either way the pixels the
    /// cursor is over belong to the window underneath.
    /// </summary>
    private static bool IsCapturableWindow(IntPtr hwnd)
    {
        if (DwmGetWindowAttributeInt(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        if (IgnoreClassNames.Contains(ClassNameOf(hwnd), StringComparer.OrdinalIgnoreCase))
            return false;

        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExToolWindow) != 0 && (exStyle & WsExNoActivate) != 0)
            return false;
        if ((exStyle & WsExTransparent) != 0 && (exStyle & WsExLayered) != 0)
            return false;

        return true;
    }

    /// <summary>
    /// The window's visible frame: the DWM extended frame bounds, which exclude the invisible
    /// resize border, falling back to GetWindowRect where DWM has no answer. Null if neither
    /// call succeeds.
    /// </summary>
    private static Rectangle? FrameBoundsOf(IntPtr hwnd)
    {
        if (DwmGetWindowAttributeRect(hwnd, DwmwaExtendedFrameBounds, out Rect frame, Marshal.SizeOf<Rect>()) != 0 &&
            !GetWindowRect(hwnd, out frame))
        {
            return null;
        }
        return Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
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

    /// <summary>
    /// The friendly app name behind a window ("Notepad", "Chrome"), for the pill the window
    /// selector floats over its target. Resolved for the ONE hovered window on demand, never
    /// for the whole enumeration — opening every process in the list would cost more than the
    /// enumeration itself. Falls back to the window title, then to nothing.
    ///
    /// NEVER call this on the UI thread. Like the rest of this class it opens the target
    /// process and reads its PE version resource off disk, and for a protected or elevated
    /// process it throws before it fails. Callers on a hover path resolve it in the
    /// background and cache the answer per window handle.
    /// </summary>
    public static string AppNameFor(IntPtr hwnd, string fallbackTitle)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                string? description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                    return description.Trim();
                if (!string.IsNullOrWhiteSpace(process.ProcessName))
                    return process.ProcessName;
            }
        }
        catch
        {
            // Access denied (elevated or protected process), or it exited between the hover
            // and this call. The title is a fine answer and this runs on the paint path.
        }
        return fallbackTitle ?? string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
