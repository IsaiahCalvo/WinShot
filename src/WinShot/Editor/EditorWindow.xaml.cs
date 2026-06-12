using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Editor;

/// <summary>
/// Annotation editor for captured screenshots. The canvas is laid out so one
/// DIP equals one bitmap pixel, which keeps annotation math exact and lets the
/// flattened export match the source resolution regardless of monitor DPI.
/// Owns the source bitmap (callers pass a clone) and disposes it on close.
/// </summary>
public partial class EditorWindow : Window
{
    private readonly SettingsService _settings;
    private readonly HistoryService _history;

    /// <summary>Current source bitmap; swapped out by crop. Everything in _owned is disposed on close.</summary>
    private SD.Bitmap _source;
    private readonly List<SD.Bitmap> _owned = new();

    private readonly Stack<EditorAction> _undoStack = new();
    private readonly Stack<EditorAction> _redoStack = new();

    private EditorTool _tool = EditorTool.Arrow;
    private Color _color = Color.FromRgb(0xFF, 0x3B, 0x30);
    private double _thickness = 4;
    private int _nextStep = 1;

    private bool _dragging;
    private Point _dragStart;
    private Shape? _activeShape;
    private TextBox? _activeText;
    private SD.Rectangle? _pendingCrop;

    public EditorWindow(SD.Bitmap source, SettingsService settings, HistoryService history)
    {
        InitializeComponent();
        _source = source;
        _owned.Add(source);
        _settings = settings;
        _history = history;

        RefreshImage();
        SetSurfaceSize(source.Width, source.Height);

        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Math.Max(900, source.Width + 76), wa.Width * 0.92);
        Height = Math.Min(Math.Max(520, source.Height + 180), wa.Height * 0.92);

