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
        BuildAlignmentGlyphs();

        Closed += (_, _) =>
        {
            // The shot-brush / blurred-backdrop conversions may still be queued on the
            // bitmap-source worker; disposing directly here races their Clone ("Parameter
            // is not valid" → blank composer/editor). Dispose behind the queue instead.
            CaptureService.DisposeAfterPendingConversions(_source);
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
        DarkTitleBar.Apply(this);
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
        ShadowOpacityValueLabel.Text = $"{(int)Math.Round(ShadowOpacitySlider.Value * 100)}%";

        // Padding is the single margin control; inset is retired (it collapsed
        // into padding and only duplicated the gutter).
        var layout = BackgroundLayout.Calculate(new SD.Size(_srcW, _srcH), pad, _aspect);
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
        // Softness drives the blur (and a proportional depth); Intensity is a
        // direct opacity control clamped to the slider's pleasant 0.2-0.8 range.
        double opacity = Math.Clamp(ShadowOpacitySlider.Value, 0.0, 1.0);
        return new DropShadowEffect
        {
            BlurRadius = blur,
            ShadowDepth = blur * 0.25,
            Direction = 270,
            Color = Colors.Black,
            Opacity = opacity,
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
    /// Builds the rounded-ring thumb used by the Gradients, Blurred and
    /// Wallpapers sections.
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
            GroupName = "Bg",
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
        // --- Gradients (all presets; Blurred is now its own frosted-self row) ---
        var presets = BackgroundPresets.All;
        for (int i = 0; i < presets.Count; i++)
        {
            var (name, brush) = presets[i];
            var rb = MakeGradientThumb(brush, name);
            string label = name;
            rb.Checked += (_, _) => { SetBackground(brush); PresetNameHeader.Text = label; };
            _bgSwatches.Add(rb);
            PresetPanel.Children.Add(rb);
        }

        // --- Blurred: frosted variants of the user's own screenshot ---
        BuildBlurredBackdrops();

        // --- Wallpapers: the current desktop wallpaper (if any) then "+" tile ---
        BuildDesktopWallpaperThumb();

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

    /// <summary>
    /// Populates the "Blurred" row with real frosted variants of the user's own
    /// screenshot: a heavy gaussian blur of the shot itself, plus light- and
    /// dark-tinted versions. Picking one sets a frosted backdrop of their own
    /// capture (not a gradient). Skipped when there is no real source.
    /// </summary>
    private void BuildBlurredBackdrops()
    {
        if (_srcW <= 1 || _srcH <= 1)
            return;

        BitmapSource baseImage;
        try
        {
            baseImage = CaptureService.ToBitmapSource(_source, 640, 640);
        }
        catch (Exception ex)
        {
            Log.Error("Background composer blurred backdrop source conversion failed", ex);
            return;
        }

        (string Name, Color? Tint)[] variants =
        {
            ("Blurred", null),
            ("Blurred · light", Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)),
            ("Blurred · dark", Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
        };

        foreach (var (name, tint) in variants)
        {
            ImageBrush? brush = TryMakeFrostedBrush(baseImage, tint);
            if (brush == null)
                continue;

            var rb = MakeGradientThumb(brush, name);
            string label = name;
            rb.Checked += (_, _) => { SetBackground(brush); PresetNameHeader.Text = label; };
            _bgSwatches.Add(rb);
            BlurredPanel.Children.Add(rb);
        }
    }

    /// <summary>
    /// Renders <paramref name="image"/> through a heavy gaussian <see cref="BlurEffect"/>
    /// (optionally over a tint overlay) into a frozen bitmap and wraps it in a
    /// UniformToFill ImageBrush so it scales to fill the canvas behind the shot.
    /// </summary>
    private static ImageBrush? TryMakeFrostedBrush(BitmapSource image, Color? tint)
    {
        try
        {
            int w = Math.Max(1, image.PixelWidth);
            int h = Math.Max(1, image.PixelHeight);

            // Overscan: lay the image out larger than the render target and offset
            // it so the blur's soft edge fringe falls outside the captured crop
            // (otherwise UniformToFill would surface faded/transparent borders).
            const double overscan = 90.0;
            var visual = new System.Windows.Controls.Image
            {
                Source = image,
                Stretch = Stretch.Fill,
                Effect = new BlurEffect
                {
                    Radius = 60,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Quality,
                },
            };
            var bounds = new Rect(-overscan, -overscan, w + 2 * overscan, h + 2 * overscan);
            visual.Measure(new Size(bounds.Width, bounds.Height));
            visual.Arrange(bounds);
            visual.UpdateLayout();

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            if (tint is Color overlay)
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(rtb, new Rect(0, 0, w, h));
                    dc.DrawRectangle(new SolidColorBrush(overlay), null, new Rect(0, 0, w, h));
                }
                var tinted = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                tinted.Render(dv);
                tinted.Freeze();
                return MakeFillBrush(tinted);
            }

            rtb.Freeze();
            return MakeFillBrush(rtb);
        }
        catch (Exception ex)
        {
            Log.Error("Background composer frosted backdrop render failed", ex);
            return null;
        }
    }

    private static ImageBrush MakeFillBrush(BitmapSource image)
    {
        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Reads the current Windows desktop wallpaper and inserts it as the first
    /// selectable Wallpapers thumbnail (before the "+" browse tile). Best-effort:
    /// any failure (no wallpaper, unsupported format) is logged and skipped.
    /// </summary>
    private void BuildDesktopWallpaperThumb()
    {
        string? path = TryGetDesktopWallpaperPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 720;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            brush.Freeze();

            const string label = "Desktop wallpaper";
            var rb = MakeGradientThumb(brush, label);
            rb.Checked += (_, _) => { SetBackground(brush); PresetNameHeader.Text = label; };
            _bgSwatches.Add(rb);
            WallpaperPanel.Children.Add(rb);
        }
        catch (Exception ex)
        {
            Log.Error($"Background composer desktop wallpaper load failed: {path}", ex);
        }
    }

    /// <summary>
    /// Resolves the current desktop wallpaper file. Tries the live
    /// SystemParametersInfo(SPI_GETDESKWALLPAPER) path first, then falls back to
    /// the cached TranscodedWallpaper under %AppData%.
    /// </summary>
    private static string? TryGetDesktopWallpaperPath()
    {
        try
        {
            const int SPI_GETDESKWALLPAPER = 0x0073;
            var sb = new System.Text.StringBuilder(520);
            if (NativeGetWallpaper(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0))
            {
                string p = sb.ToString();
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    return p;
            }
        }
        catch (Exception ex)
        {
            Log.Error("SystemParametersInfo SPI_GETDESKWALLPAPER failed", ex);
        }

        try
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            if (File.Exists(fallback))
                return fallback;
        }
        catch (Exception ex)
        {
            Log.Error("TranscodedWallpaper fallback lookup failed", ex);
        }

        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto,
        SetLastError = true, EntryPoint = "SystemParametersInfo")]
    private static extern bool NativeGetWallpaper(int uAction, int uParam,
        System.Text.StringBuilder lpvParam, int fuWinIni);

    private void UncheckBgSwatches()
    {
        foreach (var rb in _bgSwatches)
            rb.IsChecked = false;
    }

    // --------------------------------------------------------------- handlers

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateComposition();

    private void OnShadowToggled(object sender, RoutedEventArgs e) => UpdateComposition();

    /// <summary>
    /// Gives each of the nine alignment ToggleButtons a distinct directional
    /// indicator: a small dot positioned within an 18x18 frame per the button's
    /// Tag (top-left dot for TL, etc.) so the checked anchor is obvious. The dot
    /// binds to the button Foreground, which the ToolButton style flips to white
    /// when checked (over the solid blue fill) and a light gray at rest.
    /// </summary>
    private void BuildAlignmentGlyphs()
    {
        foreach (var tb in new[]
                 { AlignTL, AlignTC, AlignTR, AlignCL, AlignCC, AlignCR, AlignBL, AlignBC, AlignBR })
        {
            if (tb.Tag is not string tag) continue;
            tb.Content = MakeAlignmentDot(tb, tag);
        }
    }

    private static FrameworkElement MakeAlignmentDot(ToggleButton owner, string tag)
    {
        char v = tag.Length > 0 ? tag[0] : 'C';   // T / C / B
        char h = tag.Length > 1 ? tag[1] : 'C';   // L / C / R

        var grid = new Grid { Width = 18, Height = 18 };
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            HorizontalAlignment = h switch
            {
                'L' => HorizontalAlignment.Left,
                'R' => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center,
            },
            VerticalAlignment = v switch
            {
                'T' => VerticalAlignment.Top,
                'B' => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Center,
            },
        };
        // Track the host button's Foreground (white when checked, light at rest).
        dot.SetBinding(System.Windows.Shapes.Shape.FillProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = owner });
        grid.Children.Add(dot);
        return grid;
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
            "1.91:1" => 1.91,
            "9:16" => 9.0 / 16.0,
            _ => null,
        };
        UpdateComposition();
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        string text = HexBox.Text.Trim();

        // An empty box is neutral, not invalid: clear the error ring and bail.
        if (text.Length == 0)
        {
            SetHexInvalid(false);
            return;
        }
        if (text[0] != '#' && text.All(Uri.IsHexDigit))
            text = "#" + text;

        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(text); }
        catch
        {
            // Flag the box only when a swatch isn't silently feeding the field;
            // partial typing toward a valid value reads as an error until parsed.
            SetHexInvalid(!_suppressBg);
            return;
        }

        SetHexInvalid(false);
        UpdateHexSwatch(color);

        // When a solid swatch populated the box we only refresh the chip/ring;
        // the swatch's own Checked handler already applied the background.
        if (_suppressBg) return;

        UncheckBgSwatches();
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        SetBackground(brush);
        PresetNameHeader.Text = text;
    }

    /// <summary>Paints the live preview chip beside the hex box.</summary>
    private void UpdateHexSwatch(Color color)
    {
        var chip = new SolidColorBrush(color);
        chip.Freeze();
        HexSwatch.Background = chip;
    }

    /// <summary>
    /// Toggles the subtle invalid-state ring around the hex box (destructive red
    /// when the text doesn't parse, transparent when it clears).
    /// </summary>
    private void SetHexInvalid(bool invalid)
    {
        HexInvalidRing.BorderBrush = invalid
            ? (Brush)FindResource("AnnotationRedBrush")
            : Brushes.Transparent;
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
