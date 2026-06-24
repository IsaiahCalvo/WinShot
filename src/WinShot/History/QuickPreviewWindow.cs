using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Core;

namespace WinShot.History;

/// <summary>
/// Borderless dark quick-look window for a history item. Images render fit to
/// 80% of the work area; videos (mp4 etc.) show a "Press Open to play" hint.
/// Space, Esc, any click, or losing focus closes it.
/// </summary>
public sealed class QuickPreviewWindow : Window
{
    private static readonly string[] PreviewableExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    private bool _closing;

    public QuickPreviewWindow(string filePath)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
        };
        Content = border;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        bool looksLikeImage = PreviewableExtensions.Contains(ext);
        var wa = SystemParameters.WorkArea;
        BitmapImage? image = looksLikeImage ? TryLoad(filePath, (int)Math.Ceiling(wa.Width * 0.8)) : null;

        if (image is not null)
        {
            var size = HistoryPreviewLayout.CalculateImageSize(
                new System.Drawing.Size(image.PixelWidth, image.PixelHeight),
                new System.Drawing.Size((int)Math.Round(wa.Width), (int)Math.Round(wa.Height)));
            border.Child = new Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                Width = Math.Max(1, size.Width - HistoryPreviewLayout.TotalPadding),
                Height = Math.Max(1, size.Height - HistoryPreviewLayout.TotalPadding),
            };
        }
        else
        {
            var panel = new StackPanel { Margin = new Thickness(28, 20, 28, 20) };
            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(filePath),
                Foreground = Brushes.White,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = looksLikeImage ? "Preview unavailable" : "Press Open to play",
                Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            border.Child = panel;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Space or Key.Escape)
        {
            e.Handled = true;
            CloseOnce();
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        CloseOnce();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        CloseOnce();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    /// <summary>Close() can re-enter while the window is already tearing down
    /// (Esc → Close → Deactivated → Close); this collapses those into one call.</summary>
    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private static BitmapImage? TryLoad(string path, int decodePixelWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = Math.Max(1, decodePixelWidth);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error($"Quick preview failed for {path}", ex);
            return null;
        }
    }
}
