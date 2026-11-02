using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using WF = System.Windows.Forms;

namespace WinShot.Core;

/// <summary>
/// A drop-in for <c>new WF.Timer { Interval = Motion.FrameIntervalMs }</c> that ticks once per
/// display refresh instead of every ~10 ms.
///
/// Why: a window timer is not tied to the screen. On a 60 Hz panel a 10 ms timer lands one or
/// two ticks inside each 16.7 ms frame, and which one the compositor picks up drifts, so a
/// slide advances 10 ms on one frame and 20 ms on the next. That uneven step is the faint
/// judder in the corner card, the selector's magnifier and every other WinForms surface; WPF
/// windows never had it because WPF already paints on the compositor's beat. A WM_TIMER is
/// also the lowest-priority message Windows has, so under load ticks were dropped outright.
/// It also paints ~100 times a second on a 60 Hz screen, so this does less work, not more.
///
/// Every timer on a thread shares one clock: one tick per refresh, all of them in the same
/// frame. The <see cref="Interval"/> property exists only so call sites read the same.
/// </summary>
internal sealed class FrameTimer : IDisposable
{
    private bool _disposed;
    private FrameClock? _clock;

    public event EventHandler? Tick;

    /// <summary>Ignored; the display decides. Kept so a WF.Timer call site swaps cleanly.</summary>
    public int Interval { get; set; } = Motion.FrameIntervalMs;

    public bool Enabled
    {
        get => IsRunning;
        set
        {
            if (value) Start();
            else Stop();
        }
    }

    internal bool IsRunning { get; private set; }

    public void Start()
    {
        if (_disposed || IsRunning)
            return;
        IsRunning = true;
        _clock = FrameClock.ForCurrentThread();
        _clock.Add(this);
    }

    public void Stop()
    {
        if (!IsRunning)
            return;
        IsRunning = false;
        // Stopped from another thread (a finalizer, a test's cleanup): leave the list alone
        // and let the owning clock drop it on its next frame.
        if (_clock is { IsOwnerThread: true } clock)
            clock.Remove(this);
    }

    public void Dispose()
    {
        Stop();
        _disposed = true;
        Tick = null;
    }

    internal void Raise() => Tick?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// One per UI thread. A background thread waits for the display's vertical blank and posts a
/// message to a hidden message-only window on the UI thread, which then ticks every running
/// <see cref="FrameTimer"/>. Posting (rather than a timer) means the tick is delivered through
/// the same message loop WinForms, WPF and the tests' DoEvents pumps already run, so nothing
/// that drives a WF.Timer today needs to change.
///
/// At most one tick is ever queued: if the UI thread is busy for longer than a frame, the
/// missed refreshes collapse into the next tick instead of piling up, and input still gets
/// the gap between ticks.
///
/// Where there is no real vertical blank to wait for (a remote session, a display that is
/// asleep, a headless test desktop) the wait returns at once; the pacer notices and falls back
/// to sleeping out <see cref="Motion.FrameIntervalMs"/>, which is what the old timer did.
/// </summary>
internal sealed class FrameClock
{
    [ThreadStatic]
    private static FrameClock? _current;

    private const int WmTick = 0x0400 + 0x2F1; // WM_USER-based, private to this window
    private static readonly IntPtr HwndMessage = new(-3);

    /// <summary>A wait that returns sooner than this after the last tick did not wait for a
    /// refresh. Short enough to keep a 360 Hz panel's 2.8 ms frames.</summary>
    private static readonly long MinPeriodTicks = Stopwatch.Frequency * 25 / 10_000;

    private readonly List<FrameTimer> _timers = new();
    private readonly ManualResetEventSlim _active = new(false);
    private readonly TickWindow _window;
    private readonly IntPtr _hwnd;
    private readonly Thread _ownerThread = Thread.CurrentThread;
    private int _pending;

    private FrameClock()
    {
        _window = new TickWindow(this);
        _window.CreateHandle(new WF.CreateParams { Parent = HwndMessage });
        _hwnd = _window.Handle;
        var pacer = new Thread(PaceLoop)
        {
            IsBackground = true,
            Name = "WinShot frame clock",
            Priority = ThreadPriority.AboveNormal,
        };
        pacer.Start();
    }

    public static FrameClock ForCurrentThread() => _current ??= new FrameClock();

    /// <summary>Running timers on this thread; for tests.</summary>
    internal int Count => _timers.Count;

