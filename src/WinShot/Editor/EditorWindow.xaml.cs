using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private double _thickness = AnnotationStyle.DefaultPenSize;
    private double _penSize = AnnotationStyle.DefaultPenSize;
    private double _highlighterSize = AnnotationStyle.DefaultHighlighterSize;
    private double _shapeSize = AnnotationStyle.DefaultShapeSize;
    private double _textFontSize = AnnotationStyle.DefaultTextSize;
    private bool _syncingSizeBox;
    private bool _syncingContext;
    private string _textFontFamily = "Segoe UI";
    private bool _textBold;
    private bool _textItalic;
    private bool _textUnderline;
    private bool _textStrike;
    private string _textAlign = "left";
    private string _textVerticalAlign = "top";
    private Rect? _activeTextBox;
    private int _nextStep = 1;
    private bool _stepLetters; // Step tool: false = number badges, true = letter badges (A, B, …)
    private ShapeFillMode _fillMode = ShapeFillMode.None;
    private TextStyle _textStyle = TextStyle.Plain;
    private ArrowStyle _arrowStyle = ArrowStyle.Straight;
    private ArrowheadStyle _arrowhead = ArrowheadStyle.SolidTriangle;
    private ArrowheadStyle _startArrowhead = ArrowheadStyle.None;
    private LineBorderStyle _lineStyle = LineBorderStyle.Solid;
    private ColorWell _colorWell = ColorWell.Stroke;
    private Color _fillColor = Color.FromArgb(0, 255, 255, 255);
    private double _fillOpacity = 0;
    private EditorTool _lastDrawTool = EditorTool.Freehand;
    private EditorTool _lastShapeTool = EditorTool.Rectangle;
    private EditorTool _lastTextTool = EditorTool.Text;
    private EditorTool _lastEffectsTool = EditorTool.Blur;
    private Color _colorBeforeHighlighter;
    private double _opacityBeforeHighlighter = 1;
    private Color _highlighterColor = Color.FromRgb(0xFF, 0xFF, 0x00);
    private double _highlighterOpacity = 0.5;
    private bool _highlighterArmed;
    private EditorTool _toolBeforeCrop = EditorTool.Select;
    private bool _eraserPartial = true;
    private double _eraserSize = 10;
    private readonly List<Point> _eraserPoints = new();
    private double _pickerHue;
    private double _pickerSat = 1;
    private double _pickerVal = 1;
    private bool _svDragging;
    private bool _hueDragging;
    private double _opacity = 1.0; // annotation alpha multiplier (0–1.0) for new + restyled marks
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
    private enum ViewFitMode { Free, Fit, FitWidth, FitHeight }
    private ViewFitMode _viewFitMode = ViewFitMode.Fit;

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
    private CalloutAnnotation? _previewCallout;
    private bool _calloutReediting;
    private string _calloutReeditBefore = "";
    private bool _movingCalloutBox;
    private CalloutLayout? _calloutBoxBefore;
    private TextBox? _activeText;
    private SD.Rectangle? _pendingCrop;

    // Selection state (Select tool). _selected is the primary (format chrome);
    // _selection holds every marquee/shift-selected annotation, including primary.
    private UIElement? _selected;
    private readonly List<UIElement> _selection = new();
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
            if (_marqueeSelecting)
            {
                _marqueeSelecting = false;
                HideDragRect();
            }
        };
        Loaded += (_, _) => FitToView();
        // Loaded can fire before the window has its final size; this catches the first
        // laid-out frame for the initial fit.
        SizeChanged += (_, _) =>
        {
            if (_pendingInitialFit) FitToView();
            Dispatcher.BeginInvoke(PackFormatOverflow, DispatcherPriority.Background);
        };
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
                FitEditorCombos();
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
    /// Fits the WHOLE image into the viewport, centered. Small captures scale up so the
    /// paper is the focal point; tall scrolling captures scale down so top-to-bottom is
    /// visible. Use <see cref="FitToWidth"/> to read a long capture at full width and
    /// scroll down it. Ctrl+0 / Center button / open all route here.
    /// </summary>
    private void FitToView()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1 || _sourceWidth < 1 || _sourceHeight < 1) return; // viewport not ready; retry on next trigger

        const double margin = 24;
        double fit = Math.Min((vw - margin * 2) / _sourceWidth, (vh - margin * 2) / _sourceHeight);
        // Fit may go below MinZoom (a 32000px scroll capture in a 650px viewport fits at
        // ~0.02) — clamping it to MinZoom left only the middle band visible. MinZoom still
        // floors user zooming. Small captures scale UP so Fit is not a postage stamp at 100%.
        _zoom = Math.Clamp(fit, Math.Min(MinZoom, Math.Max(fit, 0.001)), MaxZoom);
        _viewFitMode = ViewFitMode.Fit;
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
    /// it (mouse wheel). Small captures scale up to fill the width.</summary>
    private void FitToWidth()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1 || _sourceWidth < 1 || _sourceHeight < 1) return;

        const double margin = 24;
        double fitW = (vw - margin * 2) / _sourceWidth;
        // Like FitToView: an ultra-wide capture's fit-width may drop below MinZoom.
        _zoom = Math.Clamp(fitW, Math.Min(MinZoom, Math.Max(fitW, 0.001)), MaxZoom);
        _viewFitMode = ViewFitMode.FitWidth;
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = Math.Round((vw - _sourceWidth * _zoom) / 2);
        ViewTranslate.Y = margin; // pin to the top; scroll reveals the rest
        _pendingInitialFit = false; // explicit manual choice; SizeChanged would otherwise snap to fit-whole
        OnViewChanged();
    }

    /// <summary>Fits to HEIGHT, centred horizontally — Survey's third fit mode.</summary>
    private void FitToHeight()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1 || _sourceWidth < 1 || _sourceHeight < 1) return;

        const double margin = 24;
        double fitH = (vh - margin * 2) / _sourceHeight;
        _zoom = Math.Clamp(fitH, Math.Min(MinZoom, Math.Max(fitH, 0.001)), MaxZoom);
        _viewFitMode = ViewFitMode.FitHeight;
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = Math.Round((vw - _sourceWidth * _zoom) / 2);
        ViewTranslate.Y = Math.Round((vh - _sourceHeight * _zoom) / 2);
        _pendingInitialFit = false;
        OnViewChanged();
    }

    /// <summary>Survey's TOOLBAR_ZOOM_STEP_FACTOR: each click is a quarter step about the viewport centre.</summary>
    private const double ZoomStepFactor = 1.25;

    private Point ViewportCentre() => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomAt(ViewportCentre(), _zoom * ZoomStepFactor);

    // Survey's zoom input: digits only, Enter commits and blurs, Esc reverts, blur commits.
    private void OnZoomBoxTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void OnZoomBoxFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ZoomBox.Text = Math.Round(_zoom * 100).ToString();
        ZoomBox.Dispatcher.BeginInvoke(ZoomBox.SelectAll, DispatcherPriority.Input);
    }

    private void OnZoomBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitZoomBox(); Viewport.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ZoomBox.Text = Math.Round(_zoom * 100).ToString(); Viewport.Focus(); e.Handled = true; }
    }

    private void OnZoomBoxBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitZoomBox();
        ZoomBox.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private void CommitZoomBox()
    {
        if (int.TryParse(ZoomBox.Text.Trim().TrimEnd('%'), out int pct))
            ZoomAt(ViewportCentre(), Math.Clamp(pct, 1, 4000) / 100.0);
    }

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomAt(ViewportCentre(), _zoom / ZoomStepFactor);

    private void OnFitMenuClick(object sender, RoutedEventArgs e) => FitMenuPopup.IsOpen = !FitMenuPopup.IsOpen;

    private void OnFitPage(object sender, RoutedEventArgs e) { FitMenuPopup.IsOpen = false; FitToView(); }

    private void OnFitWidth(object sender, RoutedEventArgs e) { FitMenuPopup.IsOpen = false; FitToWidth(); }

    private void OnFitHeight(object sender, RoutedEventArgs e) { FitMenuPopup.IsOpen = false; FitToHeight(); }

    /// <summary>Zooms so the content point under <paramref name="anchor"/> (viewport coords) stays put.</summary>
    private void ZoomAt(Point anchor, double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.00001) return;
        _pendingInitialFit = false; // explicit zoom — stop auto-refitting on resize
        _viewFitMode = ViewFitMode.Free;

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
        if (ZoomBox is not null && !ZoomBox.IsKeyboardFocused)
            ZoomBox.Text = $"{Math.Round(_zoom * 100)}%";
        SyncZoomPresetChrome();
        SyncCaptureShadow();
        // Keep helper chrome hairline-thin on screen at any zoom; the dash
        // pattern is in thickness units, so it compensates automatically.
        double t = 1.5 / _zoom;
        DragRect.StrokeThickness = t;
        SelectionRect.StrokeThickness = t;
        UpdateSelectionVisual();
        UpdateCurveHandleVisual();
    }

    /// <summary>
    /// Survey's zoom cluster: −/+ around a live percentage, and a fit trigger that shows the
    /// current fit mode's icon and label. Nothing here changes width per tool.
    /// </summary>
    private void SyncZoomPresetChrome()
    {
        if (FitMenuBtn is null || FitModeIcon is null) return;
        (string label, string icon) = _viewFitMode switch
        {
            ViewFitMode.Fit => ("Fit page", "SurveyIcon.FitPage"),
            ViewFitMode.FitWidth => ("Fit width", "SurveyIcon.FitWidth"),
            ViewFitMode.FitHeight => ("Fit height", "SurveyIcon.FitHeight"),
            _ => ("Manual", "SurveyIcon.FitPage"),
        };
        FitMenuBtn.ToolTip = label;
        FitModeIcon.Content = FindResource(icon);
        FitModeIcon.Opacity = _viewFitMode == ViewFitMode.Free ? 0.6 : 1;
        foreach (var (item, mode) in new[] { (FitPageItem, ViewFitMode.Fit), (FitWidthItem, ViewFitMode.FitWidth), (FitHeightItem, ViewFitMode.FitHeight) })
            if (item is not null)
                item.Foreground = (Brush)FindResource(_viewFitMode == mode ? "TextPrimaryBrush" : "TextSecondaryBrush");
    }

    private void SyncCaptureShadow()
    {
        if (CaptureShadow is null || _sourceWidth < 1 || _sourceHeight < 1) return;
        CaptureShadow.Visibility = Visibility.Visible;
        CaptureShadow.Width = Math.Max(1, _sourceWidth * _zoom);
        CaptureShadow.Height = Math.Max(1, _sourceHeight * _zoom);
        Canvas.SetLeft(CaptureShadow, ViewTranslate.X);
        Canvas.SetTop(CaptureShadow, ViewTranslate.Y);
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
            SyncCaptureShadow();
        }
        else
        {
            // Plain wheel scrolls vertically — the natural way to move through a tall scrolling
            // capture. (Ctrl+wheel still zooms.)
            ViewTranslate.Y += e.Delta;
            SyncCaptureShadow();
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
            // Keep the current annotation selected when switching to Select/Pan so
            // format chrome stays on that object. Arming a draw tool clears it.
            if (tool is not (EditorTool.Select or EditorTool.Pan))
                Select(null);
            if (_tool == EditorTool.Crop && tool != EditorTool.Crop)
                ClearCropPreview();
            if (tool == EditorTool.Eyedropper && _tool != EditorTool.Eyedropper)
                _toolBeforeEyedropper = _tool; // so a sample can return to the prior tool
            if (tool != EditorTool.Eyedropper)
                EyedropSwatch.Visibility = Visibility.Collapsed;
        }
        RememberGroupTool(tool);
        ArmHighlighterColor(tool);
        _tool = tool;
        if (ReferenceEquals(rb, RectangleToolBtn))
        {
            _fillMode = ShapeFillMode.None;
            if (FillNoneBtn is not null) FillNoneBtn.IsChecked = true;
        }
        if (IsLoaded)
        {
            UncheckOtherToolButtons(rb);
            UpdateCursor();
            UpdateContextPanels();
            UpdateGroupButtonChrome();
        }
    }

    private IEnumerable<RadioButton> ToolButtons()
    {
        if (ToolPanel is not null)
            foreach (var rb in ToolPanel.Children.OfType<RadioButton>()) yield return rb;
        if (DrawGroupPanel is not null)
            foreach (var rb in DrawGroupPanel.Children.OfType<RadioButton>()) yield return rb;
        if (ShapeGroupPanel is not null)
            foreach (var rb in ShapeGroupPanel.Children.OfType<RadioButton>()) yield return rb;
        if (TextGroupPanel is not null)
            foreach (var rb in TextGroupPanel.Children.OfType<RadioButton>()) yield return rb;
    }

    private void OnDrawGroupClick(object sender, RoutedEventArgs e)
    {
        if (EditorShellContract.GroupFor(_tool, false) != "Draw")
            CheckToolButton(_lastDrawTool);
        UpdateContextPanels();
        UpdateGroupButtonChrome();
    }

    private void OnShapeGroupClick(object sender, RoutedEventArgs e)
    {
        if (EditorShellContract.GroupFor(_tool, false) != "Shape")
            CheckToolButton(_lastShapeTool);
        UpdateContextPanels();
        UpdateGroupButtonChrome();
    }

    private void OnTextGroupClick(object sender, RoutedEventArgs e)
    {
        if (EditorShellContract.GroupFor(_tool, false) != "Text")
            CheckToolButton(_lastTextTool);
        UpdateContextPanels();
        UpdateGroupButtonChrome();
    }

    private void OnEffectsGroupClick(object sender, RoutedEventArgs e)
    {
        if (EditorShellContract.GroupFor(_tool, false) != "Effects")
            CheckToolButton(_lastEffectsTool);
        UpdateContextPanels();
        UpdateGroupButtonChrome();
    }

    private void RememberGroupTool(EditorTool tool)
    {
        switch (EditorShellContract.GroupFor(tool, false))
        {
            case "Draw": _lastDrawTool = tool; break;
            case "Shape": _lastShapeTool = tool; break;
            case "Text": _lastTextTool = tool; break;
            case "Effects": _lastEffectsTool = tool; break;
        }
    }

    private void ArmHighlighterColor(EditorTool tool)
    {
        if (tool == EditorTool.Highlighter && !_highlighterArmed)
        {
            _colorBeforeHighlighter = _color;
            _opacityBeforeHighlighter = _opacity;
            _highlighterArmed = true;
            SetCurrentColor(_highlighterColor);
            SetOpacityPercent(_highlighterOpacity * 100, restyle: false);
        }
        else if (tool != EditorTool.Highlighter && _highlighterArmed)
        {
            _highlighterColor = _color;
            _highlighterOpacity = _opacity;
            _highlighterArmed = false;
            SetCurrentColor(_colorBeforeHighlighter);
            SetOpacityPercent(_opacityBeforeHighlighter * 100, restyle: false);
        }
    }

    private void OnCropClick(object sender, RoutedEventArgs e)
    {
        CommitText();
        CommitPendingCurve();
        if (_tool == EditorTool.Crop)
        {
            ClearCropPreview(restoreTool: true);
            return;
        }
        if (_tool != EditorTool.Crop)
            _toolBeforeCrop = _tool;
        _tool = EditorTool.Crop;
        UpdateCursor();
        UpdateContextPanels();
        UpdateGroupButtonChrome();
        PaintCropChrome(true);
    }

    private void UpdateGroupButtonChrome()
    {
        string group = _tool is EditorTool.Select or EditorTool.Pan
            ? EditorShellContract.GroupFor(ChromeTool(), ChromeFilledRectangle())
            : EditorShellContract.GroupFor(_tool, false);
        void Paint(Button btn, bool on)
        {
            if (btn is null) return;
            btn.Opacity = 1;
            btn.Background = Brushes.Transparent;
            btn.Foreground = on
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("TextPrimaryBrush");
        }
        Paint(DrawGroupBtn, group == "Draw");
        Paint(ShapeGroupBtn, group == "Shape");
        Paint(TextGroupBtn, group == "Text");
        Paint(EffectsGroupBtn, group == "Effects");
        PaintCropChrome(_tool == EditorTool.Crop);
    }

    private void PaintCropChrome(bool on)
    {
        if (CropUtilityBtn is null) return;
        CropUtilityBtn.Background = on
            ? (Brush)FindResource("HoverFillBrush")
            : Brushes.Transparent;
        CropUtilityBtn.Foreground = on
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("TextPrimaryBrush");
    }

    private void UncheckOtherToolButtons(RadioButton selected)
    {
        foreach (var rb in ToolButtons())
            if (!ReferenceEquals(rb, selected) && rb.IsChecked == true)
                rb.IsChecked = false;
    }

    /// <summary>
    /// Format chrome follows the selected annotation when one is selected (Survey
    /// contextTool), otherwise the drawing tool. Group radios still follow the drawing tool.
    /// </summary>
    private void UpdateContextPanels()
    {
        EditorTool chrome = ChromeTool();
        bool filled = ChromeFilledRectangle();
        EditorContextControls controls = EditorShellContract.ContextFor(chrome, filled);
        static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
        bool Has(EditorContextControls value) => controls.HasFlag(value);

        ColorPanel.Visibility = Show(Has(EditorContextControls.Color));
        ThicknessPanel.Visibility = Show(Has(EditorContextControls.Thickness));
        FillPanel.Visibility = Visibility.Collapsed;
        OpacityPanel.Visibility = Visibility.Collapsed;
        ArrowStylePanel.Visibility = Show(Has(EditorContextControls.ArrowStyle));
        EffectStrengthPanel.Visibility = Show(Has(EditorContextControls.EffectStrength));
        TextStylePanel.Visibility = Show(Has(EditorContextControls.TextStyle));
        CropRatioPanel.Visibility = Show(Has(EditorContextControls.CropRatio));
        StepModePanel.Visibility = Show(Has(EditorContextControls.StepMode));
        LineStylePanel.Visibility = Show(Has(EditorContextControls.LineStyle));
        FillStrokeTabPanel.Visibility = Show(Has(EditorContextControls.FillStrokeTabs));
        EraserModePanel.Visibility = Show(Has(EditorContextControls.EraserMode));
        ColorWellLabel.Visibility = Visibility.Collapsed;
        if (CloudStyleItem is not null)
            CloudStyleItem.Visibility = Show(chrome == EditorTool.Rectangle);
        SyncColorWellIndicator();
        ThicknessLabel.Visibility = Visibility.Collapsed;
        if (Has(EditorContextControls.Thickness))
            SyncSizeBox();
        if (Has(EditorContextControls.TextStyle))
            SyncTextFormatChrome();
        if (FontSizeBox is not null)
            FontSizeBox.Visibility = Show(chrome == EditorTool.Callout);

        if (Has(EditorContextControls.EffectStrength))
        {
            EffectStrengthLabel.Visibility = Visibility.Collapsed;
            if (EffectStrengthBox is not null)
                EffectStrengthBox.ToolTip = chrome == EditorTool.Blur ? "Blur strength" : "Pixelate strength";
            SyncEffectStrengthButtons(chrome == EditorTool.Blur ? _blurStrength : _pixelateStrength);
        }

        string group = _tool is EditorTool.Select or EditorTool.Pan
            ? EditorShellContract.GroupFor(chrome, filled)
            : EditorShellContract.GroupFor(_tool, false);
        DrawGroupPanel.Visibility = Show(group == "Draw");
        ShapeGroupPanel.Visibility = Show(group == "Shape");
        TextGroupPanel.Visibility = Show(group == "Text");
        EffectsGroupPanel.Visibility = Show(group == "Effects");

        EditorColorBar.Visibility = Visibility.Visible;
        if (SizeEffectHost is not null)
            SizeEffectHost.Visibility = Show(Has(EditorContextControls.Thickness) || Has(EditorContextControls.EffectStrength));
        bool props = Has(EditorContextControls.Color) || Has(EditorContextControls.Thickness) ||
                     Has(EditorContextControls.EffectStrength) || Has(EditorContextControls.TextStyle) ||
                     Has(EditorContextControls.ArrowStyle) || Has(EditorContextControls.LineStyle) ||
                     Has(EditorContextControls.EraserMode) || Has(EditorContextControls.StepMode) ||
                     Has(EditorContextControls.CropRatio) || CropPanel.Visibility == Visibility.Visible;
        if (SecondaryToolPanel is not null)
            SecondaryToolPanel.Visibility = Show(props);
        EditorFormatBar.Visibility = Visibility.Visible;
        EditorGroupToolsHost.Visibility = Visibility.Visible;
        EditorGroupBar.Visibility = Show(group is "Draw" or "Shape" or "Text" or "Effects");
        Dispatcher.BeginInvoke(PackFormatOverflow, DispatcherPriority.Loaded);
    }

    private void OnFormatOverflowClick(object sender, RoutedEventArgs e)
    {
        if (FormatOverflowPopup is not null)
            FormatOverflowPopup.IsOpen = true;
    }

    private readonly Dictionary<FrameworkElement, Panel> _formatHomes = new();
    private bool _packingFormat;

    /// <summary>
    /// Keep color + size on the strip. Pack per control so B/I/U/S stay visible
    /// and long combos (font, arrowhead) go into the same-row overflow flyout.
    /// Crop Apply is packed first. Budget is leftover star-column slack after
    /// extras are collapsed, not a magic 200 DIP and not the already-grown bar.
    /// </summary>
    private void PackFormatOverflow()
    {
        if (_packingFormat || FormatOverflowPanel is null || FormatOverflowBtn is null) return;
        if (!IsLoaded || ActualWidth < 200) return;
        _packingFormat = true;
        try
        {
            RememberFormatHomes();
            foreach (var kv in _formatHomes)
            {
                if (kv.Key.Parent == kv.Value) continue;
                (kv.Key.Parent as Panel)?.Children.Remove(kv.Key);
                kv.Value.Children.Add(kv.Key);
            }
            FormatOverflowBtn.Visibility = Visibility.Collapsed;

            var candidates = FormatPackUnits()
                .Where(u => u.IsVisible)
                .ToList();
            if (candidates.Count == 0) return;

            double slack = MeasureFormatSlack();
            List<FrameworkElement> overflow = WhoOverflows(candidates, slack);

            foreach (FrameworkElement unit in overflow)
            {
                (unit.Parent as Panel)?.Children.Remove(unit);
                FormatOverflowPanel.Children.Add(unit);
            }
            FormatOverflowBtn.Visibility = overflow.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (InvalidOperationException)
        {
            // Reparent during an in-flight layout pass — the next idle pack will retry.
        }
        finally { _packingFormat = false; }
    }

    /// <summary>
    /// Room for extras after undo cluster, centered tools, color/size, and Save/Done.
    /// Does not call UpdateLayout — packing can run from SizeChanged.
    /// </summary>
    private double MeasureFormatSlack()
    {
        double inner = TopToolbar is { ActualWidth: > 0 } top
            ? top.ActualWidth
            : Math.Max(0, ActualWidth - 24);
        double tools = MeasureWidth(ToolPanel);
        // Tools are centred; the props block gets the star column to their right.
        double right = 64; // divider, panel margins, and the bar's own padding
        if (ColorPanel?.Visibility == Visibility.Visible)
            right += MeasureWidth(ColorPanel);
        if (SizeEffectHost?.Visibility == Visibility.Visible)
            right += MeasureWidth(SizeEffectHost);
        double slack = (inner - tools) / 2 - right;
        // Letters and Apply crop stay on the strip even if tools walk a few DIP.
        if (TextStylePanel?.Visibility == Visibility.Visible && TextFormatButtons is not null)
            slack = Math.Max(slack, MeasureWidth(TextFormatButtons) + 4);
        if (CropPanel?.Visibility == Visibility.Visible)
        {
            double applyW = 0;
            foreach (FrameworkElement child in CropPanel.Children.OfType<FrameworkElement>())
                if (AutomationProperties.GetName(child) == "Apply crop")
                    applyW = MeasureWidth(child);
            if (applyW > 0)
                slack = Math.Min(slack, applyW + 8);
            slack = Math.Max(slack, applyW > 0 ? applyW + 4 : 96);
        }
        return Math.Max(48, slack);
    }

    private static double MeasureWidth(FrameworkElement? el)
    {
        if (el is null || el.Visibility != Visibility.Visible) return 0;
        if (el.ActualWidth > 1) return el.ActualWidth;
        return el.DesiredSize.Width;
    }

    private static List<FrameworkElement> WhoOverflows(
        List<FrameworkElement> candidates, double max)
    {
        double used = 0;
        var overflow = new List<FrameworkElement>();
        foreach (FrameworkElement unit in candidates)
        {
            double w = unit.ActualWidth > 1 ? unit.ActualWidth : unit.DesiredSize.Width;
            if (w < 1) w = Math.Max(unit.MinWidth, 48);
            if (used + w <= max) used += w;
            else overflow.Add(unit);
        }
        return overflow;
    }

    private void RememberFormatHomes()
    {
        foreach (FrameworkElement unit in FormatPackUnits())
            if (!_formatHomes.ContainsKey(unit) && unit.Parent is Panel home)
                _formatHomes[unit] = home;
    }

    private IEnumerable<FrameworkElement> FormatPackUnits()
    {
        if (CropPanel?.Visibility == Visibility.Visible)
            foreach (FrameworkElement inner in CropPanel.Children.OfType<FrameworkElement>())
                if (AutomationProperties.GetName(inner) == "Apply crop")
                    yield return inner;
        if (TextStylePanel?.Visibility == Visibility.Visible)
            foreach (FrameworkElement inner in TextStylePanel.Children.OfType<FrameworkElement>())
            {
                if (inner is Popup) continue;
                yield return inner;
            }
        if (EraserModePanel is not null) yield return EraserModePanel;
        if (LineStylePanel is not null) yield return LineStylePanel;
        if (ArrowStylePanel is not null) yield return ArrowStylePanel;
        if (StepModePanel is not null) yield return StepModePanel;
        if (CropRatioPanel is not null) yield return CropRatioPanel;
        if (CropPanel?.Visibility == Visibility.Visible)
            foreach (FrameworkElement inner in CropPanel.Children.OfType<FrameworkElement>())
                if (AutomationProperties.GetName(inner) != "Apply crop")
                    yield return inner;
    }

    private EditorTool ChromeTool()
    {
        if (_selected is FrameworkElement fe && fe.Tag is AnnotationData meta &&
            EditorShellContract.ToolForAnnotation(meta.Type) is EditorTool mapped)
            return mapped;
        return _tool;
    }

    private bool ChromeFilledRectangle() =>
        _selected is FrameworkElement fe && fe.Tag is AnnotationData meta && EditorShellContract.IsFilledRectangle(meta);

    private void SyncChromeFromSelection()
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
            return;
        _syncingContext = true;
        _syncingSizeBox = true;
        try
        {
            if (meta.Color is string hex && TryParseColor(hex, out var stroke))
            {
                _color = AnnotationStyle.Opaque(stroke);
                _opacity = stroke.A / 255.0;
            }
            if (meta.FillColor is string fillHex && TryParseColor(fillHex, out var fill))
                _fillColor = fill;
            if (meta.Fill is string fillName && Enum.TryParse(fillName, out ShapeFillMode fillMode))
            {
                _fillMode = fillMode;
                if (fillMode == ShapeFillMode.None && FillNoneBtn is not null) FillNoneBtn.IsChecked = true;
                else if (fillMode == ShapeFillMode.Quarter && FillQuarterBtn is not null) FillQuarterBtn.IsChecked = true;
                else if (fillMode == ShapeFillMode.Solid && FillSolidBtn is not null) FillSolidBtn.IsChecked = true;
            }
            if (meta.FontSize is double fs)
                _textFontSize = AnnotationStyle.ClampTextSize(fs);
            if (meta.Thickness is double th)
            {
                switch (meta.Type)
                {
                    case AnnotationData.TypeHighlighter: _highlighterSize = th; break;
                    case AnnotationData.TypeFreehand: _penSize = th; break;
                    case AnnotationData.TypeText: break;
                    case AnnotationData.TypeCallout: _shapeSize = th; break;
                    default: _shapeSize = th; break;
                }
            }
            if (!string.IsNullOrWhiteSpace(meta.FontFamily))
                _textFontFamily = meta.FontFamily;
            _textBold = meta.Bold ?? false;
            _textItalic = meta.Italic ?? false;
            _textUnderline = meta.Underline ?? false;
            _textStrike = meta.Strike ?? false;
            if (!string.IsNullOrWhiteSpace(meta.Align)) _textAlign = meta.Align;
            if (!string.IsNullOrWhiteSpace(meta.VerticalAlign)) _textVerticalAlign = meta.VerticalAlign;
            if (meta.Style is string styleName && Enum.TryParse(styleName, out TextStyle textStyle))
                _textStyle = textStyle;
            _lineStyle = AnnotationStyle.LineStyleFrom(meta);
            (_arrowhead, _startArrowhead) = AnnotationStyle.HeadsFrom(meta);
            SelectComboByTag(LineStyleBox, _lineStyle == LineBorderStyle.Cloud ? "cloud" :
                _lineStyle == LineBorderStyle.Dashed ? "dashed" :
                _lineStyle == LineBorderStyle.Dotted ? "dotted" : "solid");
            SelectComboByTag(ArrowStyleBox, ArrowTag(_arrowhead));
            SelectComboByTag(AlignBox, _textAlign);
            SelectComboByTag(VerticalAlignBox, _textVerticalAlign);
            if (OpacitySlider is not null)
                OpacitySlider.Value = _opacity * 100;
            if (OpacityValue is not null)
                OpacityValue.Text = $"{Math.Round(_opacity * 100)}%";
        }
        finally
        {
            _syncingSizeBox = false;
            _syncingContext = false;
        }
    }

    private static string ArrowTag(ArrowheadStyle style) => style switch
    {
        ArrowheadStyle.None => "none",
        ArrowheadStyle.VShape => "vShape",
        ArrowheadStyle.OpenCircle => "openCircle",
        ArrowheadStyle.OpenTriangle => "openTriangle",
        ArrowheadStyle.HorizontalLine => "horizontalLine",
        _ => "solidTriangle",
    };

    private static void SelectComboByTag(ComboBox? box, string tag)
    {
        if (box is null) return;
        foreach (ComboBoxItem item in box.Items.OfType<ComboBoxItem>())
            if (item.Tag is string t && string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
    }

    private void SyncSizeBox()
    {
        if (SizeBox is null) return;
        bool text = ChromeTool() is EditorTool.Text;
        int[] presets = _tool == EditorTool.Eraser
            ? AnnotationStyle.EraserSizePresets
            : text ? AnnotationStyle.TextSizePresets : AnnotationStyle.SizePresets;
        _thickness = ActiveSize();

        _syncingSizeBox = true;
        try
        {
            SizeBox.Items.Clear();
            int current = _tool == EditorTool.Eraser
                ? AnnotationStyle.ClampEraserSize(_eraserSize)
                : text ? AnnotationStyle.ClampTextSize(_textFontSize) : AnnotationStyle.ClampSize(_thickness);
            ComboBoxItem? selected = null;
            foreach (int preset in presets)
            {
                var item = new ComboBoxItem { Content = preset.ToString(), Tag = (double)preset };
                SizeBox.Items.Add(item);
                if (preset == current) selected = item;
            }
            if (selected is null)
            {
                selected = new ComboBoxItem { Content = current.ToString(), Tag = (double)current };
                SizeBox.Items.Insert(0, selected);
            }
            SizeBox.SelectedItem = selected;
            FitComboToLongestItem(SizeBox,
                AnnotationStyle.SizePresets.Select(n => n.ToString())
                    .Concat(AnnotationStyle.TextSizePresets.Select(n => n.ToString()))
                    .Concat(AnnotationStyle.EraserSizePresets.Select(n => n.ToString()))
                    .ToArray());
        }
        finally
        {
            _syncingSizeBox = false;
        }
    }

    /// <summary>
    /// Sizes a combo to its longest item (plus extras) so picking a shorter
    /// value never shrinks the toolbar.
    /// </summary>
    private static void FitComboToLongestItem(ComboBox? box, params string[] extra)
    {
        if (box is null) return;
        double dip = 1;
        try { dip = VisualTreeHelper.GetDpi(box).PixelsPerDip; }
        catch (InvalidOperationException) { /* not in a visual tree yet */ }
        var typeface = new Typeface("Segoe UI");
        double max = 0;
        void Consider(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var ft = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, 12, Brushes.White, dip);
            if (ft.Width > max) max = ft.Width;
        }
        foreach (string s in extra) Consider(s);
        foreach (object item in box.Items)
            Consider(item is ComboBoxItem cbi ? cbi.Content?.ToString() : item?.ToString());
        // DarkComboBox ContentSite margin is 10 left + 24 for the chevron.
        double width = Math.Ceiling(max) + 38;
        if (width < 52) width = 52;
        box.MinWidth = width;
        box.Width = width;
    }

    private void FitEditorCombos()
    {
        FitComboToLongestItem(SizeBox,
            AnnotationStyle.SizePresets.Select(n => n.ToString())
                .Concat(AnnotationStyle.TextSizePresets.Select(n => n.ToString()))
                .Concat(AnnotationStyle.EraserSizePresets.Select(n => n.ToString()))
                .ToArray());
        FitComboToLongestItem(ArrowStyleBox);
        FitComboToLongestItem(LineStyleBox);
        FitComboToLongestItem(FontFamilyBox);
        FitComboToLongestItem(FontSizeBox);
        FitComboToLongestItem(AlignBox);
        FitComboToLongestItem(VerticalAlignBox);
        FitComboToLongestItem(CropRatioBox);
        FitComboToLongestItem(EraserModeBox);
        FitComboToLongestItem(EffectStrengthBox);
        FitComboToLongestItem(StepModeBox);

    }

    private double ActiveSize() => ChromeTool() switch
    {
        EditorTool.Highlighter => _highlighterSize,
        EditorTool.Eraser => _eraserSize,
        EditorTool.Text => _textFontSize,
        EditorTool.Callout => _shapeSize,
        EditorTool.Freehand => _penSize,
        EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow
            or EditorTool.CurvedArrow or EditorTool.Step => _shapeSize,
        _ => _penSize,
    };

    private void RememberSize(double size)
    {
        switch (ChromeTool())
        {
            case EditorTool.Highlighter: _highlighterSize = AnnotationStyle.HighlighterWidth(size); break;
            case EditorTool.Eraser: _eraserSize = AnnotationStyle.ClampEraserSize(size); break;
            case EditorTool.Text: _textFontSize = AnnotationStyle.ClampTextSize(size); break;
            case EditorTool.Callout: _shapeSize = AnnotationStyle.ClampSize(size); break;
            case EditorTool.Freehand: _penSize = AnnotationStyle.ClampSize(size); break;
            default: _shapeSize = AnnotationStyle.ClampSize(size); break;
        }
        _thickness = ActiveSize();
    }

    private void OnSizeBoxChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSizeBox || _syncingContext || SizeBox?.SelectedItem is not ComboBoxItem item) return;
        double size = item.Tag is double d ? d : AnnotationStyle.DefaultPenSize;
        RememberSize(size);
        if (!IsLoaded) return;
        if (ChromeTool() is EditorTool.Text)
            RestyleSelectedTextFormat();
        else
        {
            double applied = _tool == EditorTool.Highlighter
                ? AnnotationStyle.HighlighterWidth(_thickness)
                : _thickness;
            RestyleSelected(color: null, thickness: applied, fill: null);
        }
    }

    /// <summary>Reflects the active blur/pixelate strength on the strength combo without re-triggering an apply.</summary>
    private void SyncEffectStrengthButtons(EffectStrength strength)
    {
        bool restore = _syncingContext;
        _syncingContext = true;
        try { SelectComboByTag(EffectStrengthBox, strength.ToString()); }
        finally { _syncingContext = restore; }
    }

    /// <summary>Re-checks the toolbar radio for a tool (used by the eyedropper to restore the prior tool).</summary>
    private void CheckToolButton(EditorTool tool)
    {
        if (tool == EditorTool.Crop)
        {
            OnCropClick(this, new RoutedEventArgs());
            return;
        }
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
        if (_syncingContext) { SyncColorWellIndicator(); return; }
        if (_colorWell == ColorWell.Fill && EditorShellContract.ContextFor(ChromeTool(), ChromeFilledRectangle())
                .HasFlag(EditorContextControls.FillStrokeTabs))
        {
            _fillColor = AnnotationStyle.Opaque(color);
            if (IsLoaded)
                RestyleSelected(color: null, thickness: null, fill: _fillMode, fillColor: CurrentFillBrush());
        }
        else
        {
            _color = AnnotationStyle.Opaque(color);
            if (IsLoaded)
                RestyleSelected(color: CurrentStrokeBrush(), thickness: null, fill: null);
        }
        SyncColorWellIndicator();
    }

    private Color CurrentStrokeBrush() => AnnotationStyle.Compose(_color, _opacity);

    private Color CurrentFillBrush()
    {
        Color rgb = _fillColor.A == 0 && _fillMode != ShapeFillMode.None ? _color : AnnotationStyle.Opaque(_fillColor);
        double op = _fillMode switch
        {
            ShapeFillMode.Quarter => 0.25,
            ShapeFillMode.Solid => _fillOpacity > 0 ? _fillOpacity : 1,
            _ => _fillOpacity,
        };
        return AnnotationStyle.Compose(rgb, op);
    }

    private void OnColorWellChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        _colorWell = tag == "Fill" ? ColorWell.Fill : ColorWell.Stroke;
        SyncColorWellIndicator();
        if (IsLoaded) UpdateContextPanels();
    }

    private void SyncColorWellIndicator()
    {
        if (CurrentColorIndicator is null) return;
        Color shown = _colorWell == ColorWell.Fill &&
                      EditorShellContract.ContextFor(ChromeTool(), ChromeFilledRectangle()).HasFlag(EditorContextControls.FillStrokeTabs)
            ? CurrentFillBrush()
            : CurrentStrokeBrush();
        if (shown.A == 0)
        {
            CurrentColorIndicator.Fill = Brushes.Transparent;
            CurrentColorIndicator.StrokeDashArray = new DoubleCollection { 2, 2 };
        }
        else
        {
            CurrentColorIndicator.Fill = new SolidColorBrush(shown);
            CurrentColorIndicator.StrokeDashArray = null;
        }
    }

    private void SetOpacityPercent(double percent, bool restyle)
    {
        double pct = Math.Clamp(percent, 0, 100);
        if (_colorWell == ColorWell.Fill &&
            EditorShellContract.ContextFor(ChromeTool(), ChromeFilledRectangle()).HasFlag(EditorContextControls.FillStrokeTabs))
        {
            _fillOpacity = pct / 100.0;
            if (_fillOpacity <= 0) _fillMode = ShapeFillMode.None;
            else if (Math.Abs(_fillOpacity - 0.25) < 0.02) _fillMode = ShapeFillMode.Quarter;
            else _fillMode = ShapeFillMode.Solid;
            if (restyle && IsLoaded)
                RestyleSelected(color: null, thickness: null, fill: _fillMode, fillColor: CurrentFillBrush());
        }
        else
        {
            _opacity = pct / 100.0;
            if (restyle && IsLoaded)
                RestyleSelected(color: CurrentStrokeBrush(), thickness: null, fill: null, opacity: _opacity);
        }
        if (OpacitySlider is not null && Math.Abs(OpacitySlider.Value - pct) > 0.5)
            OpacitySlider.Value = pct;
        if (OpacityValue is not null)
            OpacityValue.Text = $"{Math.Round(pct)}%";
        SyncColorWellIndicator();
    }

    private void OnLineStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;
        _lineStyle = AnnotationStyle.ParseLineStyle(tag);
        if (IsLoaded) RestyleSelectedLineStyle(_lineStyle);
    }

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (FontFamilyBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string family) return;
        _textFontFamily = family;
        if (IsLoaded) RestyleSelectedTextFormat();
    }

    private void OnFontSizeBoxChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext || _syncingSizeBox) return;
        if (FontSizeBox?.SelectedItem is not ComboBoxItem item || item.Tag is not double size) return;
        _textFontSize = AnnotationStyle.ClampTextSize(size);
        if (IsLoaded) RestyleSelectedTextFormat();
    }

    private void OnTextBoldClick(object sender, RoutedEventArgs e)
    {
        _textBold = !_textBold;
        SyncTextFormatChrome();
        if (IsLoaded) RestyleSelectedTextFormat();
    }

    private void OnTextItalicClick(object sender, RoutedEventArgs e)
    {
        _textItalic = !_textItalic;
        SyncTextFormatChrome();
        if (IsLoaded) RestyleSelectedTextFormat();
    }

    private void OnTextUnderlineClick(object sender, RoutedEventArgs e)
    {
        _textUnderline = !_textUnderline;
        SyncTextFormatChrome();
        if (IsLoaded) RestyleSelectedTextFormat();
    }

    private void OnTextStrikeClick(object sender, RoutedEventArgs e)
    {
        _textStrike = !_textStrike;
        SyncTextFormatChrome();
        if (IsLoaded) RestyleSelectedTextFormat();
    }

    private void OnTextAlignChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (AlignBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string align) return;
        _textAlign = align;
        if (IsLoaded) RestyleSelectedTextFormat();
    }

    private void OnVerticalAlignChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (VerticalAlignBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string align) return;
        _textVerticalAlign = align;
        if (IsLoaded) RestyleSelectedTextFormat();
    }

    private void OnMoreChromeClick(object sender, RoutedEventArgs e)
    {
        if (MoreChromePopup is not null)
            MoreChromePopup.IsOpen = true;
    }

    private void OnAlignMenuClick(object sender, RoutedEventArgs e)
    {
        PaintAlignCells();
        if (AlignPopup is not null)
            AlignPopup.IsOpen = true;
    }

    private void OnAlignCellClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        string[] parts = tag.Split('|');
        if (parts.Length != 2) return;
        _textAlign = parts[0];
        _textVerticalAlign = parts[1];
        if (AlignPopup is not null)
            AlignPopup.IsOpen = false;
        SelectComboByTag(AlignBox, _textAlign);
        SelectComboByTag(VerticalAlignBox, _textVerticalAlign);
        if (IsLoaded) RestyleSelectedTextFormat();
    }

    private void SyncTextFormatChrome()
    {
        if (FontFamilyBox is not null && FontFamilyBox.Items.Count == 0)
        {
            foreach (string family in AnnotationStyle.FontFamilies)
                FontFamilyBox.Items.Add(new ComboBoxItem { Content = family, Tag = family });
            FontFamilyBox.SelectedIndex = 0;
            FitComboToLongestItem(FontFamilyBox);
        }
        if (FontSizeBox is not null && FontSizeBox.Items.Count == 0)
        {
            foreach (int n in AnnotationStyle.TextSizePresets)
                FontSizeBox.Items.Add(new ComboBoxItem { Content = n.ToString(), Tag = (double)n });
            FitComboToLongestItem(FontSizeBox);
        }
        if (FontSizeBox is not null)
        {
            bool restore = _syncingContext;
            _syncingContext = true;
            try
            {
                foreach (ComboBoxItem item in FontSizeBox.Items.OfType<ComboBoxItem>())
                    if (item.Tag is double n && (int)n == AnnotationStyle.ClampTextSize(_textFontSize))
                        FontSizeBox.SelectedItem = item;
            }
            finally { _syncingContext = restore; }
        }
        void Paint(Button? btn, bool on)
        {
            if (btn is null) return;
            btn.Background = (Brush)FindResource(on ? "AccentTintBrush" : "SurfaceAltBrush");
            btn.Foreground = (Brush)FindResource(on ? "AccentBrush" : "TextPrimaryBrush");
        }
        Paint(BoldBtn, _textBold || _textStyle == TextStyle.Bold);
        Paint(ItalicBtn, _textItalic);
        Paint(UnderlineBtn, _textUnderline);
        Paint(StrikeBtn, _textStrike);
        if (FontFamilyBox is not null)
        {
            foreach (ComboBoxItem item in FontFamilyBox.Items.OfType<ComboBoxItem>())
                if (item.Tag is string f && f == _textFontFamily)
                    FontFamilyBox.SelectedItem = item;
        }
        PaintAlignCells();
    }

    private void PaintAlignCells()
    {
        string current = $"{_textAlign}|{_textVerticalAlign}";
        var onBorder = (Brush)FindResource("BorderStrongBrush");
        var onFill = (Brush)FindResource("PressedFillBrush");
        var onDot = (Brush)FindResource("TextPrimaryBrush");
        var offDot = (Brush)FindResource("TextSecondaryBrush");
        if (AlignGrid is not null)
        {
            foreach (Button cell in AlignGrid.Children.OfType<Button>())
            {
                bool on = cell.Tag as string == current;
                cell.Background = on ? onFill : Brushes.Transparent;
                cell.BorderBrush = on ? onBorder : Brushes.Transparent;
            }
        }
        if (AlignTriggerGrid is not null)
        {
            foreach (Rectangle dot in AlignTriggerGrid.Children.OfType<Rectangle>())
                dot.Fill = dot.Tag as string == current ? onDot : offDot;
        }
    }

    private void OnEraserModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            _eraserPartial = tag != "Full";
    }

    private void OnFillChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingContext) return;
        if (sender is RadioButton rb && rb.Tag is string tag && Enum.TryParse(tag, out ShapeFillMode mode))
        {
            _fillMode = mode;
            _fillOpacity = mode switch
            {
                ShapeFillMode.Quarter => 0.25,
                ShapeFillMode.Solid => _fillOpacity > 0 ? _fillOpacity : 1,
                _ => 0,
            };
            if (_fillColor.A == 0 && mode != ShapeFillMode.None)
                _fillColor = _color;
            if (IsLoaded)
                RestyleSelected(color: null, thickness: null, fill: mode, fillColor: CurrentFillBrush());
        }
    }

}
