using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinShot.Capture;

/// <summary>
/// Topmost centered countdown bubble (big number in a dark circle) that ticks
/// once per second and closes at 0. Purely visual — no capture logic; the
/// returned task completes shortly after the bubble is gone so a follow-up
/// capture won't include it. The window is click-through.
/// </summary>
public partial class SelfTimerWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining;

    private SelfTimerWindow(int seconds)
    {
        InitializeComponent();
        _remaining = seconds;
        CountText.Text = seconds.ToString();

        SourceInitialized += (_, _) =>
        {
            // Click-through + no alt-tab entry, so the countdown never blocks the user.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GwlExstyle);
            SetWindowLong(hwnd, GwlExstyle, style | WsExTransparent | WsExToolwindow);
        };

        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _timer.Stop();
                Close();
            }
            else
            {
                CountText.Text = _remaining.ToString();
            }
        };
        _timer.Start();
    }

    /// <summary>
    /// Shows a countdown for <paramref name="seconds"/> seconds. Completes
    /// (slightly after the window closes, so the compositor has removed it
    /// from screen) when the countdown reaches 0. Completes immediately when
    /// <paramref name="seconds"/> is 0 or negative.
    /// </summary>
    public static Task RunAsync(int seconds)
    {
        if (seconds <= 0)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var win = new SelfTimerWindow(seconds);
        win.Closed += async (_, _) =>
        {
            await Task.Delay(120); // let the bubble actually leave the screen first
            tcs.TrySetResult();
        };
        win.Show();
        return tcs.Task;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
