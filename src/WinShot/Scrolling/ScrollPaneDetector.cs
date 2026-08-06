using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Scrolling;

public enum PaneSource
{
    None,
    /// <summary>A real child HWND with a vertical scrollbar — exact and trustworthy.</summary>
    Win32Exact,
    /// <summary>UIA ScrollPattern from a first-party framework (WPF/WinUI) — trustworthy.</summary>
    UiaVerified,
    /// <summary>UIA answer from a Chromium/Electron/proxy tree, or a coarse HWND fallback —
    /// must be confirmed by the wheel probe before it narrows a capture.</summary>
    UiaUnverified,
}

public readonly record struct PaneInfo(SD.Rectangle RectPx, PaneSource Source);

/// <summary>
/// Answers "which rectangle scrolls when the wheel turns at this point?" without touching
/// pixels: Tier 0 asks Win32 directly (child HWND with a real scrollbar — free and exact);
/// Tier 1 asks the UI Automation tree for the nearest vertically scrollable ancestor, waking
/// Chromium's lazily-built accessibility tree first. Callers treat any answer as a HINT —
/// the wheel probe (ScrollProbe) remains the ground truth for what actually moves.
/// ponytail: managed System.Windows.Automation behind our own timeout; swap to raw COM
/// CUIAutomation8 with cache requests if hover latency ever demands it.
/// </summary>
public static class ScrollPaneDetector
{
    private const int UiaRootObjectId = -25;
    private const uint ObjIdVScroll = 0xFFFFFFFB; // OBJID_VSCROLL
    private const int WmGetObject = 0x003D;

    /// <summary>
    /// Blocking; call from a worker thread. <paramref name="timeout"/> bounds the UIA tier —
    /// a hung provider must never freeze a selector. Returns null when nothing scrollable
    /// is found under the point.
    /// </summary>
    public static PaneInfo? FromPoint(SD.Point screenPx, TimeSpan timeout)
    {
        try
        {
            IntPtr root = RootWindowFromPoint(screenPx);
            if (root == IntPtr.Zero)
                return null;

            if (Win32ScrollablePane(root, screenPx) is SD.Rectangle exact)
                return new PaneInfo(exact, PaneSource.Win32Exact);

            bool chromium = IsChromiumFamily(root, out IntPtr renderWidget);
            if (chromium && renderWidget != IntPtr.Zero)
                WakeChromiumAccessibility(renderWidget);

            PaneInfo? uia = RunWithTimeout(() => UiaScrollablePane(screenPx, chromium), timeout);
            if (uia is not null)
                return uia;

            // Coarse Chromium fallback: the render widget HWND bounds the whole web area.
            if (renderWidget != IntPtr.Zero && GetWindowRect(renderWidget, out Rect wr))
            {
                var rect = SD.Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);
                if (rect.Contains(screenPx))
                    return new PaneInfo(rect, PaneSource.UiaUnverified);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("ScrollPaneDetector failed (non-fatal)", ex);
            return null;
        }
    }

