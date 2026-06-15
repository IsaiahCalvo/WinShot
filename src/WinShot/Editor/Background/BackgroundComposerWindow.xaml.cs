using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Editor.Background;

/// <summary>
/// CleanShot-style "Background" tool: floats the screenshot on a styled
/// backdrop (gradient preset, solid color, or custom image) with padding,
/// rounded corners and a drop shadow for social-ready images.
///
/// All composition happens in source-pixel space — <c>ComposeRoot</c> is laid
/// out at 1 DIP = 1 source pixel and the padding/radius sliders are in source
/// pixels — so the flattened export (RenderVisual of ComposeRoot at the
/// computed canvas pixel size) keeps the screenshot at full quality. The
/// preview Viewbox merely scales the live view; its scale transform lives on
/// an ancestor visual and never leaks into the export.
///
/// Owns the source bitmap (callers pass a clone) and disposes it on close.
/// </summary>
public partial class BackgroundComposerWindow : Window
{
    private static readonly string[] SolidSwatchHexes =
        { "#FFFFFF", "#F2F2F7", "#1E1E1E", "#2D7DFF", "#34C759", "#AF52DE" };

    private readonly SD.Bitmap _source;
    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly int _srcW;
    private readonly int _srcH;
    private readonly List<RadioButton> _bgSwatches = new();

    private double? _aspect;  // null = Auto (canvas hugs image + padding)
    private int _canvasW;
    private int _canvasH;
    private bool _ready;
    private bool _suppressBg;

    public BackgroundComposerWindow(SD.Bitmap source, SettingsService settings, HistoryService history)
    {
        InitializeComponent();
        _source = source;
        _settings = settings;
        _history = history;
        _srcW = source.Width;
        _srcH = source.Height;

        var shot = new ImageBrush(CaptureService.ToBitmapSource(source)) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(shot, BitmapScalingMode.HighQuality);
        ShotBorder.Background = shot;

        BuildBackgroundPickers();
        Closed += (_, _) => _source.Dispose();

        _ready = true;
        if (_bgSwatches.Count > 0)
            _bgSwatches[0].IsChecked = true; // applies the first preset
        UpdateComposition();
    }

    // ------------------------------------------------------------ composition

    /// <summary>
    /// Recomputes canvas size and restyles the floating screenshot. The image
    /// is always centered ("auto balance"): in Auto the canvas hugs image +
    /// padding; fixed ratios grow the canvas along one axis only, so padding
    /// stays the guaranteed minimum margin.
    /// </summary>
    private void UpdateComposition()
    {
        if (!_ready) return;

        int pad = (int)Math.Round(PaddingSlider.Value);
        int radius = (int)Math.Round(RadiusSlider.Value);
        int blurValue = (int)Math.Round(BlurSlider.Value);
        PadValueLabel.Text = $"{pad} px";
        RadiusValueLabel.Text = $"{radius} px";
        BlurValueLabel.Text = blurValue.ToString();

        int contentW = _srcW + 2 * pad;
        int contentH = _srcH + 2 * pad;

        _canvasW = contentW;
        _canvasH = contentH;
        if (_aspect is double ratio)
        {
            if (contentW / (double)contentH >= ratio)
                _canvasH = (int)Math.Ceiling(contentW / ratio);
            else
                _canvasW = (int)Math.Ceiling(contentH * ratio);
        }

        ComposeRoot.Width = _canvasW;
        ComposeRoot.Height = _canvasH;
        ShotBorder.Width = _srcW;
        ShotBorder.Height = _srcH;
        ShotBorder.CornerRadius = new CornerRadius(radius);

        if (ShadowCheck.IsChecked == true)
        {
            double blur = BlurSlider.Value;
            ShotBorder.Effect = new DropShadowEffect
            {
                BlurRadius = blur,
                ShadowDepth = blur * 0.25,
                Direction = 270,
                Color = Colors.Black,
                Opacity = 0.55,
                RenderingBias = RenderingBias.Quality,
            };
        }
        else
        {
            ShotBorder.Effect = null;
        }

        SizeLabel.Text = $"{_canvasW} × {_canvasH} px";
    }

