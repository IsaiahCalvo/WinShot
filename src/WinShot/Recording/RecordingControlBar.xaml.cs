using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WinShot.Core;

namespace WinShot.Recording;

/// <summary>
/// Floating pill shown while recording: pulsing red dot, elapsed time, Stop
/// and Cancel. Excluded from screen capture so it never appears in the output.
/// </summary>
public partial class RecordingControlBar : Window
{
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _actionTaken;

    public event Action? StopRequested;
    public event Action? CancelRequested;

    public RecordingControlBar()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ExcludeFromCapture();
        Loaded += (_, _) =>
        {
            PositionBottomCenter();
            _elapsed.Start();
            _timer.Tick += (_, _) => ElapsedText.Text = FormatElapsed(_elapsed.Elapsed);
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private static string FormatElapsed(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    private void PositionBottomCenter()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Bottom - ActualHeight - 24;
    }

    /// <summary>
    /// WDA_EXCLUDEFROMCAPTURE keeps the bar out of both desktop duplication
    /// (MP4) and GDI screen grabs (GIF). Best effort — on failure the bar just
    /// shows up in the recording.
    /// </summary>
    private void ExcludeFromCapture()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (!SetWindowDisplayAffinity(handle, WdaExcludeFromCapture))
                Log.Info("SetWindowDisplayAffinity failed; control bar may appear in the recording");
        }
        catch (Exception ex)
        {
            Log.Error("Could not exclude recording bar from capture", ex);
        }
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* button released mid-call */ }
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => RaiseOnce(StopRequested);

    private void OnCancel(object sender, RoutedEventArgs e) => RaiseOnce(CancelRequested);

    private void RaiseOnce(Action? action)
    {
        if (_actionTaken) return;
        _actionTaken = true;
        BtnStop.IsEnabled = false;
        BtnCancel.IsEnabled = false;
        action?.Invoke();
    }

    private const uint WdaExcludeFromCapture = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
