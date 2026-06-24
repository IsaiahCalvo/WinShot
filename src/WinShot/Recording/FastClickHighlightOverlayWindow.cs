using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastClickHighlightOverlayWindow : WF.Form, IRecordingOverlay
{
    private const int MaxConcurrentRings = 24;
    private const int RingLifetimeMs = 450;
    private static readonly SD.Color TransparentKey = SD.Color.Magenta;
    private static readonly SD.Color RingColor = ThemePalette.Accent;

    private readonly SD.Rectangle _regionPx;
    private readonly HookProc _hookProc;
    private readonly bool _installHook;
    private readonly List<Ring> _rings = new(MaxConcurrentRings);
    private readonly WF.Timer _timer = new() { Interval = 16 };
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

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = TransparentKey;
        ClientSize = new SD.Size(Math.Max(1, regionScreenPx.Width), Math.Max(1, regionScreenPx.Height));
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        TransparencyKey = TransparentKey;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        _timer.Tick += (_, _) => AdvanceRings();
    }

    protected override bool ShowWithoutActivation => true;

    public static void Prewarm()
    {
        try
        {
            using var overlay = new FastClickHighlightOverlayWindow(
                new SD.Rectangle(-32000, -32000, 160, 120),
                installHook: false);
            overlay.Show();
            WF.Application.DoEvents();
            overlay.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Fast click-highlight overlay prewarm failed", ex);
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            _rings.Clear();
            Invalidate();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        MakeClickThrough();
        PositionOverRegion();
        if (_installHook)
            InstallHook();
    }

    protected override void OnClosed(EventArgs e)
    {
        RemoveHook();
        _timer.Stop();
        _timer.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_rings.Count == 0)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        long now = Environment.TickCount64;
        foreach (var ring in _rings.ToArray())
        {
            double progress = Math.Clamp((now - ring.StartMs) / (double)RingLifetimeMs, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 2);
            float size = (float)(44 * (0.25 + 0.75 * eased));
            int alpha = (int)Math.Round(242 * (1 - progress));
            if (alpha <= 0)
                continue;

            using var pen = new SD.Pen(SD.Color.FromArgb(alpha, RingColor), 3);
            e.Graphics.DrawEllipse(
                pen,
                ring.X - size / 2,
                ring.Y - size / 2,
                size,
                size);
        }
    }

    private void AdvanceRings()
    {
        long now = Environment.TickCount64;
        _rings.RemoveAll(r => now - r.StartMs >= RingLifetimeMs);
        if (_rings.Count == 0)
            _timer.Stop();
        Invalidate();
    }

    private void AddRing(int screenX, int screenY)
    {
        if (_paused || IsDisposed)
            return;
        if (_rings.Count >= MaxConcurrentRings)
            return;

        _rings.Add(new Ring(screenX - _regionPx.X, screenY - _regionPx.Y, Environment.TickCount64));
        if (!_timer.Enabled)
            _timer.Start();
        Invalidate();
    }

    private void MakeClickThrough()
    {
        int style = GetWindowLongW(Handle, GwlExstyle);
        SetWindowLongW(Handle, GwlExstyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    private void PositionOverRegion()
    {
        SetWindowPos(Handle, HwndTopmost, _regionPx.X, _regionPx.Y, _regionPx.Width, _regionPx.Height, SwpNoActivate);
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
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X; public int Y; }

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

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
