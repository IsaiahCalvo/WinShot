using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Core;
using WinShot.Recording;
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

    /// <summary>
    /// Undo history, oldest first — a list rather than a Stack because Survey caps its history at
    /// 100 entries (<c>[...prev, action].slice(-100)</c>, PDFViewer.jsx:23652) and the oldest entry
    /// has to be droppable from the front. The redo stack needs no cap: it only ever receives what
    /// undo pops, and DiscardRedoStack drains it on every new edit.
    /// </summary>
    private readonly List<EditorAction> _undoStack = new();
    private readonly Stack<EditorAction> _redoStack = new();
    private const int UndoDepthLimit = 100;

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

    /// <summary>Mount-time height of the open plain-text editor — the grow-only floor.</summary>
    private double _textEditorFloorH;

    /// <summary>Same grow-only floor for the open callout editor: the drag-created box height.</summary>
    private double _calloutEditorFloorH;

    /// <summary>The committed label a text re-edit replaced, so blanking or cancelling restores it.</summary>
    private FrameworkElement? _reeditSource;

    /// <summary>
    /// The part of <see cref="_reeditSource"/> hidden while its editor is open. Survey renders
    /// the real annotation throughout the edit and only skips the glyph foreignObject
    /// (SVGAnnotationLayer.jsx:3896-3906), so this is the inner TextBlock for the styles that
    /// have a chrome Rectangle and the whole element for Outline/Pill, which do not.
    /// </summary>
    private UIElement? _reeditHiddenGlyphs;

    /// <summary>
    /// The re-edited element when it is still painting its own border — i.e. when the stand-in
    /// preview rectangle must NOT be drawn, or the box would be outlined twice.
    /// </summary>
    private FrameworkElement? _reeditBox;

    /// <summary>
    /// Rotation of the label being re-edited. CommitText rebuilds the label, so the angle has
    /// to survive the round trip the way Survey's deepClone does (textEditCommit.js:186-212).
    /// </summary>
    private double _reeditAngle;

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

    /// <summary>
    /// Survey's "Both ends" toggle (PDFViewer.jsx:3553): ONE ending picker plus a flag that
    /// mirrors the picked ending onto the start of the arrow, instead of a second picker.
    /// Survey persists it to localStorage; WinShot's editor keeps none of its sibling tool
    /// choices (arrowhead, line style, fill mode) past a session, so this follows the house
    /// convention and stays a session field.
    /// </summary>
    private bool _arrowBothEnds;
    private LineBorderStyle _lineStyle = LineBorderStyle.Solid;
    private ColorWell _colorWell = ColorWell.Stroke;
    private Color _fillColor = Color.FromArgb(0, 255, 255, 255);
    private double _fillOpacity = 0;
    private EditorTool _lastDrawTool = EditorTool.Freehand;
    private EditorTool _lastShapeTool = EditorTool.Rectangle;
    private EditorTool _lastTextTool = EditorTool.Text;
    private EditorTool _lastEffectsTool = EditorTool.Redact;
    private EditorTool _toolBeforeCrop = EditorTool.Select;
    /// <summary>
    /// Survey's <c>activeCategoryDropdown</c> reduced to a bool: the category sub-row is a
    /// PORTAL that only mounts while a category is open (PDFViewer.jsx:32015). Clicking the
    /// active chip closes it without changing the tool (AppShell.jsx:1637-1641), and the
    /// derive-from-tool effect re-opens it on any tool change that lands in a group
    /// (PDFViewer.jsx:4538-4555) — "closing doesn't change the tool, so this effect doesn't
    /// re-open" (:4535-4537).
    /// </summary>
    private bool _groupRowOpen = true;
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


    // Redaction defaults for the NEXT mark; a selected redaction carries its own. Survey's
    // separate Pixelate and Blur tools are two of these four styles.
    private RedactStyle _redactStyle = RedactStyle.Pixelate;
    private int _redactStrength = Redaction.DefaultStrength;
    private int _activeRedactSeed;
    private readonly System.Diagnostics.Stopwatch _redactPreviewClock = new();

    // Strength-slider gesture: a continuous drag previews live but records ONE undo entry.
    private AnnotationData? _redactStrengthBefore;
    private UIElement? _redactStrengthElement;

    /// <summary>Magnification for the NEXT callout; a selected one carries its own.</summary>
    private double _magnification = MagnifierLayout.DefaultScale;
    private AnnotationData? _magnifyBefore;
    private UIElement? _magnifyElement;

    private bool _savedColorsDirty;

    /// <summary>Set while the eyedropper was armed FROM the picker, so sampling reopens it.</summary>
    private bool _reopenPickerAfterSample;

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

    /// <summary>
    /// Copied marks, shared by every open editor so a mark can be pasted into a different
    /// capture. Static for the same reason the system clipboard is: the copy outlives the
    /// window it came from.
    /// </summary>
    private static readonly List<ClipboardMark> AnnotationClipboard = new();
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
        _openClock.Start();
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

        // The window wraps the capture (CleanShot): a small shot opens in a small window
        // and a big one is capped at 90 % of the work area and fitted by shrinking.
        // WPF's MinWidth/MinHeight floor the small case.
        var wa = SystemParameters.WorkArea;
        Size want = DesiredWindowDip();
        Width = Math.Min(want.Width, wa.Width * EditorGrowLayout.MaxWorkAreaFraction);
        Height = Math.Min(want.Height, wa.Height * EditorGrowLayout.MaxWorkAreaFraction);

        WireSelectionQuickBar();
        WireBrushAdjust();
        WireSurveyRow();
        WirePinnedDock();
        Viewport.LostMouseCapture += (_, _) =>
        {
            // A brush drag that loses the mouse changes nothing (Survey: a cancelled pointer).
            EndBrushAdjust(commit: false);
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
                _marqueeShift = _marqueeAlt = false;
                HideDragRect();
                HideLasso();
            }
        };
        Loaded += (_, _) => RestorePersistedZoom();
        Loaded += (_, _) => WirePopoverMotion();
        Loaded += (_, _) =>
        {
            EditorToolKeys.Overrides = _settings?.Current.EditorToolKeyBindings;
            AddKeysToToolTips();
        };
        Loaded += (_, _) => WireSelectModeMenu();
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
            EndBrushAdjust(commit: false);
            _spaceDown = false;
            if (!_panning) UpdateCursor();
        };
        Closed += (_, _) =>
        {
            PersistZoomPreference();
            PersistSavedColors();
            _closed = true;
            // Every timer this window owns, stopped in one place. A tick that lands after the
            // template is gone is an unhandled dispatcher exception, and an unhandled
            // dispatcher exception is the whole process, not the editor.
            StopRedactPendingTimer();
            HideRotationPill();
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

    /// <summary>
    /// Open-time perf, from the constructor: first frame on screen, then the picture in it.
    /// Logged so a slow open on a real machine shows up as a number in the log, not a feeling.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _openClock = new();

    private void OnInitialContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnInitialContentRendered;
        Log.Info($"Perf editor first frame: {_openClock.ElapsedMilliseconds} ms");
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
            Log.Info($"Perf editor image shown: {_openClock.ElapsedMilliseconds} ms ({_source.Width}x{_source.Height})");
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
    /// Fits the WHOLE image into the viewport, centered. Tall scrolling captures scale down
    /// so top-to-bottom is visible; a small capture scales up to fill the view, up to
    /// <see cref="MaxFitZoom"/>. Use <see cref="FitToWidth"/> to read a long capture
    /// at full width and scroll down it. Ctrl+0 / Center button / open all route here.
    /// </summary>
    private void FitToView()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1 || _sourceWidth < 1 || _sourceHeight < 1) return; // viewport not ready; retry on next trigger

        double fit = Math.Min(FitWidthScale(vw), FitHeightScale(vh));
        // Fit may go below MinZoom (a 32000px scroll capture in a 650px viewport fits at
        // ~0.02) — clamping it to MinZoom left only the middle band visible. MinZoom still
        // floors user zooming.
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

        double fitW = FitWidthScale(vw);
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

        double fitH = FitHeightScale(vh);
        _zoom = Math.Clamp(fitH, Math.Min(MinZoom, Math.Max(fitH, 0.001)), MaxZoom);
        _viewFitMode = ViewFitMode.FitHeight;
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = Math.Round((vw - _sourceWidth * _zoom) / 2);
        ViewTranslate.Y = Math.Round((vh - _sourceHeight * _zoom) / 2);
        OnViewChanged();
    }

    /// <summary>
    /// Survey's fit insets (PdfjsViewerContainer.jsx:51-52): PAD is subtracted per side
    /// horizontally, GAP is added to the page height as a DIVISOR — vertical breathing room
    /// scales with the content instead of being subtracted flat, and desktop has no
    /// padTop/padBottom at all (:129-135, :137-140, :1891-1895).
    /// </summary>
    private const double FitPadX = 20;
    private const double FitGap = 16;

    /// <summary>
    /// The most a fit may MAGNIFY a capture. Survey's arithmetic is unchanged below; this is
    /// the one place the result is capped, so every fit mode inherits it instead of three
    /// call sites each remembering to.
    ///
    /// <para>History: until 2026-09-17 fit had no cap, and Survey's formula blew small
    /// captures up to 331% (300x200) or over 900% (80x50) — blurry. That day the cap became
    /// 100%, which swung too far the other way: a small capture opened at 100% as a little
    /// island with big empty bands above and below it. Isaiah, 2026-11-02: "it's too small.
    /// You have these massive borders above and below it."</para>
    ///
    /// <para>So fit now fills the viewport (with Survey's small margin) and may magnify, but
    /// never past 200%: big enough to work on, not so far that the pixels go soft. The rail,
    /// Ctrl+wheel and the %-box still go to MaxZoom 40.</para>
    /// </summary>
    private const double MaxFitZoom = 2.0;

    private static double CapFit(double scale) => Math.Min(MaxFitZoom, scale);

    /// <summary>Survey's getFitWidthForContainer / pw (PdfjsViewerContainer.jsx:137-140, :1891).</summary>
    private double FitWidthScale(double vw) => CapFit(Math.Max(1, vw - FitPadX * 2) / _sourceWidth);

    /// <summary>Survey's fh: clientHeight / (ph + 2*gap) (PdfjsViewerContainer.jsx:1892-1893).</summary>
    private double FitHeightScale(double vh) => CapFit(vh / (_sourceHeight + 2 * FitGap));

    /// <summary>The pan bound used by <see cref="ClampView"/>; unrelated to the fit insets.</summary>
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
        // Survey commitPointerForZoom (FabricEraserCanvas.jsx:2255-2286): a zoom that lands
        // mid-swipe commits the erase in flight rather than discarding it.
        CommitInterruptedEraser();
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

    private void OnFitMenuClick(object sender, RoutedEventArgs e)
    {
        FitMenuPopup.CustomPopupPlacementCallback ??= PlaceFitMenu;
        FitMenuPopup.IsOpen = !FitMenuPopup.IsOpen;
    }

    /// <summary>
    /// Survey's collapsed rail opens the fit menu with
    /// <c>{ right: '100%', bottom: 0, marginRight: '6px' }</c> (AppShell.jsx:3236): it flies
    /// out LEFT over the page, its right edge against the trigger's left edge with a 6px gap,
    /// and its BOTTOM edge level with the trigger's bottom. WPF's PlacementMode.Left gets the
    /// horizontal edge right but aligns TOPS, which for a 3-row menu hanging off a 28 DIP
    /// button at the very bottom of the rail put the list ~62 DIP too low and left WPF to
    /// flip it back on screen — so where it landed depended on the window height. A custom
    /// placement says the thing Survey says instead of approximating it with a margin.
    /// </summary>
    private static CustomPopupPlacement[] PlaceFitMenu(Size popup, Size target, Point offset) =>
        new[]
        {
            new CustomPopupPlacement(
                new Point(-FitMenuGap - popup.Width, target.Height - popup.Height),
                PopupPrimaryAxis.Horizontal),
            // If there is no room to the left, mirror to the right rather than let WPF
            // slide the menu somewhere neither edge lines up with anything.
            new CustomPopupPlacement(
                new Point(target.Width + FitMenuGap, target.Height - popup.Height),
                PopupPrimaryAxis.Horizontal),
        };

    private const double FitMenuGap = 6;

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
            // "Free" (the last capture was zoomed by hand) opens fitted too. A hand zoom
            // belongs to the capture it was made on: a stored scale bigger than this capture's
            // fit showed a sliver of a tall scroll capture, and a smaller one (often the 100%
            // left by Ctrl+0) opened a small capture as a little island with empty bands
            // around it (Isaiah, 2026-11-02).
            default: FitToView(); break;
        }
    }

    /// <summary>Zooms so the content point under <paramref name="anchor"/> (viewport coords) stays put.</summary>
    private void ZoomAt(Point anchor, double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.00001) return;
        CommitInterruptedEraser(); // Survey commitPointerForZoom (FabricEraserCanvas.jsx:2255-2286)
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
        // Survey's page has ONE edge treatment: box-shadow 0 2px 14px rgba(0,0,0,0.45)
        // (PdfjsViewerContainer.jsx:351), so 0.45 is a hard ceiling on how dark any pixel
        // around the page can get. WPF needs an opaque caster for DropShadowEffect to cast
        // at all, so the caster is inset 1px on every side: rounding between Canvas.SetLeft
        // (raw ViewTranslate) and the image's own ScaleTransform otherwise lets the black
        // rectangle peek past the image's antialiased bottom/right edge as a hard line.
        // The blur shifts inward by 1px, which is imperceptible against a 14px radius.
        CaptureShadow.Width = Math.Max(1, _sourceWidth * _zoom - 2);
        CaptureShadow.Height = Math.Max(1, _sourceHeight * _zoom - 2);
        Canvas.SetLeft(CaptureShadow, ViewTranslate.X + 1);
        Canvas.SetTop(CaptureShadow, ViewTranslate.Y + 1);

        // The hairline half of the sheet's edge (see EditorWindow.xaml). Grown one DIP
        // PROUD on every side, so its 1px stroke lands on the desk rather than over the
        // outermost row of the capture. It sits under EditorSurface in z-order, which is
        // why growing it outward is what makes it visible at all — and why a selection
        // handle sitting on the sheet's corner still draws over the top of it.
        if (CaptureEdge is null) return;
        CaptureEdge.Visibility = Visibility.Visible;
        CaptureEdge.Width = _sourceWidth * _zoom + 2;
        CaptureEdge.Height = _sourceHeight * _zoom + 2;
        Canvas.SetLeft(CaptureEdge, ViewTranslate.X - 1);
        Canvas.SetTop(CaptureEdge, ViewTranslate.Y - 1);
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

    /// <summary>
    /// EditorSurface sits INSIDE Viewport, and WPF's QueryCursor bubbles outward, so the
    /// innermost element with a non-null Cursor wins. Anything that wants a cursor over the
    /// image has to set both.
    /// </summary>
    /// <summary>
    /// Survey's preset popover is Radix <c>align="center"</c> (AnnotationSizeControl.jsx:143), so
    /// the 126px panel centres on the ~69px field. WPF's Placement="Bottom" is left-aligned, so
    /// shift it by half the difference — measured, never hardcoded, since the field is DPI-scaled.
    /// </summary>
    private void OnSizePresetsPopupOpened(object sender, EventArgs e)
    {
        // Survey's pill card is Radix align="start": its left edge under the pill's.
        SizePresetsPopup.HorizontalOffset = 0;
        // The card opens with its number ready to type over (AnnotationSizeControl SizeCard).
        Dispatcher.BeginInvoke(() =>
        {
            if (!SizePresetsPopup.IsOpen || SizeInput is null) return;
            SizeInput.Focus();
            SizeInput.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SetSurfaceCursor(Cursor cursor)
    {
        Viewport.Cursor = cursor;
        EditorSurface.Cursor = cursor;
    }

    private void UpdateCursor()
    {
        if (_panning)
            SetSurfaceCursor(PanCursors.Grabbing(VisualTreeHelper.GetDpi(this).DpiScaleX));
        else if (_spaceDown || _tool == EditorTool.Pan)
            SetSurfaceCursor(PanCursors.Grab(VisualTreeHelper.GetDpi(this).DpiScaleX));
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
        if (_syncingShapeButtons) return;
        ArmTool(rb, tool);
        // Survey cloudShapeTool.js: pressing Rectangle while the cloud border is on goes back
        // to a solid rectangle — Cloud and Rectangle are two buttons for one tool.
        if (IsLoaded && ReferenceEquals(rb, RectangleToolBtn) && _lineStyle == LineBorderStyle.Cloud)
        {
            ApplyQuickBarLineStyle(LineBorderStyle.Solid);
            UpdateContextPanels();
        }
    }

    /// <summary>
    /// Survey's Shape row Cloud button: Rectangle with the cloud border
    /// (utils/cloudShapeTool.js resolveShapeToolPick).
    /// </summary>
    private void OnCloudToolChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingShapeButtons || sender is not RadioButton rb) return;
        ArmTool(rb, EditorTool.Rectangle);
        if (!IsLoaded) return;
        ApplyQuickBarLineStyle(LineBorderStyle.Cloud);
        UpdateContextPanels();
    }

    private bool _syncingShapeButtons;

    /// <summary>
    /// Cloud shows pressed exactly while Rectangle is armed with the cloud border, and
    /// Rectangle never shows pressed then (cloudShapeTool.js isShapeToolButtonActive). Keys,
    /// pins and the line-style menu can all get there without pressing either button.
    /// </summary>
    private void SyncCloudButton()
    {
        if (CloudToolBtn is null || RectangleToolBtn is null || _tool != EditorTool.Rectangle) return;
        bool cloud = _lineStyle == LineBorderStyle.Cloud;
        RadioButton want = cloud ? CloudToolBtn : RectangleToolBtn;
        if (want.IsChecked == true) return;
        _syncingShapeButtons = true;
        try { want.IsChecked = true; }
        finally { _syncingShapeButtons = false; }
    }

    private void ArmTool(RadioButton rb, EditorTool tool)
    {
        // Checked also fires while XAML is parsing, before sibling elements exist.
        if (IsLoaded)
        {
            CommitText();
            CommitPendingCurve();
            // An unfinished polygon / polyline is dropped when the user picks another tool.
            // It is NOT committed: finishing is always explicit, and switching tools is not
            // one of the three gestures that mean "finish".
            if (!PolyDraft.IsPolyTool(tool)) CancelPolyDraft();
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
        // Survey closes the group row when Pan or Select is armed (PDFViewer.jsx "Close
        // secondary toolbar when pan or select tools are active").
        if (tool is EditorTool.Select or EditorTool.Pan) _imageGroupOpen = false;
        _tool = tool;
        if (ReferenceEquals(rb, RectangleToolBtn) || ReferenceEquals(rb, CloudToolBtn))
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
            // Survey's releaseFocusForDraftTool (PDFViewer.jsx:34077-34079): arming Polygon or
            // Polyline hands keyboard focus back to the page. These are the two tools finished
            // with ENTER, and leaving focus on the toolbar button puts Enter in the button's
            // lap — it re-clicks the button instead of closing the shape.
            if (PolyDraft.IsPolyTool(tool)) Viewport.Focus();
        }
    }

    private IEnumerable<RadioButton> ToolButtons()
    {
        // Pan/Select live outside the centred cluster (Survey's `right:100%` block), so they
        // need their own sweep or CheckToolButton can never restore them by Tag.
        if (PanSelectPanel is not null)
            foreach (var rb in PanSelectPanel.Children.OfType<RadioButton>()) yield return rb;
        if (ToolPanel is not null)
            foreach (var rb in ToolPanel.Children.OfType<RadioButton>()) yield return rb;
        if (DrawGroupPanel is not null)
            foreach (var rb in DrawGroupPanel.Children.OfType<RadioButton>()) yield return rb;
        if (ShapeGroupPanel is not null)
            foreach (var rb in ShapeGroupPanel.Children.OfType<RadioButton>()) yield return rb;
        if (TextGroupPanel is not null)
            foreach (var rb in TextGroupPanel.Children.OfType<RadioButton>()) yield return rb;
        if (EffectsGroupPanel is not null)
            foreach (var rb in EffectsGroupPanel.Children.OfType<RadioButton>()) yield return rb;
    }

    private void OnDrawGroupClick(object sender, RoutedEventArgs e)
    {
        // Survey (UX 2026-09-22): a second click on the lit chip is a NO-OP. While one of a
        // group's tools is armed its tools stay on screen, so you can always see and change
        // which tool it is; Pan, Select or Escape is how you put it down.
        if (EditorShellContract.GroupFor(_tool, false) != "Draw" || _imageGroupOpen)
            CheckToolButton(_lastDrawTool);
        _imageGroupOpen = false;
        UpdateContextPanels();
        UpdateGroupButtonChrome();
    }

    private void OnShapeGroupClick(object sender, RoutedEventArgs e)
    {
        // Survey (UX 2026-09-22): a second click on the lit chip is a NO-OP. While one of a
        // group's tools is armed its tools stay on screen, so you can always see and change
        // which tool it is; Pan, Select or Escape is how you put it down.
        if (EditorShellContract.GroupFor(_tool, false) != "Shapes" || _imageGroupOpen)
            CheckToolButton(_lastShapeTool);
        _imageGroupOpen = false;
        UpdateContextPanels();
        UpdateGroupButtonChrome();
    }

    private void OnTextGroupClick(object sender, RoutedEventArgs e)
    {
        // Survey (UX 2026-09-22): a second click on the lit chip is a NO-OP. While one of a
        // group's tools is armed its tools stay on screen, so you can always see and change
        // which tool it is; Pan, Select or Escape is how you put it down.
        if (EditorShellContract.GroupFor(_tool, false) != "Text" || _imageGroupOpen)
            CheckToolButton(_lastTextTool);
        _imageGroupOpen = false;
        UpdateContextPanels();
        UpdateGroupButtonChrome();
    }

    /// <summary>
    /// The Image group's chip. Same toggle as the other four, with one difference: its row
    /// is mostly one-shot actions, so opening it arms nothing — clicking "Image" must not
    /// drop the user into a crop.
    /// </summary>
    private void OnImageGroupClick(object sender, RoutedEventArgs e)
    {
        // The one chip that still toggles: its row is mostly one-shot actions and it arms
        // nothing, so closing it never leaves a live tool with no row on screen.
        if (EditorShellContract.GroupFor(_tool, false) == "Image")
            return;
        _imageGroupOpen = !_imageGroupOpen;
        UpdateContextPanels();
        UpdateGroupButtonChrome();
    }

    /// <summary>
    /// Whether the Image strip is showing because its chip was clicked rather than because
    /// a tool in it is armed. Crop is the group's only tool, so without this the strip
    /// could only ever be open while cropping.
    /// </summary>
    private bool _imageGroupOpen;

    private void OnEffectsGroupClick(object sender, RoutedEventArgs e)
    {
        // Survey (UX 2026-09-22): a second click on the lit chip is a NO-OP. While one of a
        // group's tools is armed its tools stay on screen, so you can always see and change
        // which tool it is; Pan, Select or Escape is how you put it down.
        if (EditorShellContract.GroupFor(_tool, false) != "Effects" || _imageGroupOpen)
            CheckToolButton(_lastEffectsTool);
        _imageGroupOpen = false;
        UpdateContextPanels();
        UpdateGroupButtonChrome();
    }

    /// <summary>
    /// Survey's derive-from-tool effect (PDFViewer.jsx:4538-4555): every tool change that
    /// lands in a group both remembers it as that group's last tool AND opens that group's
    /// sub-row, so keyboard shortcuts are indistinguishable from clicking the chip.
    /// </summary>
    private void RememberGroupTool(EditorTool tool)
    {
        switch (EditorShellContract.GroupFor(tool, false))
        {
            case "Draw": _lastDrawTool = tool; _groupRowOpen = true; break;
            case "Shapes": _lastShapeTool = tool; _groupRowOpen = true; break;
            case "Text": _lastTextTool = tool; _groupRowOpen = true; break;
            case "Effects": _lastEffectsTool = tool; _groupRowOpen = true; break;
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
        // Every chip's `.btn-active` is gated on `activeTool !== 'pan' && activeTool !== 'select'`
        // FIRST (AppShell.jsx:1647/1666/1685/1719), so no chip is ever gold while Pan or Select
        // is armed — whatever is selected. GroupFor already returns "" for both.
        string group = EditorShellContract.GroupFor(_tool, false);
        // `.btn-active`: transparent box, gold glyph (styles.css:1547-1558).
        void Paint(Button btn, bool on)
        {
            if (btn is null) return;
            btn.Opacity = 1;
            btn.Background = Brushes.Transparent;
            btn.Foreground = on
                ? (Brush)FindResource("ChromeAccentBrush")
                : (Brush)FindResource("ChromeTextBrush");
        }
        Paint(DrawGroupBtn, group == "Draw");
        Paint(ShapeGroupBtn, group == "Shapes");
        Paint(TextGroupBtn, group == "Text");
        Paint(EffectsGroupBtn, group == "Effects");
        Paint(ImageGroupBtn, _imageGroupOpen || group == "Image");
        PaintCropChrome(_tool == EditorTool.Crop);
    }

    private void PaintCropChrome(bool on)
    {
        if (CropUtilityBtn is null) return;
        CropUtilityBtn.Background = on
            ? (Brush)FindResource("ChromeAccentHoverTintBrush")
            : Brushes.Transparent;
        CropUtilityBtn.Foreground = on
            ? (Brush)FindResource("ChromeAccentBrush")
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
        // Survey's row 2 swap / leave motion wraps every change (EditorWindow.RowMotion.cs).
        var motion = BeginRowMotion();
        try { UpdateContextPanelsCore(); }
        finally { EndRowMotion(motion); }
    }

    private void UpdateContextPanelsCore()
    {
        QueueQuickBarUpdate();
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
        // Survey shows "Both ends" on the ARROW tool only — a callout's leader has one end
        // (AppShell.jsx:2758). The curved arrow is excluded for the same kind of reason, and
        // it is not cosmetic: its commit path (EditorWindow.Input.cs) carries no start head
        // and RestyleSelectedArrowhead refuses TypeCurvedArrow, so a toggle there would do
        // nothing. Same row, same panel, arrow only.
        if (ArrowBothEndsBtn is not null)
            ArrowBothEndsBtn.Visibility = Show(chrome == EditorTool.Arrow);
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
        // Survey's 'start' slot renders only with a counter PICKED (selectedCounterSeriesId).
        if (StepStartPanel is not null)
            StepStartPanel.Visibility = Show(Has(EditorContextControls.StepMode) &&
                _selected is FrameworkElement { Tag: AnnotationData { Type: AnnotationData.TypeStep } });
        // The series rows are derived from the pins on the canvas, so they are rebuilt whenever
        // the counter row comes into view — that covers a freshly loaded project, whose pins
        // arrive with their series ids already stamped, without a load-path hook of its own.
        if (StepModePanel.Visibility == Visibility.Visible) RefreshCounterSeriesChrome();
        LineStylePanel.Visibility = Show(Has(EditorContextControls.LineStyle));
        FillStrokeTabPanel.Visibility = Show(Has(EditorContextControls.FillStrokeTabs));
        ColorWellLabel.Visibility = Visibility.Collapsed;
        // Survey offers Cloud on rect / ellipse / polygon / polyline — every shape a revision
        // cloud can mark. All four are live: CloudPath.For takes an arbitrary ring for the two
        // poly tools, and CloudPath.ForBox picks the rectangle or ellipse walk for the two
        // sized ones.
        // w43: the text box and callout cloud their box border too (textCloudBorder.js).
        bool cloudCapable = chrome is EditorTool.Rectangle or EditorTool.Ellipse
            or EditorTool.Polygon or EditorTool.Polyline or EditorTool.Text or EditorTool.Callout;
        if (CloudStyleItem is not null)
            CloudStyleItem.Visibility = Show(cloudCapable);
        // Survey gates the Bump field on a cloud-capable tool + the cloud border (AppShell.jsx:2590).
        if (CloudBumpPanel is not null)
            CloudBumpPanel.Visibility = Show(cloudCapable && _lineStyle == LineBorderStyle.Cloud);
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

        RedactStylePanel.Visibility = Show(Has(EditorContextControls.RedactStyle));
        // The strength is GONE for Black Out rather than greyed out: there is no such thing
        // as a stronger or weaker black rectangle, and a dead control is worse than none.
        RedactStrengthPanel.Visibility = Show(
            Has(EditorContextControls.RedactStrength) &&
            Redaction.UsesStrength(SelectedRedactStyle() ?? _redactStyle));
        MagnifyPanel.Visibility = Show(Has(EditorContextControls.Magnification));
        CropToolsPanel.Visibility = Show(Has(EditorContextControls.CropTools));
        if (Has(EditorContextControls.RedactStyle)) SyncRedactControls();
        if (Has(EditorContextControls.Magnification)) SyncMagnifySlider();

        // Row 1, right of the group chips (Survey toolbarRows.js resolveToolBarGroup): the
        // armed tool's group; under Select or Pan with a mark picked, THAT mark's group, so a
        // picked pen stroke reads pen / highlighter / eraser up top (pressing one arms it);
        // under Select with nothing picked (or a mark no group makes), Box / Lasso.
        string group = ToolBarGroup(chrome);
        DrawGroupPanel.Visibility = Show(group == "Draw");
        ShapeGroupPanel.Visibility = Show(group == "Shapes");
        TextGroupPanel.Visibility = Show(group == "Text");
        EffectsGroupPanel.Visibility = Show(group == "Effects");
        ImageGroupPanel.Visibility = Show(group == "Image");
        bool selectModes = group.Length == 0 && _tool == EditorTool.Select;
        if (SelectModePanel is not null) SelectModePanel.Visibility = Show(selectModes);
        if (ToolSlotDivider is not null) ToolSlotDivider.Visibility = Show(group.Length > 0 || selectModes);
        SyncCloudButton();

        EditorColorBar.Visibility = Visibility.Visible;
        if (SizeEffectHost is not null)
            SizeEffectHost.Visibility = Show(Has(EditorContextControls.Thickness));
        bool props = Has(EditorContextControls.Color) || Has(EditorContextControls.Thickness) ||
                     Has(EditorContextControls.RedactStyle) || Has(EditorContextControls.TextStyle) ||
                     Has(EditorContextControls.Magnification) ||
                     Has(EditorContextControls.ArrowStyle) || Has(EditorContextControls.LineStyle) ||
                     Has(EditorContextControls.EraserMode) || Has(EditorContextControls.StepMode) ||
                     Has(EditorContextControls.CropRatio) || CropPanel.Visibility == Visibility.Visible;
        if (SecondaryToolPanel is not null)
            SecondaryToolPanel.Visibility = Show(props);
        EditorFormatBar.Visibility = Visibility.Visible;
        // The text formatting sits IN row 2 after Aa (Survey first-run walk 2026-10-21 #10),
        // not in a bar of its own, so it never leaves its home in the format row.
        bool richText = TextStylePanel.Visibility == Visibility.Visible;
        HostRichTextRow(false);
        // Row 2 is on screen exactly when it has settings to show (toolbarRows.js
        // showsFormatRow): Pan, and Select with nothing picked, have none, and then there is
        // no band and no hairline — the top bar butts straight onto the canvas. The band
        // overlays the canvas, so the picture never moves when it comes and goes.
        bool formatRow = props || richText;
        EditorFormatHost.Visibility = Show(formatRow);
        EditorGroupBar.Visibility = Show(formatRow);
        SyncSurveyRow();
        if (_pinDock is not null) Dispatcher.BeginInvoke(PlacePinDock, DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(PackFormatOverflow, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Which group's tools row 1 shows right of the chips ("" for none). Survey
    /// toolbarRows.js resolveToolBarGroup: an open chip first (WinShot's Image chip, which
    /// arms nothing), then the armed tool's group, then — under Select or Pan — the picked
    /// mark's.
    /// </summary>
    private string ToolBarGroup(EditorTool chrome)
    {
        string armed = EditorShellContract.GroupFor(_tool, false);
        if (armed.Length > 0 && armed != "Image") _imageGroupOpen = false;
        if (_imageGroupOpen) return "Image";
        if (armed.Length > 0) return armed;
        if (_tool is EditorTool.Select or EditorTool.Pan && _selected is not null && _activeText is null)
        {
            string picked = EditorShellContract.GroupFor(chrome, false);
            if (picked is "Draw" or "Shapes" or "Text" or "Effects") return picked;
        }
        return "";
    }

    private bool _syncingSelectMode;

    /// <summary>Row 1's Box / Lasso pair under Select (Survey w46 "select modes in top bar").</summary>
    private void OnSelectModeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSelectMode || sender is not RadioButton rb) return;
        SetLassoSelect(rb.Tag as string == "Lasso");
    }

    private void SyncSelectModeButtons()
    {
        if (SelectBoxModeBtn is null || SelectLassoModeBtn is null) return;
        _syncingSelectMode = true;
        try
        {
            SelectBoxModeBtn.IsChecked = !_lassoSelect;
            SelectLassoModeBtn.IsChecked = _lassoSelect;
        }
        finally { _syncingSelectMode = false; }
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
        // The settings own row 2 (Survey w44): the whole band less its 48px centring margin
        // and a gutter each side, less what always stays (colour and size) and the "..." button.
        double inner = EditorGroupBar is { ActualWidth: > 0 } bar
            ? bar.ActualWidth - 48
            : Math.Max(0, (Viewport?.ActualWidth ?? ActualWidth) - 48);
        double right = 24 + 40;
        if (ColorPanel?.Visibility == Visibility.Visible)
            right += MeasureWidth(ColorPanel);
        if (SizeEffectHost?.Visibility == Visibility.Visible)
            right += MeasureWidth(SizeEffectHost);
        double slack = inner - right;
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
        // Order here is KEEP priority, not give-up priority: WhoOverflows walks this list
        // accumulating width and overflows everything that no longer fits, so whatever is
        // yielded FIRST is the last thing to be taken away. The previous order said it was
        // listing "the order they can be given up" and then yielded the qualifiers first,
        // which is backwards — it kept them and overflowed the choices they qualify. With
        // Arrow armed at a 1400 DIP window that put the ARROWHEAD dropdown into the "..."
        // flyout (measured: slack 344.35, LineStyle 90 + Arrowhead 257.3 = 347.3, so it
        // lost by 2.95 DIP) while a qualifier toggle kept its place. That is Isaiah's
        // "I can't change arrow heads": the control was wired and working, just filed away
        // behind an overflow button on the one tool that exists to use it.
        //
        // So: each tool's DEFINING control first, then the shared ones, then the qualifiers
        // — a redaction's strength is a refinement of a choice made elsewhere on the row,
        // and is the right thing to lose first.
        if (ArrowStylePanel is not null) yield return ArrowStylePanel;
        if (RedactStylePanel is not null) yield return RedactStylePanel;
        if (StepModePanel is not null) yield return StepModePanel;
        if (MagnifyPanel is not null) yield return MagnifyPanel;
        if (CropRatioPanel is not null) yield return CropRatioPanel;
        if (LineStylePanel is not null) yield return LineStylePanel;
        if (RedactStrengthPanel is not null) yield return RedactStrengthPanel;
        if (CropToolsPanel is not null) yield return CropToolsPanel;
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
                    // Survey reads a selected text box's strokeWidth back into the Width box
                    // like any other shape (PDFViewer.jsx:3836-3863).
                    case AnnotationData.TypeText: _shapeSize = th; break;
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
            _lineStyle = AnnotationStyle.LineStyleFrom(meta);
            // Survey's Bump field follows the selection like every other style input.
            if (meta.CloudIntensity is double bump)
                _cloudIntensity = CloudPath.ClampIntensity(bump);
            (_arrowhead, _startArrowhead) = AnnotationStyle.HeadsFrom(meta);
            _arrowBothEnds = AnnotationStyle.BothEnds(_arrowhead, _startArrowhead);
            if (ArrowBothEndsBtn is not null) ArrowBothEndsBtn.IsChecked = _arrowBothEnds;
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
            if (SizePillLabel is not null)
                SizePillLabel.Text = current.ToString(CultureInfo.InvariantCulture) + " pt";
            // label is 'Size' for the counter and the eraser, 'Width' otherwise
            // (AppShell.jsx:2476).
            bool sizeLabel = _tool == EditorTool.Eraser || ChromeTool() is EditorTool.Step;
            string label = sizeLabel ? "Size" : "Width";
            if (SizePresetsHeading is not null)
            {
                // Survey tracks this heading out by 0.065em. That is Survey TYPOGRAPHY, not
                // Survey behaviour, and WinShot's type scale owns letter spacing — so the
                // heading is the plain uppercase word, which is also what automation reads.
                SizePresetsHeading.Text = label.ToUpperInvariant();
                AutomationProperties.SetName(SizePresetsHeading, label.ToUpperInvariant());
            }
            if (SizePresetsBtn is not null)
            {
                SizePresetsBtn.ToolTip = label;
                System.Windows.Automation.AutomationProperties.SetName(SizePresetsBtn,
                    $"{label}: {current.ToString(CultureInfo.InvariantCulture)} pt");
            }
            BuildSizePresetRows(current);
        }
        finally
        {
            _syncingSizeBox = false;
        }
    }

    /// <summary>
    /// Survey SizeCard chips (AnnotationSizeControl.jsx): six to a row, each 40 tall with a
    /// mark over its number. Marks grow by RANK, not value: a line width shows a 22px line of
    /// weight round((1 + rank * 7) * 2) / 2; a round size (counter, eraser) a dot of
    /// round(2 + rank * 18). The current pick is accent text; hover is a surface step.
    /// </summary>
    private void BuildSizePresetRows(int current)
    {
        if (SizePresetList is null) return;
        SizePresetList.Children.Clear();
        int[] presets = ActiveSizePresets();
        bool dot = _tool == EditorTool.Eraser || ChromeTool() is EditorTool.Step;
        for (int index = 0; index < presets.Length; index++)
        {
            int preset = presets[index];
            double rank = presets.Length > 1 ? index / (double)(presets.Length - 1) : 0;
            FrameworkElement mark;
            if (dot)
            {
                double size = Math.Round(2 + rank * 18);
                mark = new Ellipse { Width = size, Height = size };
            }
            else
            {
                double weight = Math.Round((1 + rank * 7) * 2) / 2;
                mark = new Rectangle { Width = 22, Height = weight, RadiusX = weight / 2, RadiusY = weight / 2 };
            }
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.VerticalAlignment = VerticalAlignment.Center;
            var face = new Grid { Height = 40 };
            face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            face.Margin = new Thickness(0, 3, 0, 4);
            face.Children.Add(mark);
            var numeral = new TextBlock
            {
                Text = preset.ToString(CultureInfo.InvariantCulture),
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetRow(numeral, 1);
            face.Children.Add(numeral);
            var chip = new Button
            {
                Content = face,
                Tag = (double)preset,
                Margin = new Thickness(1),
                Cursor = Cursors.Hand,
                Focusable = true,
                Template = SizeChipTemplate,
                Foreground = preset == current
                    ? (Brush)FindResource("ChromeAccentBrush")
                    : (Brush)FindResource("TextSecondaryBrush"),
            };
            System.Windows.Automation.AutomationProperties.SetName(chip, $"{preset} pt");
            ((Shape)mark).SetBinding(Shape.FillProperty, new Binding(nameof(Control.Foreground)) { Source = chip });
            numeral.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = chip });
            chip.Click += OnSizePresetClick;
            SizePresetList.Children.Add(chip);
        }
    }

    private static readonly ControlTemplate SizeChipTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
        "<Border x:Name='Bg' Background='Transparent' CornerRadius='6' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
        "<ContentPresenter/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bg' Property='Background' Value='#1FFFFFFF'/></Trigger>" +
        "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Bg' Property='Background' Value='#1FFFFFFF'/></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='Bg' Property='RenderTransformOrigin' Value='0.5,0.5'/>" +
        "<Setter TargetName='Bg' Property='RenderTransform'><Setter.Value><ScaleTransform ScaleX='0.94' ScaleY='0.94'/></Setter.Value></Setter></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");

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
        if (sender is not ButtonBase b || b.Tag is not double size) return;
        if (SizePresetsBtn is not null) SizePresetsBtn.IsChecked = false;
        ApplySize(size);
    }

    // sanitizeAnnotationSizeDraft: at most three digits, nothing else.
    private void OnSizeInputText(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void OnSizeInputKey(object sender, KeyEventArgs e)
    {
        // Enter commits and closes the card (AnnotationSizeControl.jsx:118).
        if (e.Key == Key.Enter)
        {
            CommitSizeInput();
            if (SizePresetsBtn is not null) SizePresetsBtn.IsChecked = false;
            Viewport.Focus();
            e.Handled = true;
            return;
        }
        // SizeCard: Up / Down step the number by 1 and apply it at once.
        if (e.Key is Key.Up or Key.Down && SizeInput is not null)
        {
            double typed = double.TryParse(SizeInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out double v)
                ? v : ActiveSize();
            ApplySize(typed + (e.Key == Key.Up ? 1 : -1));
            SizeInput.SelectAll();
            e.Handled = true;
        }
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
        // Survey's Width input is the STROKE control for every kind, text boxes included
        // (isEditableShapeSelected covers 'textbox', PDFViewer.jsx:7428-7433) — the font size
        // is a separate dropdown inside the rich-text portal (AppShell.jsx:1855-1876). It also
        // keeps patching strokeWidth while the rich-text editor is mounted.
        double applied = _tool == EditorTool.Highlighter
            ? AnnotationStyle.HighlighterWidth(_thickness)
            : _thickness;
        RestyleSelected(color: null, thickness: applied, fill: null);
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
        // SurveyDropdown renders its trigger in Arial SemiBold 11 (Survey .chrome-pill); measuring in the UI
        // font would under-size the combo and clip the label.
        var typeface = new Typeface(new FontFamily("Arial"), FontStyles.Normal,
            FontWeights.SemiBold, FontStretches.Normal);
        double max = 0;
        void Consider(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var ft = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, 11, Brushes.White, dip);
            if (ft.Width > max) max = ft.Width;
        }
        foreach (string s in extra) Consider(s);
        foreach (object item in box.Items)
            Consider(item is ComboBoxItem cbi ? cbi.Content?.ToString() : item?.ToString());
        // Survey .chrome-pill: 8 px inset, then the label, then 6 + 9 + 6 for the chevron.
        double width = Math.Ceiling(max) + 29;
        // AnnotationDropdown.css:9 min-width: 72px.
        if (width < 72) width = 72;
        box.MinWidth = width;
        box.Width = width;
    }

    private void FitEditorCombos()
    {
        // SizeField is a fixed-width composite (43px input + 24px chevron), so it
        // never needs measuring.
        // The arrowhead (90) and line style (92) pills are Survey's fixed
        // --chrome-field-w-arrowhead / --chrome-field-w-style; their cards match.
        if (ArrowStyleBox is not null) { ArrowStyleBox.MinWidth = 90; ArrowStyleBox.Width = 90; }
        if (LineStyleBox is not null) { LineStyleBox.MinWidth = 92; LineStyleBox.Width = 92; }
        FitComboToLongestItem(FontFamilyBox);
        FitComboToLongestItem(FontSizeBox);
        FitComboToLongestItem(AlignBox);
        FitComboToLongestItem(VerticalAlignBox);
        FitComboToLongestItem(CropRatioBox);

    }

    private double ActiveSize() => SizeForTool(ChromeTool());

    private double SizeForTool(EditorTool tool) => tool switch
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


    /// <summary>Re-checks the toolbar radio for a tool (used by the eyedropper to restore the prior tool).</summary>
    private void CheckToolButton(EditorTool tool)
    {
        // Arming anything that is not the eyedropper cancels the picker's pending return:
        // the user went somewhere else, so the picker must not spring back open over the
        // canvas the moment the NEXT sample happens to be taken.
        if (tool != EditorTool.Eyedropper) _reopenPickerAfterSample = false;
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
        SyncSurveyRow();
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
        if (IsLoaded) RestyleSelectedTextFormat(m => m.FontFamily = family);
        ReturnFocusToEditor();
    }

    private void OnFontSizeBoxChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext || _syncingSizeBox) return;
        if (FontSizeBox?.SelectedItem is not ComboBoxItem item || item.Tag is not double size) return;
        _textFontSize = AnnotationStyle.ClampTextSize(size);
        if (IsLoaded) RestyleSelectedTextFormat(m => m.FontSize = _textFontSize);
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

    /// <summary>
    /// Survey has ONE preset palette: the font-colour swatch renders the same shared
    /// CompactColorPicker as the border swatch (AppShell.jsx:1798-1825), so picking a text colour
    /// must offer exactly the colours picking a stroke colour does.
    /// </summary>
    private void BuildFontColorPalette()
    {
        if (FontColorGrid is null || FontColorGrid.Children.Count > 0) return;
        foreach (Color c in AnnotationStyle.PickerPresets)
        {
            var btn = MakePickerSwatch(c, "Font color");
            btn.Click += (_, _) => SetFontColor(c);
            FontColorGrid.Children.Add(btn);
        }
        SyncPickerSelection(FontColorGrid, _textFontColor);
    }

    private void SetFontColor(Color color)
    {
        _textFontColor = color;
        if (FontColorPopup is not null) FontColorPopup.IsOpen = false;
        SyncPickerSelection(FontColorGrid, color);
        SyncFontColorSwatch();
        if (IsLoaded) RestyleSelectedTextFormat(m => m.FontColor = ToHex(color));
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
        if (IsLoaded) RestyleSelectedTextFormat(m => m.Bold = _textBold);
    }

    private void OnTextItalicClick(object sender, RoutedEventArgs e)
    {
        _textItalic = !_textItalic;
        SyncTextFormatChrome();
        if (IsLoaded) RestyleSelectedTextFormat(m => m.Italic = _textItalic);
    }

    private void OnTextUnderlineClick(object sender, RoutedEventArgs e)
    {
        _textUnderline = !_textUnderline;
        SyncTextFormatChrome();
        if (IsLoaded) RestyleSelectedTextFormat(m => m.Underline = _textUnderline);
    }

    private void OnTextStrikeClick(object sender, RoutedEventArgs e)
    {
        _textStrike = !_textStrike;
        SyncTextFormatChrome();
        if (IsLoaded) RestyleSelectedTextFormat(m => m.Strike = _textStrike);
    }

    private void OnTextAlignChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (AlignBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string align) return;
        _textAlign = align;
        if (IsLoaded) RestyleSelectedTextFormat(m => m.Align = align);
    }

    private void OnVerticalAlignChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (VerticalAlignBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string align) return;
        _textVerticalAlign = align;
        if (IsLoaded) RestyleSelectedTextFormat(m => m.VerticalAlign = align);
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
        // Assigning ComboBox.SelectedItem raises SelectionChanged synchronously, and both
        // handlers restyle again — one click used to cost three undo entries and three full
        // text-visual rebuilds. Survey's cell fires exactly two single-key patches
        // (AppShell.jsx:1970-1978), so update the combos for display only.
        bool restore = _syncingContext;
        _syncingContext = true;
        try
        {
            SelectComboByTag(AlignBox, _textAlign);
            SelectComboByTag(VerticalAlignBox, _textVerticalAlign);
        }
        finally { _syncingContext = restore; }
        if (IsLoaded)
            RestyleSelectedTextFormat(m =>
            {
                m.Align = _textAlign;
                m.VerticalAlign = _textVerticalAlign;
            });
    }

    private void SyncTextFormatChrome()
    {
        // Survey's font-family and font-size controls are CONTROLLED renders off the bridge
        // state (`value={currentFamily}`, AppShell.jsx:1832-1852); `onSelect` fires ONLY from a
        // user gesture (AnnotationDropdown.jsx:174-180, :43-48). Re-rendering with a new value
        // mutates nothing. This method is pure chrome painting, so nothing it writes may reach
        // an input handler — otherwise merely SELECTING a text box restyles it, pushing a
        // phantom undo entry and rebuilding the element out from under the pointer.
        bool restoreSync = _syncingContext;
        _syncingContext = true;
        try
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
        // AppShell.jsx:1902-1903 — Survey tints the chip and inks the glyph in its own accent;
        // off: #3a4252 + #e8e2d4.
        void Paint(Button? btn, bool on)
        {
            if (btn is null) return;
            btn.Background = (Brush)FindResource(on ? "ChromeAccentTintBrush" : "ChromeControlBrush");
            btn.Foreground = (Brush)FindResource(on ? "ChromeAccentBrush" : "ChromeTextBrush");
        }
        Paint(BoldBtn, _textBold);
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
        finally { _syncingContext = restoreSync; }
    }

    private void PaintAlignCells()
    {
        string current = $"{_textAlign}|{_textVerticalAlign}";
        // Survey's active cell is accent-on-accent-tint with an accent border, and INACTIVE
        // cells still carry chrome (#14171c on 1px #2a3140) rather than being invisible
        // (AppShell.jsx:1960-2001).
        var gold = (Brush)FindResource("ChromeAccentBrush");
        var onFill = (Brush)FindResource("ChromeAccentCellBrush");
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
            if (el is FrameworkElement { Tag: AnnotationData m }
                && AnnotationStyle.LineStyleFrom(m) == LineBorderStyle.Cloud)
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
        // Survey clamps the start number to >= 1 (AppShell.jsx:2540-2556). Since counters
        // became series-numbered, the field sets the ACTIVE series' seriesStart — the number
        // its first pin shows — rather than a free-floating "next number", because every pin's
        // number is now derived from its position in its series and cannot be set directly.
        if (int.TryParse(StepStartInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            SetCounterSeriesStart(Math.Max(1, v));
        StepStartInput.Text = _nextStep.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Survey's `Aa` button re-opens the selected text box or callout for editing —
    /// the same entry point double-click uses (AppShell.jsx:2641-2678).
    /// </summary>
    private void OnEnterTextEdit(object sender, RoutedEventArgs e)
    {
        if (_activeText is not null) return;
        // Survey publishes the toolbar's selected annotation ONLY for a single selection
        // (SVGAnnotationLayer.jsx:1562-1573), so canEnterTextEdit is false with 2+ marks
        // selected. `_selection` is empty for a plain single click, hence <= 1, not == 1.
        if (_selection.Count > 1) return;
        // Survey gates on the annotation's TYPE, never on the rendered element
        // (PDFViewer.jsx:23087-23089, :11310-11328). A committed plain text is a Grid
        // (Path for Outline, Border for Pill) wrapping the TextBlock, so a visual-class
        // test is false for every normal text box and the button does nothing.
        if (_selected is not FrameworkElement { Tag: AnnotationData m } fe) return;
        if (m.Type == AnnotationData.TypeCallout && fe is CalloutAnnotation callout) BeginCalloutReEdit(callout);
        else if (m.Type == AnnotationData.TypeText) BeginTextReEdit(fe);
    }

    /// <summary>
    /// The Aa button stays visible but disabled with nothing editable selected, and
    /// carries the gold pressed look while an editor is live.
    /// </summary>
    private void SyncTextEditButton()
    {
        if (TextEditBtn is null) return;
        bool editing = _activeText is not null;
        // Same annotation-type gate Survey's canEnterTextEdit uses (PDFViewer.jsx:23087).
        bool canEdit = editing
            || (_selection.Count <= 1
                && _selected is FrameworkElement { Tag: AnnotationData sm }
                && sm.Type is AnnotationData.TypeText or AnnotationData.TypeCallout);
        TextEditBtn.IsEnabled = canEdit;
        TextEditBtn.Opacity = canEdit ? 1 : 0.5;
        TextEditBtn.Background = editing
            ? (Brush)FindResource("ChromeAccentTintBrush")
            : (Brush)FindResource("ChromeControlBrush");
        TextEditBtn.Foreground = editing
            ? (Brush)FindResource("ChromeAccentBrush")
            : (Brush)FindResource(canEdit ? "ChromeTextBrush" : "ChromeTextDisabledBrush");
        TextEditBtn.ToolTip = canEdit ? "Edit text" : "Select a text box or callout to edit its text";
    }

    /// <summary>
    /// Survey's eraser mode is persisted ('eraserMode' in localStorage, default 'partial')
    /// and the eraser TOOL's tooltip / accessible name is the CURRENT MODE rather than a
    /// static "Eraser" (PDFViewer.jsx:4274-4281, :32080-32097).
    /// </summary>
    private void SetEraserPartial(bool partial)
    {
        _eraserPartial = partial;
        SyncSurveyRow();

        // Survey never shows a static "Eraser": the chip IS the mode (PDFViewer.jsx:32080-32082).
        // Survey PASS 7 (2026-09-22 review): the tool is called "Eraser" whatever kind is live —
        // the Partial / Whole toggle in row 2 shows the kind, and two controls must not answer
        // to one name.
        if (EraserToolBtn is not null)
        {
            EraserToolBtn.ToolTip = ToolTipWithKey("Eraser", "E");
            System.Windows.Automation.AutomationProperties.SetName(EraserToolBtn, "Eraser");
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


    /// <summary>
    /// Where this editor came from on screen, in device pixels. Set it before Show() and
    /// the window grows out of that rectangle and opens on THAT display — which for a
    /// capture taken on a second monitor is the monitor the user is looking at, not the
    /// primary. Leave it unset for History and the CLI: there is no origin to grow from,
    /// so the window just opens centred.
    /// </summary>
    internal void GrowFrom(SD.Rectangle originScreenRect)
    {
        if (originScreenRect.Width < 1 || originScreenRect.Height < 1) return;
        _origin = originScreenRect;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    /// <summary>
    /// The editor's landing size in DIP. Not a guess: the one-row toolbar's measured width
    /// plus the frame, which is what <see cref="EditorShellContract.MinimumEditorLogicalWidth"/>
    /// is a floor for.
    /// </summary>
    private const double DefaultWidthDip = 1167;
    private const double DefaultHeightDip = 590;

    /// <summary>
    /// Everything around the viewport at the default size, in DIP: the 48 DIP rail plus the
    /// frame across, and the title bar, both toolbar rows and the bottom bar down. Read off
    /// the installed build at 125 %: a 1167x590 window holds a 1105x422 viewport.
    /// </summary>
    private const double ChromeWidthDip = DefaultWidthDip - 1105;
    private const double ChromeHeightDip = DefaultHeightDip - 422;

    /// <summary>
    /// The landing size: the capture's own footprint (see
    /// <see cref="EditorGrowLayout.DesiredForCapture"/>), but never narrower than the default,
    /// because the one-row toolbar needs that width (EditorShellContract). The height has no
    /// such need, so a short capture gets a short window down to MinHeight and fills it.
    /// </summary>
    private Size DesiredWindowDip()
    {
        Size want = EditorGrowLayout.DesiredForCapture(
            _sourceWidth, _sourceHeight,
            new Size(FitPadX * 2, FitGap * 2),
            new Size(ChromeWidthDip, ChromeHeightDip));
        return new Size(Math.Max(DefaultWidthDip, want.Width), want.Height);
    }

    private SD.Rectangle? _origin;
    private IDisposable? _growMotion;

    /// <summary>
    /// Lowers the declared minimum size to whatever the display can actually hold.
    ///
    /// <see cref="EditorShellContract.MinimumEditorLogicalWidth"/> is a consequence of the
    /// one-row toolbar, not a promise a screen can keep: 1152 DIP is wider than a
    /// 1536x1024 laptop at 150 % (1024 DIP) or a 2160x1440 at 200 %. Windows enforces
    /// MinWidth through WM_GETMINMAXINFO, so leaving it declared on a display that small
    /// opens the window WIDER than the monitor — and because Save as… / Done are docked to
    /// the right of the row, what goes off the edge is the pair that finishes the job, with
    /// no way to drag narrower because the minimum is what put them there. A toolbar
    /// clipped at its right end is recoverable; an unreachable Done button is not.
    ///
    /// Every open path needs this, not just the grow: the constructor sizes itself from the
    /// work area but never touched the floor, so History and the CLI hit exactly that case.
    /// <see cref="EditorShellContract.FitsOnMonitor"/> is the same question asked as a
    /// predicate.
    /// </summary>
    private void ClampMinimumsTo(double availableWidthDip, double availableHeightDip)
    {
        if (double.IsFinite(availableWidthDip) && availableWidthDip > 0)
            MinWidth = Math.Min(MinWidth, availableWidthDip);
        if (double.IsFinite(availableHeightDip) && availableHeightDip > 0)
            MinHeight = Math.Min(MinHeight, availableHeightDip);
    }

    /// <summary>
    /// Places the window on the origin's display and tweens it out of the origin rect.
    /// Runs once, on the first frame that has an HWND, and entirely in DEVICE PIXELS with
    /// the ORIGIN monitor's DPI: at this moment the window has not been moved yet, so its
    /// own PresentationSource still reports the primary's scale, which on a mixed-DPI
    /// desktop is the wrong answer for every number involved. History places itself the
    /// same way (SetWindowPos + GetDpiForMonitor) and for the same reason.
    /// </summary>
    private void BeginGrowFromOrigin()
    {
        if (_origin is not SD.Rectangle origin) return;
        _origin = null;

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        double scale = RecordingMonitorDpi.ScaleFor(origin);
        SD.Rectangle work = System.Windows.Forms.Screen.FromRectangle(origin).WorkingArea;

        // WPF enforces MinWidth/MinHeight through WM_GETMINMAXINFO, so without this every
        // frame of the tween would paint at the 1152x520 floor and only the position would
        // move — a full-size window sliding in from the card's corner instead of growing
        // out of it. The floor is a resize constraint for the user, not for the entrance.
        double minWidth = MinWidth, minHeight = MinHeight;

        // The target has to KNOW about that floor, or the two disagree: on a display where
        // 90 % of the work area is under 980x520 the tween lands on the smaller number and
        // the restored minimum then yanks the window up to the larger one on the next
        // layout pass, which is exactly the size snap the motion rule forbids.
        SD.Rectangle to = EditorGrowLayout.Target(
            work, DesiredWindowDip(), scale, new Size(minWidth, minHeight));
        SD.Rectangle from = EditorGrowLayout.Origin(origin);

        // Restored to at most where the window actually landed, so a work area smaller than
        // the declared floor cannot leave a minimum wider than the screen it opened on.
        void RestoreMinimums()
        {
            MinWidth = minWidth;
            MinHeight = minHeight;
            ClampMinimumsTo(to.Width / scale, to.Height / scale);
        }

        MinWidth = 0;
        MinHeight = 0;

        // Motion.Run applies nothing synchronously — its first frame arrives on the first
        // timer tick — so the start rect is written here. Otherwise the window paints once
        // at CW_USEDEFAULT on the PRIMARY monitor before the tween ever starts.
        PlaceWindow(hwnd, from);

        _growMotion?.Dispose();
        _growMotion = Motion.Run(Motion.SwapMs, Motion.EaseOutCubic, p =>
        {
            if (_closed) return;
            // Motion swallows a throwing frame and never runs onDone; without this a single
            // bad frame would leave a LIVE editor resizable down to nothing.
            try { PlaceWindow(hwnd, EditorGrowLayout.Lerp(from, to, p)); }
            catch { RestoreMinimums(); throw; }
        }, onDone: () =>
        {
            _growMotion = null;
            RestoreMinimums();
        });
    }

    /// <summary>
    /// One frame of the grow. Left/Top/Width/Height are DIPs in whichever monitor's scale
    /// the window currently believes it is on, which is exactly what breaks when it moves
    /// between monitors mid-tween; SetWindowPos takes the physical rect and means it.
    /// </summary>
    private static void PlaceWindow(IntPtr hwnd, SD.Rectangle rect) =>
        SetWindowPos(hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height,
            SwpNoZOrder | SwpNoActivate);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);


    /// <summary>
    /// Every popover the toolbar owns, paired with the control that opens it. One list, so
    /// "close them all", "Escape closes the open one and gives focus back", and the entrance
    /// motion cannot drift apart as the row changes shape.
    /// </summary>
    private (Popup Popup, Control Owner)[] ToolbarPopups() => new (Popup, Control)[]
    {
        (ColorPickerPopup, ColorWellBtn),
        (SizePresetsPopup, SizePresetsBtn),
        (AlignPopup, AlignMenuBtn),
        (FontColorPopup, FontColorBtn),
        (FitMenuPopup, FitMenuBtn),
        (FormatOverflowPopup, FormatOverflowBtn),
    };

    private void CloseToolbarPopups()
    {
        foreach ((Popup popup, _) in ToolbarPopups())
            if (popup is not null) popup.IsOpen = false;
    }

    /// <summary>
    /// Escape closes the open popover and hands focus back to the button that opened it,
    /// so a keyboard user is never trapped inside a menu. Returns false when none is open,
    /// and Escape goes on to mean what it always meant.
    /// </summary>
    private bool CloseOpenPopover()
    {
        foreach ((Popup popup, Control owner) in ToolbarPopups())
        {
            if (popup is null || !popup.IsOpen) continue;
            popup.IsOpen = false;
            owner?.Focus();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Wave 0's popover entrance, applied to every toolbar popover from one place: a 0.98
    /// scale-up under the Popup's own fade. Wired once at load rather than repeated in the
    /// markup, so a popover added later cannot forget it. The EXIT stays the Popup's
    /// PopupAnimation — a closing popup tears its child down before a storyboard could run
    /// it out, so entrance and exit have to be the same mechanism or the exit is a hard cut.
    /// </summary>
    private void WirePopoverMotion()
    {
        foreach ((Popup popup, _) in ToolbarPopups())
        {
            if (popup is null) continue;
            popup.Opened += (s, _) =>
            {
                if (_closed || s is not Popup p || p.Child is not FrameworkElement child) return;
                // A frozen transform cannot be animated, and a shared one would animate
                // every popover at once, so each child gets its own.
                if (child.RenderTransform is not ScaleTransform)
                {
                    child.RenderTransform = new ScaleTransform(1, 1);
                    child.RenderTransformOrigin = new Point(0.5, 0);
                }
                // A Popup runs on its own HWND, so anything that throws here is thrown from
                // inside a window procedure, where it is unhandled and takes the process with
                // it. An entrance animation is never worth that.
                try { child.BeginStoryboard((Storyboard)FindResource("PopoverIn")); }
                catch (Exception ex) { Log.Error("Popover entrance failed (non-fatal)", ex); }
            };
        }
    }

}
