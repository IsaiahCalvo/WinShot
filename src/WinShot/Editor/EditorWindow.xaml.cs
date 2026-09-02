using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    // Survey's zoomController MIN_SCALE / MAX_SCALE (utils/zoomController.js:23-24).
    private const double MinZoom = 0.01;
    private const double MaxZoom = 40.0;

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
    private double _shapeSize = AnnotationStyle.DefaultShapeSize;
    // Survey gives the counter its own persisted size (useDatabase.js:1017) because the Size
    // box IS the badge radius there, not a stroke width.
    private double _counterSize = AnnotationStyle.DefaultCounterSize;
    private double _textFontSize = AnnotationStyle.DefaultTextSize;
    private bool _syncingSizeBox;
    private bool _syncingContext;
    private string _textFontFamily = "Helvetica";

    /// <summary>
    /// Survey's callout <c>style.fontColor</c> (Callout/types.js:184) - the text colour is its
    /// own field, independent of the border colour, and the renderer feeds it straight to the
    /// label (svgAnnotationRenderers.jsx:1643). Null = "not chosen", which falls back to the
    /// stroke colour so legacy projects and freshly drawn callouts keep the old look.
    /// </summary>
    private Color? _textFontColor;
    private bool _textBold;
    private bool _textItalic;
    private bool _textUnderline;
    private bool _textStrike;
    private string _textAlign = "left";
    private string _textVerticalAlign = "top";
    private Rect? _activeTextBox;

    /// <summary>The committed label a text re-edit replaced, so blanking or cancelling restores it.</summary>
    private FrameworkElement? _reeditSource;

    /// <summary>Live box chrome painted behind the chromeless editor with the committed look.</summary>
    private System.Windows.Shapes.Rectangle? _textPreview;
    private int _nextStep = 1;
    private bool _stepLetters; // Step tool: false = number badges, true = letter badges (A, B, …)
    /// <summary>Survey's cloud "Bump" size (AppShell.jsx:2590-2622), clamped 1-20.</summary>
    private double _cloudIntensity = CloudPath.DefaultIntensity;
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
    private EditorTool _toolBeforeCrop = EditorTool.Select;
    private bool _eraserPartial = true;
    private double _eraserSize = 20; // Survey default
    // Survey snapshots the eraser's mode and radius at pointer-down and never re-reads the
    // toolbar mid-gesture (FabricEraserCanvas.jsx:2146-2152).
    private double _gestureEraserSize = 20;
    private bool _gestureEraserPartial = true;
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
    private Point _dragStart;
    private Shape? _activeShape;
    private CalloutAnnotation? _previewCallout;
    private bool _calloutReediting;
    private string _calloutReeditBefore = "";
    private CalloutLayout? _calloutReeditLayoutBefore;
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
            // A counter drag owns PointerMove outright, so it must never survive a lost capture.
            CancelCounterDrag();
            AbortDrag();
            AbortPan();
            AbortMove();
            AbortCurveHandle();
            AbortResize();
            if (_marqueeSelecting)
            {
                _marqueeSelecting = false;
                _marqueeArmed = false;
                HideDragRect();
            }
        };
        Loaded += (_, _) => RestorePersistedZoom();
        SetEraserPartial(_settings.Current.EraserPartial);
        // Survey re-runs the CURRENT non-manual zoom mode on every layout change
        // (applyLayoutDrivenZoom, PDFViewer.jsx:7999-8009); only MANUAL opts out.
        SizeChanged += (_, _) =>
        {
            ReapplyFit();
            Dispatcher.BeginInvoke(PackFormatOverflow, DispatcherPriority.Background);
        };
        // The WINDOW can stop resizing while the VIEWPORT keeps shrinking as the toolbars / zoom
        // bar finish laying out, so the viewport's own size changes re-fit too.
        Viewport.SizeChanged += (_, _) => ReapplyFit();
        Deactivated += (_, _) =>
        {
            _spaceDown = false;
            if (!_panning) UpdateCursor();
        };
        Closed += (_, _) =>
        {
            PersistZoomPreference();
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

        double fit = Math.Min((vw - ViewMargin * 2) / _sourceWidth, (vh - ViewMargin * 2) / _sourceHeight);
        // Fit may go below MinZoom (a 32000px scroll capture in a 650px viewport fits at
        // ~0.02) — clamping it to MinZoom left only the middle band visible. MinZoom still
        // floors user zooming. Small captures scale UP so Fit is not a postage stamp at 100%.
        _zoom = Math.Clamp(fit, Math.Min(MinZoom, Math.Max(fit, 0.001)), MaxZoom);
        _viewFitMode = ViewFitMode.Fit;
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = Math.Round((vw - _sourceWidth * _zoom) / 2);
        ViewTranslate.Y = Math.Round((vh - _sourceHeight * _zoom) / 2);
        Log.Info($"Editor fit: src={_sourceWidth}x{_sourceHeight} viewport={vw:0}x{vh:0} zoom={_zoom:0.000}");
        OnViewChanged();
    }

    /// <summary>
    /// Fits to WIDTH. Survey anchors the scale change on the viewport centre
    /// (zoomToScale('fitw') → applyAnchoredScale(clientWidth/2, clientHeight/2),
    /// PdfjsViewerContainer.jsx:1883-1899, :1082-1113), so the content the user was reading
    /// stays put instead of the view snapping back to the top of the capture.
    /// </summary>
    private void FitToWidth()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1 || _sourceWidth < 1 || _sourceHeight < 1) return;

        double fitW = (vw - ViewMargin * 2) / _sourceWidth;
        // Like FitToView: an ultra-wide capture's fit-width may drop below MinZoom.
        ApplyAnchoredScale(Math.Clamp(fitW, Math.Min(MinZoom, Math.Max(fitW, 0.001)), MaxZoom));
        _viewFitMode = ViewFitMode.FitWidth;
        OnViewChanged();
    }

    /// <summary>Fits to HEIGHT, centred horizontally — Survey's third fit mode.</summary>
    private void FitToHeight()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1 || _sourceWidth < 1 || _sourceHeight < 1) return;

        double fitH = (vh - ViewMargin * 2) / _sourceHeight;
        _zoom = Math.Clamp(fitH, Math.Min(MinZoom, Math.Max(fitH, 0.001)), MaxZoom);
        _viewFitMode = ViewFitMode.FitHeight;
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = Math.Round((vw - _sourceWidth * _zoom) / 2);
        ViewTranslate.Y = Math.Round((vh - _sourceHeight * _zoom) / 2);
        OnViewChanged();
    }

    /// <summary>Survey's fit-page breathing room, also the bound used by <see cref="ClampView"/>.</summary>
    private const double ViewMargin = 12;

    /// <summary>
    /// Re-runs the CURRENT fit mode after a layout change, exactly like Survey's
    /// applyLayoutDrivenZoom (PDFViewer.jsx:7999-8009): every mode but MANUAL tracks the
    /// viewport, so Fit width stays fit-to-width as the window resizes.
    /// </summary>
    private void ReapplyFit()
    {
        switch (_viewFitMode)
        {
            case ViewFitMode.Fit: FitToView(); break;
            case ViewFitMode.FitWidth: FitToWidth(); break;
            case ViewFitMode.FitHeight: FitToHeight(); break;
        }
    }

    /// <summary>
    /// Survey's applyAnchoredScale (PdfjsViewerContainer.jsx:1082-1113): re-projects the
    /// content point under the viewport centre so it lands back under the viewport centre.
    /// </summary>
    private void ApplyAnchoredScale(double newZoom)
    {
        Point anchor = ViewportCentre();
        double cx = (anchor.X - ViewTranslate.X) / _zoom;
        double cy = (anchor.Y - ViewTranslate.Y) / _zoom;
        _zoom = newZoom;
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = anchor.X - cx * _zoom;
        ViewTranslate.Y = anchor.Y - cy * _zoom;
        ClampView();
    }

    /// <summary>
    /// Survey's viewer is a native bounded scroller (position:absolute; inset:0;
    /// overflow:auto; contain:strict — PdfjsViewerContainer.jsx:2096-2108), so the browser
    /// hard-clamps scroll to [0, scrollMax] and content can never be scrolled or panned out
    /// of view; content smaller than the container is RECENTRED by margin, not by scroll
    /// (:834-846, applied at :2194-2197). WPF's free transform needs that made explicit.
    /// The 12px fit margin is folded into the bounds so the fit modes' breathing room and
    /// FitToWidth's top pin survive the clamp unchanged.
    /// </summary>
    private void ClampView()
    {
        if (_sourceWidth < 1 || _sourceHeight < 1) return;
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1) return;
        ViewTranslate.X = ClampViewAxis(ViewTranslate.X, vw, _sourceWidth * _zoom);
        ViewTranslate.Y = ClampViewAxis(ViewTranslate.Y, vh, _sourceHeight * _zoom);
        SyncCaptureShadow();
    }

    private static double ClampViewAxis(double offset, double viewSize, double content) =>
        content + 2 * ViewMargin <= viewSize
            ? (viewSize - content) / 2
            : Math.Clamp(offset, viewSize - ViewMargin - content, ViewMargin);

    /// <summary>Survey's TOOLBAR_ZOOM_STEP_FACTOR: each click is a quarter step about the viewport centre.</summary>
    private const double ZoomStepFactor = 1.25;

    /// <summary>Survey's WHEEL_NOTCH_PX / WHEEL_NOTCH_FACTOR (pdfZoomMath.js:4-5).</summary>
    private const double WheelNotchPx = 100;
    private const double WheelNotchFactor = 1.1;

    private Point ViewportCentre() => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomAt(ViewportCentre(), _zoom * ZoomStepFactor);

    // Survey's zoom input: digits only, Enter commits and blurs, Esc reverts, blur commits.
    private void OnZoomBoxTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void OnZoomBoxFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        // Survey's input mounts with the current value and never selects it
        // (AppShell.jsx:2914-2932 — autoFocus, no select()).
        ZoomBox.Text = Math.Round(_zoom * 100).ToString();

    /// <summary>
    /// Survey strips every non-digit from the incoming value and caps it at 4000
    /// mid-keystroke (handleZoomInputChange, PDFViewer.jsx:22775-22787). React's onChange
    /// covers pastes too, which WPF's PreviewTextInput does not.
    /// </summary>
    private void OnZoomBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        string incoming = e.DataObject.GetDataPresent(DataFormats.UnicodeText)
            ? (string)e.DataObject.GetData(DataFormats.UnicodeText)!
            : string.Empty;
        string digits = new(incoming.Where(char.IsDigit).ToArray());
        if (digits == incoming) return;
        e.CancelCommand();
        if (digits.Length == 0) return;
        int start = ZoomBox.SelectionStart;
        ZoomBox.Text = ZoomBox.Text.Remove(start, ZoomBox.SelectionLength).Insert(start, digits);
        ZoomBox.SelectionStart = start + digits.Length;
    }

    private void OnZoomBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(ZoomBox.Text.Trim().TrimEnd('%'), out int pct) && pct > 4000)
        {
            ZoomBox.Text = "4000";
            ZoomBox.SelectionStart = ZoomBox.Text.Length;
        }
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

    private void OnFitPage(object sender, RoutedEventArgs e)
    {
        FitMenuPopup.IsOpen = false;
        FitToView();
        PersistZoomPreference();
    }

    private void OnFitWidth(object sender, RoutedEventArgs e)
    {
        FitMenuPopup.IsOpen = false;
        FitToWidth();
        PersistZoomPreference();
    }

    private void OnFitHeight(object sender, RoutedEventArgs e)
    {
        FitMenuPopup.IsOpen = false;
        FitToHeight();
        PersistZoomPreference();
    }

    /// <summary>
    /// Survey saves the zoom mode and the manual scale on every change and restores them on
    /// load (zoomController.js:36-71, :149-157; PDFViewer.jsx:916-937, :7856-7863).
    /// </summary>
    /// <remarks>
    /// Called on the explicit mode choices and once on close rather than on every wheel notch:
    /// Survey writes localStorage, WinShot writes a settings file.
    /// </remarks>
    private void PersistZoomPreference()
    {
        if (!IsLoaded) return;
        var settings = _settings.Current;
        string mode = _viewFitMode.ToString();
        double manual = _viewFitMode == ViewFitMode.Free ? _zoom : settings.EditorManualZoom;
        if (settings.EditorZoomMode == mode && Math.Abs(settings.EditorManualZoom - manual) < 0.0001) return;
        settings.EditorZoomMode = mode;
        settings.EditorManualZoom = manual;
        _settings.Save();
    }

    private void RestorePersistedZoom()
    {
        switch (_settings.Current.EditorZoomMode)
        {
            case "FitWidth": FitToWidth(); break;
            case "FitHeight": FitToHeight(); break;
            case "Free":
                FitToView(); // establishes a sane translation before the stored scale is applied
                ZoomAt(ViewportCentre(), _settings.Current.EditorManualZoom);
                break;
            default: FitToView(); break;
        }
    }

    /// <summary>Zooms so the content point under <paramref name="anchor"/> (viewport coords) stays put.</summary>
    private void ZoomAt(Point anchor, double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.00001) return;
        _viewFitMode = ViewFitMode.Free;

        double cx = (anchor.X - ViewTranslate.X) / _zoom;
        double cy = (anchor.Y - ViewTranslate.Y) / _zoom;
        _zoom = newZoom;
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = anchor.X - cx * _zoom;
        ViewTranslate.Y = anchor.Y - cy * _zoom;
        ClampView();
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
        DragRect.StrokeThickness = 1.5 / _zoom;
        SelectionRect.StrokeThickness = 2 / _zoom; // Survey selection frame is 2 screen px
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
            _ => ($"Manual {Math.Round(_zoom * 100)}%", "SurveyIcon.FitPage"),
        };
        // Survey prefixes every state with "Page fit: " and carries the live percentage in
        // manual (AppShell.jsx:3072; PDFViewer.jsx:7865-7870).
        FitMenuBtn.ToolTip = $"Page fit: {label}";
        FitModeIcon.Content = FindResource(icon);
        // AppShell.jsx:3073 recolours the whole cell — chevron included — rather than
        // dimming the glyph: #e8e2d4 while a fit mode is active, #8d96a6 in manual.
        FitMenuBtn.Foreground = (Brush)FindResource(
            _viewFitMode == ViewFitMode.Free ? "ChromeTextMutedBrush" : "ChromeTextBrush");
        foreach (var (item, mode) in new[] { (FitPageItem, ViewFitMode.Fit), (FitWidthItem, ViewFitMode.FitWidth), (FitHeightItem, ViewFitMode.FitHeight) })
            if (item is not null)
                item.Foreground = (Brush)FindResource(_viewFitMode == mode ? "ChromeTextBrush" : "ChromeTextMutedBrush");
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
            // Ctrl+wheel zooms around the cursor; ZoomAt sets ViewFitMode.Free, which is
            // Survey's MANUAL and the only thing that stops the view tracking the viewport.
            // Survey: exp(-cappedDelta * ln(1.1)/100) — 1.1x per notch, one notch per
            // event (pdfZoomMath.js:3-6, :74-88). WPF's 120-unit notch maps onto WHEEL_NOTCH_PX.
            double notch = Math.Clamp(e.Delta / 1.2, -WheelNotchPx, WheelNotchPx);
            ZoomAt(e.GetPosition(Viewport), _zoom * Math.Exp(notch * Math.Log(WheelNotchFactor) / WheelNotchPx));
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift+wheel scrolls horizontally.
            ViewTranslate.X += e.Delta;
            ClampView();
        }
        else
        {
            // Plain wheel scrolls vertically — the natural way to move through a tall scrolling
            // capture. (Ctrl+wheel still zooms.)
            ViewTranslate.Y += e.Delta;
            ClampView();
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
        else if (_tool == EditorTool.Eraser)
        {
            // Survey's eraser surface is cursor:'none' so the ring is the ONLY cursor
            // (FabricEraserCanvas.jsx:2419; PageAnnotationLayer.jsx:4592-4595).
            Viewport.Cursor = Cursors.None;
            EditorSurface.Cursor = Cursors.None;
        }
        else
        {
            Viewport.Cursor = Cursors.Arrow;
            // Survey gives the text tool an I-beam, never the shape crosshair
            // (PDFViewer.jsx:31456 cursor:'text'; PageAnnotationLayer.jsx:4593).
            EditorSurface.Cursor = _tool switch
            {
                EditorTool.Select => Cursors.Arrow,
                EditorTool.Text => Cursors.IBeam,
                _ => Cursors.Cross,
            };
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
        // `.btn-active`: transparent box, gold glyph (styles.css:1547-1558).
        void Paint(Button btn, bool on)
        {
            if (btn is null) return;
            btn.Opacity = 1;
            btn.Background = Brushes.Transparent;
            btn.Foreground = on
                ? (Brush)FindResource("SurveyGoldBrush")
                : (Brush)FindResource("ChromeTextBrush");
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
            ? (Brush)FindResource("SurveyGoldHoverTintBrush")
            : Brushes.Transparent;
        CropUtilityBtn.Foreground = on
            ? (Brush)FindResource("SurveyGoldBrush")
            : (Brush)FindResource("ChromeTextBrush");
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
        // Survey portals the rich-text row into the sub-toolbar ONLY while an editor is
        // mounted (AppShell.jsx:1756, bridge cleared on commit/cancel/unmount,
        // TextEditOverlay.jsx:324-330, :400-402, :432).
        TextStylePanel.Visibility = Show(Has(EditorContextControls.TextStyle) && _activeText is not null);
        // Survey's Aa button is NOT part of the portalled strip: it renders in the top row
        // whenever contextTool is text or callout, or a rich-text session is live
        // (AppShell.jsx:2646-2649), and is disabled - never hidden - with nothing editable.
        if (TextEditPanel is not null)
            TextEditPanel.Visibility = Show(
                chrome is EditorTool.Text or EditorTool.Callout || _activeText is not null);
        CropRatioPanel.Visibility = Show(Has(EditorContextControls.CropRatio));
        StepModePanel.Visibility = Show(Has(EditorContextControls.StepMode));
        LineStylePanel.Visibility = Show(Has(EditorContextControls.LineStyle));
        FillStrokeTabPanel.Visibility = Show(Has(EditorContextControls.FillStrokeTabs));
        ColorWellLabel.Visibility = Visibility.Collapsed;
        if (CloudStyleItem is not null)
            CloudStyleItem.Visibility = Show(chrome == EditorTool.Rectangle);
        // Survey gates the Bump field on rect + cloud border (AppShell.jsx:2590).
        if (CloudBumpPanel is not null)
            CloudBumpPanel.Visibility = Show(chrome == EditorTool.Rectangle && _lineStyle == LineBorderStyle.Cloud);
        if (CloudBumpInput is not null && !CloudBumpInput.IsKeyboardFocusWithin)
            CloudBumpInput.Text = ((int)_cloudIntensity).ToString(CultureInfo.InvariantCulture);
        if (StepStartInput is not null && !StepStartInput.IsKeyboardFocusWithin)
            StepStartInput.Text = _nextStep.ToString(CultureInfo.InvariantCulture);
        SyncTextEditButton();
        SyncColorWellIndicator();
        SyncFontColorSwatch();
        ThicknessLabel.Visibility = Visibility.Collapsed;
        if (Has(EditorContextControls.Thickness))
            SyncSizeBox();
        if (Has(EditorContextControls.TextStyle))
            SyncTextFormatChrome();
        // Survey's rich-text row carries a dedicated 'Font size' dropdown for BOTH plain
        // text and callout text (AppShell.jsx:1855-1876), and it is only up while editing.
        if (FontSizeBox is not null)
            FontSizeBox.Visibility = Show(chrome is EditorTool.Text or EditorTool.Callout && _activeText is not null);

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
        // Survey portals the rich-text strip into the 34px sub-row and suppresses the
        // category portal for as long as it is up (AppShell.jsx:1756-1772, 2009).
        bool richText = TextStylePanel.Visibility == Visibility.Visible;
        HostRichTextRow(richText);
        EditorGroupToolsHost.Visibility = Show(!richText && group is "Draw" or "Shape" or "Text" or "Effects");
        Dispatcher.BeginInvoke(PackFormatOverflow, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Moves the rich-text formatting strip between the top row and the 34px sub-row.
    /// Survey mounts it in the sub-row (`#chrome-sub-toolbar-host`) only while an editor
    /// is live; the rest of the time the row belongs to the category chips.
    /// </summary>
    private void HostRichTextRow(bool inSubRow)
    {
        if (TextStylePanel is null || EditorRichTextHost is null || ContextPanel is null) return;
        if (inSubRow)
        {
            if (ReferenceEquals(EditorRichTextHost.Child, TextStylePanel)) return;
            _textStyleHomeIndex ??= ContextPanel.Children.IndexOf(TextStylePanel);
            ContextPanel.Children.Remove(TextStylePanel);
            TextStylePanel.Margin = new Thickness(0);
            EditorRichTextHost.Child = TextStylePanel;
            EditorRichTextHost.Visibility = Visibility.Visible;
        }
        else
        {
            EditorRichTextHost.Visibility = Visibility.Collapsed;
            if (!ReferenceEquals(EditorRichTextHost.Child, TextStylePanel)) return;
            EditorRichTextHost.Child = null;
            TextStylePanel.Margin = new Thickness(0, 0, 8, 0);
            ContextPanel.Children.Insert(
                Math.Clamp(_textStyleHomeIndex ?? ContextPanel.Children.Count, 0, ContextPanel.Children.Count),
                TextStylePanel);
        }
    }

    private int? _textStyleHomeIndex;

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
        // While the strip is portalled into the 34px sub-row it owns a full-width row of
        // its own, so nothing in it can overflow the top bar.
        if (TextStylePanel?.Visibility == Visibility.Visible
            && !ReferenceEquals(EditorRichTextHost?.Child, TextStylePanel))
            foreach (FrameworkElement inner in TextStylePanel.Children.OfType<FrameworkElement>())
            {
                if (inner is Popup) continue;
                yield return inner;
            }
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
        // An OPEN editor owns the chrome, whatever tool is armed: Survey's rich-text row is
        // published by the mounted overlay, so a Select-tool double-click re-edit shows the
        // same formatting controls a fresh text placement does (TextEditOverlay.jsx:278-318).
        if (_activeText is { } editor)
            return editor.Tag is CalloutAnnotation ? EditorTool.Callout : EditorTool.Text;
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
                    // Survey has one shared strokeWidth for pen and highlighter; a committed
                    // highlighter's width is max(strokeWidth, 8), so it cannot be read back
                    // into the shared box without inflating the pen. Only the pen writes it.
                    case AnnotationData.TypeHighlighter: break;
                    case AnnotationData.TypeFreehand: _penSize = th; break;
                    case AnnotationData.TypeText: break;
                    case AnnotationData.TypeCallout: _shapeSize = th; break;
                    // Survey puts the counter's RADIUS in the Size box on selection
                    // (PDFViewer.jsx:3861), not a stroke width.
                    case AnnotationData.TypeStep: _counterSize = th; break;
                    default: _shapeSize = th; break;
                }
            }
            if (!string.IsNullOrWhiteSpace(meta.FontFamily))
                _textFontFamily = meta.FontFamily;
            // fontColor follows the selection like every other rich-text field; absent means
            // "never recoloured", which is the stroke-colour fallback, so clear it.
            _textFontColor = meta.FontColor is string fcHex && TryParseColor(fcHex, out var fcSel)
                ? fcSel
                : null;
            _textBold = meta.Bold ?? false;
            _textItalic = meta.Italic ?? false;
            _textUnderline = meta.Underline ?? false;
            _textStrike = meta.Strike ?? false;
            if (!string.IsNullOrWhiteSpace(meta.Align)) _textAlign = meta.Align;
            if (!string.IsNullOrWhiteSpace(meta.VerticalAlign)) _textVerticalAlign = meta.VerticalAlign;
            if (meta.Style is string styleName && Enum.TryParse(styleName, out TextStyle textStyle))
                _textStyle = textStyle;
            _lineStyle = AnnotationStyle.LineStyleFrom(meta);
            // Survey's Bump field follows the selection like every other style input.
            if (meta.CloudIntensity is double bump)
                _cloudIntensity = CloudPath.ClampIntensity(bump);
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

    /// <summary>
    /// Survey's AnnotationSizeControl: an editable whole-number field plus a preset
    /// popover whose rows preview the stroke THICKNESS
    /// (components/AnnotationSizeControl.jsx:10-190).
    /// </summary>
    private int[] ActiveSizePresets() => _tool == EditorTool.Eraser
        ? AnnotationStyle.EraserSizePresets
        : ChromeTool() is EditorTool.Step ? AnnotationStyle.CounterSizePresets
        : AnnotationStyle.SizePresets;

    private int ClampActiveSize(double size) => _tool == EditorTool.Eraser
        ? AnnotationStyle.ClampEraserSize(size)
        : ChromeTool() is EditorTool.Step ? AnnotationStyle.ClampCounterSize(size)
        : AnnotationStyle.ClampSize(size);

    private void SyncSizeBox()
    {
        if (SizeInput is null) return;
        // Survey's Width control for text is a STROKE width (AppShell.jsx:2464-2482), not the
        // font size; the font size lives in its own dropdown next to B/I/U/S.
        _thickness = ActiveSize();
        int current = ClampActiveSize(_tool == EditorTool.Eraser ? _eraserSize
            : ChromeTool() is EditorTool.Step ? _counterSize : _thickness);

        _syncingSizeBox = true;
        try
        {
            SizeInput.Text = current.ToString(CultureInfo.InvariantCulture);
            // label is 'Size' for the counter and the eraser, 'Width' otherwise
            // (AppShell.jsx:2476).
            bool sizeLabel = _tool == EditorTool.Eraser || ChromeTool() is EditorTool.Step;
            string label = sizeLabel ? "Size" : "Width";
            if (SizePresetsHeading is not null) SizePresetsHeading.Text = label.ToUpperInvariant();
            if (SizePresetsBtn is not null)
            {
                SizePresetsBtn.ToolTip = $"{label} presets";
                System.Windows.Automation.AutomationProperties.SetName(SizePresetsBtn, $"{label} presets");
            }
            BuildSizePresetRows(current);
        }
        finally
        {
            _syncingSizeBox = false;
        }
    }

    private void BuildSizePresetRows(int current)
    {
        if (SizePresetList is null) return;
        SizePresetList.Children.Clear();
        int[] presets = ActiveSizePresets();
        int scaleMin = presets[0], scaleMax = presets[^1];
        foreach (int preset in presets)
        {
            double thickness = SizePreviewThickness(preset, scaleMin, scaleMax);
            var bar = new Rectangle
            {
                Width = 64,
                Height = thickness,
                // CSS border-radius:999px clamps to half the short side — a stadium,
                // not the ellipse WPF would draw from a literal 999 radius.
                RadiusX = thickness / 2,
                RadiusY = thickness / 2,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var preview = new Grid { Height = 22 };
            preview.Children.Add(bar);
            row.Children.Add(preview);
            var numeral = new TextBlock
            {
                Text = preset.ToString(CultureInfo.InvariantCulture),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(numeral, 1);
            row.Children.Add(numeral);

            var button = new Button
            {
                Style = (Style)FindResource("SizePresetRow"),
                Content = row,
                Tag = (double)preset,
            };
            // `.is-active`: #f4f6f8 on #2b313a, persisting under the hover wash.
            if (preset == current)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x31, 0x3A));
                button.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8));
            }
            // The bar is `background: currentColor`, so it tracks the row foreground.
            bar.SetBinding(Shape.FillProperty,
                new Binding(nameof(Control.Foreground)) { Source = button });
            button.Click += OnSizePresetClick;
            SizePresetList.Children.Add(button);
        }
    }

    /// <summary>
    /// utils/annotationSize.js getAnnotationSizePreviewThickness: square-root scaling
    /// into 1..22px so large presets stay distinguishable inside a 34px row.
    /// </summary>
    internal static double SizePreviewThickness(double value, double min, double max,
        double minPixels = 1, double maxPixels = 22)
    {
        double safeMin = min, safeMax = Math.Max(min, max);
        if (safeMax == safeMin) return minPixels;
        double clamped = Math.Clamp(value, safeMin, safeMax);
        double ratio = (Math.Sqrt(clamped) - Math.Sqrt(safeMin))
            / (Math.Sqrt(safeMax) - Math.Sqrt(safeMin));
        return Math.Round(minPixels + ratio * (maxPixels - minPixels), 2);
    }

    private void OnSizePresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not double size) return;
        if (SizePresetsBtn is not null) SizePresetsBtn.IsChecked = false;
        ApplySize(size);
    }

    // sanitizeAnnotationSizeDraft: at most three digits, nothing else.
    private void OnSizeInputText(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void OnSizeInputKey(object sender, KeyEventArgs e)
    {
        // Enter blurs, which commits (AnnotationSizeControl.jsx:118).
        if (e.Key == Key.Enter) { CommitSizeInput(); Viewport.Focus(); e.Handled = true; }
    }

    private void OnSizeInputBlur(object sender, KeyboardFocusChangedEventArgs e) => CommitSizeInput();

    private void CommitSizeInput()
    {
        if (_syncingSizeBox || SizeInput is null) return;
        double parsed = double.TryParse(SizeInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out double v)
            ? v
            : ActiveSize();
        ApplySize(parsed);
    }

    private void ApplySize(double size)
    {
        double clamped = ClampActiveSize(size);
        RememberSize(clamped);
        SyncSizeBox();
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
        // SurveyDropdown renders its trigger in Arial SemiBold 12; measuring in the UI
        // font would under-size the combo and clip the label.
        var typeface = new Typeface(new FontFamily("Arial"), FontStyles.Normal,
            FontWeights.SemiBold, FontStretches.Normal);
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
        // SurveyDropdown ContentSite margin is 9 left + 29 for the chevron column.
        double width = Math.Ceiling(max) + 38;
        // AnnotationDropdown.css:9 min-width: 72px.
        if (width < 72) width = 72;
        box.MinWidth = width;
        box.Width = width;
    }

    private void FitEditorCombos()
    {
        // SizeField is a fixed-width composite (43px input + 24px chevron), so it
        // never needs measuring.
        FitComboToLongestItem(ArrowStyleBox);
        FitComboToLongestItem(LineStyleBox);
        FitComboToLongestItem(FontFamilyBox);
        FitComboToLongestItem(FontSizeBox);
        FitComboToLongestItem(AlignBox);
        FitComboToLongestItem(VerticalAlignBox);
        FitComboToLongestItem(CropRatioBox);
        FitComboToLongestItem(EffectStrengthBox);
        FitComboToLongestItem(StepModeBox);

    }

    private double ActiveSize() => ChromeTool() switch
    {
        // Survey has no highlighter width of its own: the Width box shows the shared
        // strokeWidth (default 3) and the highlighter draws at max(that, 8).
        EditorTool.Highlighter => _penSize,
        EditorTool.Eraser => _eraserSize,
        EditorTool.Text => _shapeSize,
        EditorTool.Callout => _shapeSize,
        EditorTool.Freehand => _penSize,
        EditorTool.Step => _counterSize,
        EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow
            or EditorTool.CurvedArrow => _shapeSize,
        _ => _penSize,
    };

    private void RememberSize(double size)
    {
        switch (ChromeTool())
        {
            case EditorTool.Highlighter: _penSize = AnnotationStyle.ClampSize(size); break;
            case EditorTool.Eraser: _eraserSize = AnnotationStyle.ClampEraserSize(size); break;
            case EditorTool.Text: _shapeSize = AnnotationStyle.ClampSize(size); break;
            case EditorTool.Callout: _shapeSize = AnnotationStyle.ClampSize(size); break;
            case EditorTool.Freehand: _penSize = AnnotationStyle.ClampSize(size); break;
            case EditorTool.Step: _counterSize = AnnotationStyle.ClampCounterSize(size); break;
            default: _shapeSize = AnnotationStyle.ClampSize(size); break;
        }
        _thickness = ActiveSize();
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

    /// <summary>
    /// Survey ships three swatch variants (AppShell.jsx:2013-2124): a bare disc for
    /// pen/highlighter/arrow/line, a disc inside a 2px stroke-colour ring for
    /// rect/ellipse/text/callout, and a disc with a centred bold "1" for the counter.
    /// The eraser shows no swatch at all. Transparency is communicated by the
    /// checkerboard behind the disc, never by a dashed outline.
    /// </summary>
    private void SyncColorWellIndicator()
    {
        if (CurrentColorIndicator is null) return;
        EditorTool chrome = ChromeTool();
        bool fillTabs = EditorShellContract.ContextFor(chrome, ChromeFilledRectangle())
            .HasFlag(EditorContextControls.FillStrokeTabs);
        Color stroke = CurrentStrokeBrush();
        // In the ring variant the ring always paints the stroke colour and the disc the
        // fill colour, whichever well the Fill/Border tabs have selected.
        bool ringed = chrome is EditorTool.Rectangle or EditorTool.Ellipse
            or EditorTool.Text or EditorTool.Callout;
        bool counter = chrome is EditorTool.Step;
        // Ring and counter variants both put the FILL colour in the disc and the stroke
        // colour in the ring / numeral; the bare variant shows the stroke colour.
        Color disc = ringed || counter || (_colorWell == ColorWell.Fill && fillTabs)
            ? CurrentFillBrush()
            : stroke;
        if (ringed && !fillTabs) disc = stroke;
        CurrentColorIndicator.Fill = new SolidColorBrush(disc);

        if (ColorWellStrokeRing is not null)
        {
            ColorWellStrokeRing.Visibility = ringed ? Visibility.Visible : Visibility.Collapsed;
            ColorWellStrokeRing.Stroke = new SolidColorBrush(stroke);
        }
        if (ColorWellCounterGlyph is not null)
        {
            ColorWellCounterGlyph.Visibility = counter ? Visibility.Visible : Visibility.Collapsed;
            ColorWellCounterGlyph.Foreground = new SolidColorBrush(stroke);
        }
        if (ColorWellBtn is not null)
        {
            ColorWellBtn.ToolTip = counter ? "Counter colors" : "Color";
            System.Windows.Automation.AutomationProperties.SetName(
                ColorWellBtn, counter ? "Counter colors" : "Color");
            ColorWellBtn.Visibility = chrome == EditorTool.Eraser ? Visibility.Collapsed : Visibility.Visible;
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
        if (!IsLoaded) return;
        RestyleSelectedLineStyle(_lineStyle);
        // Survey shows the Bump field only while the cloud border is selected.
        UpdateContextPanels();
    }

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (FontFamilyBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string family) return;
        _textFontFamily = family;
        if (IsLoaded) RestyleSelectedTextFormat();
        ReturnFocusToEditor();
    }

    private void OnFontSizeBoxChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext || _syncingSizeBox) return;
        if (FontSizeBox?.SelectedItem is not ComboBoxItem item || item.Tag is not double size) return;
        _textFontSize = AnnotationStyle.ClampTextSize(size);
        if (IsLoaded) RestyleSelectedTextFormat();
        ReturnFocusToEditor();
    }

    /// <summary>
    /// Survey's rich-text dropdowns are `preserveFocus` (AnnotationDropdown.jsx:139, 164, 175):
    /// picking a value hands the caret straight back to the still-open editor. WPF's ComboBox
    /// has to take keyboard focus to run its popup, so the hand-back happens once the pick is in.
    /// </summary>
    private void ReturnFocusToEditor()
    {
        if (_activeText is not { } editor) return;
        Dispatcher.BeginInvoke(() => editor.Focus(), DispatcherPriority.Input);
    }

    // ---- font colour: Survey's dedicated rich-text swatch (AppShell.jsx:1780-1826) ----

    private void OnFontColorClick(object sender, RoutedEventArgs e)
    {
        if (FontColorPopup is null) return;
        BuildFontColorPalette();
        FontColorPopup.IsOpen = !FontColorPopup.IsOpen;
    }

    private void BuildFontColorPalette()
    {
        if (FontColorGrid is null || FontColorGrid.Children.Count > 0) return;
        foreach (string hex in FontColorPresets)
        {
            if (!TryParseColor(hex, out var c)) continue;
            var btn = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(2),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = hex,
                Tag = c,
                Background = new SolidColorBrush(c),
                BorderBrush = (Brush)FindResource("BorderStrongBrush"),
                BorderThickness = new Thickness(1),
            };
            AutomationProperties.SetName(btn, $"Font color {hex}");
            btn.Click += (_, _) => SetFontColor(c);
            FontColorGrid.Children.Add(btn);
        }
    }

    /// <summary>Survey's CompactColorPicker presets, trimmed to the two rows the swatch shows.</summary>
    private static readonly string[] FontColorPresets =
    {
        "#1E293B", "#000000", "#FFFFFF", "#8D96A6", "#FF3B30", "#FF9500", "#FFCC00", "#34C759",
        "#00C7BE", "#0A84FF", "#5856D6", "#AF52DE", "#FF2D55", "#A2845E", "#D8A84E", "#248A3D",
    };

    private void SetFontColor(Color color)
    {
        _textFontColor = color;
        if (FontColorPopup is not null) FontColorPopup.IsOpen = false;
        SyncFontColorSwatch();
        if (IsLoaded) RestyleSelectedTextFormat();
        ReturnFocusToEditor();
    }

    /// <summary>The swatch shows the chosen font colour, or the stroke colour it falls back to.</summary>
    private void SyncFontColorSwatch()
    {
        if (FontColorIndicator is null) return;
        FontColorIndicator.Fill = new SolidColorBrush(
            _textFontColor ?? AnnotationStyle.Opaque(_color));
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
            // Survey renders each option IN ITS OWN FACE (AppShell.jsx:1841-1845 sets
            // style:{fontFamily} per option; AnnotationDropdown.jsx:163 applies it).
            foreach (string family in AnnotationStyle.FontFamilies)
                FontFamilyBox.Items.Add(new ComboBoxItem
                {
                    Content = family,
                    Tag = family,
                    FontFamily = new FontFamily(family),
                });
            FontFamilyBox.SelectedIndex = 0;
            FitComboToLongestItem(FontFamilyBox);
        }
        if (FontSizeBox is not null)
        {
            // Survey rebuilds the preset list per render and PREPENDS the live size when it is
            // not one of the presets, so the trigger label always matches (AppShell.jsx:1861-1863).
            bool restore = _syncingContext;
            _syncingContext = true;
            try
            {
                int current = AnnotationStyle.ClampTextSize(_textFontSize);
                FontSizeBox.Items.Clear();
                ComboBoxItem? selected = null;
                foreach (int n in AnnotationStyle.TextSizePresets)
                {
                    var item = new ComboBoxItem { Content = n.ToString(), Tag = (double)n };
                    FontSizeBox.Items.Add(item);
                    if (n == current) selected = item;
                }
                if (selected is null)
                {
                    selected = new ComboBoxItem { Content = current.ToString(), Tag = (double)current };
                    FontSizeBox.Items.Insert(0, selected);
                }
                FontSizeBox.SelectedItem = selected;
                FitComboToLongestItem(FontSizeBox,
                    AnnotationStyle.TextSizePresets.Select(n => n.ToString()).ToArray());
            }
            finally { _syncingContext = restore; }
        }
        // AppShell.jsx:1902-1903 — on: rgba(216,168,78,0.18) + #d8a84e;
        // off: #3a4252 + #e8e2d4.
        void Paint(Button? btn, bool on)
        {
            if (btn is null) return;
            btn.Background = (Brush)FindResource(on ? "SurveyGoldTintBrush" : "ChromeControlBrush");
            btn.Foreground = (Brush)FindResource(on ? "SurveyGoldBrush" : "ChromeTextBrush");
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
        // Survey's active cell is accent-on-accent-tint with an accent border, and INACTIVE
        // cells still carry chrome (#14171c on 1px #2a3140) rather than being invisible
        // (AppShell.jsx:1960-2001).
        var gold = (Brush)FindResource("SurveyGoldBrush");
        var onFill = (Brush)FindResource("SurveyGoldCellBrush");   // rgba(216,168,78,0.10)
        var offBorder = (Brush)FindResource("ChromeHairlineBrush"); // #2a3140
        var offFill = (Brush)FindResource("ChromeCellBrush");       // #14171c
        var offCellDot = (Brush)FindResource("ChromeCellDotBrush"); // #5a606a
        var offTriggerDot = (Brush)FindResource("ChromeDotOffBrush"); // rgba(168,176,191,0.4)
        if (AlignGrid is not null)
        {
            foreach (Button cell in AlignGrid.Children.OfType<Button>())
            {
                bool on = cell.Tag as string == current;
                cell.Background = on ? onFill : offFill;
                cell.BorderBrush = on ? gold : offBorder;
                foreach (Shape glyph in ((cell.Content as Panel)?.Children).OfType<Shape>())
                    glyph.Fill = on ? gold : offCellDot;
            }
        }
        if (AlignTriggerGrid is not null)
        {
            // Each cell is a Grid: [0] the 6px box-shadow halo, [1] the 4px dot.
            foreach (Grid cell in AlignTriggerGrid.Children.OfType<Grid>())
            {
                bool on = cell.Tag as string == current;
                var shapes = cell.Children.OfType<Shape>().ToList();
                if (shapes.Count < 2) continue;
                shapes[0].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                shapes[1].Fill = on ? gold : offTriggerDot;
            }
        }
    }

    // ---- cloud bump / counter start / Aa: Survey's small labelled chrome inputs ----

    private void OnCloudBumpKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitCloudBump(); Viewport.Focus(); e.Handled = true; }
    }

    private void OnCloudBumpBlur(object sender, KeyboardFocusChangedEventArgs e) => CommitCloudBump();

    private void CommitCloudBump()
    {
        if (CloudBumpInput is null) return;
        double parsed = double.TryParse(CloudBumpInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out double v)
            ? v
            : _cloudIntensity;
        _cloudIntensity = CloudPath.ClampIntensity(parsed);
        CloudBumpInput.Text = ((int)_cloudIntensity).ToString(CultureInfo.InvariantCulture);
        if (!IsLoaded) return;
        // The bump is per-annotation, so stamp it on the selection before the restyle
        // rebuilds their geometry from it.
        foreach (UIElement el in SelectionList())
            if (el is FrameworkElement { Tag: AnnotationData { Type: AnnotationData.TypeRectangle } m })
                m.CloudIntensity = _cloudIntensity;
        RestyleSelectedLineStyle(_lineStyle);
    }

    private void OnStepStartKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitStepStart(); Viewport.Focus(); e.Handled = true; }
    }

    private void OnStepStartBlur(object sender, KeyboardFocusChangedEventArgs e) => CommitStepStart();

    private void CommitStepStart()
    {
        if (StepStartInput is null) return;
        // Survey clamps the start number to >= 1 (AppShell.jsx:2540-2556).
        _nextStep = int.TryParse(StepStartInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? Math.Max(1, v)
            : _nextStep;
        StepStartInput.Text = _nextStep.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Survey's `Aa` button re-opens the selected text box or callout for editing —
    /// the same entry point double-click uses (AppShell.jsx:2641-2678).
    /// </summary>
    private void OnEnterTextEdit(object sender, RoutedEventArgs e)
    {
        if (_activeText is not null) return;
        if (_selected is CalloutAnnotation callout) BeginCalloutReEdit(callout);
        else if (_selected is TextBlock label) BeginTextReEdit(label);
    }

    /// <summary>
    /// The Aa button stays visible but disabled with nothing editable selected, and
    /// carries the gold pressed look while an editor is live.
    /// </summary>
    private void SyncTextEditButton()
    {
        if (TextEditBtn is null) return;
        bool editing = _activeText is not null;
        bool canEdit = editing || _selected is CalloutAnnotation or TextBlock;
        TextEditBtn.IsEnabled = canEdit;
        TextEditBtn.Opacity = canEdit ? 1 : 0.5;
        TextEditBtn.Background = editing
            ? (Brush)FindResource("SurveyGoldTintBrush")
            : (Brush)FindResource("ChromeControlBrush");
        TextEditBtn.Foreground = editing
            ? (Brush)FindResource("SurveyGoldBrush")
            : (Brush)FindResource(canEdit ? "ChromeTextBrush" : "ChromeTextDisabledBrush");
        TextEditBtn.ToolTip = canEdit ? "Edit text" : "Select a text box or callout to edit its text";
    }

    /// <summary>
    /// The caret both arms the eraser and toggles its mode popup, exactly as Survey's
    /// in-button split trigger does (PDFViewer.jsx:32107-32120).
    /// </summary>
    private void OnEraserCaretClick(object sender, RoutedEventArgs e)
    {
        if (EraserToolBtn is not null) EraserToolBtn.IsChecked = true;
        if (EraserModePopup is not null) EraserModePopup.IsOpen = !EraserModePopup.IsOpen;
    }

    private void OnEraserModeRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        if (EraserModePopup is not null) EraserModePopup.IsOpen = false;
        SetEraserPartial(tag != "Full");
    }

    /// <summary>
    /// Survey's eraser mode is persisted ('eraserMode' in localStorage, default 'partial')
    /// and the eraser TOOL's tooltip / accessible name is the CURRENT MODE rather than a
    /// static "Eraser" (PDFViewer.jsx:4274-4281, :32080-32097).
    /// </summary>
    private void SetEraserPartial(bool partial)
    {
        _eraserPartial = partial;
        // The selected row keeps Survey's #1f2430 wash (PDFViewer.jsx:32170).
        var selectedRow = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x30));
        if (EraserPartialItem is not null)
            EraserPartialItem.Background = partial ? selectedRow : Brushes.Transparent;
        if (EraserFullItem is not null)
            EraserFullItem.Background = partial ? Brushes.Transparent : selectedRow;

        // Survey never shows a static "Eraser": the chip IS the mode (PDFViewer.jsx:32080-32082).
        string label = partial ? "Partial erase" : "Full stroke erase";
        if (EraserToolBtn is not null)
        {
            EraserToolBtn.ToolTip = label;
            System.Windows.Automation.AutomationProperties.SetName(EraserToolBtn, label);
        }
        // The combo's XAML SelectedIndex fires this before the ctor has assigned _settings.
        if (_settings is not null && _settings.Current.EraserPartial != partial)
        {
            _settings.Current.EraserPartial = partial;
            _settings.Save();
        }
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
