using System.Globalization;
using System.Windows;
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

    private static EditorWindow? _prewarmInstance;

    private readonly SettingsService _settings;
    private readonly HistoryService _history;

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
    private int _nextStep = 1;
    private ShapeFillMode _fillMode = ShapeFillMode.None;
    private TextStyle _textStyle = TextStyle.Plain;
    private double? _cropRatio; // null = free
    private EditorTool _toolBeforeEyedropper = EditorTool.Select;
    private string _pendingEmoji = "😀";
    private bool _emojiPaletteBuilt;

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
    private Point _dragStart;
    private Shape? _activeShape;
    private TextBox? _activeText;
    private SD.Rectangle? _pendingCrop;

    // Selection state (Select tool).
    private UIElement? _selected;
    private bool _movingSelection;
    private Point _moveLast;   // content coords
    private Vector _moveTotal; // accumulated drag delta for the undo record
    private bool _replenishPrewarmOnClose;

    public EditorWindow(SD.Bitmap source, SettingsService settings, HistoryService history)
        : this(source, settings, history, loadSourceImage: true)
    {
    }

    private EditorWindow(SD.Bitmap source, SettingsService settings, HistoryService history, bool loadSourceImage)
    {
        ThemeResources.EnsureLoaded();
        InitializeComponent();
        _source = source;
        _owned.Add(source);
        _settings = settings;
        _history = history;

        SetSurfaceSize(source.Width, source.Height);
        if (loadSourceImage)
        {
            _sourceOperationActive = true;
            Cursor = Cursors.Wait;
            ContentRendered += OnInitialContentRendered;
        }
        else
        {
            _sourceOperationActive = false;
        }
        ContentRendered += OnChromeContentRendered;

        // The viewport zooms/pans instead of the window sizing itself to the
        // image, so a sensible default footprint is enough; the image is
        // fitted and centered once layout is known.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1240, wa.Width * 0.9);
        Height = Math.Min(800, wa.Height * 0.9);

        Viewport.LostMouseCapture += (_, _) =>
        {
            AbortDrag();
            AbortPan();
            AbortMove();
            AbortCurveHandle();
        };
        Loaded += (_, _) => FitToView();
        Deactivated += (_, _) =>
        {
            _spaceDown = false;
            if (!_panning) UpdateCursor();
        };
        Closed += (_, _) =>
        {
            foreach (var bmp in _owned) bmp.Dispose();
            _owned.Clear();
            MemoryCleanup.Request();
            if (_replenishPrewarmOnClose)
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => Prewarm(_settings, _history)));
        };
        UpdateCursor();
        DarkTitleBar.Apply(this);
    }

    private void OnInitialContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnInitialContentRendered;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => _ = LoadInitialSourceImageAsync()));
    }

    private void OnChromeContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnChromeContentRendered;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                EditorStyleBar.Visibility = Visibility.Visible;
                EditorBottomBar.Visibility = Visibility.Visible;
            }));
    }

    public static void Prewarm(SettingsService settings, HistoryService history)
    {
        try
        {
            if (_prewarmInstance is not null)
                return;

            var bitmap = new SD.Bitmap(1, 1);
            var window = new EditorWindow(bitmap, settings, history, loadSourceImage: false)
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            window.Closed += (_, _) => _prewarmInstance = null;
            _prewarmInstance = window;
            window.Show();
            FlushPrewarmRender(window.Dispatcher);
            window.Hide();
        }
        catch (Exception ex)
        {
            _prewarmInstance = null;
            Log.Error("Editor prewarm failed", ex);
        }
    }

    public static EditorWindow CreateForCapture(SD.Bitmap source, SettingsService settings, HistoryService history)
    {
        if (_prewarmInstance is { } window && ReferenceEquals(window._settings, settings) && ReferenceEquals(window._history, history))
        {
            _prewarmInstance = null;
            window.ResetForSource(source);
            window._replenishPrewarmOnClose = false;
            return window;
        }

        return new EditorWindow(source, settings, history);
    }

    private void ResetForSource(SD.Bitmap source)
    {
        foreach (var bmp in _owned) bmp.Dispose();
        _owned.Clear();
        _source = source;
        _owned.Add(source);

        _undoStack.Clear();
        _redoStack.Clear();
        _projectPath = null;
        _selected = null;
        _activeShape = null;
        _activeText = null;
        _pendingCurve = null;
        _pendingCrop = null;
        _dragging = false;
        _panning = false;
        _movingSelection = false;
        _sourceOperationActive = true;

        BaseImage.Source = null;
        AnnotationCanvas.Children.Clear();
        InteractionCanvas.Children.Clear();
        InteractionCanvas.Children.Add(CropDim);
        InteractionCanvas.Children.Add(DragRect);
        InteractionCanvas.Children.Add(SelectionRect);
        InteractionCanvas.Children.Add(CurveHandle);
        InteractionCanvas.Children.Add(EyedropSwatch);
        CropDim.Visibility = Visibility.Collapsed;
        DragRect.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Collapsed;
        CurveHandle.Visibility = Visibility.Collapsed;
        EyedropSwatch.Visibility = Visibility.Collapsed;
        CropPanel.Visibility = Visibility.Collapsed;
        TextStylePanel.Visibility = Visibility.Collapsed;
        CropRatioPanel.Visibility = Visibility.Collapsed;

        Width = Math.Min(1240, SystemParameters.WorkArea.Width * 0.9);
        Height = Math.Min(800, SystemParameters.WorkArea.Height * 0.9);
        CenterOnWorkArea();
        Opacity = 1;
        ShowInTaskbar = true;
        ShowActivated = true;
        WindowState = WindowState.Normal;
        Cursor = Cursors.Wait;
        SetSurfaceSize(source.Width, source.Height);
        FitToView();
        UpdateUndoRedoButtons();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => _ = LoadInitialSourceImageAsync()));
    }

    private void CenterOnWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        Left = Math.Round(wa.Left + Math.Max(0, (wa.Width - Width) / 2));
        Top = Math.Round(wa.Top + Math.Max(0, (wa.Height - Height) / 2));
    }

    private static void FlushPrewarmRender(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private async Task LoadInitialSourceImageAsync()
    {
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
            _sourceOperationActive = false;
            UpdateCursor();
        }
    }

    private async Task RefreshImageAsync()
    {
        var source = await CaptureService.ToBitmapSourceSnapshotAsync(_source);
        await Dispatcher.InvokeAsync(() => BaseImage.Source = source);
    }

    private void SetSurfaceSize(double w, double h)
    {
        EditorSurface.Width = w;
        EditorSurface.Height = h;
        CanvasHost.Width = w;
        CanvasHost.Height = h;
        BaseImage.Width = w;
        BaseImage.Height = h;
        AnnotationCanvas.Width = w;
        AnnotationCanvas.Height = h;
        InteractionCanvas.Width = w;
        InteractionCanvas.Height = h;
    }

    // --------------------------------------------------------- view (zoom/pan)

    /// <summary>Fits the image to the viewport (never above 100%) and centers it. Ctrl+0 / Center button.</summary>
    private void FitToView()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1 || _source.Width < 1 || _source.Height < 1) return;

        const double margin = 24;
        double fit = Math.Min((vw - margin * 2) / _source.Width, (vh - margin * 2) / _source.Height);
        _zoom = Math.Clamp(Math.Min(fit, 1.0), MinZoom, MaxZoom);
        ViewScale.ScaleX = ViewScale.ScaleY = _zoom;
        ViewTranslate.X = Math.Round((vw - _source.Width * _zoom) / 2);
        ViewTranslate.Y = Math.Round((vh - _source.Height * _zoom) / 2);
        OnViewChanged();
    }

    /// <summary>Zooms so the content point under <paramref name="anchor"/> (viewport coords) stays put.</summary>
    private void ZoomAt(Point anchor, double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.00001) return;

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
        // Plain wheel and Ctrl+wheel both zoom around the cursor.
        ZoomAt(e.GetPosition(Viewport), _zoom * Math.Pow(1.2, e.Delta / 120.0));
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
        if (IsLoaded)
        {
            UpdateCursor();
            UpdateContextPanels();
        }
    }

    /// <summary>Shows the crop-ratio and text-style dropdowns only while their tool is active.</summary>
    private void UpdateContextPanels()
    {
        CropRatioPanel.Visibility = _tool == EditorTool.Crop ? Visibility.Visible : Visibility.Collapsed;
        TextStylePanel.Visibility = _tool == EditorTool.Text ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Re-checks the toolbar radio for a tool (used by the eyedropper to restore the prior tool).</summary>
    private void CheckToolButton(EditorTool tool)
    {
        foreach (var child in ToolPanel.Children)
        {
            if (child is RadioButton rb && rb.Tag is string tag &&
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
    }

    private void OnThicknessChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string s && double.TryParse(s, out double t))
            _thickness = t;
    }

    private void OnFillChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && Enum.TryParse(tag, out ShapeFillMode mode))
            _fillMode = mode;
    }

    private void OnTextStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag && Enum.TryParse(tag, out TextStyle style))
            _textStyle = style;
    }

    private void OnCropRatioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item)
            return;
        if (item.Tag is not string tag || string.IsNullOrEmpty(tag))
        {
            _cropRatio = null;
            return;
        }
        string[] parts = tag.Split(':');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
            w > 0 && h > 0)
        {
            _cropRatio = w / h;
        }
    }

    private void OnEmojiButtonClick(object sender, RoutedEventArgs e)
    {
        BuildEmojiPalette();
        EmojiPopup.IsOpen = true;
    }

    private void BuildEmojiPalette()
    {
        if (_emojiPaletteBuilt)
            return;

        _emojiPaletteBuilt = true;
        var style = (Style)FindResource("EmojiButton");
        string[] emojis =
        [
            "\U0001F600", "\U0001F605", "\U0001F602", "\U0001F60D", "\U0001F914", "\U0001F60E",
            "\U0001F622", "\U0001F621", "\U0001F44D", "\U0001F44E", "\U0001F44F", "\U0001F64F",
            "\U0001F440", "\U0001F4AA", "\u2764\uFE0F", "\U0001F525", "\u2B50", "\U0001F389",
            "\u2705", "\u274C", "\u26A0\uFE0F", "\u2757", "\U0001F4A1", "\U0001F680",
        ];

        foreach (string emoji in emojis)
        {
            var button = new Button
            {
                Style = style,
                Content = emoji,
            };
            button.Click += OnEmojiPicked;
            EmojiGrid.Children.Add(button);
        }
    }

    private void OnEmojiPicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Content is string emoji)
            _pendingEmoji = emoji;
        EmojiPopup.IsOpen = false;
        if (EmojiToolBtn.IsChecked != true)
            EmojiToolBtn.IsChecked = true;
    }

    // ------------------------------------------------------------ mouse input

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_sourceOperationActive) return;
        bool panGesture = e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && (_spaceDown || _tool == EditorTool.Pan));
        if (panGesture)
        {
            if (!_panning && !_dragging && !_movingSelection)
            {
                StartPan(e.GetPosition(Viewport));
                e.Handled = true;
            }
            return;
        }
        if (e.ChangedButton != MouseButton.Left || _panning) return;

        // GetPosition against the content element inverts the view transform,
        // so every tool works in content (source-pixel) coordinates.
        var pos = e.GetPosition(AnnotationCanvas);

        // A pending curved arrow eats this click: on its handle → start bending,
        // anywhere else → commit it (and fall through, e.g. to start a new curve).
        if (_pendingCurve is not null)
        {
            if ((pos - _curveControl).Length <= 9 / _zoom)
            {
                _draggingCurveHandle = true;
                Viewport.CaptureMouse();
                e.Handled = true;
                return;
            }
            CommitPendingCurve();
        }

        if (_tool == EditorTool.Select)
        {
            SelectMouseDown(pos, e);
            return;
        }
        DrawMouseDown(pos, e);
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_sourceOperationActive) return;
        if (_panning)
        {
            var p = e.GetPosition(Viewport);
            ViewTranslate.X += p.X - _panLast.X;
            ViewTranslate.Y += p.Y - _panLast.Y;
            _panLast = p;
            return;
        }
        if (_draggingCurveHandle && _pendingCurve is not null)
        {
            _curveControl = e.GetPosition(AnnotationCanvas);
            _pendingCurve.Data = AnnotationFactory.CurvedArrowGeometry(
                _curveFrom, _curveControl, _curveTo, _curveThickness);
            UpdateCurveHandleVisual();
            return;
        }
        if (_movingSelection && _selected is not null)
        {
            var posMove = e.GetPosition(AnnotationCanvas);
            var d = posMove - _moveLast;
            if (d.X != 0 || d.Y != 0)
            {
                MoveElement(_selected, d.X, d.Y);
                _moveTotal += d;
                _moveLast = posMove;
                UpdateSelectionVisual();
            }
            return;
        }
        if (_tool == EditorTool.Eyedropper && !_dragging)
        {
            UpdateEyedropperSwatch(e.GetPosition(AnnotationCanvas));
            return;
        }
        if (!_dragging) return;
        var pos = e.GetPosition(AnnotationCanvas);

        switch (_tool)
        {
            case EditorTool.Arrow when _activeShape is Path arrow:
                arrow.Data = AnnotationFactory.ArrowGeometry(_dragStart, pos, _thickness);
                break;
            case EditorTool.CurvedArrow when _activeShape is Path curve:
                curve.Data = AnnotationFactory.CurvedArrowGeometry(
                    _dragStart, AnnotationFactory.DefaultCurveControl(_dragStart, pos), pos, _thickness);
                break;
            case EditorTool.Line when _activeShape is Line line:
                line.X2 = pos.X;
                line.Y2 = pos.Y;
                break;
            case EditorTool.Rectangle or EditorTool.Ellipse when _activeShape is not null:
                var r = new Rect(_dragStart, pos);
                Canvas.SetLeft(_activeShape, r.X);
                Canvas.SetTop(_activeShape, r.Y);
                _activeShape.Width = r.Width;
                _activeShape.Height = r.Height;
                break;
            case EditorTool.Freehand or EditorTool.Highlighter when _activeShape is Polyline stroke:
                if (stroke.Points.Count == 0 || (pos - stroke.Points[^1]).Length > 1.2)
                    stroke.Points.Add(pos);
                break;
            case EditorTool.Blur:
            case EditorTool.Pixelate:
                ShowDragRect(new Rect(_dragStart, ClampToSurface(pos)), dim: false);
                break;
            case EditorTool.Spotlight:
                ShowDragRect(new Rect(_dragStart, ClampToSurface(pos)), dim: true);
                break;
            case EditorTool.Crop:
                ShowDragRect(CropSelectionRect(pos), dim: true);
                break;
        }
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_sourceOperationActive) return;
        if (_panning && e.ChangedButton is MouseButton.Middle or MouseButton.Left)
        {
            EndPan();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (_draggingCurveHandle)
        {
            // The handle stays live (and re-draggable) until a click elsewhere commits.
            _draggingCurveHandle = false;
            Viewport.ReleaseMouseCapture();
            return;
        }
        if (_movingSelection)
        {
            EndMove();
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        var pos = e.GetPosition(AnnotationCanvas);
        var shape = _activeShape;
        _activeShape = null;
        Viewport.ReleaseMouseCapture();

        switch (_tool)
        {
            case EditorTool.Arrow or EditorTool.Line:
                if (shape is null) return;
                if ((pos - _dragStart).Length < 3)
                {
                    AnnotationCanvas.Children.Remove(shape);
                }
                else
                {
                    shape.Tag = AnnotationData.ForStroke(
                        _tool == EditorTool.Arrow ? AnnotationData.TypeArrow : AnnotationData.TypeLine,
                        new[] { _dragStart, pos }, StrokeColorOf(shape), shape.StrokeThickness);
                    PushAddElement(shape);
                }
                return;
            case EditorTool.CurvedArrow:
                if (shape is not Path curve) return;
                if ((pos - _dragStart).Length < 3) AnnotationCanvas.Children.Remove(curve);
                else BeginCurveEdit(curve, _dragStart, pos);
                return;
            case EditorTool.Rectangle or EditorTool.Ellipse:
                if (shape is null) return;
                var r = new Rect(_dragStart, pos);
                if (r.Width < 3 && r.Height < 3)
                {
                    AnnotationCanvas.Children.Remove(shape);
                }
                else
                {
                    // Prefer the element's own placement (set during the drag); the
                    // mouse-up rect is only a fallback for a release without a move.
                    double bx = Canvas.GetLeft(shape), by = Canvas.GetTop(shape);
                    var bounds = double.IsNaN(bx) || double.IsNaN(by) ||
                                 double.IsNaN(shape.Width) || double.IsNaN(shape.Height)
                        ? r
                        : new Rect(bx, by, shape.Width, shape.Height);
                    shape.Tag = AnnotationData.ForShape(
                        _tool == EditorTool.Rectangle ? AnnotationData.TypeRectangle : AnnotationData.TypeEllipse,
                        bounds, StrokeColorOf(shape), shape.StrokeThickness, _fillMode);
                    PushAddElement(shape);
                }
                return;
            case EditorTool.Freehand or EditorTool.Highlighter:
                if (shape is Polyline stroke && stroke.Points.Count >= 2)
                {
                    stroke.Tag = AnnotationData.ForStroke(
                        _tool == EditorTool.Freehand ? AnnotationData.TypeFreehand : AnnotationData.TypeHighlighter,
                        stroke.Points, StrokeColorOf(stroke), stroke.StrokeThickness);
                    PushAddElement(shape);
                }
                else if (shape is not null) AnnotationCanvas.Children.Remove(shape);
                return;
            case EditorTool.Blur:
                HideDragRect();
                ApplyBlur(ToPixelRect(new Rect(_dragStart, ClampToSurface(pos))));
                return;
            case EditorTool.Pixelate:
                HideDragRect();
                ApplyPixelate(ToPixelRect(new Rect(_dragStart, ClampToSurface(pos))));
                return;
            case EditorTool.Spotlight:
                HideDragRect();
                var hole = new Rect(_dragStart, ClampToSurface(pos));
                if (hole.Width >= 2 && hole.Height >= 2)
                {
                    var spot = AnnotationFactory.CreateSpotlight(
                        new Size(_source.Width, _source.Height), hole);
                    spot.Tag = AnnotationData.ForSpotlight(new Size(_source.Width, _source.Height), hole);
                    AnnotationCanvas.Children.Add(spot);
                    PushAddElement(spot);
                }
                return;
            case EditorTool.Crop:
                var sel = CropSelectionRect(pos);
                var px = ToPixelRect(sel);
                if (px.Width < 2 || px.Height < 2)
                {
                    ClearCropPreview();
                    return;
                }
                _pendingCrop = px;
                ShowDragRect(sel, dim: true);
                CropPanel.Visibility = Visibility.Visible;
                return;
        }
    }

    private void DrawMouseDown(Point pos, MouseButtonEventArgs e)
    {
        bool hadOpenText = _activeText is not null;
        CommitText();
        bool inImage = pos.X >= 0 && pos.Y >= 0 && pos.X <= _source.Width && pos.Y <= _source.Height;

        if (_tool == EditorTool.Text)
        {
            // A click that just committed an open text box should not immediately
            // open another; clicks on the backdrop outside the image place nothing.
            if (!hadOpenText && inImage) PlaceText(pos);
            e.Handled = true;
            return;
        }
        if (_tool == EditorTool.Step)
        {
            if (inImage) PlaceStep(pos);
            e.Handled = true;
            return;
        }
        if (_tool == EditorTool.Emoji)
        {
            if (inImage) PlaceEmoji(pos);
            e.Handled = true;
            return;
        }
        if (_tool == EditorTool.Eyedropper)
        {
            if (inImage) SampleEyedropper(pos);
            e.Handled = true;
            return;
        }

        _dragStart = _tool is EditorTool.Blur or EditorTool.Crop or EditorTool.Pixelate or EditorTool.Spotlight
            ? ClampToSurface(pos) : pos;
        _dragging = true;
        Viewport.CaptureMouse();

        var brush = new SolidColorBrush(_color);
        switch (_tool)
        {
            case EditorTool.Arrow:
                _activeShape = new Path
                {
                    Stroke = brush,
                    Fill = brush,
                    StrokeThickness = _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = AnnotationFactory.ArrowGeometry(_dragStart, _dragStart, _thickness),
                };
                break;
            case EditorTool.CurvedArrow:
                _activeShape = new Path
                {
                    Stroke = brush,
                    Fill = brush,
                    StrokeThickness = _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = AnnotationFactory.CurvedArrowGeometry(_dragStart, _dragStart, _dragStart, _thickness),
                };
                break;
            case EditorTool.Line:
                _activeShape = new Line
                {
                    X1 = pos.X, Y1 = pos.Y, X2 = pos.X, Y2 = pos.Y,
                    Stroke = brush,
                    StrokeThickness = _thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                break;
            case EditorTool.Rectangle:
                _activeShape = new Rectangle
                {
                    Stroke = brush,
                    StrokeThickness = _thickness,
                    RadiusX = 2, RadiusY = 2,
                    Fill = ShapeFillBrush.Create(_fillMode, _color),
                };
                Canvas.SetLeft(_activeShape, pos.X);
                Canvas.SetTop(_activeShape, pos.Y);
                break;
            case EditorTool.Ellipse:
                _activeShape = new Ellipse
                {
                    Stroke = brush,
                    StrokeThickness = _thickness,
                    Fill = ShapeFillBrush.Create(_fillMode, _color),
                };
                Canvas.SetLeft(_activeShape, pos.X);
                Canvas.SetTop(_activeShape, pos.Y);
                break;
            case EditorTool.Freehand:
                _activeShape = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Points = new PointCollection { pos },
                };
                break;
            case EditorTool.Highlighter:
                var highlight = _color;
                highlight.A = 0x59;
                _activeShape = new Polyline
                {
                    Stroke = new SolidColorBrush(highlight),
                    StrokeThickness = Math.Max(12, _thickness * 4),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Points = new PointCollection { pos },
                };
                break;
            case EditorTool.Blur:
            case EditorTool.Pixelate:
                ShowDragRect(new Rect(_dragStart, _dragStart), dim: false);
                break;
            case EditorTool.Spotlight:
                ShowDragRect(new Rect(_dragStart, _dragStart), dim: true);
                break;
            case EditorTool.Crop:
                ClearCropPreview();
                ShowDragRect(new Rect(_dragStart, _dragStart), dim: true);
                break;
        }

        if (_activeShape is not null)
            AnnotationCanvas.Children.Add(_activeShape);
    }

    /// <summary>Stroke color of a committed shape — the project metadata's source of truth.</summary>
    private static Color StrokeColorOf(Shape shape) =>
        shape.Stroke is SolidColorBrush brush ? brush.Color : Colors.White;

    private void AbortDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_activeShape is not null)
        {
            AnnotationCanvas.Children.Remove(_activeShape);
            _activeShape = null;
        }
        if (_pendingCrop is null) HideDragRect();
    }

    private Point ClampToSurface(Point p) =>
        new(Math.Clamp(p.X, 0, _source.Width), Math.Clamp(p.Y, 0, _source.Height));

    private SD.Rectangle ToPixelRect(Rect r)
    {
        var px = new SD.Rectangle(
            (int)Math.Round(r.X), (int)Math.Round(r.Y),
            (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        px.Intersect(new SD.Rectangle(0, 0, _source.Width, _source.Height));
        return px;
    }

    // ------------------------------------------------------- crop constraints

    /// <summary>
    /// Crop selection from the drag anchor to <paramref name="pos"/>: constrained to
    /// the preset ratio when one is active, and with edges snapped to the image.
    /// </summary>
    private Rect CropSelectionRect(Point pos) =>
        CropSelectionLayout.Calculate(
            new Size(_source.Width, _source.Height),
            _dragStart,
            pos,
            _cropRatio,
            CropSnapPx);

    // ------------------------------------------------------ curved arrow tool

    /// <summary>Enters the pending state: the released arrow shows a draggable control-point handle.</summary>
    private void BeginCurveEdit(Path curve, Point from, Point to)
    {
        _pendingCurve = curve;
        _curveFrom = from;
        _curveTo = to;
        _curveControl = AnnotationFactory.DefaultCurveControl(from, to);
        _curveThickness = _thickness;
        UpdateCurveHandleVisual();
    }

    /// <summary>Commits the pending curved arrow as a normal selectable annotation (one undo entry).</summary>
    private void CommitPendingCurve()
    {
        var curve = _pendingCurve;
        if (curve is null) return;
        _pendingCurve = null;
        _draggingCurveHandle = false;
        CurveHandle.Visibility = Visibility.Collapsed;
        curve.Tag = AnnotationData.ForStroke(AnnotationData.TypeCurvedArrow,
            new[] { _curveFrom, _curveControl, _curveTo }, StrokeColorOf(curve), curve.StrokeThickness);
        PushAddElement(curve); // already on the canvas; records add for undo/redo
    }

    private void CancelPendingCurve()
    {
        var curve = _pendingCurve;
        if (curve is null) return;
        _pendingCurve = null;
        _draggingCurveHandle = false;
        CurveHandle.Visibility = Visibility.Collapsed;
        AnnotationCanvas.Children.Remove(curve);
    }

    private void AbortCurveHandle() => _draggingCurveHandle = false;

    /// <summary>Positions the control-point handle; sized in screen px regardless of zoom.</summary>
    private void UpdateCurveHandleVisual()
    {
        if (_pendingCurve is null)
        {
            CurveHandle.Visibility = Visibility.Collapsed;
            return;
        }
        double d = 10 / _zoom;
        CurveHandle.Width = d;
        CurveHandle.Height = d;
        CurveHandle.StrokeThickness = 1.5 / _zoom;
        Canvas.SetLeft(CurveHandle, _curveControl.X - d / 2);
        Canvas.SetTop(CurveHandle, _curveControl.Y - d / 2);
        CurveHandle.Visibility = Visibility.Visible;
    }

    // -------------------------------------------------------- eyedropper tool

    /// <summary>
    /// Samples the SOURCE bitmap (annotations are vector overlays, so the source is
    /// the correct ground truth), makes it the stroke color, and returns to the tool
    /// that was active before the eyedropper.
    /// </summary>
    private void SampleEyedropper(Point pos)
    {
        SetCurrentColor(EyedropperSampler.SampleClamped(_source, pos));
        ClearSwatchSelection(); // a sampled color rarely matches a preset swatch
        EyedropSwatch.Visibility = Visibility.Collapsed;
        CheckToolButton(_toolBeforeEyedropper);
    }

    /// <summary>Hover preview: a small swatch + hex readout near the cursor while the eyedropper is active.</summary>
    private void UpdateEyedropperSwatch(Point pos)
    {
        EyedropperPreview preview = EyedropperSampler.Preview(_source, pos, _zoom);
        if (!preview.Visible)
        {
            EyedropSwatch.Visibility = Visibility.Collapsed;
            return;
        }

        EyedropColorRect.Fill = new SolidColorBrush(preview.Color);
        EyedropHexText.Text = preview.Hex;
        EyedropSwatch.RenderTransform = new ScaleTransform(preview.Scale, preview.Scale);
        Canvas.SetLeft(EyedropSwatch, preview.Left);
        Canvas.SetTop(EyedropSwatch, preview.Top);
        EyedropSwatch.Visibility = Visibility.Visible;
    }

    private void ClearSwatchSelection()
    {
        foreach (var child in ColorPanel.Children)
            if (child is RadioButton rb)
                rb.IsChecked = false;
    }

    // ------------------------------------------------------------ select tool

    private void SelectMouseDown(Point pos, MouseButtonEventArgs e)
    {
        CommitText();
        var hit = HitTestAnnotation(pos);
        if (hit is null)
        {
            Select(null); // clicking empty space (or the backdrop) deselects
            return;
        }
        if (e.ClickCount == 2 && hit is TextBlock label)
        {
            BeginTextReEdit(label);
            e.Handled = true;
            return;
        }
        Select(hit);
        _movingSelection = true;
        _moveLast = pos;
        _moveTotal = new Vector();
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void EndMove()
    {
        if (!_movingSelection) return;
        _movingSelection = false;
        Viewport.ReleaseMouseCapture();
        if (_selected is not null && _moveTotal.Length >= 0.5)
        {
            UIElement el = _selected;
            double dx = _moveTotal.X, dy = _moveTotal.Y;
            // The move was applied live during the drag, so record it without re-applying.
            Push(new EditorAction(
                undo: () => MoveElement(el, -dx, -dy),
                redo: () => MoveElement(el, dx, dy)), apply: false);
        }
        _moveTotal = new Vector();
    }

    private void AbortMove()
    {
        if (!_movingSelection) return;
        _movingSelection = false;
        if (_selected is not null && (_moveTotal.X != 0 || _moveTotal.Y != 0))
            MoveElement(_selected, -_moveTotal.X, -_moveTotal.Y);
        _moveTotal = new Vector();
        UpdateSelectionVisual();
    }

    private void Select(UIElement? element)
    {
        _selected = element;
        UpdateSelectionVisual();
    }

    private void UpdateSelectionVisual()
    {
        if (_selected is null || !AnnotationCanvas.Children.Contains(_selected))
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }
        Rect b = GetCanvasBounds(_selected);
        if (b.IsEmpty || (b.Width < 0.01 && b.Height < 0.01))
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }
        double pad = 3 / _zoom;
        b.Inflate(pad, pad);
        Canvas.SetLeft(SelectionRect, b.X);
        Canvas.SetTop(SelectionRect, b.Y);
        SelectionRect.Width = b.Width;
        SelectionRect.Height = b.Height;
        SelectionRect.Visibility = Visibility.Visible;
    }

    /// <summary>Bounds of an annotation in canvas coordinates, including its own render transform.</summary>
    private Rect GetCanvasBounds(UIElement element)
    {
        Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
        if (bounds.IsEmpty) bounds = new Rect(element.RenderSize);
        return element.TransformToAncestor(AnnotationCanvas).TransformBounds(bounds);
    }

    /// <summary>
    /// Topmost vector annotation at a content point. Exact hit test first, then a
    /// small zoom-aware tolerance so hairline strokes stay clickable when zoomed out.
    /// Blur and crop are baked into the bitmap, so only canvas children qualify.
    /// </summary>
    private UIElement? HitTestAnnotation(Point pos)
    {
        if (DirectAnnotationFor(AnnotationCanvas.InputHitTest(pos) as DependencyObject) is { } direct)
            return direct;

        UIElement? found = null;
        double r = 4 / _zoom;
        VisualTreeHelper.HitTest(
            AnnotationCanvas,
            null,
            result =>
            {
                if (DirectAnnotationFor(result.VisualHit) is { } el)
                {
                    found = el;
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            },
            new GeometryHitTestParameters(new EllipseGeometry(pos, r, r)));
        return found;
    }

    /// <summary>Walks up to the direct AnnotationCanvas child owning a hit visual; null for non-annotations.</summary>
    private UIElement? DirectAnnotationFor(DependencyObject? d)
    {
        while (d is not null)
        {
            var parent = VisualTreeHelper.GetParent(d);
            if (ReferenceEquals(parent, AnnotationCanvas))
                return d is UIElement el && el is not TextBox ? el : null; // open text editor is not selectable
            d = parent;
        }
        return null;
    }

    private void DeleteSelected()
    {
        if (_selected is null || _movingSelection) return;
        UIElement el = _selected;
        Select(null);
        Push(new EditorAction(
            undo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(el))
                    AnnotationCanvas.Children.Add(el);
            },
            redo: () => AnnotationCanvas.Children.Remove(el)));
    }

    /// <summary>Moves an annotation by composing into its TranslateTransform (same channel crop shifting uses).</summary>
    private static void MoveElement(UIElement element, double dx, double dy)
    {
        if (element.RenderTransform is TranslateTransform t)
        {
            t.X += dx;
            t.Y += dy;
        }
        else
        {
            element.RenderTransform = new TranslateTransform(dx, dy);
        }
    }

    // ------------------------------------------------------------- text tool

    private void PlaceText(Point pos)
    {
        var style = _textStyle;
        double fontSize = AnnotationFactory.FontSizeFor(_thickness);
        if (style == TextStyle.Huge) fontSize *= 2.2;
        var tb = AnnotationFactory.CreateTextEditor(new SolidColorBrush(_color), fontSize);
        tb.Tag = style; // CommitText reads the style back when building the label
        // Offset by the editor chrome (1px border + 2px padding) so committed
        // text lands exactly where the user clicked.
        Canvas.SetLeft(tb, pos.X - 3);
        Canvas.SetTop(tb, pos.Y - 3);
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        HookTextEditor(tb);
        Dispatcher.InvokeAsync(() => tb.Focus());
    }

    /// <summary>Reopens a committed text label for editing (Select tool double-click).</summary>
    private void BeginTextReEdit(TextBlock label)
    {
        Select(null);
        double x = Canvas.GetLeft(label), y = Canvas.GetTop(label);
        if (label.RenderTransform is TranslateTransform t)
        {
            x += t.X;
            y += t.Y;
        }

        // Removing the old label is its own undoable step; committing the editor
        // pushes the replacement add, so two Ctrl+Z steps restore the original.
        Push(new EditorAction(
            undo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(label))
                    AnnotationCanvas.Children.Add(label);
            },
            redo: () => AnnotationCanvas.Children.Remove(label)));

        var tb = AnnotationFactory.CreateTextEditor(label.Foreground, label.FontSize);
        // Only TextBlock-backed styles reach re-edit; weight identifies Bold, the
        // (possibly enlarged) font size already carries the Huge look.
        tb.Tag = label.FontWeight == FontWeights.Bold ? TextStyle.Bold : TextStyle.Plain;
        tb.Text = label.Text;
        Canvas.SetLeft(tb, x - 3);
        Canvas.SetTop(tb, y - 3);
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        HookTextEditor(tb);
        Dispatcher.InvokeAsync(() =>
        {
            tb.Focus();
            tb.SelectAll();
        });
    }

    private void HookTextEditor(TextBox tb)
    {
        tb.LostKeyboardFocus += (_, _) => CommitText();
        tb.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter) { CommitText(); ev.Handled = true; }
            else if (ev.Key == Key.Escape) { CancelText(); ev.Handled = true; }
        };
    }

    private void CommitText()
    {
        var tb = _activeText;
        if (tb is null) return;
        _activeText = null; // guards against re-entry from LostKeyboardFocus on removal

        double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
        string text = tb.Text;
        AnnotationCanvas.Children.Remove(tb);
        if (string.IsNullOrWhiteSpace(text)) return;

        var style = tb.Tag is TextStyle s ? s : TextStyle.Plain;
        var label = AnnotationFactory.CreateStyledTextLabel(text, tb.Foreground, tb.FontSize, style);
        Canvas.SetLeft(label, x + 3);
        Canvas.SetTop(label, y + 3);
        label.Tag = AnnotationData.ForText(new Point(x + 3, y + 3), text, style, tb.FontSize,
            tb.Foreground is SolidColorBrush fg ? fg.Color : Colors.White);
        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(label),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(label))
                    AnnotationCanvas.Children.Add(label);
            }));
    }

    private void CancelText()
    {
        var tb = _activeText;
        if (tb is null) return;
        _activeText = null;
        AnnotationCanvas.Children.Remove(tb);
    }

    // ------------------------------------------------------------- step tool

    private void PlaceStep(Point pos)
    {
        int number = _nextStep;
        var badge = AnnotationFactory.CreateStepBadge(number, _color, _thickness);
        double left = pos.X - badge.Width / 2, top = pos.Y - badge.Height / 2;
        Canvas.SetLeft(badge, left);
        Canvas.SetTop(badge, top);
        badge.Tag = AnnotationData.ForStep(new Point(left, top), number, _color, _thickness);
        Push(new EditorAction(
            undo: () =>
            {
                AnnotationCanvas.Children.Remove(badge);
                _nextStep = number;
            },
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(badge))
                    AnnotationCanvas.Children.Add(badge);
                _nextStep = number + 1;
            }));
    }

    // ------------------------------------------------------------ emoji tool

    /// <summary>Drops the picked emoji as a 32px text annotation centered on the click.</summary>
    private void PlaceEmoji(Point pos)
    {
        var label = AnnotationFactory.CreateEmojiLabel(_pendingEmoji);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = pos.X - label.DesiredSize.Width / 2, top = pos.Y - label.DesiredSize.Height / 2;
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        label.Tag = AnnotationData.ForEmoji(new Point(left, top), _pendingEmoji);
        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(label),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(label))
                    AnnotationCanvas.Children.Add(label);
            }));
    }

    // ------------------------------------------------------------------ blur

    private async void ApplyBlur(SD.Rectangle region)
    {
        if (_sourceOperationActive) return;
        region.Intersect(new SD.Rectangle(0, 0, _source.Width, _source.Height));
        if (region.Width < 2 || region.Height < 2) return;

        var backup = _source.Clone(region, _source.PixelFormat);
        _owned.Add(backup);
        var r = region;
        if (!await ApplySourceRegionEffectAsync(
                () => BitmapEffects.Blur(_source, r),
                "Blur failed"))
        {
            if (_owned.Remove(backup)) backup.Dispose();
            return;
        }

        Push(new EditorAction(
            undo: async () =>
            {
                await ApplySourceRegionEffectAsync(
                    () => BitmapEffects.RestoreRegion(_source, backup, r),
                    "Blur undo failed");
            },
            redo: async () =>
            {
                await ApplySourceRegionEffectAsync(
                    () => BitmapEffects.Blur(_source, r),
                    "Blur redo failed");
            },
            onDiscard: () =>
            {
                if (_owned.Remove(backup)) backup.Dispose();
            }), apply: false);
    }

    /// <summary>
    /// Same interaction and undo pattern as blur, but the mosaic gets per-cell random
    /// jitter so the censored text cannot be reconstructed. The seed is captured per
    /// action, which keeps undo → redo byte-identical.
    /// </summary>
    private async void ApplyPixelate(SD.Rectangle region)
    {
        if (_sourceOperationActive) return;
        region.Intersect(new SD.Rectangle(0, 0, _source.Width, _source.Height));
        if (region.Width < 2 || region.Height < 2) return;

        var backup = _source.Clone(region, _source.PixelFormat);
        _owned.Add(backup);
        var r = region;
        int seed = ToolRandom.Next();
        if (!await ApplySourceRegionEffectAsync(
                () => BitmapEffects.PixelateRandomized(_source, r, seed),
                "Pixelate failed"))
        {
            if (_owned.Remove(backup)) backup.Dispose();
            return;
        }

        Push(new EditorAction(
            undo: async () =>
            {
                await ApplySourceRegionEffectAsync(
                    () => BitmapEffects.RestoreRegion(_source, backup, r),
                    "Pixelate undo failed");
            },
            redo: async () =>
            {
                await ApplySourceRegionEffectAsync(
                    () => BitmapEffects.PixelateRandomized(_source, r, seed),
                    "Pixelate redo failed");
            },
            onDiscard: () =>
            {
                if (_owned.Remove(backup)) backup.Dispose();
            }), apply: false);
    }

    private async Task<bool> ApplySourceRegionEffectAsync(Action effect, string logContext)
    {
        _sourceOperationActive = true;
        Cursor = Cursors.Wait;
        try
        {
            await Task.Run(effect);
            await RefreshImageAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(logContext, ex);
            return false;
        }
        finally
        {
            _sourceOperationActive = false;
            UpdateCursor();
        }
    }

    // ------------------------------------------------------------------ crop

    private async void OnApplyCrop(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        if (_pendingCrop is not SD.Rectangle region)
        {
            ClearCropPreview();
            return;
        }
        ClearCropPreview();

        SD.Bitmap before = _source;
        SD.Bitmap after;
        try
        {
            after = CaptureService.Crop(before, region);
        }
        catch (ArgumentException)
        {
            return;
        }
        _owned.Add(after);

        // Apply the crop now, then record the undo entry WITHOUT re-applying (apply:false),
        // exactly like rotate/flip. Pushing with apply:true would run Redo synchronously via
        // GetAwaiter().GetResult() on the UI thread, which awaits Dispatcher.InvokeAsync inside
        // RefreshImageAsync -> the UI thread blocks waiting on itself -> deadlock.
        _source = after;
        await OnSourceReplacedAsync();
        ShiftAnnotations(-region.X, -region.Y);

        Push(new EditorAction(
            undo: async () =>
            {
                _source = before;
                await OnSourceReplacedAsync();
                ShiftAnnotations(region.X, region.Y);
            },
            redo: async () =>
            {
                _source = after;
                await OnSourceReplacedAsync();
                ShiftAnnotations(-region.X, -region.Y);
            }), apply: false);
    }

    private void OnCancelCrop(object sender, RoutedEventArgs e) => ClearCropPreview();

    private async Task OnSourceReplacedAsync()
    {
        _sourceOperationActive = true;
        Cursor = Cursors.Wait;
        try
        {
            await RefreshImageAsync();
        }
        finally
        {
            _sourceOperationActive = false;
            UpdateCursor();
        }
        SetSurfaceSize(_source.Width, _source.Height);
        Select(null);
        FitToView();
    }

    /// <summary>Translates every live annotation so it stays glued to the same image content after a crop.</summary>
    private void ShiftAnnotations(double dx, double dy)
    {
        foreach (UIElement el in AnnotationCanvas.Children)
            MoveElement(el, dx, dy);
    }

    // ------------------------------------------------- rotate / flip / resize

    private void OnRotateCw(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        ApplySourceTransform(SD.RotateFlipType.Rotate90FlipNone);
    }

    private void OnRotateCcw(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        ApplySourceTransform(SD.RotateFlipType.Rotate270FlipNone);
    }

    private void OnFlipHorizontal(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        ApplySourceTransform(SD.RotateFlipType.RotateNoneFlipX);
    }

    private void OnFlipVertical(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        ApplySourceTransform(SD.RotateFlipType.RotateNoneFlipY);
    }

    private void OnResizeImage(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        CommitText();
        CommitPendingCurve();
        var dialog = new ResizeDialog(_source.Width, _source.Height) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        int w = dialog.ResultWidth, h = dialog.ResultHeight;
        if (w == _source.Width && h == _source.Height) return;
        ApplySourceTransform(src => BitmapEffects.Resize(src, w, h));
    }

    private void ApplySourceTransform(SD.RotateFlipType type) =>
        ApplySourceTransform(src => SourceImageTransform.RotateFlip(src, type));

    /// <summary>
    /// Replaces the source bitmap with a transformed copy (rotate/flip/resize) as a
    /// SINGLE compound undo entry. If vector annotations exist, the user confirms and
    /// they are flattened into the image first; undo restores both the previous bitmap
    /// and the live annotations in one step.
    /// </summary>
    private async void ApplySourceTransform(Func<SD.Bitmap, SD.Bitmap> transform)
    {
        if (_sourceOperationActive) return;
        CommitText();
        CommitPendingCurve();
        ClearCropPreview();

        var annotations = AnnotationCanvas.Children.Cast<UIElement>().ToList();
        if (annotations.Count > 0 &&
            MessageBox.Show(this,
                "Your annotations will be flattened into the image before this operation. " +
                "They will no longer be editable as separate objects. Continue?",
                "WinShot", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        Select(null);
        SD.Bitmap before = _source;
        SD.Bitmap flat = annotations.Count > 0 ? Flatten() : before;
        SD.Bitmap after;
        _sourceOperationActive = true;
        Cursor = Cursors.Wait;
        try
        {
            after = await Task.Run(() => transform(flat));
        }
        catch (Exception ex)
        {
            Log.Error("Source transform failed", ex);
            return;
        }
        finally
        {
            _sourceOperationActive = false;
            UpdateCursor();
            if (!ReferenceEquals(flat, before)) flat.Dispose(); // temp flatten only
        }
        _owned.Add(after);

        foreach (var el in annotations)
            AnnotationCanvas.Children.Remove(el);
        _source = after;
        await OnSourceReplacedAsync();

        Push(new EditorAction(
            undo: async () =>
            {
                _source = before;
                foreach (var el in annotations)
                    if (!AnnotationCanvas.Children.Contains(el))
                        AnnotationCanvas.Children.Add(el);
                await OnSourceReplacedAsync();
            },
            redo: async () =>
            {
                foreach (var el in annotations)
                    AnnotationCanvas.Children.Remove(el);
                _source = after;
                await OnSourceReplacedAsync();
            }), apply: false);
    }

    private void ShowDragRect(Rect r, bool dim)
    {
        Canvas.SetLeft(DragRect, r.X);
        Canvas.SetTop(DragRect, r.Y);
        DragRect.Width = r.Width;
        DragRect.Height = r.Height;
        DragRect.Visibility = Visibility.Visible;
        if (dim)
        {
            CropDim.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, _source.Width, _source.Height)),
                new RectangleGeometry(r));
            CropDim.Visibility = Visibility.Visible;
        }
    }

    private void HideDragRect()
    {
        DragRect.Visibility = Visibility.Collapsed;
        CropDim.Visibility = Visibility.Collapsed;
    }

    private void ClearCropPreview()
    {
        _pendingCrop = null;
        HideDragRect();
        CropPanel.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------- undo/redo

    private void Push(EditorAction action, bool apply = true)
    {
        if (apply) action.Redo();
        _undoStack.Push(action);
        DiscardRedoStack();
        UpdateUndoRedoButtons();
    }

    /// <summary>Clears the redo stack, letting each dropped action release any
    /// resources it can no longer replay (e.g. blur/pixelate backup bitmaps).</summary>
    private void DiscardRedoStack()
    {
        while (_redoStack.Count > 0)
            _redoStack.Pop().Discard();
    }

    private void PushAddElement(UIElement element)
    {
        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(element),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(element))
                    AnnotationCanvas.Children.Add(element);
            }), apply: false);
    }

    private async void OnUndo(object sender, RoutedEventArgs e) => await UndoAsync();
    private async void OnRedo(object sender, RoutedEventArgs e) => await RedoAsync();

    private async Task UndoAsync()
    {
        if (_sourceOperationActive || _dragging || _movingSelection || _draggingCurveHandle) return;
        CommitText();
        CommitPendingCurve();
        ClearCropPreview();
        Select(null); // the undone action may remove or reshape the selected element
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        await action.UndoAsync();
        _redoStack.Push(action);
        UpdateUndoRedoButtons();
    }

    private async Task RedoAsync()
    {
        if (_sourceOperationActive || _dragging || _movingSelection || _draggingCurveHandle) return;
        CommitPendingCurve(); // a pending curve is a new edit: it clears redo, like any other
        if (_redoStack.Count == 0) return;
        Select(null);
        var action = _redoStack.Pop();
        await action.RedoAsync();
        _undoStack.Push(action);
        UpdateUndoRedoButtons();
    }

    private void UpdateUndoRedoButtons()
    {
        BtnUndo.IsEnabled = _undoStack.Count > 0;
        BtnRedo.IsEnabled = _redoStack.Count > 0;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Let an open text annotation keep its own keyboard behavior.
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.Space)
        {
            if (!_spaceDown)
            {
                _spaceDown = true;
                UpdateCursor();
            }
            e.Handled = true; // keep Space from clicking a focused toolbar button
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.Z) { _ = UndoAsync(); e.Handled = true; }
            else if (e.Key == Key.Y) { _ = RedoAsync(); e.Handled = true; }
            else if (e.Key is Key.D0 or Key.NumPad0) { FitToView(); e.Handled = true; }
        }
        else if (e.Key == Key.Delete)
        {
            if (_selected is not null) { DeleteSelected(); e.Handled = true; }
        }
        else if (e.Key == Key.Escape)
        {
            if (_movingSelection)
            {
                AbortMove();
                Viewport.ReleaseMouseCapture();
                e.Handled = true;
            }
            else if (_draggingCurveHandle)
            {
                _draggingCurveHandle = false;
                Viewport.ReleaseMouseCapture();
                CancelPendingCurve();
                e.Handled = true;
            }
            else if (_pendingCurve is not null)
            {
                CancelPendingCurve();
                e.Handled = true;
            }
            else if (_selected is not null)
            {
                Select(null);
                e.Handled = true;
            }
            else if (_pendingCrop is not null)
            {
                ClearCropPreview();
                e.Handled = true;
            }
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key != Key.Space || Keyboard.FocusedElement is TextBox) return;
        if (_spaceDown)
        {
            _spaceDown = false;
            if (!_panning) UpdateCursor();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------ image annotations

    /// <summary>"Add image…" toolbar button: inserts picked files at the viewport center.</summary>
    private async void OnAddImage(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        CommitText();
        CommitPendingCurve();
        var dialog = new OpenFileDialog
        {
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        await InsertImageFilesAsync(dialog.FileNames, ViewportCenterInContent());
    }

    private void OnEditorDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnEditorDrop(object sender, DragEventArgs e)
    {
        if (_sourceOperationActive) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;
        CommitText();
        CommitPendingCurve();
        await InsertImageFilesAsync(files, e.GetPosition(AnnotationCanvas));
        e.Handled = true;
    }

    /// <summary>Content-space point currently at the middle of the viewport.</summary>
    private Point ViewportCenterInContent() => new(
        (Viewport.ActualWidth / 2 - ViewTranslate.X) / _zoom,
        (Viewport.ActualHeight / 2 - ViewTranslate.Y) / _zoom);

    /// <summary>
    /// Inserts each decodable file as an image annotation centered at
    /// <paramref name="dropPoint"/> (cascaded slightly for multiple files),
    /// then switches to Select with the last one selected so it can be moved.
    /// </summary>
    private async Task InsertImageFilesAsync(IEnumerable<string> files, Point dropPoint)
    {
        Image? last = null;
        int placed = 0;
        foreach (string file in files.ToList())
        {
            var src = await Task.Run(() => ProjectSerializer.LoadImageFile(file));
            if (src is null) continue; // not an image; already logged
            if (!IsVisible) return;
            last = InsertImageAnnotation(src, new Point(dropPoint.X + placed * 24, dropPoint.Y + placed * 24));
            placed++;
        }
        if (last is null) return;
        CheckToolButton(EditorTool.Select);
        Select(last);
    }

    /// <summary>
    /// Adds one image as a movable/selectable/deletable annotation, centered on a
    /// content point, at natural size capped to 50% of the source image's smaller
    /// dimension. Undo-aware like every other annotation.
    /// </summary>
    private Image InsertImageAnnotation(BitmapSource src, Point center)
    {
        center = ClampToSurface(center);
        double cap = Math.Min(_source.Width, _source.Height) * 0.5;
        double scale = Math.Min(1.0, cap / Math.Max(src.PixelWidth, (double)src.PixelHeight));
        double w = Math.Max(1, src.PixelWidth * scale);
        double h = Math.Max(1, src.PixelHeight * scale);

        var img = new Image { Source = src, Width = w, Height = h, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        double left = center.X - w / 2, top = center.Y - h / 2;
        Canvas.SetLeft(img, left);
        Canvas.SetTop(img, top);
        img.Tag = AnnotationData.ForImage(new Rect(left, top, w, h));

        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(img),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(img))
                    AnnotationCanvas.Children.Add(img);
            }));
        return img;
    }

    // ------------------------------------------------------- copy/save/close

    /// <summary>
    /// Flattens at identity transform: RenderVisual snapshots CanvasHost in its
    /// own coordinate space at the source bitmap's pixel size, so the viewport's
    /// zoom/pan (applied on the EditorSurface ancestor) never affects the output,
    /// and the selection/crop chrome lives on InteractionCanvas outside CanvasHost.
    /// </summary>
    private SD.Bitmap Flatten()
    {
        CommitText();
        CommitPendingCurve();
        CanvasHost.UpdateLayout();
        return BitmapEffects.RenderVisual(CanvasHost, _source.Width, _source.Height);
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        try
        {
            var flat = Flatten();
            await CaptureService.CopyToClipboardAsync(flat, takeOwnership: true);
            if (!IsVisible) return;
            // BtnCopy is a round glyph button — flash a checkmark, then restore the copy glyph.
            BtnCopy.Content = "";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (_, _) => { timer.Stop(); BtnCopy.Content = ""; };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Editor copy failed", ex);
        }
    }

    /// <summary>Bottom-bar zoom preset dropdown: Fit / 50% / 100% / 150% / 200%.</summary>
    private void OnZoomPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is not ComboBoxItem item) return;
        string choice = (item.Content as string) ?? "Fit";
        if (choice == "Fit")
        {
            FitToView();
            return;
        }
        if (double.TryParse(choice.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out double pct)
            && Viewport.ActualWidth > 1 && Viewport.ActualHeight > 1)
        {
            ZoomAt(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), pct / 100.0);
        }
    }

    /// <summary>Pin the flattened result to the screen as a floating always-on-top window.</summary>
    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        try
        {
            // FastPinWindow takes ownership of the bitmap and disposes it on close.
            var pin = new WinShot.Pin.FastPinWindow(Flatten(), _settings);
            WinShot.Pin.FastPinWindow.TrackFirstShown(pin, "editor pin window");
            pin.Show();
        }
        catch (Exception ex)
        {
            Log.Error("Editor pin failed", ex);
        }
    }

    /// <summary>"Drag me" handle: start a file drag-out of the flattened image.</summary>
    private async void OnDragMeDown(object sender, MouseButtonEventArgs e)
    {
        if (_sourceOperationActive) return;
        try
        {
            string dir = TempFileJanitor.WinShotTempDirectory;
            string path = FileNamer.NextUniquePath(_settings, dir, "png");
            var flat = Flatten();
            await Task.Run(() =>
            {
                using (flat)
                {
                    System.IO.Directory.CreateDirectory(dir);
                    TempFileJanitor.DeleteOldFiles(dir, DateTimeOffset.UtcNow, TimeSpan.FromDays(1), maxFilesToDelete: 50);
                    ImageSaver.Save(flat, path);
                }
            });
            if (!IsVisible) return;
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Log.Error("Editor drag-out failed", ex);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        try
        {
            System.IO.Directory.CreateDirectory(_settings.Current.SaveFolder);
            SaveFileDialog dialog;
            if (_projectPath is string proj)
            {
                // A session opened from (or saved as) a project defaults back to that file.
                dialog = new SaveFileDialog
                {
                    FileName = System.IO.Path.GetFileName(proj),
                    InitialDirectory = System.IO.Path.GetDirectoryName(proj) is { Length: > 0 } dir
                        ? dir : _settings.Current.SaveFolder,
                    Filter = SaveDialogFilter,
                    FilterIndex = 4,
                };
            }
            else
            {
                dialog = new SaveFileDialog
                {
                    FileName = FileNamer.Next(_settings, _settings.Current.ImageFormat),
                    InitialDirectory = _settings.Current.SaveFolder,
                    Filter = SaveDialogFilter,
                    FilterIndex = _settings.Current.ImageFormat switch
                    {
                        "jpg" => 2,
                        "webp" => 3,
                        _ => 1,
                    },
                };
            }
            if (dialog.ShowDialog(this) != true) return;

            if (string.Equals(System.IO.Path.GetExtension(dialog.FileName), ".winshot",
                    StringComparison.OrdinalIgnoreCase))
            {
                await SaveProjectAsync(dialog.FileName);
                return;
            }

            var flat = Flatten();
            await Task.Run(() =>
            {
                using (flat)
                {
                    ImageSaver.Save(flat, dialog.FileName);
                    _history.Add(flat);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Editor save failed", ex);
        }
    }

    // ------------------------------------------------------ project (.winshot)

    /// <summary>
    /// Writes the current session to a .winshot project: a ZIP with the source bitmap
    /// (including any baked blur/pixelate/crop), annotations.json describing every
    /// live annotation, and the embedded bitmaps of image annotations. FileMode.Create
    /// inside the serializer means re-saving overwrites the previous file cleanly.
    /// </summary>
    private async Task SaveProjectAsync(string path)
    {
        ProjectSnapshot snapshot = CreateProjectSnapshot();
        try
        {
            await Task.Run(() =>
            {
                using (snapshot.Source)
                    ProjectSerializer.Save(path, snapshot.Source, snapshot.Document, snapshot.Images);
            });
        }
        catch
        {
            snapshot.Source.Dispose();
            throw;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            _projectPath = path;
            Title = $"WinShot Editor — {System.IO.Path.GetFileName(path)}";
        });
    }

    private ProjectSnapshot CreateProjectSnapshot()
    {
        CommitText();
        CommitPendingCurve();

        var doc = new ProjectDocument();
        var images = new List<BitmapSource>();
        int z = 0;
        foreach (UIElement el in AnnotationCanvas.Children)
        {
            if (el is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
                continue; // transient editor visuals are not part of the project
            var data = meta.Clone();
            data.Z = z++;
            if (el.RenderTransform is TranslateTransform t)
            {
                data.Tx = t.X;
                data.Ty = t.Y;
            }
            else
            {
                data.Tx = 0;
                data.Ty = 0;
            }
            if (data.Type == AnnotationData.TypeImage && el is Image img && img.Source is BitmapSource bs)
            {
                data.ImageIndex = images.Count;
                if (bs.CanFreeze && !bs.IsFrozen)
                    bs.Freeze();
                images.Add(bs);
                data.Rect = new[] { Canvas.GetLeft(img), Canvas.GetTop(img), img.Width, img.Height };
            }
            doc.Annotations.Add(data);
        }

        return new ProjectSnapshot((SD.Bitmap)_source.Clone(), doc, images);
    }

    private sealed record ProjectSnapshot(
        SD.Bitmap Source,
        ProjectDocument Document,
        IReadOnlyList<BitmapSource> Images);

    /// <summary>
    /// Reopens a .winshot project file and reconstructs the editing session: the
    /// source bitmap plus every annotation as a live, editable canvas element.
    /// Returns null (after Log.Error) when the file cannot be parsed.
    /// </summary>
    public static EditorWindow? OpenProject(string path, SettingsService settings, HistoryService history)
    {
        SD.Bitmap? source = null;
        try
        {
            var (bitmap, doc, images) = ProjectSerializer.Load(path);
            source = bitmap;

            // Build (and validate) every element before constructing the window so a
            // malformed entry can never leak a half-initialized editor.
            var built = doc.Annotations
                .OrderBy(a => a.Z)
                .Select(a => (Data: a, Element: ProjectSerializer.CreateElement(a, images)))
                .ToList();

            var win = new EditorWindow(bitmap, settings, history);
            source = null; // the window owns the bitmap now (disposed on close)
            foreach (var (data, element) in built)
            {
                if (element is FrameworkElement fe) fe.Tag = data;
                if (data.Tx != 0 || data.Ty != 0)
                    element.RenderTransform = new TranslateTransform(data.Tx, data.Ty);
                win.AnnotationCanvas.Children.Add(element);
                if (data.Type == AnnotationData.TypeStep && data.Number is int n && n >= win._nextStep)
                    win._nextStep = n + 1;
            }
            win._projectPath = path;
            win.Title = $"WinShot Editor — {System.IO.Path.GetFileName(path)}";
            return win;
        }
        catch (Exception ex)
        {
            source?.Dispose();
            Log.Error($"Failed to open WinShot project: {path}", ex);
            return null;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