    /// <summary>
    /// Hover-time lookup for the region selector: Tier 0 only (child-HWND scrollbars +
    /// the Chromium render-widget rect), starting from a KNOWN root so the selector's own
    /// full-screen overlay can't hijack the hit test. Synchronous and cheap (&lt;1ms) — no
    /// UIA here; the full ladder (UIA + wheel probe) runs after the selection confirms,
    /// once the overlay is gone.
    /// </summary>
    public static SD.Rectangle? QuickPaneRect(IntPtr root, SD.Point screenPx)
    {
        try
        {
            if (root == IntPtr.Zero)
                return null;
            if (Win32ScrollablePane(root, screenPx) is SD.Rectangle exact)
                return exact;
            IntPtr widget = FindWindowEx(root, IntPtr.Zero, "Chrome_RenderWidgetHostHWND", null);
            if (widget != IntPtr.Zero && GetWindowRect(widget, out Rect wr))
            {
                var rect = SD.Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);
                if (rect.Contains(screenPx))
                    return rect;
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Element-level lookup for Alt-hover in the region selector: the deepest UIA element
    /// whose bounds contain the point, found by GEOMETRIC descent from the window root
    /// (child-contains-point at each level). No ElementFromPoint — hit-testing would land
    /// on the selector's own overlay; geometry works against the frozen screen. Blocking;
    /// call from a worker thread.
    /// </summary>
    public static SD.Rectangle? ElementRectFromPoint(IntPtr root, SD.Point p, TimeSpan timeout)
    {
        try
        {
            if (root == IntPtr.Zero)
                return null;
            bool chromium = IsChromiumFamily(root, out IntPtr renderWidget);
            if (chromium && renderWidget != IntPtr.Zero)
                WakeChromiumAccessibility(renderWidget);
            var task = Task.Run(() =>
            {
                // The tree materializes asynchronously after a Chromium wake — retry.
                int[] delays = chromium ? new[] { 0, 160, 350 } : new[] { 0 };
                foreach (int delay in delays)
                {
                    if (delay > 0)
                        Thread.Sleep(delay);
                    if (DescendToElement(root, p) is SD.Rectangle found)
                        return (SD.Rectangle?)found;
                }
                return null;
            });
            return task.Wait(timeout) ? task.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SD.Rectangle? DescendToElement(IntPtr root, SD.Point p)
    {
        AutomationElement? current;
        try
        {
            current = AutomationElement.FromHandle(root);
        }
        catch (Exception)
        {
            return null;
        }

        var walker = TreeWalker.ControlViewWalker;
        SD.Rectangle? best = null;
        int siblingBudget = 600; // total cross-process rect reads we are willing to pay
        for (int depth = 0; depth < 24 && current is not null && siblingBudget > 0; depth++)
        {
            AutomationElement? child;
            try
            {
                child = walker.GetFirstChild(current);
            }
            catch (Exception)
            {
                break;
            }
            AutomationElement? hit = null;
            while (child is not null && --siblingBudget > 0)
            {
                try
                {
                    System.Windows.Rect r = child.Current.BoundingRectangle;
                    if (!r.IsEmpty && r.Width >= 8 && r.Height >= 8 &&
                        p.X >= r.X && p.X < r.X + r.Width && p.Y >= r.Y && p.Y < r.Y + r.Height)
                    {
                        hit = child;
                        best = new SD.Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                        break; // descend into the first containing child (documents nest cleanly)
                    }
                    child = walker.GetNextSibling(child);
                }
                catch (Exception)
                {
                    break;
                }
            }
            if (hit is null)
                break;
            current = hit;
        }
        return best;
    }

    // ---- Tier 0: Win32 ----

    private static IntPtr RootWindowFromPoint(SD.Point p)
    {
        IntPtr hwnd = WindowFromPoint(new Point32 { X = p.X, Y = p.Y });
        return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, 2 /* GA_ROOT */);
    }

    /// <summary>Walks to the deepest real child under the point, then back up looking for a
    /// window that owns a vertical scrollbar. Modern single-HWND apps simply return null.</summary>
    private static SD.Rectangle? Win32ScrollablePane(IntPtr root, SD.Point p)
    {
        var chain = new List<IntPtr> { root };
        IntPtr current = root;
        for (int depth = 0; depth < 32; depth++)
        {
            var client = new Point32 { X = p.X, Y = p.Y };
            ScreenToClient(current, ref client);
            IntPtr child = ChildWindowFromPointEx(current, client, CwpSkipInvisible | CwpSkipTransparent);
            if (child == IntPtr.Zero || child == current)
                break;
            chain.Add(child);
            current = child;
        }

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            IntPtr hwnd = chain[i];
            bool hasStyle = ((long)GetWindowLongPtr(hwnd, -16 /* GWL_STYLE */) & 0x00200000 /* WS_VSCROLL */) != 0;
            bool hasBar = false;
            if (!hasStyle)
            {
                var sbi = new ScrollBarInfo { cbSize = Marshal.SizeOf<ScrollBarInfo>() };
                hasBar = GetScrollBarInfo(hwnd, ObjIdVScroll, ref sbi) &&
                    (sbi.rgstate0 & 0x8000 /* STATE_SYSTEM_INVISIBLE */) == 0;
            }
            if ((hasStyle || hasBar) && GetWindowRect(hwnd, out Rect r))
            {
                var rect = SD.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                // The root itself having WS_VSCROLL means "whole window scrolls" — true, but
                // not a pane; only trust children (a root rect adds nothing over the window).
                if (hwnd != root || i > 0)
                    return rect;
            }
        }
        return null;
    }

    // ---- Tier 1: UIA ----

    private static bool IsChromiumFamily(IntPtr root, out IntPtr renderWidget)
    {
        renderWidget = FindWindowEx(root, IntPtr.Zero, "Chrome_RenderWidgetHostHWND", null);
        if (renderWidget != IntPtr.Zero)
            return true;
        var sb = new StringBuilder(64);
        GetClassName(root, sb, sb.Capacity);
        return sb.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal);
    }

    /// <summary>Chromium builds its accessibility tree lazily and tears it down when idle.
    /// Asking the render widget for its UIA root switches the renderer's accessibility on;
    /// the tree then materializes asynchronously (callers should retry briefly).</summary>
    private static void WakeChromiumAccessibility(IntPtr renderWidget) =>
        SendMessageTimeout(renderWidget, WmGetObject, IntPtr.Zero, new IntPtr(UiaRootObjectId),
            0x0002 /* SMTO_ABORTIFHUNG */, 200, out _);

    private static PaneInfo? UiaScrollablePane(SD.Point p, bool chromium)
    {
        // The tree may still be materializing after a wake — retry with backoff.
        int[] delays = chromium ? new[] { 0, 120, 300 } : new[] { 0 };
        foreach (int delay in delays)
        {
            if (delay > 0)
                Thread.Sleep(delay);
            AutomationElement? element;
            try
            {
                element = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
            }
            catch (Exception)
            {
                continue;
            }

            var walker = TreeWalker.RawViewWalker;
            for (int hops = 0; element is not null && hops < 20; hops++)
            {
                if (TryGetVerticalScrollRect(element, p) is SD.Rectangle rect)
                    return new PaneInfo(rect,
                        chromium ? PaneSource.UiaUnverified : PaneSource.UiaVerified);
                try
                {
                    element = walker.GetParent(element);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
        return null;
    }

    private static SD.Rectangle? TryGetVerticalScrollRect(AutomationElement element, SD.Point p)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out object patternObj) ||
                patternObj is not ScrollPattern scroll || !scroll.Current.VerticallyScrollable)
                return null;
            System.Windows.Rect r = element.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width < 80 || r.Height < 80)
                return null;
            var rect = new SD.Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            return rect.Contains(p) ? rect : null;
        }
        catch (Exception)
        {
            return null; // provider hiccup on one node — keep walking
        }
    }

    private static PaneInfo? RunWithTimeout(Func<PaneInfo?> work, TimeSpan timeout)
    {
        var task = Task.Run(work);
        // ponytail: an abandoned lookup's thread may linger until the provider responds —
        // acceptable; the alternative (raw COM + IUIAutomation2.ConnectionTimeout) is the
        // documented upgrade path.
        return task.Wait(timeout) ? task.Result : null;
    }

    // ---- P/Invoke ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollBarInfo
    {
        public int cbSize;
        public Rect rcScrollBar;
        public int dxyLineButton, xyThumbTop, xyThumbBottom, reserved;
        public uint rgstate0, rgstate1, rgstate2, rgstate3, rgstate4, rgstate5;
    }

    private const uint CwpSkipInvisible = 0x0001;
    private const uint CwpSkipTransparent = 0x0004;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point32 point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, Point32 point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref Point32 point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetScrollBarInfo(IntPtr hwnd, uint objId, ref ScrollBarInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxLength);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);
}