    internal bool IsOwnerThread => Thread.CurrentThread == _ownerThread;

    public void Add(FrameTimer timer)
    {
        if (!_timers.Contains(timer))
            _timers.Add(timer);
        _active.Set();
    }

    public void Remove(FrameTimer timer)
    {
        _timers.Remove(timer);
        if (_timers.Count == 0)
            _active.Reset();
    }

    private void OnTick()
    {
        try
        {
            if (_timers.Count == 0)
                return;
            // A snapshot: a tick may start, stop or dispose any timer, its own included.
            // A timer started during this frame first ticks on the next one, as WF.Timer did.
            FrameTimer[] due = _timers.ToArray();
            foreach (FrameTimer timer in due)
            {
                if (!timer.IsRunning)
                    continue;
                try
                {
                    timer.Raise();
                }
                catch (Exception ex)
                {
                    // One broken frame loop must not freeze every other animation on screen.
                    Log.Error("Frame tick failed", ex);
                }
            }
        }
        finally
        {
            _timers.RemoveAll(static t => !t.IsRunning);
            if (_timers.Count == 0)
                _active.Reset();
            // Cleared after the frame, not before: a pacer that wakes while this frame is
            // still painting must not queue another one behind it.
            Volatile.Write(ref _pending, 0);
        }
    }

    private void PaceLoop()
    {
        var vblank = new VBlankSource();
        long last = Stopwatch.GetTimestamp();
        while (true)
        {
            // Idle until a timer starts, waking now and then to see whether the UI thread
            // that owns this clock has ended (each test thread gets its own clock).
            if (!_active.Wait(5000))
            {
                if (!_ownerThread.IsAlive)
                    return;
                continue;
            }

            bool waited = vblank.Wait();
            long now = Stopwatch.GetTimestamp();
            if (!waited || now - last < MinPeriodTicks)
            {
                // No real refresh to pace by. Sleep out the old timer's interval instead of
                // spinning; the tween clocks read wall time, so motion stays correct.
                int elapsedMs = (int)((now - last) * 1000 / Stopwatch.Frequency);
                Thread.Sleep(Math.Max(1, Motion.FrameIntervalMs - elapsedMs));
                now = Stopwatch.GetTimestamp();
            }
            last = now;

            if (!_active.IsSet)
                continue;
            if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
                continue;
            if (!PostMessage(_hwnd, WmTick, IntPtr.Zero, IntPtr.Zero))
            {
                // The UI thread (and its window) is gone; nothing will ever tick again.
                if (!_ownerThread.IsAlive)
                    return;
                Volatile.Write(ref _pending, 0);
            }
        }
    }

    private sealed class TickWindow : WF.NativeWindow
    {
        private readonly FrameClock _owner;

        public TickWindow(FrameClock owner) => _owner = owner;

        protected override void WndProc(ref WF.Message m)
        {
            if (m.Msg == WmTick)
            {
                _owner.OnTick();
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// Waits for the primary display's vertical blank through DXGI. Any failure (no output,
    /// a display change, a remote session) drops the output and reports "did not wait"; the
    /// next call looks it up again, so a monitor swap heals by itself.
    /// </summary>
    private sealed class VBlankSource
    {
        private IDXGIOutput? _output;
        private long _retryAfter;

        public bool Wait()
        {
            try
            {
                if (_output is null)
                {
                    if (Stopwatch.GetTimestamp() < _retryAfter)
                        return false;
                    _output = FindPrimaryOutput();
                    if (_output is null)
                    {
                        _retryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
                        return false;
                    }
                }

                // Throws on failure (Vortice turns the HRESULT into an exception).
                _output.WaitForVBlank();
                return true;
            }
            catch (Exception)
            {
                // Fall through: same as a failed wait.
            }

            _output?.Dispose();
            _output = null;
            _retryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
            return false;
        }

        private static IDXGIOutput? FindPrimaryOutput()
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIOutput? first = null;
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success && adapter is not null; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success && output is not null; o++)
                    {
                        var bounds = output.Description.DesktopCoordinates;
                        if (bounds.Left <= 0 && bounds.Top <= 0 && bounds.Right > 0 && bounds.Bottom > 0)
                        {
                            first?.Dispose();
                            return output;
                        }
                        if (first is null)
                            first = output;
                        else
                            output.Dispose();
                    }
                }
            }
            return first;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
