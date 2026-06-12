using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Overlay;

/// <summary>
/// CleanShot-style floating thumbnail shown after every capture. Stacks
/// bottom-right of the primary work area. Owns its bitmap and disposes it on
/// close — consumers that outlive the overlay must take CloneImage().
/// </summary>
public partial class QuickActionsWindow : Window
{
    private static readonly List<QuickActionsWindow> OpenWindows = new();

    private readonly SD.Bitmap _image;
    private readonly SettingsService _settings;

    public event Action<QuickActionsWindow>? EditRequested;
    public event Action<QuickActionsWindow>? PinRequested;
    public event Action<QuickActionsWindow>? OcrRequested;

    public QuickActionsWindow(SD.Bitmap image, SettingsService settings)
    {
        InitializeComponent();
        _image = image;
        _settings = settings;
        Thumb.Source = CaptureService.ToBitmapSource(image);

        OpenWindows.Add(this);
        Loaded += (_, _) => PositionBottomRight();
        Closed += (_, _) =>
        {
            OpenWindows.Remove(this);
            _image.Dispose();
        };

        int seconds = settings.Current.OverlayAutoCloseSeconds;
        if (seconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        }
    }

    public SD.Bitmap CloneImage() => (SD.Bitmap)_image.Clone();

    private void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        double offset = OpenWindows
            .Where(w => !ReferenceEquals(w, this) && w.IsVisible)
            .Sum(w => w.ActualHeight + 12);
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Bottom - ActualHeight - 16 - offset;
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* button released mid-call */ }
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            CaptureService.CopyToClipboard(_image);
            BtnCopy.Content = "Copied ✓";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (_, _) => { timer.Stop(); BtnCopy.Content = "Copy"; };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Copy to clipboard failed", ex);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.Current.SaveFolder);
            var dialog = new SaveFileDialog
            {
                FileName = CaptureService.DefaultFileName(_settings.Current.ImageFormat),
                InitialDirectory = _settings.Current.SaveFolder,
                Filter = "PNG image|*.png|JPEG image|*.jpg",
                FilterIndex = _settings.Current.ImageFormat == "jpg" ? 2 : 1,
            };
            if (dialog.ShowDialog() == true)
            {
                CaptureService.Save(_image, dialog.FileName);
                Close();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
        }
    }

    private void OnEdit(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this);
    private void OnPin(object sender, RoutedEventArgs e) => PinRequested?.Invoke(this);
    private void OnOcr(object sender, RoutedEventArgs e) => OcrRequested?.Invoke(this);
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
