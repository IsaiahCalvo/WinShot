using System.Runtime.InteropServices;

namespace WinShot.Core;

/// <summary>
/// One motion vocabulary for the whole app: the animation frame interval, the standard
/// durations, the shared easings, and the high-resolution timer scope that makes any of it
/// actually hit its interval.
///
/// Windows' default timer tick is ~15.6 ms, so a WinForms timer asking for 8 ms silently runs
/// at ~64 fps and coalesces under load — that is the stutter. timeBeginPeriod(1) drops the tick
/// to 1 ms, but it is process-wide and costs power, so it is reference-counted here and held
/// only while something is actually animating.
/// </summary>
internal static class Motion
{
    /// <summary>
    /// Animation tick. USER32 clamps window timers to 10 ms, so this asks for the floor and
    /// lets high-refresh displays take everything they can get.
    /// </summary>
    public const int FrameIntervalMs = 8;

    /// <summary>Hover highlight gliding between elements (ShareX parity — see RectangleTween).</summary>
    public const int GlideDurationMs = 200;

    /// <summary>Overlay cards restacking when another capture lands.</summary>
    public const int RestackDurationMs = 280;

    /// <summary>Overlay card flying off screen on dismiss.</summary>
    public const int ExitDurationMs = 360;

    private static readonly object Gate = new();
    private static int _holders;

    /// <summary>
    /// Raises the system timer resolution for as long as the returned handle is alive.
    /// Nesting is safe; the resolution drops back when the last holder is disposed.
    /// </summary>
    public static IDisposable Acquire() => new Hold();

    /// <summary>Standard ease for motion that starts and stops in place.</summary>
    public static double EaseInOutSine(double progress)
    {
        double value = Math.Clamp(progress, 0d, 1d);
        return -(Math.Cos(Math.PI * value) - 1d) / 2d;
    }

    /// <summary>Standard ease for motion that flies out and never comes back.</summary>
    public static double EaseOutCubic(double progress)
    {
        double value = 1d - Math.Clamp(progress, 0d, 1d);
        return 1d - value * value * value;
    }

    /// <summary>Live holder count; for tests.</summary>
    internal static int Holders
    {
        get { lock (Gate) return _holders; }
    }

    private sealed class Hold : IDisposable
    {
        private bool _released;

        public Hold()
        {
            lock (Gate)
            {
                if (_holders == 0)
                    TimeBeginPeriod(1);
                _holders++;
            }
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            lock (Gate)
            {
                _holders--;
                if (_holders == 0)
                    TimeEndPeriod(1);
            }
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}
