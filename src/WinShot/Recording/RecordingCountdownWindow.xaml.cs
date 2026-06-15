using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Recording;

/// <summary>
/// Big topmost countdown shown before a recording starts, centered over the
/// region that is about to be recorded. Show with <see cref="Window.ShowDialog"/>:
/// returns true when the countdown completed, false when the user pressed
/// Escape (or clicked the bubble) to cancel.
/// </summary>
public partial class RecordingCountdownWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SD.Rectangle _regionPx;
    private int _remaining;
    private bool _done;

    /// <param name="seconds">Seconds to count down from (clamped to at least 1).</param>
    /// <param name="regionScreenPx">Recording region in physical screen pixels; the bubble is centered over it.</param>
    public RecordingCountdownWindow(int seconds, SD.Rectangle regionScreenPx)
    {
        InitializeComponent();
        _remaining = Math.Max(1, seconds);
        _regionPx = regionScreenPx;
        CountText.Text = _remaining.ToString();

        SourceInitialized += (_, _) => PositionOverRegion();
        Loaded += (_, _) =>
        {
            PositionOverRegion(); // WPF layout may resize between init and show
            Activate();
            Focus();
            _timer.Tick += OnTick;
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
        MouseLeftButtonDown += (_, _) => Cancel();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            if (!_done)
            {
                _done = true;
                DialogResult = true;
            }
            return;
        }
        CountText.Text = _remaining.ToString();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Cancel();
        base.OnKeyDown(e);
    }

    private void Cancel()
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        DialogResult = false;
    }

    /// <summary>
    /// Centers the window over the recording region using physical pixels so
    /// the placement is exact regardless of per-monitor DPI.
    /// </summary>
    private void PositionOverRegion()
    {
        try
        {
            double scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int w = (int)Math.Round(Width * scale);
            int h = (int)Math.Round(Height * scale);
            int x = _regionPx.X + (_regionPx.Width - w) / 2;
            int y = _regionPx.Y + (_regionPx.Height - h) / 2;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, HwndTopmost, x, y, w, h, 0);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to position countdown window", ex);
        }
    }

    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
