using System.Diagnostics;
using System.Runtime.InteropServices;
using W = System.Windows;
using WF = System.Windows.Forms;

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

    /// <summary>
    /// Hover highlight gliding between elements in the region selector (see RectangleTween).
    /// Do not retune this to match the other durations — 200 ms is the feel Isaiah signed off
    /// on for the selector, and it is deliberately its own number.
    /// </summary>
    public const int GlideDurationMs = 200;

    /// <summary>Overlay cards restacking when another capture lands.</summary>
    public const int RestackDurationMs = 280;

    /// <summary>Overlay card flying off screen on dismiss.</summary>
    public const int ExitDurationMs = 360;

    /// <summary>Hover / press fills and other same-place tint changes.</summary>
    public const int QuickMs = 140;

    /// <summary>One panel or pane replacing another in the same slot.</summary>
    public const int SwapMs = 220;

    /// <summary>A surface arriving on screen for the first time.</summary>
    public const int EnterDurationMs = 280;

    private static readonly object Gate = new();
    private static int _holders;

    /// <summary>
    /// Whether the user wants animation at all (Settings &gt; Accessibility &gt; Visual effects,
    /// SPI_GETCLIENTAREAANIMATION). Every tween honours it by jumping straight to its end
    /// state. Tests swap this out; nothing else should.
    /// </summary>
    internal static Func<bool> AnimationsEnabled { get; set; } = () => W.SystemParameters.ClientAreaAnimation;

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

    /// <summary>
    /// The mirror of <see cref="EaseOutCubic"/>: starts slow and accelerates away. An exit
    /// wants this, so that leaving reads as the entrance played backwards — running the
    /// entrance ease over reversed time instead dumps most of the opacity in the first third
    /// and then lingers invisible for the rest of the tween.
    /// </summary>
    public static double EaseInCubic(double progress) => 1d - EaseOutCubic(1d - progress);

    /// <summary>
    /// Overshoots past the target and settles back — the ease for a surface arriving on
    /// screen, so an entrance reads as landing rather than sliding to a halt. Starts at 0 and
    /// ends at exactly 1; in between it goes above 1, which is the whole point, so callers
    /// must not clamp the value they get.
    /// </summary>
    public static double EaseOutBack(double progress)
    {
        const double overshoot = 1.70158d;
        double value = Math.Clamp(progress, 0d, 1d) - 1d;
        return 1d + ((overshoot + 1d) * value * value * value) + (overshoot * value * value);
    }

    /// <summary>
    /// Runs a tween: one shared timer at <see cref="FrameIntervalMs"/>, wall-clock time from a
    /// stopwatch (a dropped frame shortens the animation instead of stretching it), and the
    /// high-resolution clock held for exactly as long as it runs.
    ///
    /// <paramref name="onFrame"/> receives the eased position, and is always called one last
    /// time with exactly 1.0 before <paramref name="onDone"/> — so a caller never has to paint
    /// the end state itself. Disposing the returned handle mid-flight cancels: no final frame,
    /// no <paramref name="onDone"/>. Disposing after it finished does nothing.
    ///
    /// With animation turned off system-wide the end state is applied inline and no timer is
    /// ever created.
    /// </summary>
    public static IDisposable Run(
        int durationMs,
        Func<double, double> ease,
        Action<double> onFrame,
        Action? onDone = null)
    {
        ArgumentNullException.ThrowIfNull(ease);
        ArgumentNullException.ThrowIfNull(onFrame);

        if (durationMs <= 0 || !AnimationsEnabled())
        {
            onFrame(1d);
            onDone?.Invoke();
            return Finished;
        }

        return new Tween(durationMs, ease, onFrame, onDone);
    }

    /// <summary>A handle for a tween that never started; disposing it is a no-op.</summary>
    private static readonly IDisposable Finished = new NoTween();

    private sealed class NoTween : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Tween : IDisposable
    {
        private readonly Func<double, double> _ease;
        private readonly Action<double> _onFrame;
        private readonly Action? _onDone;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly double _durationMs;
        private WF.Timer? _timer;
        private IDisposable? _hold;
        private bool _cancelled;

        public Tween(int durationMs, Func<double, double> ease, Action<double> onFrame, Action? onDone)
        {
            _durationMs = durationMs;
            _ease = ease;
            _onFrame = onFrame;
            _onDone = onDone;
            _hold = Acquire();
            _timer = new WF.Timer { Interval = FrameIntervalMs };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_timer is null)
                return;

            double progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / _durationMs, 0d, 1d);
            try
            {
                if (progress < 1d)
                {
                    _onFrame(_ease(progress));
                    return;
                }

                Release();
                _onFrame(1d);
                // A caller that cancels (or retargets) from inside the final frame has
                // replaced this tween; firing its onDone afterwards would apply the dead
                // tween's end state over the new one.
                if (!_cancelled)
                    _onDone?.Invoke();
            }
            catch (Exception ex)
            {
                // A throwing frame must not strand the process-wide timer resolution — and
                // it must not be rethrown either: a tween ticks on a WinForms timer, so the
                // exception leaves through NativeWindow's callback rather than WPF's
                // dispatcher. That pops a modal ThreadExceptionDialog (invisible, and
                // unclickable, on the hidden test desktop) and DispatcherUnhandledException
                // never sees it. Cancel the tween and log instead.
                Release();
                Log.Error("Animation frame failed; tween cancelled", ex);
            }
        }

        public void Dispose()
        {
            _cancelled = true;
            Release();
        }

        private void Release()
        {
            WF.Timer? timer = _timer;
            _timer = null;
            if (timer is not null)
            {
                timer.Tick -= OnTick;
                timer.Stop();
                timer.Dispose();
            }

            _clock.Stop();
            IDisposable? hold = _hold;
            _hold = null;
            hold?.Dispose();
        }
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
            // The released flag is read and set inside the same lock the count uses: two
            // threads disposing one handle would otherwise both get past an unguarded check
            // and unbalance timeEndPeriod. Every holder today is created and disposed by a
            // Tween on the UI thread, so this is a guard against the next caller, not a bug.
            lock (Gate)
            {
                if (_released) return;
                _released = true;
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