    private void SetBackground(Brush brush) => ComposeRoot.Background = brush;

    private void BuildBackgroundPickers()
    {
        var presetStyle = (Style)FindResource("PresetSwatch");
        foreach (var (name, brush) in BackgroundPresets.All)
        {
            var rb = new RadioButton
            {
                Style = presetStyle,
                Background = brush,
                GroupName = "Bg",
                ToolTip = name,
            };
            rb.Checked += (_, _) => SetBackground(brush);
            _bgSwatches.Add(rb);
            PresetPanel.Children.Add(rb);
        }

        var solidStyle = (Style)FindResource("SolidSwatch");
        foreach (string hex in SolidSwatchHexes)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            var rb = new RadioButton
            {
                Style = solidStyle,
                Background = brush,
                GroupName = "Bg",
                ToolTip = hex,
            };
            rb.Checked += (_, _) =>
            {
                _suppressBg = true;
                HexBox.Text = hex;
                _suppressBg = false;
                SetBackground(brush);
            };
            _bgSwatches.Add(rb);
            SolidPanel.Children.Add(rb);
        }
    }

    private void UncheckBgSwatches()
    {
        foreach (var rb in _bgSwatches)
            rb.IsChecked = false;
    }

    // --------------------------------------------------------------- handlers

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateComposition();

    private void OnShadowToggled(object sender, RoutedEventArgs e) => UpdateComposition();

    private void OnAspectChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        _aspect = tag switch
        {
            "1:1" => 1.0,
            "4:3" => 4.0 / 3.0,
            "16:9" => 16.0 / 9.0,
            "9:16" => 9.0 / 16.0,
            _ => null,
        };
        UpdateComposition();
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _suppressBg) return;
        string text = HexBox.Text.Trim();
        if (text.Length == 0) return;
        if (text[0] != '#' && text.All(Uri.IsHexDigit))
            text = "#" + text;

        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(text); }
        catch { return; } // incomplete input while typing

        UncheckBgSwatches();
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        SetBackground(brush);
    }

    private void OnBrowseImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose background image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(dialog.FileName);
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            UncheckBgSwatches();
            SetBackground(brush);
            ImageNameText.Text = Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error($"Background image load failed: {dialog.FileName}", ex);
            MessageBox.Show(this, "Couldn't load that image.", "WinShot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ----------------------------------------------------------------- output

    /// <summary>
    /// Flattens the composition at output pixel size. ComposeRoot's layout is
    /// already in source pixels, so RenderVisual produces a full-quality image
    /// regardless of the preview Viewbox scale or monitor DPI.
    /// </summary>
    private SD.Bitmap Flatten()
    {
        ComposeRoot.UpdateLayout();
        return BitmapEffects.RenderVisual(ComposeRoot, _canvasW, _canvasH);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            using var flat = Flatten();
            CaptureService.CopyToClipboard(flat);
            FlashButton(BtnCopy, "Copied ✓", "Copy");
        }
        catch (Exception ex)
        {
            Log.Error("Background composer copy failed", ex);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.Current.SaveFolder);
            string format = _settings.Current.ImageFormat;
            var dialog = new SaveFileDialog
            {
                FileName = FileNamer.Next(_settings, format),
                InitialDirectory = _settings.Current.SaveFolder,
                Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image|*.webp",
                FilterIndex = format switch { "jpg" => 2, "webp" => 3, _ => 1 },
            };
            if (dialog.ShowDialog(this) != true) return;

            using var flat = Flatten();
            ImageSaver.Save(flat, dialog.FileName);
            _history.Add(flat);
            FlashButton(BtnSave, "Saved ✓", "Save…");
        }
        catch (Exception ex)
        {
            Log.Error("Background composer save failed", ex);
        }
    }

    private static void FlashButton(Button button, string flash, string normal)
    {
        button.Content = flash;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) => { timer.Stop(); button.Content = normal; };
        timer.Start();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control
                 && Keyboard.FocusedElement is not TextBox)
        {
            OnCopy(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnSave(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
