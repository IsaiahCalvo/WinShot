using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>Restores keyboard focus when a global hotkey opens a selector over another app.</summary>
internal static class SelectorForeground
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>
    /// True when the window under the cursor is not one of the selector's own surfaces -
    /// i.e. something is sitting ABOVE the overlay and will swallow clicks inside its rect.
    /// </summary>
    internal static bool IsCovered(IntPtr windowUnderCursor, IReadOnlyList<IntPtr> surfaces)
    {
        if (windowUnderCursor == IntPtr.Zero)
            return false;
        for (int i = 0; i < surfaces.Count; i++)
        {
            if (surfaces[i] == windowUnderCursor)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Re-raises the selector surfaces to the top of the topmost band when something has
    /// covered them. Form.TopMost only wins the band at the moment it is set: a window that
    /// raises itself afterwards (a crash dialog, an always-on-top utility) lands above the
    /// overlay and eats every click inside its rect. The hover highlight keeps tracking
    /// regardless because it runs off the cursor-follow timer, so the selector looks alive
    /// while clicking the highlighted window does nothing and only a drag started outside
    /// it works. Cheap: one WindowFromPoint per tick, SetWindowPos only when actually
    /// covered. Surfaces are re-raised in order, so pass the toolbar last.
    /// </summary>
    public static void KeepOnTop(IReadOnlyList<IntPtr> surfaces, SD.Point cursor)
    {
        IntPtr under = WindowEnumerator.TopLevelWindowFromPoint(cursor);
        if (!IsCovered(under, surfaces))
            return;

        for (int i = 0; i < surfaces.Count; i++)
        {
            if (surfaces[i] != IntPtr.Zero)
                SetWindowPos(surfaces[i], HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    public static void Restore(WF.Form form)
    {
        IntPtr hwnd = form.Handle;
        if (hwnd == IntPtr.Zero)
            return;

        IntPtr foreground = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = foreground != IntPtr.Zero
            ? GetWindowThreadProcessId(foreground, out _)
            : 0u;
        bool attached = ShouldAttachInput(currentThread, foregroundThread) &&
            AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    internal static bool ShouldAttachInput(uint currentThread, uint foregroundThread) =>
        foregroundThread != 0 && foregroundThread != currentThread;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
