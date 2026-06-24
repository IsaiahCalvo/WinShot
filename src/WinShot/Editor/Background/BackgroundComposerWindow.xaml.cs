using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly System.Diagnostics.Stopwatch _startup = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<RadioButton> _bgSwatches = new();

    private double? _aspect;  // null = Auto (canvas hugs image + padding)
    private int _canvasW;
    private int _canvasH;
    private bool _ready;
    private bool _suppressBg;
    private bool _allowPreviewShadow;
    private bool _compositionInitialized;
    private string _anchor = "CC";  // 3x3 alignment of the shot within the canvas

    public BackgroundComposerWindow(SD.Bitmap source, SettingsService settings, HistoryService history)
        : this(source, settings, history, loadSourceImage: true)
    {
    }

    private BackgroundComposerWindow(SD.Bitmap source, SettingsService settings, HistoryService history, bool loadSourceImage)
    {
        ThemeResources.EnsureLoaded();
        InitializeComponent();
        _source = source;
        _settings = settings;
        _history = history;
        _srcW = source.Width;
        _srcH = source.Height;
        BtnCopy.Content = "Copy";
        BtnSave.Content = "Save as...";

        Closed += (_, _) =>
        {
            _source.Dispose();
            MemoryCleanup.Request();
        };
        SourceInitialized += (_, _) => Log.Info($"Perf background composer source initialized: {_startup.ElapsedMilliseconds} ms");
        Loaded += (_, _) => Log.Info($"Perf background composer loaded: {_startup.ElapsedMilliseconds} ms");

        _ready = true;
        ContentRendered += (_, _) =>
        {
            Log.Info($"Perf background composer content rendered internal: {_startup.ElapsedMilliseconds} ms");
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    _allowPreviewShadow = true;
                    EnsureCompositionInitialized();
                    UpdateComposition();
                    PreviewBox.Visibility = Visibility.Visible;
                    ComposerControls.Visibility = Visibility.Visible;
                    if (_bgSwatches.Count == 0)
                    {
                        BuildBackgroundPickers();
                        if (_bgSwatches.Count > 0)
                            _bgSwatches[0].IsChecked = true; // applies the first preset
                    }

                    if (loadSourceImage)
                        _ = LoadShotBrushAsync(source);

                    Log.Info($"Perf background composer deferred init: {_startup.ElapsedMilliseconds} ms");
                }));
        };
    }

    public static void Prewarm(SettingsService settings, HistoryService history)
    {
        try
        {
            var bitmap = new SD.Bitmap(1, 1);
            var window = new BackgroundComposerWindow(bitmap, settings, history, loadSourceImage: false)
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            fallback.Tick += (_, _) =>
            {
                fallback.Stop();
                if (window.IsVisible)
                    window.Close();
            };
            window.ContentRendered += (_, _) =>
            {
                fallback.Stop();
                window.Close();
            };
            window.Show();
            fallback.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Background composer prewarm failed", ex);
        }
    }

    private async Task LoadShotBrushAsync(SD.Bitmap source)
    {
        try
        {
            var image = await CaptureService.ToBitmapSourceSnapshotAsync(source);
            await Dispatcher.InvokeAsync(() =>
            {
                var shot = new ImageBrush(image) { Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(shot, BitmapScalingMode.HighQuality);
                ShotBorder.Background = shot;
            });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load background composer screenshot", ex);
        }
    }

    // ------------------------------------------------------------ composition

    /// <summary>
    /// Recomputes canvas size and restyles the floating screenshot. The shot is
    /// anchored within the canvas by the 3x3 alignment picker (center default);
    /// in Auto the canvas hugs image + padding, fixed ratios grow the canvas
    /// along one axis only so padding stays the guaranteed minimum margin.
    /// "Inset" adds an extra symmetric inner margin around the shot.
    /// </summary>
    private void UpdateComposition()
    {
        if (!_ready) return;

        int pad = (int)Math.Round(PaddingSlider.Value);
        int inset = (int)Math.Round(InsetSlider.Value);
        int radius = (int)Math.Round(RadiusSlider.Value);
        int blurValue = (int)Math.Round(BlurSlider.Value);
        PadValueLabel.Text = $"{pad} px";
        InsetValueLabel.Text = $"{inset} px";
        RadiusValueLabel.Text = $"{radius} px";
        BlurValueLabel.Text = blurValue.ToString();

        var layout = BackgroundLayout.Calculate(new SD.Size(_srcW, _srcH), pad, inset, _aspect);
        _canvasW = layout.CanvasSize.Width;
        _canvasH = layout.CanvasSize.Height;

        ComposeRoot.Width = _canvasW;
        ComposeRoot.Height = _canvasH;
        ShotBorder.Width = _srcW;
        ShotBorder.Height = _srcH;
        ShotBorder.CornerRadius = new CornerRadius(radius);

        // Anchor the shot inside the (possibly oversized) canvas. The minimum
        // gutter is the effective margin; extra space from a fixed ratio is
        // distributed per the alignment picker.
        ApplyAnchor(layout.Margin);

        if (ShadowCheck.IsChecked == true)
        {
            ShotBorder.Effect = _allowPreviewShadow
                ? CreateShadowEffect(RenderingBias.Performance)
                : null;
        }
        else
        {
            ShotBorder.Effect = null;
        }

        SizeLabel.Text = $"{_canvasW} x {_canvasH} px";
    }

    private void EnsureCompositionInitialized()
    {
        if (_compositionInitialized)
            return;

        _compositionInitialized = true;
        UpdateComposition();
    }

    private DropShadowEffect CreateShadowEffect(RenderingBias renderingBias)
    {
        double blur = BlurSlider.Value;
        return new DropShadowEffect
        {
            BlurRadius = blur,
            ShadowDepth = blur * 0.25,
            Direction = 270,
            Color = Colors.Black,
            Opacity = 0.55,
            RenderingBias = renderingBias,
        };
    }

    /// <summary>
    /// Positions <see cref="ShotBorder"/> within <see cref="ComposeRoot"/> using
    /// the selected 3x3 anchor. The shot keeps at least <paramref name="margin"/>
    /// gutter on every side; the picker decides where any extra canvas space
    /// (from a fixed ratio) collects.
    /// </summary>
    private void ApplyAnchor(int margin)
    {
        char v = _anchor.Length > 0 ? _anchor[0] : 'C';   // T / C / B
        char h = _anchor.Length > 1 ? _anchor[1] : 'C';   // L / C / R

        ShotBorder.HorizontalAlignment = h switch
        {
            'L' => HorizontalAlignment.Left,
            'R' => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        ShotBorder.VerticalAlignment = v switch
        {
            'T' => VerticalAlignment.Top,
            'B' => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center,
        };

        // Guarantee the margin gutter on the anchored edges.
        double left = h == 'L' ? margin : 0;
        double right = h == 'R' ? margin : 0;
        double top = v == 'T' ? margin : 0;
        double bottom = v == 'B' ? margin : 0;
        ShotBorder.Margin = new Thickness(left, top, right, bottom);
    }

    private void SetBackground(Brush brush) => ComposeRoot.Background = brush;

    /// <summary>
    /// Builds the gradient thumb used in the Gradients and Blurred sections.
    /// Replaces the former local <c>PresetSwatch</c> style: a rounded ring that
    /// turns white when checked, wrapping a 38x26 brush rectangle.
    /// </summary>
    private static RadioButton MakeGradientThumb(Brush brush, string tooltip)
    {
        var rb = new RadioButton
        {
            Template = BuildThumbTemplate(showContent: false),
            Background = brush,
            ToolTip = tooltip,
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
        };
        return rb;
    }

    /// <summary>
    /// Builds the rounded-ring thumbnail template shared by gradient, blurred
    /// and wallpaper swatches. When <paramref name="showContent"/> is true the
    /// inner tile hosts a centered ContentPresenter (used for the "+" tile);
    /// otherwise it is a plain brush rectangle bound to Background.
    /// </summary>
    private static ControlTemplate BuildThumbTemplate(bool showContent)
    {
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.Name = "Ring";
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.PaddingProperty, new Thickness(1));

        var inner = new FrameworkElementFactory(typeof(Border));
        inner.SetValue(FrameworkElement.WidthProperty, 38.0);
        inner.SetValue(FrameworkElement.HeightProperty, 26.0);
        inner.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        inner.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        ring.AppendChild(inner);

        if (showContent)
        {
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(System.Windows.Documents.TextElement.ForegroundProperty,
                new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)));
            cp.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 18.0);
            inner.AppendChild(cp);
        }

        var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = ring };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)), "Ring"));
        var checkedTrig = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrig.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.White, "Ring"));
        template.Triggers.Add(hover);
        template.Triggers.Add(checkedTrig);
        return template;
    }

    private void BuildBackgroundPickers()
    {
        // --- Gradients (and Blurred, which reuses the same gradient thumbs) ---
        var presets = BackgroundPresets.All;
        int splitAt = Math.Max(0, presets.Count - 3); // last 3 feed the Blurred row
        for (int i = 0; i < presets.Count; i++)
        {
            var (name, brush) = presets[i];
            var rb = MakeGradientThumb(brush, name);
            rb.GroupName = "Bg";
            rb.ToolTip = name;
            string label = name;
            rb.Checked += (_, _) => { SetBackground(brush); PresetNameHeader.Text = label; };
            _bgSwatches.Add(rb);

            if (i < splitAt)
                PresetPanel.Children.Add(rb);
            else
                BlurredPanel.Children.Add(rb);
        }

        // --- Wallpapers: a custom-image thumb + a "+" browse tile ---
        var browseTile = new RadioButton
        {
            Template = BuildThumbTemplate(showContent: true),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            GroupName = "Bg",
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            Content = "+",
            ToolTip = "Choose image...",
        };
        browseTile.Checked += (_, _) =>
        {
            // Re-arm so the tile can be clicked again next time, then browse.
            _suppressBg = true;
            browseTile.IsChecked = false;
            _suppressBg = false;
            OnBrowseImage(browseTile, new RoutedEventArgs());
        };
        WallpaperPanel.Children.Add(browseTile);

        // --- Plain color: dot grid (theme ColorSwatch) + hex box ---
        var solidStyle = (Style)FindResource("ColorSwatch");
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
            string hexLabel = hex;
            rb.Checked += (_, _) =>
            {
                _suppressBg = true;
                HexBox.Text = hexLabel;
                _suppressBg = false;
                SetBackground(brush);
                PresetNameHeader.Text = hexLabel;
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

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Auto-balance mirrors padding into inset so the inner/outer margins
        // stay symmetric while the user drags either slider.
        if (_ready && AutoBalanceCheck?.IsChecked == true && ReferenceEquals(sender, PaddingSlider))
        {
            double v = PaddingSlider.Value * 0.5;
            if (Math.Abs(InsetSlider.Value - v) > 0.5)
            {
                InsetSlider.Value = Math.Min(InsetSlider.Maximum, v);
                return; // the InsetSlider change re-enters and composes once
            }
        }
        UpdateComposition();
    }

    private void OnShadowToggled(object sender, RoutedEventArgs e) => UpdateComposition();

    private void OnAutoBalanceToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (AutoBalanceCheck.IsChecked == true)
            InsetSlider.Value = Math.Min(InsetSlider.Maximum, PaddingSlider.Value * 0.5);
        UpdateComposition();
    }

    private void OnAlignChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string tag) return;
        if (!_ready)
        {
            _anchor = tag;
            return;
        }

        // ToggleButton has no GroupName, so enforce single-selection manually.
        foreach (var other in new[]
                 { AlignTL, AlignTC, AlignTR, AlignCL, AlignCC, AlignCR, AlignBL, AlignBC, AlignBR })
        {
            if (!ReferenceEquals(other, tb))
                other.IsChecked = false;
        }

        _anchor = tag;
        UpdateComposition();
    }

    private void OnRatioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RatioBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
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
        PresetNameHeader.Text = text;
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
            image.DecodePixelWidth = Math.Max(_canvasW, _canvasH) * 2;
            image.UriSource = new Uri(dialog.FileName);
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            UncheckBgSwatches();
            SetBackground(brush);

            string fileName = Path.GetFileName(dialog.FileName);
            PresetNameHeader.Text = fileName;

            // Reflect the chosen wallpaper as a live thumb in the Wallpapers row.
            ShowWallpaperThumb(brush, fileName);
        }
        catch (Exception ex)
        {
            Log.Error($"Background image load failed: {dialog.FileName}", ex);
            MessageBox.Show(this, "Couldn't load that image.", "WinShot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Inserts (or refreshes) a selectable thumbnail for the currently chosen
    /// wallpaper image, before the "+" browse tile.
    /// </summary>
    private void ShowWallpaperThumb(ImageBrush brush, string tooltip)
    {
        var thumb = MakeGradientThumb(brush, tooltip);
        thumb.GroupName = "Bg";
        thumb.ToolTip = tooltip;
        thumb.Checked += (_, _) => { SetBackground(brush); PresetNameHeader.Text = tooltip; };
        _bgSwatches.Add(thumb);
        WallpaperPanel.Children.Insert(Math.Max(0, WallpaperPanel.Children.Count - 1), thumb);
        _suppressBg = true;
        thumb.IsChecked = true;
        _suppressBg = false;
    }

    // ----------------------------------------------------------------- output

    /// <summary>
    /// Flattens the composition at output pixel size. ComposeRoot's layout is
    /// already in source pixels, so RenderVisual produces a full-quality image
    /// regardless of the preview Viewbox scale or monitor DPI.
    /// </summary>
    private SD.Bitmap Flatten()
    {
        EnsureCompositionInitialized();
        Effect? previousEffect = ShotBorder.Effect;
        if (ShadowCheck.IsChecked == true)
            ShotBorder.Effect = CreateShadowEffect(RenderingBias.Quality);

        try
        {
            ComposeRoot.UpdateLayout();
            return BitmapEffects.RenderVisual(ComposeRoot, _canvasW, _canvasH);
        }
        finally
        {
            ShotBorder.Effect = previousEffect;
        }
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var flat = Flatten();
            await CaptureService.CopyToClipboardAsync(flat, takeOwnership: true);
            if (!IsVisible) return;
            FlashButton(BtnCopy, "Copied", "Copy");
        }
        catch (Exception ex)
        {
            Log.Error("Background composer copy failed", ex);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
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

            var flat = Flatten();
            await Task.Run(() =>
            {
                using (flat)
                {
                    ImageSaver.Save(flat, dialog.FileName);
                    _history.Add(flat);
                }
            });
            if (!IsVisible) return;
            FlashButton(BtnSave, "Saved", "Save as...");
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