        EditorSurface.LostMouseCapture += (_, _) => AbortDrag();
        Closed += (_, _) =>
        {
            foreach (var bmp in _owned) bmp.Dispose();
            _owned.Clear();
        };
    }

    private void RefreshImage() => BaseImage.Source = CaptureService.ToBitmapSource(_source);

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

    // ---------------------------------------------------------------- toolbar

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag || !Enum.TryParse(tag, out EditorTool tool))
            return;
        // Checked also fires while XAML is parsing, before sibling elements exist.
        if (IsLoaded)
        {
            CommitText();
            if (_tool == EditorTool.Crop && tool != EditorTool.Crop)
                ClearCropPreview();
        }
        _tool = tool;
    }

    private void OnColorChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Background is SolidColorBrush brush)
            _color = brush.Color;
    }

    private void OnThicknessChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string s && double.TryParse(s, out double t))
            _thickness = t;
    }

    // ------------------------------------------------------------ mouse input

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(AnnotationCanvas);
        bool hadOpenText = _activeText is not null;
        CommitText();

        if (_tool == EditorTool.Text)
        {
            // A click that just committed an open text box should not immediately open another.
            if (!hadOpenText) PlaceText(pos);
            e.Handled = true;
            return;
        }
        if (_tool == EditorTool.Step)
        {
            PlaceStep(pos);
            e.Handled = true;
            return;
        }

        _dragStart = _tool is EditorTool.Blur or EditorTool.Crop ? ClampToSurface(pos) : pos;
        _dragging = true;
        EditorSurface.CaptureMouse();

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
                    IsHitTestVisible = false,
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
                    IsHitTestVisible = false,
                };
                break;
            case EditorTool.Rectangle:
                _activeShape = new Rectangle
                {
                    Stroke = brush,
                    StrokeThickness = _thickness,
                    RadiusX = 2, RadiusY = 2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(_activeShape, pos.X);
                Canvas.SetTop(_activeShape, pos.Y);
                break;
            case EditorTool.Ellipse:
                _activeShape = new Ellipse
                {
                    Stroke = brush,
                    StrokeThickness = _thickness,
                    IsHitTestVisible = false,
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
                    IsHitTestVisible = false,
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
                    IsHitTestVisible = false,
                };
                break;
            case EditorTool.Blur:
                ShowDragRect(new Rect(_dragStart, _dragStart), dim: false);
                break;
            case EditorTool.Crop:
                ClearCropPreview();
                ShowDragRect(new Rect(_dragStart, _dragStart), dim: true);
                break;
        }

        if (_activeShape is not null)
            AnnotationCanvas.Children.Add(_activeShape);
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetPosition(AnnotationCanvas);

        switch (_tool)
        {
            case EditorTool.Arrow when _activeShape is Path arrow:
                arrow.Data = AnnotationFactory.ArrowGeometry(_dragStart, pos, _thickness);
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
                ShowDragRect(new Rect(_dragStart, ClampToSurface(pos)), dim: false);
                break;
            case EditorTool.Crop:
                ShowDragRect(new Rect(_dragStart, ClampToSurface(pos)), dim: true);
                break;
        }
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        var pos = e.GetPosition(AnnotationCanvas);
        var shape = _activeShape;
        _activeShape = null;
        EditorSurface.ReleaseMouseCapture();

        switch (_tool)
        {
            case EditorTool.Arrow or EditorTool.Line:
                if (shape is null) return;
                if ((pos - _dragStart).Length < 3) AnnotationCanvas.Children.Remove(shape);
                else PushAddElement(shape);
                return;
            case EditorTool.Rectangle or EditorTool.Ellipse:
                if (shape is null) return;
                var r = new Rect(_dragStart, pos);
                if (r.Width < 3 && r.Height < 3) AnnotationCanvas.Children.Remove(shape);
                else PushAddElement(shape);
                return;
            case EditorTool.Freehand or EditorTool.Highlighter:
                if (shape is Polyline stroke && stroke.Points.Count >= 2) PushAddElement(shape);
                else if (shape is not null) AnnotationCanvas.Children.Remove(shape);
                return;
            case EditorTool.Blur:
                HideDragRect();
                ApplyBlur(ToPixelRect(new Rect(_dragStart, ClampToSurface(pos))));
                return;
            case EditorTool.Crop:
                var sel = new Rect(_dragStart, ClampToSurface(pos));
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

    // ------------------------------------------------------------- text tool

    private void PlaceText(Point pos)
    {
        var tb = AnnotationFactory.CreateTextEditor(_color, _thickness);
        // Offset by the editor chrome (1px border + 2px padding) so committed
        // text lands exactly where the user clicked.
        Canvas.SetLeft(tb, pos.X - 3);
        Canvas.SetTop(tb, pos.Y - 3);
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;

        tb.LostKeyboardFocus += (_, _) => CommitText();
        tb.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter) { CommitText(); ev.Handled = true; }
            else if (ev.Key == Key.Escape) { CancelText(); ev.Handled = true; }
        };
        Dispatcher.InvokeAsync(() => tb.Focus());
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

        var label = AnnotationFactory.CreateTextLabel(text, tb.Foreground, tb.FontSize);
        Canvas.SetLeft(label, x + 3);
        Canvas.SetTop(label, y + 3);
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
        Canvas.SetLeft(badge, pos.X - badge.Width / 2);
        Canvas.SetTop(badge, pos.Y - badge.Height / 2);
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

    // ------------------------------------------------------------------ blur

    private void ApplyBlur(SD.Rectangle region)
    {
        region.Intersect(new SD.Rectangle(0, 0, _source.Width, _source.Height));
        if (region.Width < 2 || region.Height < 2) return;

        var backup = _source.Clone(region, _source.PixelFormat);
        _owned.Add(backup);
        var r = region;
        Push(new EditorAction(
            undo: () =>
            {
                BitmapEffects.RestoreRegion(_source, backup, r);
                RefreshImage();
            },
            redo: () =>
            {
                BitmapEffects.Pixelate(_source, r);
                RefreshImage();
            }));
    }

    // ------------------------------------------------------------------ crop

    private void OnApplyCrop(object sender, RoutedEventArgs e)
    {
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

        Push(new EditorAction(
            undo: () =>
            {
                _source = before;
                OnSourceReplaced();
                ShiftAnnotations(region.X, region.Y);
            },
            redo: () =>
            {
                _source = after;
                OnSourceReplaced();
                ShiftAnnotations(-region.X, -region.Y);
            }));
    }

    private void OnCancelCrop(object sender, RoutedEventArgs e) => ClearCropPreview();

    private void OnSourceReplaced()
    {
        RefreshImage();
        SetSurfaceSize(_source.Width, _source.Height);
    }

    /// <summary>Translates every live annotation so it stays glued to the same image content after a crop.</summary>
    private void ShiftAnnotations(double dx, double dy)
    {
        foreach (UIElement el in AnnotationCanvas.Children)
        {
            if (el.RenderTransform is TranslateTransform t)
            {
                t.X += dx;
                t.Y += dy;
            }
            else
            {
                el.RenderTransform = new TranslateTransform(dx, dy);
            }
        }
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
        _redoStack.Clear();
        UpdateUndoRedoButtons();
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

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_dragging) return;
        CommitText();
        ClearCropPreview();
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);
        UpdateUndoRedoButtons();
    }

    private void Redo()
    {
        if (_dragging || _redoStack.Count == 0) return;
        var action = _redoStack.Pop();
        action.Redo();
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
        // Let an open text annotation keep its own Ctrl+Z behavior.
        if (Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.Z) { Undo(); e.Handled = true; }
            else if (e.Key == Key.Y) { Redo(); e.Handled = true; }
        }
    }

    // ------------------------------------------------------- copy/save/close

    private SD.Bitmap Flatten()
    {
        CommitText();
        CanvasHost.UpdateLayout();
        return BitmapEffects.RenderVisual(CanvasHost, _source.Width, _source.Height);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            using var flat = Flatten();
            CaptureService.CopyToClipboard(flat);
            BtnCopy.Content = "Copied ✓";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (_, _) => { timer.Stop(); BtnCopy.Content = "Copy"; };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Editor copy failed", ex);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_settings.Current.SaveFolder);
            var dialog = new SaveFileDialog
            {
                FileName = CaptureService.DefaultFileName(_settings.Current.ImageFormat),
                InitialDirectory = _settings.Current.SaveFolder,
                Filter = "PNG image|*.png|JPEG image|*.jpg",
                FilterIndex = _settings.Current.ImageFormat == "jpg" ? 2 : 1,
            };
            if (dialog.ShowDialog(this) != true) return;

            using var flat = Flatten();
            CaptureService.Save(flat, dialog.FileName);
            _history.Add(flat);
        }
        catch (Exception ex)
        {
            Log.Error("Editor save failed", ex);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
