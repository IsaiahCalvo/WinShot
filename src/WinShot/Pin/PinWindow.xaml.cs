using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Pin;

/// <summary>
/// Borderless always-on-top floating image ("pin to screen"). Owns its bitmap
/// (callers pass a clone) and disposes it on close. Mouse wheel resizes around
/// the cursor, Ctrl+wheel adjusts opacity, double-click or Esc closes.
/// </summary>
public partial class PinWindow : Window
{
    private const double MinScale = 0.2;
    private const double MaxScale = 3.0;

    /// <summary>Cascades successive pins so they don't cover each other exactly.</summary>
    private static int _openCount;

    private readonly SD.Bitmap _image;
    private readonly double _naturalWidth;
    private readonly double _naturalHeight;
    private double _scale;

    public PinWindow(SD.Bitmap image)
    {
        InitializeComponent();
        _image = image;
        _naturalWidth = image.Width;
        _naturalHeight = image.Height;
        Img.Source = CaptureService.ToBitmapSource(image);

        var wa = SystemParameters.WorkArea;
        _scale = Math.Min(1.0, Math.Min(wa.Width * 0.6 / _naturalWidth, wa.Height * 0.6 / _naturalHeight));
        ApplyScale();

        double offset = (_openCount++ % 8) * 24;
        Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2) + offset;
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2) + offset;

        Closed += (_, _) => _image.Dispose();
    }

    private void ApplyScale()
    {
        Width = _naturalWidth * _scale + 2;   // +2 for the 1px frame border on each side
        Height = _naturalHeight * _scale + 2;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }
        try { DragMove(); }
        catch (InvalidOperationException) { /* button released mid-call */ }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.3, 1.0);
            return;
        }

        double newScale = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), MinScale, MaxScale);
        if (Math.Abs(newScale - _scale) < 0.0001) return;

        // Keep the image point under the cursor stationary while resizing.
        var pos = e.GetPosition(this);
        double factor = newScale / _scale;
        Left -= pos.X * (factor - 1);
        Top -= pos.Y * (factor - 1);
        _scale = newScale;
        ApplyScale();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            CaptureService.CopyToClipboard(_image);
        }
        catch (Exception ex)
        {
            Log.Error("Pin copy failed", ex);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinShot");
            System.IO.Directory.CreateDirectory(folder);
            var dialog = new SaveFileDialog
            {
                FileName = CaptureService.DefaultFileName("png"),
                InitialDirectory = folder,
                Filter = "PNG image|*.png|JPEG image|*.jpg",
            };
            if (dialog.ShowDialog(this) == true)
                CaptureService.Save(_image, dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error("Pin save failed", ex);
        }
    }

    private void OnCloseItem(object sender, RoutedEventArgs e) => Close();
}
