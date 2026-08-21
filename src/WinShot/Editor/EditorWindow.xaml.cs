using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Editor;

/// <summary>
/// Annotation editor for captured screenshots. The content is laid out so one
/// DIP equals one bitmap pixel, which keeps annotation math exact and lets the
/// flattened export match the source resolution regardless of monitor DPI.
/// The content floats on an infinite canvas: a ScaleTransform + TranslateTransform
/// on <c>EditorSurface</c> provide zoom/pan, while every tool reads positions via
/// <c>e.GetPosition(AnnotationCanvas)</c>, so coordinates are always content-space
/// and the view transform can never leak into crop/blur math or the export
/// (Flatten renders <c>CanvasHost</c>, whose own transform stays identity).
/// Owns the source bitmap (callers pass a clone) and disposes it on close.
/// </summary>
public partial class EditorWindow : Window
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 16.0;

    private const string SaveDialogFilter =
        "PNG image|*.png|JPEG image|*.jpg|WebP image|*.webp|WinShot project|*.winshot";

    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly Func<Task>? _afterInitialPreview;
    private readonly TaskCompletionSource<object?> _initialPreviewReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _initialCaptureReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly EditorSourceOperationLifetime _sourceLifetime = new();
    private bool _initialLoadStarted;
    private bool _closed;
    private double _sourceWidth;
    private double _sourceHeight;
    internal Func<SD.Bitmap, int, Task<IReadOnlyList<BitmapSource>>>? SourceTileConverterForTest { get; set; }

    /// <summary>Current source bitmap; swapped out by crop. Everything in _owned is disposed on close.</summary>
    private SD.Bitmap _source;
    private readonly List<SD.Bitmap> _owned = new();

    private readonly Stack<EditorAction> _undoStack = new();
    private readonly Stack<EditorAction> _redoStack = new();

    /// <summary>Per-application random source used to seed irreversible pixelation jitter.</summary>
    private static readonly Random ToolRandom = new();

    private const double CropSnapPx = 8; // content px within which crop edges snap to image edges

    private EditorTool _tool = EditorTool.Select;
    private Color _color = Color.FromRgb(0xFF, 0x3B, 0x30);
    private double _thickness = 4;
    private double _strokeThickness = 4;
    private double _textThickness = 4;
    private bool _syncingThicknessButtons;
    private int _nextStep = 1;
    private bool _stepLetters; // Step tool: false = number badges, true = letter badges (A, B, …)
    private ShapeFillMode _fillMode = ShapeFillMode.None;
    private bool _filledRectangleMode;
    private TextStyle _textStyle = TextStyle.Plain;
    private ArrowStyle _arrowStyle = ArrowStyle.Straight;
    private double _opacity = 1.0; // annotation alpha multiplier (0.25–1.0) for new + restyled marks
    private EffectStrength _blurStrength = EffectStrength.Medium;
    private EffectStrength _pixelateStrength = EffectStrength.Medium;

    // Opacity-slider gesture: a continuous drag previews live but records ONE undo entry.
    // _opacityBefore is the selected element's style captured at gesture start.
    private StyleSnapshot? _opacityBefore;
    private UIElement? _opacityElement;
    private double? _cropRatio; // null = free
    private EditorTool _toolBeforeEyedropper = EditorTool.Select;
    private bool _colorPickerBuilt;
    private Color _customColor = Color.FromRgb(0xFF, 0x3B, 0x30); // last custom / eyedropper color
    private bool _suppressHexEvents;

    /// <summary>Path of the .winshot project this session was opened from / saved to, if any.</summary>
    private string? _projectPath;

    // Curved-arrow tool: after release the arrow stays "pending" with a draggable
    // control-point handle until the user clicks elsewhere or switches tools.
    private Path? _pendingCurve;
    private Point _curveFrom;
    private Point _curveTo;
    private Point _curveControl;
    private double _curveThickness;
    private bool _draggingCurveHandle;

    // View state (zoom/pan). _zoom mirrors ViewScale so math never reads the transform.
    private double _zoom = 1.0;
    private bool _panning;
    private Point _panLast; // viewport coords
    private bool _spaceDown;

    // Drawing state.
    private bool _dragging;
    private bool _sourceOperationActive;
    // Set when a new source is loaded; the first layout pass with a real viewport runs the
    // initial fit-to-view and clears it. Guarantees large/tall captures fit on open
    // regardless of layout timing.
    private bool _pendingInitialFit;
    private Point _dragStart;
    private Shape? _activeShape;
    private TextBox? _activeText;
    private SD.Rectangle? _pendingCrop;

    // Selection state (Select tool).
    private UIElement? _selected;
    private bool _movingSelection;
    private Point _moveLast;   // content coords
    private Vector _moveTotal; // accumulated drag delta for the undo record

    // Resize-handle state (shared between selected-annotation resize and crop adjust).
    // The eight box handles are indexed 0..7 (corners 0-3, edge midpoints 4-7); endpoint
    // handles (arrow/line/curved arrow) use indices 0 and 1 (and 2 = the bend control).
    private readonly List<Rectangle> _handleThumbs = new();
    private HandleKind _handleKind = HandleKind.None;
    private int _activeHandle = -1;          // which thumb is being dragged (-1 = none)
    private ResizeSnapshot? _resizeBefore;    // geometry captured at drag start (for undo)
    private bool _adjustingCrop;              // a handle drag is editing the pending crop rect, not an annotation

    public EditorWindow(SD.Bitmap source, SettingsService settings, HistoryService history)
        : this(source, settings, history, afterInitialPreview: null)
    {
    }

    private EditorWindow(
        SD.Bitmap source,
        SettingsService settings,
        HistoryService history,
        Func<Task>? afterInitialPreview)
    {
        ThemeResources.EnsureLoaded();
        InitializeComponent();
        _source = source;
        _owned.Add(source);
        _pendingInitialFit = true;
        _settings = settings;
        _history = history;
        _afterInitialPreview = afterInitialPreview;

        SetSurfaceSize(source.Width, source.Height);
        _sourceOperationActive = true;
        Cursor = Cursors.Wait;
        ContentRendered += OnInitialContentRendered;
        ContentRendered += OnChromeContentRendered;

        // The viewport zooms/pans instead of the window sizing itself to the
        // image, so a sensible default footprint is enough; the image is
        // fitted and centered once layout is known.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1167, wa.Width * 0.9);
        Height = Math.Min(590, wa.Height * 0.9);

        Viewport.LostMouseCapture += (_, _) =>
        {
            AbortDrag();
            AbortPan();
            AbortMove();
            AbortCurveHandle();
            AbortResize();
        };
        Loaded += (_, _) => FitToView();
        // Loaded can fire before the window has its final size; this catches the first
        // laid-out frame for the initial fit.
        SizeChanged += (_, _) => { if (_pendingInitialFit) FitToView(); };
        // The WINDOW can stop resizing while the VIEWPORT keeps shrinking as the toolbars / zoom
        // bar finish laying out — fit computed against the early (taller) viewport leaves a tall
        // capture too zoomed-in and clips its bottom. Re-fit on the viewport's own size changes
        // until the user takes control.
        Viewport.SizeChanged += (_, _) => { if (_pendingInitialFit) FitToView(); };
        Deactivated += (_, _) =>
        {
            _spaceDown = false;
            if (!_panning) UpdateCursor();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _dragPreview?.Dispose();
            _dragPreview = null;
            Task sourceOperationsDrained = _sourceLifetime.BeginClose();
            SD.Bitmap[] owned = _owned.ToArray();
            _owned.Clear();
            if (!_initialLoadStarted)
            {
                _initialPreviewReady.TrySetResult(null);
                _initialCaptureReady.TrySetResult(null);
            }

            if (sourceOperationsDrained.IsCompletedSuccessfully)
                DisposeOwned(owned);
            else
                _ = DisposeOwnedAfterAsync(sourceOperationsDrained, owned);
            MemoryCleanup.Request();
        };
        UpdateCursor();
        DarkTitleBar.Apply(this);
    }

    private void OnInitialContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnInitialContentRendered;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!_closed)
                    _ = LoadInitialSourceImageAsync();
            }));
    }

    private void OnChromeContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnChromeContentRendered;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                UpdateContextPanels();
                EditorBottomBar.Visibility = Visibility.Visible;
            }));
    }

    public static EditorWindow CreateForCapture(SD.Bitmap source, SettingsService settings, HistoryService history)
    {
        return new EditorWindow(source, settings, history);
    }

    internal static EditorWindow CreateForDirectCapture(
        SD.Bitmap source,
        SettingsService settings,
        HistoryService history,
        Func<Task> afterInitialPreview)
    {
        return new EditorWindow(source, settings, history, afterInitialPreview);
    }

    internal Task InitialPreviewReady => _initialPreviewReady.Task;
    internal Task InitialCaptureReady => _initialCaptureReady.Task;

    private static async Task DisposeOwnedAfterAsync(Task operationsDrained, IReadOnlyList<SD.Bitmap> owned)
    {
        try { await operationsDrained.ConfigureAwait(false); }
        catch { }
        DisposeOwned(owned);
        MemoryCleanup.Request();
    }

    private static void DisposeOwned(IEnumerable<SD.Bitmap> owned)
    {
        foreach (SD.Bitmap bmp in owned)
            bmp.Dispose();
    }

    private async Task LoadInitialSourceImageAsync()
    {
        _initialLoadStarted = true;
        using IDisposable sourceOperation = _sourceLifetime.Acquire();
        try
        {
            await RefreshImageAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load editor image", ex);
        }
        finally
        {
            _initialPreviewReady.TrySetResult(null);
        }

        try
        {
            if (_afterInitialPreview is not null)
                await _afterInitialPreview();
        }
        catch (Exception ex)
        {
            Log.Error("Direct Edit post-capture work failed", ex);
        }
        finally
        {
            _sourceOperationActive = false;
            _initialCaptureReady.TrySetResult(null);
            if (!_closed)
                UpdateCursor();
        }
    }

    /// <summary>Max tile height (px). 2048 is the lower of the common GPU texture limits and well
    /// under the software rasterizer's 16.16 (32768) ceiling, so every tile renders even over RDP.</summary>
    private const int BaseTileHeight = 2048;

    private async Task RefreshImageAsync()
    {
        using IDisposable sourceOperation = _sourceLifetime.AcquireNested();
        SD.Bitmap source = _source;
        Func<SD.Bitmap, int, Task<IReadOnlyList<BitmapSource>>> converter =
            SourceTileConverterForTest ?? CaptureService.ToBitmapSourceTilesBorrowedAsync;
        IReadOnlyList<BitmapSource> tiles = await converter(source, BaseTileHeight);
        if (_closed)
            return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (!_closed)
                SetBaseTiles(tiles);
        });
    }

    private void SetBaseTiles(IReadOnlyList<BitmapSource> tiles)
    {
        BaseTiles.Children.Clear();
        foreach (var t in tiles)
        {
            var img = new System.Windows.Controls.Image
            {
                Source = t,
                Width = t.PixelWidth,
                Height = t.PixelHeight,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            BaseTiles.Children.Add(img);
        }
    }

    private void SetSurfaceSize(double w, double h)
    {
        _sourceWidth = w;
        _sourceHeight = h;
        EditorSurface.Width = w;
        EditorSurface.Height = h;
        CanvasHost.Width = w;
        CanvasHost.Height = h;
        BaseTiles.Width = w;
        BaseTiles.Height = h;
        AnnotationCanvas.Width = w;
        AnnotationCanvas.Height = h;
        InteractionCanvas.Width = w;
        InteractionCanvas.Height = h;
    }

    // --------------------------------------------------------- view (zoom/pan)

    /// <summary>
    /// Fits the WHOLE image into the viewport, centered (never above 100%) — so a tall scrolling
    /// capture shows top-to-bottom at once instead of only its top. Use <see cref="FitToWidth"/>
    /// (the "Fit Width" preset) to read a long capture at full width and scroll down it.
    /// Ctrl+0 / Center button / open all route here.
    /// </summary>
    private void FitToView()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1 || _sourceWidth < 1 || _sourceHeight < 1) return; // viewport not ready; retry on next trigger

        const double margin = 24;
        double fit = Math.Min((vw - margin * 2) / _sourceWidth, (vh - margin * 2) / _sourceHeight);
        // Fit may legitimately need to go below MinZoom (a 32000px scroll capture in a 650px
        // viewport fits at ~0.02) — clamping it to MinZoom left only the middle band visible,
        // which read as "the editor clipped my capture". MinZoom still floors user zooming.
        _zoom = Math.Clamp(Math.Min(fit, 1.0), Math.Min(MinZoom, Math.Max(fit, 0.001)), MaxZoom);
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = Math.Round((vw - _sourceWidth * _zoom) / 2);
        ViewTranslate.Y = Math.Round((vh - _sourceHeight * _zoom) / 2);
        // Do NOT clear _pendingInitialFit here: the viewport keeps resizing as toolbars/the zoom
        // bar lay out after open, and an early fit computed against a too-large viewport leaves the
        // image too zoomed-in (bottom clipped). Keep re-fitting on every SizeChanged until the user
        // actually takes control of the view (clicks/scrolls/zooms/pans — those clear the flag).
        Log.Info($"Editor fit: src={_sourceWidth}x{_sourceHeight} viewport={vw:0}x{vh:0} zoom={_zoom:0.000}");
        OnViewChanged();
    }

    /// <summary>Fits to WIDTH and pins the top, so a long capture is readable and you scroll down
    /// it (mouse wheel). Capped at 100%.</summary>
    private void FitToWidth()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1 || _sourceWidth < 1 || _sourceHeight < 1) return;

        const double margin = 24;
        double fitW = (vw - margin * 2) / _sourceWidth;
        // Like FitToView: an ultra-wide capture's fit-width may drop below MinZoom.
        _zoom = Math.Clamp(Math.Min(fitW, 1.0), Math.Min(MinZoom, Math.Max(fitW, 0.001)), MaxZoom);
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = Math.Round((vw - _sourceWidth * _zoom) / 2);
        ViewTranslate.Y = margin; // pin to the top; scroll reveals the rest
        _pendingInitialFit = false; // explicit manual choice; SizeChanged would otherwise snap to fit-whole
        OnViewChanged();
    }

    /// <summary>Zooms so the content point under <paramref name="anchor"/> (viewport coords) stays put.</summary>
    private void ZoomAt(Point anchor, double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.00001) return;
        _pendingInitialFit = false; // explicit zoom — stop auto-refitting on resize

        double cx = (anchor.X - ViewTranslate.X) / _zoom;
        double cy = (anchor.Y - ViewTranslate.Y) / _zoom;
        _zoom = newZoom;
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = anchor.X - cx * _zoom;
        ViewTranslate.Y = anchor.Y - cy * _zoom;
        OnViewChanged();
    }

    private void OnViewChanged()
    {
        ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";
        // Keep helper chrome hairline-thin on screen at any zoom; the dash
        // pattern is in thickness units, so it compensates automatically.
        double t = 1.5 / _zoom;
        DragRect.StrokeThickness = t;
        SelectionRect.StrokeThickness = t;
        UpdateSelectionVisual();
        UpdateCurveHandleVisual();
    }

    private void OnCenterView(object sender, RoutedEventArgs e) => FitToView();

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Ctrl+wheel zooms around the cursor (ZoomAt clears _pendingInitialFit). A plain
            // scroll must NOT clear it: one stray wheel tick during the just-opened window's
            // toolbar layout would otherwise permanently cancel the pending refit and freeze
            // a bottom-clipped view of a tall capture.
            ZoomAt(e.GetPosition(Viewport), _zoom * Math.Pow(1.2, e.Delta / 120.0));
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift+wheel scrolls horizontally.
            ViewTranslate.X += e.Delta;
        }
        else
        {
            // Plain wheel scrolls vertically — the natural way to move through a tall scrolling
            // capture. (Ctrl+wheel still zooms.)
            ViewTranslate.Y += e.Delta;
        }
        e.Handled = true;
    }

    private void StartPan(Point viewportPos)
    {
        CommitText();
        _panning = true;
        _panLast = viewportPos;
        Viewport.CaptureMouse();
        UpdateCursor();
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        Viewport.ReleaseMouseCapture();
        UpdateCursor();
    }

    private void AbortPan()
    {
        if (!_panning) return;
        _panning = false;
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (_panning)
        {
            Viewport.Cursor = Cursors.SizeAll;
            EditorSurface.Cursor = Cursors.SizeAll;
        }
        else if (_spaceDown || _tool == EditorTool.Pan)
        {
            Viewport.Cursor = Cursors.Hand;
            EditorSurface.Cursor = Cursors.Hand;
        }
        else
        {
            Viewport.Cursor = Cursors.Arrow;
            EditorSurface.Cursor = _tool == EditorTool.Select ? Cursors.Arrow : Cursors.Cross;
        }
    }

    // ---------------------------------------------------------------- toolbar

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag || !Enum.TryParse(tag, out EditorTool tool))
            return;
        // Checked also fires while XAML is parsing, before sibling elements exist.
        if (IsLoaded)
        {
            CommitText();
            CommitPendingCurve();
            Select(null);
            if (_tool == EditorTool.Crop && tool != EditorTool.Crop)
                ClearCropPreview();
            if (tool == EditorTool.Eyedropper && _tool != EditorTool.Eyedropper)
                _toolBeforeEyedropper = _tool; // so a sample can return to the prior tool
            if (tool != EditorTool.Eyedropper)
                EyedropSwatch.Visibility = Visibility.Collapsed;
        }
        _tool = tool;
        if (ReferenceEquals(rb, RectangleToolBtn))
        {
            _filledRectangleMode = false;
            _fillMode = ShapeFillMode.None;
            if (FillNoneBtn is not null) FillNoneBtn.IsChecked = true;
        }
        if (IsLoaded)
        {
            UncheckOtherToolButtons(rb);
            MoreToolsPopup.IsOpen = false;
            UpdateCursor();
            UpdateContextPanels();
        }
    }

    private void OnFilledRectangleToolChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        if (IsLoaded)
        {
            CommitText();
            CommitPendingCurve();
            Select(null);
            if (_tool == EditorTool.Crop) ClearCropPreview();
            UncheckOtherToolButtons(rb);
        }

        _tool = EditorTool.Rectangle;
        _filledRectangleMode = true;
        _fillMode = ShapeFillMode.Solid;
        if (FillSolidBtn is not null) FillSolidBtn.IsChecked = true;
        if (IsLoaded)
        {
            MoreToolsPopup.IsOpen = false;
            UpdateCursor();
            UpdateContextPanels();
        }
    }

    private IEnumerable<RadioButton> ToolButtons()
    {
        if (CropUtilityBtn is not null) yield return CropUtilityBtn;
        if (ToolPanel is not null)
            foreach (var rb in ToolPanel.Children.OfType<RadioButton>()) yield return rb;
        if (MoreToolPanel is not null)
            foreach (var rb in MoreToolPanel.Children.OfType<RadioButton>()) yield return rb;
    }

    private void UncheckOtherToolButtons(RadioButton selected)
    {
        foreach (var rb in ToolButtons())
            if (!ReferenceEquals(rb, selected) && rb.IsChecked == true)
                rb.IsChecked = false;
    }

    private void OnMoreToolsClick(object sender, RoutedEventArgs e) =>
        MoreToolsPopup.IsOpen = !MoreToolsPopup.IsOpen;

    /// <summary>Shows the crop-ratio, text-style, step-mode, arrow-style and effect-strength controls only while their tool is active.</summary>
    private void UpdateContextPanels()
    {
        EditorContextControls controls = EditorShellContract.ContextFor(_tool, _filledRectangleMode);
        static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
        bool Has(EditorContextControls value) => controls.HasFlag(value);

        ColorPanel.Visibility = Show(Has(EditorContextControls.Color));
        ThicknessPanel.Visibility = Show(Has(EditorContextControls.Thickness));
        FillPanel.Visibility = Show(Has(EditorContextControls.Fill));
        OpacityPanel.Visibility = Show(Has(EditorContextControls.Opacity));
        ArrowStylePanel.Visibility = Show(Has(EditorContextControls.ArrowStyle));
        EffectStrengthPanel.Visibility = Show(Has(EditorContextControls.EffectStrength));
        TextStylePanel.Visibility = Show(Has(EditorContextControls.TextStyle));
        CropRatioPanel.Visibility = Show(Has(EditorContextControls.CropRatio));
        StepModePanel.Visibility = Show(Has(EditorContextControls.StepMode));
        ThicknessLabel.Text = _tool == EditorTool.Text ? "Size" : "Thickness";
        if (Has(EditorContextControls.Thickness))
            SyncThicknessButtons();

        if (Has(EditorContextControls.EffectStrength))
        {
            EffectStrengthLabel.Text = _tool == EditorTool.Blur ? "Blur" : "Pixelate";
            SyncEffectStrengthButtons(_tool == EditorTool.Blur ? _blurStrength : _pixelateStrength);
        }

        EditorStyleBar.Visibility = controls == EditorContextControls.None
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// The project format keeps the established 2/4/6 thickness values. Text presents the
    /// corresponding runtime font sizes (19/27/35 pt), while other tools keep stroke values.
    /// Separate remembered values stop a thick shape from silently making the next text 35 pt.
    /// </summary>
    private void SyncThicknessButtons()
    {
        bool text = _tool == EditorTool.Text;
        _thickness = text ? _textThickness : _strokeThickness;

        var buttons = new[] { ThicknessThinBtn, ThicknessMediumBtn, ThicknessThickBtn };
        string[] contents = text ? new[] { "19", "27", "35" } : new[] { "2", "4", "6" };
        string[] names = text
            ? new[] { "19 point text", "27 point text", "35 point text" }
            : new[] { "Thin stroke", "Medium stroke", "Thick stroke" };

        _syncingThicknessButtons = true;
        try
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                RadioButton button = buttons[i];
                button.Content = contents[i];
                button.ToolTip = names[i];
                AutomationProperties.SetName(button, names[i]);
                if (button.Tag is string tag && double.TryParse(tag, out double value))
                    button.IsChecked = Math.Abs(value - _thickness) < 0.01;
            }
        }
        finally
        {
            _syncingThicknessButtons = false;
        }
    }

    /// <summary>Reflects the active blur/pixelate strength on the strength radio group without re-triggering an apply.</summary>
    private void SyncEffectStrengthButtons(EffectStrength strength)
    {
        RadioButton target = strength switch
        {
            EffectStrength.Light => StrengthLightBtn,
            EffectStrength.Strong => StrengthStrongBtn,
            _ => StrengthMediumBtn,
        };
        if (target.IsChecked != true)
            target.IsChecked = true; // OnEffectStrengthChecked re-stores the same value (idempotent)
    }

    /// <summary>Re-checks the toolbar radio for a tool (used by the eyedropper to restore the prior tool).</summary>
    private void CheckToolButton(EditorTool tool)
    {
        foreach (var rb in ToolButtons())
        {
            if (rb.Tag is string tag &&
                string.Equals(tag, tool.ToString(), StringComparison.Ordinal))
            {
                rb.IsChecked = true; // Checked → OnToolChecked updates _tool
                return;
            }
        }
        _tool = tool;
        UpdateCursor();
        UpdateContextPanels();
    }

    private void OnColorChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Background is SolidColorBrush brush)
            SetCurrentColor(brush.Color);
    }

    private void SetCurrentColor(Color color)
    {
        _color = color;
        // Fires during XAML parse for the default swatch, before the indicator exists.
        if (CurrentColorIndicator is not null)
            CurrentColorIndicator.Fill = new SolidColorBrush(color);
        if (IsLoaded)
            RestyleSelected(color: color, thickness: null, fill: null);
    }

    private void OnThicknessChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string s && double.TryParse(s, out double t))
        {
            _thickness = t;
            if (_tool == EditorTool.Text)
                _textThickness = t;
            else
                _strokeThickness = t;
            if (IsLoaded && !_syncingThicknessButtons)
                RestyleSelected(color: null, thickness: t, fill: null);
        }
    }

    private void OnFillChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && Enum.TryParse(tag, out ShapeFillMode mode))
        {
            _fillMode = mode;
            if (IsLoaded)
                RestyleSelected(color: null, thickness: null, fill: mode);
        }
    }

}
