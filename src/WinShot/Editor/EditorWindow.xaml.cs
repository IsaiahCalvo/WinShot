using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    /// <summary>
    /// Per-tool style memory — colour, opacity, fill and size, each remembered against the
    /// tool that set it. Replaces the old single _strokeThickness / _textThickness pair.
    /// </summary>
    private ToolPreferences _toolPrefs = new();

    private bool _syncingThicknessButtons;

    /// <summary>Which category's sub-row is showing.</summary>
    private EditorToolCategory _category = EditorToolCategory.Shapes;

    /// <summary>Guards the category radios while a tool switch re-checks them, so no tool is re-armed.</summary>
    private bool _syncingCategory;

    /// <summary>The contextual set currently on screen, so only a real change animates.</summary>
    private EditorContextControls _shownContext = EditorContextControls.None;

    /// <summary>The Select chevron's rubber-band rule; Auto keeps the drag-direction gesture.</summary>
    private MarqueeModePreference _marqueePreference = MarqueeModePreference.Auto;

    /// <summary>Re-entrancy guard for the size field (see ApplySize).</summary>
    private bool _applyingSize;
    private int _nextStep = 1;
    private bool _stepLetters; // Step tool: false = number badges, true = letter badges (A, B, …)
    private ShapeFillMode _fillMode = ShapeFillMode.None;
    private bool _filledRectangleMode;
    private TextStyle _textStyle = TextStyle.Plain;
    private ArrowStyle _arrowStyle = ArrowStyle.Straight;

    /// <summary>Outline dashes and line quality for the NEXT shape; a selected one carries its own.</summary>
    private StrokeDash _strokeDash = StrokeDash.Solid;
    private StrokeRender _strokeRender = StrokeRender.Clean;

    /// <summary>The jitter seed of the shape being drawn, so its wobble is stable as the drag grows.</summary>
    private int _sketchSeed;
    private double _opacity = 1.0; // annotation alpha multiplier (0.25–1.0) for new + restyled marks
    // Redaction defaults for the NEXT mark; a selected redaction carries its own.
    private RedactStyle _redactStyle = RedactStyle.Pixelate;
    private int _redactStrength = Redaction.DefaultStrength;
    private int _activeRedactSeed;
    private readonly System.Diagnostics.Stopwatch _redactPreviewClock = new();

    // Strength-slider gesture: a continuous drag previews live but records ONE undo entry.
    private AnnotationData? _redactStrengthBefore;
    private UIElement? _redactStrengthElement;

    // Counter re-numbering run: digits typed against one selected badge, committed as one
    // undo entry when the selection changes or a non-digit key arrives.
    private UIElement? _stepRetypeElement;
    private AnnotationData? _stepRetypeBefore;
    private int _stepRetypeValue;

    // Opacity-slider gesture: a continuous drag previews live but records ONE undo entry.
    // _opacityBefore is the selected element's style captured at gesture start.
    private StyleSnapshot? _opacityBefore;
    private UIElement? _opacityElement;
    private double? _cropRatio; // null = free
    private string _cropRatioTag = ""; // the picked ratio menu row ("" = Free)
    private EditorTool _toolBeforeEyedropper = EditorTool.Select;
    private bool _colorPickerBuilt;
    private Color _customColor = Color.FromRgb(0xFF, 0x3B, 0x30); // last custom / eyedropper color
    private bool _suppressHexEvents;

    // Colour picker state. Hue is kept separately from the colour so dragging into the
    // white corner does not lose which hue the user was on.
    private double _pickerHue;
    private double _pickerSaturation = 1;
    private double _pickerValue = 1;
    private byte _pickerAlpha = 255;
    private bool _pickerFillTarget;
    private bool _draggingSv;
    private bool _suppressPickerEvents;

    /// <summary>Set by the picker's own eyedropper, so a sample returns to the picker.</summary>
    private bool _reopenPickerAfterSample;

    /// <summary>
    /// The fill colour for shapes, or null for "derive it from the stroke" — which is what
    /// every shape drawn before the Fill tab existed did, so nothing already on a canvas
    /// changes colour.
    /// </summary>
    private Color? _fillColor;

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

    /// <summary>
    /// Marquee multi-selection: two or more annotations picked with a rubber band. The
    /// group is move-and-delete only; a single pick still goes through <see cref="_selected"/>
    /// so every existing single-selection path is untouched.
    /// </summary>
    private readonly List<UIElement> _multiSelected = new();
    private bool _marqueeActive;
    private Point _marqueeStart;
    private MarqueeMode _marqueeMode = MarqueeMode.Window;
    private bool _movingSelection;
    private Point _moveLast;   // content coords
    private Vector _moveTotal; // the delta actually applied — the undo record
    private Vector _moveRaw;   // the unconstrained drag, so Shift can be let go mid-move

    /// <summary>
    /// Cross-window annotation clipboard. Static so a mark copied in one editor pastes
    /// into another — the data is a plain serialisable record, so there is nothing
    /// window-specific to keep.
    /// </summary>
    private static readonly List<ClipboardMark> AnnotationClipboard = new();

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
            AbortMarquee();
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
            _growMotion?.Dispose();
            _growMotion = null;
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
        // "Keep the editor on top" was declared and never wired; the row it belongs to
        // was collapsed because of it.
        Topmost = _settings.Current.AlwaysOnTop;
        SourceInitialized += (_, _) => BeginGrowFromOrigin();
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
                // The sub-row opens on Shapes so the editor never starts with an empty
                // tool strip — but no tool is armed: the pointer stays the active tool.
                _syncingCategory = true;
                try { CategoryShapesBtn.IsChecked = true; }
                finally { _syncingCategory = false; }
                ShowCategoryRow(EditorToolCategory.Shapes);
                UpdateContextPanels();
                EditorBottomBar.Visibility = Visibility.Visible;
            }));
    }

    public static EditorWindow CreateForCapture(SD.Bitmap source, SettingsService settings, HistoryService history)
    {
        return new EditorWindow(source, settings, history);
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

    private SD.Rectangle? _origin;
    private IDisposable? _growMotion;

    /// <summary>~220 ms, the same "one surface becoming another" beat as the card's restack.</summary>
    private const int GrowDurationMs = 220;

    /// <summary>
    /// Places the window on the origin's display and tweens it out of the origin rect.
    /// Runs once, on the first frame that has an HWND — device-to-DIP conversion needs the
    /// window's own PresentationSource, and on a mixed-DPI desktop the primary monitor's
    /// scale is the wrong answer.
    /// </summary>
    private void BeginGrowFromOrigin()
    {
        if (_origin is not SD.Rectangle origin) return;
        _origin = null;

        if (PresentationSource.FromVisual(this)?.CompositionTarget is not CompositionTarget target)
            return;
        Matrix toDip = target.TransformFromDevice;

        var screen = System.Windows.Forms.Screen.FromRectangle(origin);
        SD.Rectangle work = screen.WorkingArea;
        Point workTopLeft = toDip.Transform(new Point(work.Left, work.Top));
        Point workSize = toDip.Transform(new Point(work.Width, work.Height));

        double width = Math.Min(Width, workSize.X * 0.9);
        double height = Math.Min(Height, workSize.Y * 0.9);
        var to = new Rect(
            workTopLeft.X + (workSize.X - width) / 2,
            workTopLeft.Y + (workSize.Y - height) / 2,
            width,
            height);

        Point fromTopLeft = toDip.Transform(new Point(origin.Left, origin.Top));
        Point fromSize = toDip.Transform(new Point(origin.Width, origin.Height));
        var from = new Rect(fromTopLeft.X, fromTopLeft.Y, Math.Max(1, fromSize.X), Math.Max(1, fromSize.Y));

        _growMotion?.Dispose();
        _growMotion = Motion.Run(GrowDurationMs, Motion.EaseOutCubic, p =>
        {
            if (_closed) return;
            Left = from.X + (to.X - from.X) * p;
            Top = from.Y + (to.Y - from.Y) * p;
            Width = from.Width + (to.Width - from.Width) * p;
            Height = from.Height + (to.Height - from.Height) * p;
        }, onDone: () => _growMotion = null);
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

    /// <summary>The viewport's own middle — what a keyboard zoom pivots around.</summary>
    private Point ViewportCentre() => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    /// <summary>Ctrl+0 and the zoom menu's fixed percentages.</summary>
    private void ZoomToPercent(double zoom)
    {
        if (Viewport.ActualWidth < 1 || Viewport.ActualHeight < 1) return;
        ZoomAt(ViewportCentre(), zoom);
    }

    /// <summary>Ctrl+plus / Ctrl+minus: one step of the same 1.2 ratio the wheel uses.</summary>
    private void StepZoom(double factor)
    {
        if (Viewport.ActualWidth < 1 || Viewport.ActualHeight < 1) return;
        ZoomAt(ViewportCentre(), _zoom * factor);
    }

    private void OnViewChanged()
    {
        // The one place the zoom readout is written, so it can never go stale.
        ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";
        // Keep helper chrome hairline-thin on screen at any zoom; the dash
        // pattern is in thickness units, so it compensates automatically.
        double t = 1.5 / _zoom;
        DragRect.StrokeThickness = t;
        SelectionRect.StrokeThickness = t;
        UpdateSelectionVisual();
        UpdateCurveHandleVisual();
        UpdateScrollOverlay();
    }

    /// <summary>
    /// Repositions the overlay scroll indicators and flashes them. They exist to answer
    /// "is there more above this?" while the view moves, and to be gone once it has been
    /// answered — so they fade out rather than sitting on the picture.
    /// </summary>
    private void UpdateScrollOverlay()
    {
        if (ScrollOverlay is null) return;
        (Rect? horizontal, Rect? vertical) = EditorScrollBars.Calculate(
            new Size(Viewport.ActualWidth, Viewport.ActualHeight),
            new Size(_sourceWidth, _sourceHeight),
            _zoom,
            new Vector(ViewTranslate.X, ViewTranslate.Y));

        static void Place(System.Windows.Shapes.Rectangle bar, Rect? area)
        {
            if (area is not Rect r)
            {
                bar.Visibility = Visibility.Collapsed;
                return;
            }
            Canvas.SetLeft(bar, r.X);
            Canvas.SetTop(bar, r.Y);
            bar.Width = r.Width;
            bar.Height = r.Height;
            bar.Visibility = Visibility.Visible;
        }

        Place(ScrollBarH, horizontal);
        Place(ScrollBarV, vertical);
        if (horizontal is null && vertical is null)
        {
            ScrollOverlay.BeginAnimation(OpacityProperty, null);
            ScrollOverlay.Opacity = 0;
            return;
        }
        ScrollOverlay.BeginStoryboard((Storyboard)FindResource("ScrollHintFlash"));
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
        UpdateScrollOverlay();
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
            ClearMultiSelection();
            if (_tool == EditorTool.Crop && tool != EditorTool.Crop)
                ClearCropPreview();
            if (tool == EditorTool.Eyedropper && _tool != EditorTool.Eyedropper)
                _toolBeforeEyedropper = _tool; // so a sample can return to the prior tool
            if (tool != EditorTool.Eyedropper)
                HideEyedropperChrome();
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
            CloseToolbarPopups();
            SyncCategoryToTool();
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
            ClearMultiSelection();
            if (_tool == EditorTool.Crop) ClearCropPreview();
            UncheckOtherToolButtons(rb);
        }

        _tool = EditorTool.Rectangle;
        _filledRectangleMode = true;
        _fillMode = ShapeFillMode.Solid;
        if (FillSolidBtn is not null) FillSolidBtn.IsChecked = true;
        if (IsLoaded)
        {
            CloseToolbarPopups();
            SyncCategoryToTool();
            UpdateCursor();
            UpdateContextPanels();
        }
    }

    private IEnumerable<RadioButton> ToolButtons()
    {
        if (SelectToolBtn is not null) yield return SelectToolBtn;
        if (PanToolBtn is not null) yield return PanToolBtn;
        if (CategoryToolRow is null) yield break;
        foreach (Panel row in CategoryRows())
            foreach (var rb in row.Children.OfType<RadioButton>())
                yield return rb;
    }

    private IEnumerable<Panel> CategoryRows()
    {
        if (DrawToolRow is not null) yield return DrawToolRow;
        if (ShapesToolRow is not null) yield return ShapesToolRow;
        if (TextToolRow is not null) yield return TextToolRow;
        if (RedactToolRow is not null) yield return RedactToolRow;
        if (ImageToolRow is not null) yield return ImageToolRow;
    }

    private Panel? RowFor(EditorToolCategory category) => category switch
    {
        EditorToolCategory.Draw => DrawToolRow,
        EditorToolCategory.Shapes => ShapesToolRow,
        EditorToolCategory.Text => TextToolRow,
        EditorToolCategory.Redact => RedactToolRow,
        EditorToolCategory.Image => ImageToolRow,
        _ => null,
    };

    private RadioButton? CategoryButton(EditorToolCategory category) => category switch
    {
        EditorToolCategory.Draw => CategoryDrawBtn,
        EditorToolCategory.Shapes => CategoryShapesBtn,
        EditorToolCategory.Text => CategoryTextBtn,
        EditorToolCategory.Redact => CategoryRedactBtn,
        EditorToolCategory.Image => CategoryImageBtn,
        _ => null,
    };

    private void UncheckOtherToolButtons(RadioButton selected)
    {
        foreach (var rb in ToolButtons())
            if (!ReferenceEquals(rb, selected) && rb.IsChecked == true)
                rb.IsChecked = false;
    }

    private void CloseToolbarPopups()
    {
        SizePresetPopup.IsOpen = false;
        SelectModePopup.IsOpen = false;
        ArrowStylePopup.IsOpen = false;
        TextStylePopup.IsOpen = false;
        CropRatioPopup.IsOpen = false;
        ColorPickerPopup.IsOpen = false;
    }

    // ------------------------------------------------------------- categories

    /// <summary>
    /// A category button opens its sub-row and arms the category's first tool — except
    /// Image, whose row is mostly one-shot actions, so clicking it must not silently drop
    /// the user into a crop. Re-picking the category the active tool already belongs to
    /// leaves that tool alone.
    /// </summary>
    private void OnCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag ||
            !Enum.TryParse(tag, out EditorToolCategory category))
            return;
        if (!IsLoaded || _syncingCategory) { _category = category; return; }

        _category = category;
        ShowCategoryRow(category);
        CloseToolbarPopups();

        if (EditorShellContract.CategoryOf(_tool) == category)
            return;
        if (EditorShellContract.DefaultToolFor(category) is EditorTool armed)
            CheckToolButton(armed);
        else
            UpdateContextPanels();
    }

    /// <summary>Lights the category that owns the active tool, without re-arming a tool.</summary>
    private void SyncCategoryToTool()
    {
        if (EditorShellContract.CategoryOf(_tool) is not EditorToolCategory category)
            return;
        _category = category;
        _syncingCategory = true;
        try
        {
            if (CategoryButton(category) is { IsChecked: not true } button)
                button.IsChecked = true;
        }
        finally
        {
            _syncingCategory = false;
        }
        ShowCategoryRow(category);
    }

    /// <summary>
    /// Swaps the sub-row to one category's tools. The row floats over the canvas, so the
    /// swap is a 6 DIP drop + fade rather than a cut — and the image never moves.
    /// </summary>
    private void ShowCategoryRow(EditorToolCategory category)
    {
        Panel? target = RowFor(category);
        if (target is null) return;

        bool wasHidden = CategoryToolRow.Visibility != Visibility.Visible;
        bool changed = wasHidden;
        foreach (Panel row in CategoryRows())
        {
            bool show = ReferenceEquals(row, target);
            if (show && row.Visibility != Visibility.Visible) changed = true;
            row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        CategoryToolRow.Visibility = Visibility.Visible;
        if (changed)
            CategoryToolRow.BeginStoryboard((Storyboard)FindResource("SubRowIn"));
    }

    /// <summary>Shows the active tool's own controls, and hides the row entirely when it has none.</summary>
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
        RedactStylePanel.Visibility = Show(Has(EditorContextControls.RedactStyle));
        // Black Out has no strength, so the control leaves rather than greying out.
        RedactStrengthPanel.Visibility = Show(
            Has(EditorContextControls.RedactStrength) && Redaction.UsesStrength(_redactStyle));
        TextStylePanel.Visibility = Show(Has(EditorContextControls.TextStyle));
        CropRatioPanel.Visibility = Show(Has(EditorContextControls.CropRatio));
        CropToolsPanel.Visibility = Show(Has(EditorContextControls.CropTools));
        StepModePanel.Visibility = Show(Has(EditorContextControls.StepMode));
        if (Has(EditorContextControls.Thickness))
            SyncSizeField();

        // Colour and opacity are per-tool too, so both follow the active tool.
        if (Has(EditorContextControls.Color))
            SyncColorFromTool();

        if (Has(EditorContextControls.RedactStrength))
            SyncRedactStrengthSlider();

        if (Has(EditorContextControls.CropTools))
            SyncImageSizeLabel();

        ContextPanel.Visibility = controls == EditorContextControls.None
            ? Visibility.Collapsed
            : Visibility.Visible;

        // The highest-frequency interaction in the app used to hard-cut nine panels in and
        // out. Fading up from mostly-there reads as the row re-labelling itself rather than
        // blinking; a full 0 -> 1 fade would read as a flash on every tool click.
        if (IsLoaded && controls != _shownContext && controls != EditorContextControls.None)
            ContextPanel.BeginStoryboard((Storyboard)FindResource("ContextSwapIn"));
        _shownContext = controls;
    }

    /// <summary>
    /// Reflects the active tool's remembered size in the size field and its preset menu.
    /// Each tool keeps its own value (<see cref="ToolPreferences"/>), so switching from a
    /// 20-wide highlighter to the pen restores 3 rather than carrying 20 across.
    /// </summary>
    private void SyncSizeField()
    {
        _thickness = _toolPrefs.For(_tool).Width;

        _syncingThicknessButtons = true;
        try
        {
            // The field lost its "Width" / "Size" caption when the toolbar dropped its
            // labels; the word now lives in the preset menu's heading, the tooltip and
            // the accessible name, which is where it was always doing the real work.
            string label = AnnotationSize.LabelFor(_tool);
            SizePresetHeading.Text = label;
            SizeBox.Text = ((int)_thickness).ToString(CultureInfo.InvariantCulture);
            SizeFieldBorder.ToolTip = label;
            AutomationProperties.SetName(SizeBox, $"Annotation {label.ToLowerInvariant()}");
            SyncSizePresetRows();
        }
        finally
        {
            _syncingThicknessButtons = false;
        }
    }

    /// <summary>
    /// Builds the preset menu once, then keeps the checked row in sync. Each row draws a bar
    /// at the preset's real relative weight so the list reads as thicknesses, not numbers.
    /// </summary>
    private void SyncSizePresetRows()
    {
        // TextChanged fires while XAML is still parsing (the size box has a literal Text),
        // which is before the popup's contents exist.
        if (SizePresetList is null) return;

        if (SizePresetList.Children.Count == 0)
        {
            foreach (int preset in AnnotationSize.Presets)
            {
                var row = new RadioButton
                {
                    Style = (Style)FindResource("SizePresetRow"),
                    GroupName = "SizePreset",
                    Tag = preset,
                    Content = BuildSizePresetContent(preset),
                };
                row.Click += OnSizePresetPicked;
                AutomationProperties.SetName(row, $"Size {preset}");
                SizePresetList.Children.Add(row);
            }
        }

        foreach (RadioButton row in SizePresetList.Children.OfType<RadioButton>())
            row.IsChecked = row.Tag is int preset && Math.Abs(preset - _thickness) < 0.01;
    }

    private static Grid BuildSizePresetContent(int preset)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = new Border
        {
            Height = AnnotationSize.PreviewThickness(preset),
            CornerRadius = new CornerRadius(999),
            Background = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        // The row's Foreground carries the accent/secondary state, so the bar follows it.
        bar.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(
                System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(RadioButton), 1),
        });
        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);

        var number = new TextBlock
        {
            Text = preset.ToString(CultureInfo.InvariantCulture),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 22,
            TextAlignment = TextAlignment.Right,
        };
        // The last inline FontSize in the app: 12.5 was off both scales, and this is
        // chrome rather than a display numeral, so it belongs on the type scale.
        number.SetResourceReference(TextBlock.FontSizeProperty, "TextBodyLg");
        Grid.SetColumn(number, 1);
        grid.Children.Add(number);

        return grid;
    }

    private void OnSizePresetClick(object sender, RoutedEventArgs e) =>
        SizePresetPopup.IsOpen = !SizePresetPopup.IsOpen;

    private void OnSizePresetPicked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: int preset })
        {
            ApplySize(preset);
            SizePresetPopup.IsOpen = false;
        }
    }

    /// <summary>Digits only — the size field never accepts a decimal point or a sign.</summary>
    private void OnSizeBoxPreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnSizeBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        // Also fires during XAML parse, before the rest of the style bar is built.
        if (!IsLoaded || _syncingThicknessButtons) return;
        // An empty or half-typed box must not snap the size to a default mid-edit.
        if (AnnotationSize.TryParse(SizeBox.Text, out int value))
            ApplySize(value, echoToBox: false);
    }

    private void OnSizeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            CommitSizeBox();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // Up/Down nudge by one, with Shift stepping through the preset list.
        int direction = e.Key switch { Key.Up => 1, Key.Down => -1, _ => 0 };
        if (direction == 0) return;

        int current = (int)_thickness;
        int next;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            next = direction > 0
                ? AnnotationSize.Presets.FirstOrDefault(p => p > current, AnnotationSize.MaxWidth)
                : AnnotationSize.Presets.LastOrDefault(p => p < current, AnnotationSize.MinWidth);
        }
        else
        {
            next = current + direction;
        }

        ApplySize(next);
        SizeBox.CaretIndex = SizeBox.Text.Length;
        e.Handled = true;
    }

    private void OnSizeBoxLostFocus(object sender, RoutedEventArgs e) => CommitSizeBox();

    /// <summary>Normalises whatever is left in the box back to the committed value.</summary>
    private void CommitSizeBox()
    {
        if (AnnotationSize.TryParse(SizeBox.Text, out int value))
            ApplySize(value);
        else
            SyncSizeField(); // junk or empty: put the remembered value back
    }

    /// <summary>
    /// Stores a size against the ACTIVE tool and restyles the selection to match.
    /// </summary>
    private void ApplySize(int value, bool echoToBox = true)
    {
        // Restyling the selection can bounce back through the style bar and re-enter here.
        // Without this guard that loop is unbounded and takes the process down with it.
        if (_applyingSize) return;

        int clamped = AnnotationSize.Clamp(value);
        _toolPrefs.Update(_tool, s => s.WithWidth(clamped));
        _thickness = clamped;

        _applyingSize = true;
        _syncingThicknessButtons = true;
        try
        {
            if (echoToBox && SizeBox is not null)
                SizeBox.Text = clamped.ToString(CultureInfo.InvariantCulture);
            SyncSizePresetRows();
            _syncingThicknessButtons = false;

            if (IsLoaded)
                RestyleSelected(color: null, thickness: clamped, fill: null);
        }
        finally
        {
            _syncingThicknessButtons = false;
            _applyingSize = false;
        }
    }

    /// <summary>Puts the current strength on the slider without treating it as a user edit.</summary>
    private void SyncRedactStrengthSlider()
    {
        if (RedactStrengthSlider is null) return;
        _syncingThicknessButtons = true;
        try
        {
            RedactStrengthSlider.Value = _redactStrength;
            RedactStrengthValue.Text = _redactStrength.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncingThicknessButtons = false;
        }
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

    /// <summary>
    /// Arms a tool that has no button of its own — today only CurvedArrow, which is the
    /// Curved row of the arrow-style menu. The Arrow button stays lit, because to the user
    /// this is still the arrow tool wearing a different style.
    /// </summary>
    private void SetToolWithoutButton(EditorTool tool)
    {
        CommitText();
        CommitPendingCurve();
        _tool = tool;
        UpdateCursor();
        UpdateContextPanels();
    }

    /// <summary>
    /// Pulls the active tool's remembered colour and opacity into the style bar without
    /// restyling anything — this runs on a tool switch, not on a user edit.
    /// </summary>
    private void SyncColorFromTool()
    {
        ToolStyle style = _toolPrefs.For(_tool);
        _color = style.BorderColor;
        _opacity = style.BorderAlpha;

        if (CurrentColorIndicator is not null)
            CurrentColorIndicator.Background = new SolidColorBrush(style.BorderColor);

        if (OpacitySlider is not null)
        {
            _syncingThicknessButtons = true;
            try
            {
                OpacitySlider.Value = style.BorderOpacity;
                if (OpacityValue is not null)
                    OpacityValue.Text = $"{style.BorderOpacity}%";
            }
            finally
            {
                _syncingThicknessButtons = false;
            }
        }
    }

    private void SetCurrentColor(Color color)
    {
        _color = color;
        // Colour belongs to the active tool, so the pen stays red when the highlighter turns blue.
        _toolPrefs.Update(_tool, s => s.WithBorderColor(color));
        // Fires during XAML parse for the default swatch, before the indicator exists.
        if (CurrentColorIndicator is not null)
            CurrentColorIndicator.Background = new SolidColorBrush(color);
        if (IsLoaded)
            RestyleSelected(color: color, thickness: null, fill: null);
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

    /// <summary>
    /// Cycles the outline: solid → dashed → dotted → solid. One button rather than three,
    /// because there are only three states and the button's own look says which one it is;
    /// three radio chips for a property this small would cost more toolbar than it earns.
    /// </summary>
    private void OnDashClick(object sender, RoutedEventArgs e)
    {
        _strokeDash = _strokeDash switch
        {
            StrokeDash.Solid => StrokeDash.Dashed,
            StrokeDash.Dashed => StrokeDash.Dotted,
            _ => StrokeDash.Solid,
        };
        SyncStrokeStyleButtons();
        ApplyStrokeStyleToSelection();
    }

    private void OnSketchClick(object sender, RoutedEventArgs e)
    {
        _strokeRender = _strokeRender == StrokeRender.Sketchy ? StrokeRender.Clean : StrokeRender.Sketchy;
        SyncStrokeStyleButtons();
        ApplyStrokeStyleToSelection();
    }

    private void SyncStrokeStyleButtons()
    {
        if (DashToolBtn is null || SketchToolBtn is null) return;
        DashToolBtn.IsChecked = _strokeDash != StrokeDash.Solid;
        DashToolBtn.ToolTip = _strokeDash switch
        {
            StrokeDash.Dashed => "Dashed outline (again for dotted)",
            StrokeDash.Dotted => "Dotted outline (again for solid)",
            _ => "Dashed outline (again for dotted)",
        };
        SketchToolBtn.IsChecked = _strokeRender == StrokeRender.Sketchy;
    }

    /// <summary>
    /// Re-draws the selected shape in the current outline style. A dash change restyles the
    /// element in place; changing the line quality has to swap the element, because clean
    /// and sketchy are two different geometries and a Rectangle cannot become a Path. The
    /// swap keeps size, position, angle and z-order, and is one undo step either way.
    /// </summary>
    private void ApplyStrokeStyleToSelection()
    {
        if (!IsLoaded) return;
        if (_selected is not Shape old || old.Tag is not AnnotationData meta) return;
        if (meta.Type is not (AnnotationData.TypeRectangle or AnnotationData.TypeEllipse)) return;

        var before = meta.Clone();
        var after = meta.Clone();
        after.Dash = _strokeDash.ToString();
        after.Sketch = _strokeRender.ToString();

        if (StrokeStyles.ParseRender(before.Sketch) == _strokeRender)
        {
            if (StrokeStyles.ParseDash(before.Dash) == _strokeDash) return;
            Push(new EditorAction(
                undo: () => ApplyDashData(old, before),
                redo: () => ApplyDashData(old, after)),
                apply: false);
            ApplyDashData(old, after);
            return;
        }

        after.Seed = meta.Seed ?? Random.Shared.Next();
        Shape next = BuildRestyledShape(old, after);
        int z = AnnotationCanvas.Children.IndexOf(old);
        if (z < 0) return;

        Push(new EditorAction(
            undo: () => SwapShape(next, old, z),
            redo: () => SwapShape(old, next, z)),
            apply: false);
        SwapShape(old, next, z);
    }

    private static void ApplyDashData(Shape shape, AnnotationData meta)
    {
        AnnotationFactory.ApplyDash(shape, StrokeStyles.ParseDash(meta.Dash));
        shape.Tag = meta;
    }

    /// <summary>The same mark in the other line quality: everything but the geometry is copied.</summary>
    private static Shape BuildRestyledShape(Shape from, AnnotationData meta)
    {
        var shape = AnnotationFactory.CreateShape(
            meta.Type == AnnotationData.TypeEllipse,
            StrokeStyles.ParseRender(meta.Sketch),
            meta.Seed ?? 0);
        shape.Stroke = from.Stroke;
        shape.StrokeThickness = from.StrokeThickness;
        shape.Fill = from.Fill;
        shape.Opacity = from.Opacity;
        AnnotationFactory.ApplyDash(shape, StrokeStyles.ParseDash(meta.Dash));
        shape.Width = from.Width;
        shape.Height = from.Height;
        Canvas.SetLeft(shape, Canvas.GetLeft(from));
        Canvas.SetTop(shape, Canvas.GetTop(from));
        shape.RenderTransform = from.RenderTransform;
        shape.RenderTransformOrigin = from.RenderTransformOrigin;
        shape.Tag = meta;
        return shape;
    }

    /// <summary>
    /// Puts <paramref name="next"/> where <paramref name="old"/> was, at the same z-index,
    /// and moves the selection with it — a mark that vanished from under the handles after
    /// its own restyle would read as a delete.
    /// </summary>
    private void SwapShape(Shape old, Shape next, int z)
    {
        int at = AnnotationCanvas.Children.IndexOf(old);
        if (at >= 0) AnnotationCanvas.Children.RemoveAt(at);
        int insert = Math.Clamp(at >= 0 ? at : z, 0, AnnotationCanvas.Children.Count);
        AnnotationCanvas.Children.Insert(insert, next);
        if (ReferenceEquals(_selected, old))
        {
            _selected = next;
            UpdateSelectionVisual();
        }
    }

}
