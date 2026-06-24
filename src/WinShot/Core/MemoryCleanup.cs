using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace WinShot.Core;

internal static class MemoryCleanup
{
    private const long MinIntervalMs = 15_000;
    private const int IdleDelayMs = 5_000;
    private const long HighPrivateBytes = 220L * 1024 * 1024;
    private const long HighWorkingSetBytes = 600L * 1024 * 1024;
    private static long _lastCleanupTick;
    private static long _lastRequestTick;
    private static int _cleanupScheduled;

    public static void Request()
    {
        Interlocked.Exchange(ref _lastRequestTick, Environment.TickCount64);
        if (Interlocked.Exchange(ref _cleanupScheduled, 1) == 1)
            return;

        _ = Task.Run(RunWhenDueAsync);
    }

    private static async Task RunWhenDueAsync()
    {
        try
        {
            while (true)
            {
                long requestTick = Interlocked.Read(ref _lastRequestTick);
                long lastCleanupTick = Interlocked.Read(ref _lastCleanupTick);
                long now = Environment.TickCount64;
                long cleanupDueTick = lastCleanupTick == 0 ? now : lastCleanupTick + MinIntervalMs;
                long idleDueTick = requestTick + IdleDelayMs;
                long dueTick = Math.Max(cleanupDueTick, idleDueTick);
                if (dueTick > now)
                    await Task.Delay((int)Math.Min(int.MaxValue, dueTick - now)).ConfigureAwait(false);

                if (requestTick != Interlocked.Read(ref _lastRequestTick))
                    continue;

                var snapshot = MemorySnapshot.Current();
                if (snapshot.PrivateBytes >= HighPrivateBytes)
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }

                if (snapshot.WorkingSetBytes >= HighWorkingSetBytes)
                    TrimWorkingSet();

                Interlocked.Exchange(ref _lastCleanupTick, Environment.TickCount64);

                if (requestTick == Interlocked.Read(ref _lastRequestTick))
                    return;
            }
        }
        finally
        {
            Volatile.Write(ref _cleanupScheduled, 0);
            if (Interlocked.Read(ref _lastRequestTick) > Interlocked.Read(ref _lastCleanupTick))
                Request();
        }
    }

    private static void TrimWorkingSet()
    {
        try
        {
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            // Cleanup is best-effort and must never affect capture flow.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    private readonly record struct MemorySnapshot(long PrivateBytes, long WorkingSetBytes)
    {
        public static MemorySnapshot Current()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return new MemorySnapshot(process.PrivateMemorySize64, process.WorkingSet64);
            }
            catch
            {
                return new MemorySnapshot(0, 0);
            }
        }
    }
}
