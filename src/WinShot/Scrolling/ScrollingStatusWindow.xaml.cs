using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WinShot.Core;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace WinShot.Scrolling;

/// <summary>
/// Tiny topmost pill shown during a scrolling capture. Sits at the top-center
/// of the primary work area (away from typical page content) with live status,
/// a live preview thumbnail of the growing stitch, an indeterminate accent
/// shimmer, and a Stop button; Esc also ends the capture (polled by the service).
/// Never activated, so wheel input keeps going to the content being scrolled.
/// </summary>
public partial class ScrollingStatusWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    private ScrollingStatusWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PositionTopCenter();
            StartShimmer();
        };
    }

    /// <summary>
    /// Asks the user whether WinShot should auto-scroll or they will scroll
    /// themselves (and in which direction), then shows the pill, runs the
    /// scrolling capture for <paramref name="screenRegion"/> (physical screen
    /// coordinates) and closes itself when done. Must be called on the UI
    /// thread. Returns the stitched bitmap (caller owns disposal), or null if
    /// nothing was captured or the chooser was cancelled.
    /// </summary>
    public static async Task<SD.Bitmap?> Run(SD.Rectangle screenRegion, ScrollCaptureChoice? presetChoice = null)
    {
        ScrollCaptureChoice choice;
        if (presetChoice is ScrollCaptureChoice preset)
            choice = preset;
        else if (ScrollingModeDialog.Choose() is ScrollCaptureChoice picked)
            choice = picked;
        else
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
                window._cts.Token,
                thumb => window.UpdatePreview(thumb));
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

    /// <summary>
    /// Called from the capture thread with a short-lived downscaled snapshot of the growing
    /// stitch. The snapshot is disposed by the service the moment this returns, so we convert
    /// it to a frozen WPF <see cref="BitmapSource"/> SYNCHRONOUSLY on the UI thread.
    /// </summary>
    private void UpdatePreview(SD.Bitmap thumb)
    {
        BitmapSource? source = TryConvert(thumb);
        if (source is null)
            return;
        Dispatcher.Invoke(() =>
        {
            PreviewImage.Source = source;
            if (PreviewHost.Visibility != Visibility.Visible)
                PreviewHost.Visibility = Visibility.Visible;
        });
    }

    /// <summary>Converts a GDI+ bitmap to a frozen BGRA32 BitmapSource (safe to hand to WPF).</summary>
    private static BitmapSource? TryConvert(SD.Bitmap bmp)
    {
        try
        {
            var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
            SDI.BitmapData data = bmp.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppArgb);
            try
            {
                var source = BitmapSource.Create(
                    bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null,
                    data.Scan0, data.Stride * bmp.Height, data.Stride);
                source.Freeze(); // cross-thread safe + immutable
                return source;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Scrolling status: preview conversion failed (non-fatal)", ex);
            return null;
        }
    }

    private void StartShimmer()
    {
        // Sweep the accent bar left-to-right across the actual content width, forever.
        double travel = Math.Max(ActualWidth, 120) + Shimmer.Width;
        var anim = new DoubleAnimation
        {
            From = -Shimmer.Width,
            To = travel,
            Duration = TimeSpan.FromMilliseconds(1100),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        ShimmerXform.BeginAnimation(TranslateTransform.XProperty, anim);
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
