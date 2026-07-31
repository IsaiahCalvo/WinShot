using System.Runtime.InteropServices;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>Restores keyboard focus when a global hotkey opens a selector over another app.</summary>
internal static class SelectorForeground
{
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
}
