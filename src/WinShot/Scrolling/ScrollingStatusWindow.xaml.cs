using System.Windows;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>
/// Tiny topmost pill shown during a scrolling capture. Sits at the top-center
/// of the primary work area (away from typical page content) with live status
/// and a Stop button; Esc also ends the capture (polled by the service).
/// Never activated, so wheel input keeps going to the content being scrolled.
/// </summary>
public partial class ScrollingStatusWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    private ScrollingStatusWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionTopCenter();
    }

    /// <summary>
    /// Asks the user whether WinShot should auto-scroll or they will scroll
    /// themselves (and in which direction), then shows the pill, runs the
    /// scrolling capture for <paramref name="screenRegion"/> (physical screen
    /// coordinates) and closes itself when done. Must be called on the UI
    /// thread. Returns the stitched bitmap (caller owns disposal), or null if
    /// nothing was captured or the chooser was cancelled.
    /// </summary>
    public static async Task<SD.Bitmap?> Run(SD.Rectangle screenRegion)
    {
        if (ScrollingModeDialog.Choose() is not ScrollCaptureChoice choice)
            return null; // cancelled

        var window = new ScrollingStatusWindow();
        window.Show();
        try
        {
            return await ScrollingCaptureService.RunAsync(
                screenRegion,
                choice.Mode,
                choice.Direction,
                text => window.Dispatcher.Invoke(() => window.StatusText.Text = text),
                window._cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error("Scrolling capture failed", ex);
            return null;
        }
        finally
        {
            window.Close();
            window._cts.Dispose();
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        BtnStop.IsEnabled = false;
        StatusText.Text = "Stopping…";
        _cts.Cancel();
    }

    private void PositionTopCenter()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + 12;
    }
}
