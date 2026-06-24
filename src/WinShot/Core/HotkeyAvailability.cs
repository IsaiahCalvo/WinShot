using System.Runtime.InteropServices;
using System.Threading;

namespace WinShot.Core;

public enum HotkeyAvailabilityStatus
{
    Invalid,
    Available,
    Unavailable,
}

public static class HotkeyAvailability
{
    private static int _nextProbeId = 40_000;

    public static HotkeyAvailabilityStatus Check(string gesture)
    {
        if (!HotkeyManager.TryGetRegistrationParts(gesture, out uint modifiers, out uint virtualKey))
            return HotkeyAvailabilityStatus.Invalid;

        int id = Interlocked.Increment(ref _nextProbeId);
        if (!RegisterHotKey(IntPtr.Zero, id, modifiers, virtualKey))
            return HotkeyAvailabilityStatus.Unavailable;

        UnregisterHotKey(IntPtr.Zero, id);
        return HotkeyAvailabilityStatus.Available;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
