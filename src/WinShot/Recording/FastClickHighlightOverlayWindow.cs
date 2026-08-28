using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastClickHighlightOverlayWindow : AlphaOverlayWindow, IRecordingOverlay
{
    private const int MaxConcurrentRings = 24;
    private const int RingLifetimeMs = 450;
    private static readonly SD.Color RingColor = ThemePalette.Accent;

    private readonly SD.Rectangle _regionPx;
    private readonly HookProc _hookProc;
    private readonly bool _installHook;
    private readonly List<Ring> _rings = new(MaxConcurrentRings);
    private readonly WF.Timer _timer = new() { Interval = WinShot.Core.Motion.FrameIntervalMs };
    private IDisposable? _motionClock;
    private readonly double _scale;
    private IntPtr _hook;
    private volatile bool _paused;

    public FastClickHighlightOverlayWindow(SD.Rectangle regionScreenPx)
        : this(regionScreenPx, installHook: true)
    {
    }

    public static FastClickHighlightOverlayWindow CreateForSmokeTest(SD.Rectangle regionScreenPx) =>
        new(regionScreenPx, installHook: false);

    private FastClickHighlightOverlayWindow(SD.Rectangle regionScreenPx, bool installHook)
    {
        _regionPx = regionScreenPx;
        _installHook = installHook;
        _hookProc = MouseHookCallback;
        _scale = RecordingMonitorDpi.ScaleFor(regionScreenPx);

        _timer.Tick += (_, _) => AdvanceRings();
    }

    private int S(int logical) => (int)Math.Round(logical * _scale);

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            _rings.Clear();
            PresentEmpty();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PresentEmpty();
        if (_installHook)
            InstallHook();
    }

    protected override void OnClosed(EventArgs e)
    {
        RemoveHook();
        StopMotion();
        _timer.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Draws the live rings into one ARGB frame covering their union and presents it,
    /// so the eased accent fade blends against the real desktop (the old magenta-key
    /// window turned every semi-transparent ring pixel purple in recordings).
    /// </summary>
    private void RenderFrame()
    {
        if (_rings.Count == 0)
        {
            PresentEmpty();
            return;
        }

        int maxRing = S(44) + 8;
        long now = Environment.TickCount64;

        SD.Rectangle union = SD.Rectangle.Empty;
        foreach (var ring in _rings)
        {
            var bounds = new SD.Rectangle(ring.X - maxRing / 2, ring.Y - maxRing / 2, maxRing, maxRing);
            union = union.IsEmpty ? bounds : SD.Rectangle.Union(union, bounds);
        }
        union.Intersect(new SD.Rectangle(0, 0, _regionPx.Width, _regionPx.Height));
        if (union.Width <= 0 || union.Height <= 0)
        {
            PresentEmpty();
            return;
        }

        using var frame = new SD.Bitmap(union.Width, union.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(frame))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var ring in _rings)
            {
                double progress = Math.Clamp((now - ring.StartMs) / (double)RingLifetimeMs, 0, 1);
                double eased = 1 - Math.Pow(1 - progress, 2);
                float size = (float)(S(44) * (0.25 + 0.75 * eased));
                int alpha = (int)Math.Round(242 * (1 - progress));
                if (alpha <= 0)
                    continue;

                using var pen = new SD.Pen(SD.Color.FromArgb(alpha, RingColor), Math.Max(3f, (float)(3 * _scale)));
                g.DrawEllipse(
                    pen,
                    ring.X - union.X - size / 2,
                    ring.Y - union.Y - size / 2,
                    size,
                    size);
            }
        }

        Present(frame, new SD.Point(_regionPx.X + union.X, _regionPx.Y + union.Y));
    }

    private void AdvanceRings()
    {
        long now = Environment.TickCount64;
        _rings.RemoveAll(r => now - r.StartMs >= RingLifetimeMs);
        if (_rings.Count == 0)
            StopMotion();
        RenderFrame();
    }

    private void AddRing(int screenX, int screenY)
    {
        if (_paused || IsDisposed)
            return;
        if (_rings.Count >= MaxConcurrentRings)
            return;

        _rings.Add(new Ring(screenX - _regionPx.X, screenY - _regionPx.Y, Environment.TickCount64));
        if (!_timer.Enabled)
            StartMotion();
        RenderFrame();
    }

    private void InstallHook()
    {
        _hook = SetWindowsHookExW(WhMouseLl, _hookProc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Error($"Failed to install mouse hook for fast click highlights (error {Marshal.GetLastWin32Error()})");
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero)
            return;
        if (!UnhookWindowsHookEx(_hook))
            Log.Error("Failed to remove fast click-highlight mouse hook");
        _hook = IntPtr.Zero;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_paused)
        {
            long msg = wParam.ToInt64();
            if (msg is WmLButtonDown or WmRButtonDown or WmMButtonDown)
            {
                var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                int x = data.pt.X;
                int y = data.pt.Y;
                if (_regionPx.Contains(x, y) && !IsDisposed)
                    BeginInvoke(new Action(() => AddRing(x, y)));
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private sealed record Ring(int X, int Y, long StartMs);

    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public Point32 pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>
    /// Animation ticks only land on time while the high-resolution clock is held, so it is
    /// acquired with the timer and released with it — never for longer.
    /// </summary>
    private void StartMotion()
    {
        _motionClock ??= WinShot.Core.Motion.Acquire();
        _timer.Start();
    }

    private void StopMotion()
    {
        _timer.Stop();
        _motionClock?.Dispose();
        _motionClock = null;
    }

}
