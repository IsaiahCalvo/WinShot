using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Recording;

/// <summary>
/// Click-through transparent overlay covering the recorded region. A low-level
/// mouse hook shows an expanding accent ring at every click so clicks are
/// visible in the recording. Deliberately NOT excluded from capture: the rings
/// must appear in both the MP4 (desktop duplication) and the GIF (GDI grabs).
/// Construct and show on the UI thread; close to release the hook.
/// </summary>
public partial class ClickHighlightOverlayWindow : Window
{
    private const int MaxConcurrentRings = 24;

    private static readonly SolidColorBrush RingBrush = CreateFrozenBrush();

    private readonly SD.Rectangle _regionPx;
    private readonly HookProc _hookProc; // field keeps the delegate alive for the native hook
    private IntPtr _hook;
    private volatile bool _paused;

    public ClickHighlightOverlayWindow(SD.Rectangle regionScreenPx)
    {
        InitializeComponent();
        _regionPx = regionScreenPx;
        _hookProc = MouseHookCallback;

        SourceInitialized += (_, _) =>
        {
            MakeClickThrough();
            PositionOverRegion();
        };
        Loaded += (_, _) =>
        {
            PositionOverRegion(); // WPF layout may resize between init and show
            InstallHook();
        };
        Closed += (_, _) => RemoveHook();
    }

    /// <summary>While paused, clicks are ignored (no rings are drawn).</summary>
    public void SetPaused(bool paused) => _paused = paused;

    private static SolidColorBrush CreateFrozenBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF));
        brush.Freeze();
        return brush;
    }

    private void MakeClickThrough()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLongW(handle, GwlExstyle);
        SetWindowLongW(handle, GwlExstyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    private void PositionOverRegion()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, HwndTopmost, _regionPx.X, _regionPx.Y, _regionPx.Width, _regionPx.Height, SwpNoActivate);
    }

    private void InstallHook()
    {
        _hook = SetWindowsHookExW(WhMouseLl, _hookProc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Error($"Failed to install mouse hook for click highlights (error {Marshal.GetLastWin32Error()})");
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        if (!UnhookWindowsHookEx(_hook))
            Log.Error("Failed to remove click-highlight mouse hook");
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
                if (_regionPx.Contains(x, y))
                {
                    // Keep the hook callback fast: queue the visual work.
                    Dispatcher.InvokeAsync(() => SpawnRing(x, y));
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void SpawnRing(int screenX, int screenY)
    {
        try
        {
            if (RingCanvas.Children.Count >= MaxConcurrentRings) return;
            if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;

            Point dip = target.TransformFromDevice.Transform(
                new Point(screenX - _regionPx.X, screenY - _regionPx.Y));

            const double size = 44;
            var scale = new ScaleTransform(0.25, 0.25);
            var ring = new Ellipse
            {
                Width = size,
                Height = size,
                Stroke = RingBrush,
                StrokeThickness = 3,
                Opacity = 0.95,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            };
            Canvas.SetLeft(ring, dip.X - size / 2);
            Canvas.SetTop(ring, dip.Y - size / 2);
            RingCanvas.Children.Add(ring);

            var duration = TimeSpan.FromMilliseconds(450);
            var grow = new DoubleAnimation(0.25, 1.0, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            var fade = new DoubleAnimation(0.95, 0.0, duration);
            fade.Completed += (_, _) => RingCanvas.Children.Remove(ring);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(OpacityProperty, fade);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to draw click highlight", ex);
        }
    }

    // ---- native ----

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
