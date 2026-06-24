using System.Threading;

namespace WinShot.Core;

/// <summary>Tracks when WinShot is recording a hotkey instead of running one.</summary>
public static class HotkeyInputCapture
{
    private static int _activeCount;

    public static bool IsActive => Volatile.Read(ref _activeCount) > 0;

    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _activeCount);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _activeCount);
        }
    }
}
