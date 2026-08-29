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

public partial class EditorWindow : Window
{
    // ------------------------------------------------------------ mouse input

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_sourceOperationActive) return;
        _pendingInitialFit = false; // user is interacting — stop auto-refitting on resize
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

        // Resize handles take priority over selection/draw so grabbing a thumb on a
        // selected annotation (or a pending crop rect) starts a resize rather than a move.
        if (_handleKind != HandleKind.None)
        {
            int handle = HitTestHandle(pos);
            if (handle >= 0)
            {
                BeginResize(handle);
                e.Handled = true;
                return;
            }
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
        if (_activeHandle >= 0)
        {
            DragResize(e.GetPosition(AnnotationCanvas));
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
                arrow.Data = AnnotationFactory.ArrowGeometry(_dragStart, pos, _thickness, _arrowhead, _startArrowhead);
                break;
            case EditorTool.Callout:
                ShowDragRect(new Rect(_dragStart, pos), dim: false);
                break;
            case EditorTool.CurvedArrow when _activeShape is Path curve:
                curve.Data = AnnotationFactory.CurvedArrowGeometry(
                    _dragStart, AnnotationFactory.DefaultCurveControl(_dragStart, pos), pos, _thickness);
                break;
            case EditorTool.Line when _activeShape is Path linePath:
                linePath.Data = LineCurve.Stroke(_dragStart, pos, null);
                break;
            case EditorTool.Rectangle or EditorTool.Ellipse when _activeShape is not null:
                var r = new Rect(_dragStart, pos);
                Canvas.SetLeft(_activeShape, r.X);
                Canvas.SetTop(_activeShape, r.Y);
                _activeShape.Width = Math.Max(1, r.Width);
                _activeShape.Height = Math.Max(1, r.Height);
                if (_activeShape is Path cloudPath && _lineStyle == LineBorderStyle.Cloud)
                    cloudPath.Data = CloudPath.ForRectangle(new Rect(0, 0, cloudPath.Width, cloudPath.Height));
                break;
            case EditorTool.Freehand or EditorTool.Highlighter when _activeShape is Polyline stroke:
                if (stroke.Points.Count == 0 || (pos - stroke.Points[^1]).Length > 1.2)
                    stroke.Points.Add(pos);
                break;
            case EditorTool.Eraser:
                if (_eraserPoints.Count == 0 || (pos - _eraserPoints[^1]).Length > 1)
                    _eraserPoints.Add(pos);
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
        if (_activeHandle >= 0)
        {
            EndResize();
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
                    var strokeData = AnnotationData.ForStroke(
                        _tool == EditorTool.Arrow ? AnnotationData.TypeArrow : AnnotationData.TypeLine,
                        new[] { _dragStart, pos }, StrokeColorOf(shape), shape.StrokeThickness,
                        _lineStyle,
                        _tool == EditorTool.Arrow ? _arrowhead : ArrowheadStyle.None,
                        _tool == EditorTool.Arrow ? _startArrowhead : ArrowheadStyle.None);
                    if (_tool == EditorTool.Arrow) strokeData.Style = _arrowStyle.ToString();
                    shape.Tag = strokeData;
                    PushAddElement(shape);
                }
                return;
            case EditorTool.Callout:
                HideDragRect();
                if ((pos - _dragStart).Length < CalloutLayout.MinDrag) return;
                CommitCallout(_dragStart, pos);
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
                    var (strokeColor, fillColor) = AnnotationStyle.EnforceOneVisible(
                        StrokeColorOf(shape), CurrentFillBrush());
                    var lineStyle = _tool == EditorTool.Ellipse && _lineStyle == LineBorderStyle.Cloud
                        ? LineBorderStyle.Solid
                        : _lineStyle;
                    shape.Tag = AnnotationData.ForShape(
                        _tool == EditorTool.Rectangle ? AnnotationData.TypeRectangle : AnnotationData.TypeEllipse,
                        bounds, strokeColor, shape.StrokeThickness, _fillMode, fillColor, lineStyle);
                    PushAddElement(shape);
                }
                return;
            case EditorTool.Freehand or EditorTool.Highlighter:
                if (shape is Polyline stroke && stroke.Points.Count >= 1)
                {
                    var pts = AnnotationFactory.SmoothFreehandPoints(stroke.Points);
                    AnnotationCanvas.Children.Remove(stroke);
                    bool highlighter = _tool == EditorTool.Highlighter;
                    double width = highlighter
                        ? AnnotationStyle.HighlighterWidth(_highlighterSize)
                        : stroke.StrokeThickness;
                    var ink = AnnotationFactory.CreateInk(pts, StrokeColorOf(stroke), width, highlighter);
                    ink.Tag = AnnotationData.ForStroke(
                        highlighter ? AnnotationData.TypeHighlighter : AnnotationData.TypeFreehand,
                        pts, StrokeColorOf(stroke), width);
                    AnnotationCanvas.Children.Add(ink);
                    PushAddElement(ink);
                }
                else if (shape is not null) AnnotationCanvas.Children.Remove(shape);
                return;
            case EditorTool.Eraser:
                ApplyEraserStroke();
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
                ShowCropAdjustHandles(); // 8 handles to fine-tune the rect before Apply
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
        if (_tool == EditorTool.Eyedropper)
        {
            if (inImage) SampleEyedropper(pos);
            e.Handled = true;
            return;
        }
        if (_tool == EditorTool.Eraser)
        {
            _eraserPoints.Clear();
            _eraserPoints.Add(pos);
            _dragStart = pos;
            _dragging = true;
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }

        _dragStart = _tool is EditorTool.Blur or EditorTool.Crop or EditorTool.Pixelate or EditorTool.Spotlight
            ? ClampToSurface(pos) : pos;
        _dragging = true;
        Viewport.CaptureMouse();
        _thickness = ActiveSize();
        double strokeWidth = _tool == EditorTool.Highlighter
            ? AnnotationStyle.HighlighterWidth(_highlighterSize)
            : _tool is EditorTool.Text or EditorTool.Callout
                ? _shapeSize
                : _thickness;

        // New shapes/arrows/lines pick up the current opacity by baking it into the brush alpha.
        var (stroke, fill) = AnnotationStyle.EnforceOneVisible(CurrentStrokeBrush(), CurrentFillBrush());
        var brush = new SolidColorBrush(stroke);
        var fillBrush = fill.A == 0 ? ShapeFillBrush.Create(_fillMode, stroke) : new SolidColorBrush(fill);
        switch (_tool)
        {
            case EditorTool.Arrow:
                _activeShape = new Path
                {
                    Stroke = brush,
                    Fill = brush,
                    StrokeThickness = strokeWidth,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = AnnotationFactory.ArrowGeometry(_dragStart, _dragStart, strokeWidth, _arrowhead, _startArrowhead),
                };
                AnnotationFactory.ApplyDash(_activeShape, _lineStyle);
                break;
            case EditorTool.CurvedArrow:
                _activeShape = new Path
                {
                    Stroke = brush,
                    Fill = brush,
                    StrokeThickness = strokeWidth,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = AnnotationFactory.CurvedArrowGeometry(_dragStart, _dragStart, _dragStart, strokeWidth),
                };
                AnnotationFactory.ApplyDash(_activeShape, _lineStyle);
                break;
            case EditorTool.Line:
                _activeShape = new Path
                {
                    Stroke = brush,
                    StrokeThickness = strokeWidth,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Data = LineCurve.Stroke(pos, pos, null),
                };
                AnnotationFactory.ApplyDash(_activeShape, _lineStyle);
                break;
            case EditorTool.Rectangle:
                if (_lineStyle == LineBorderStyle.Cloud)
                {
                    _activeShape = new Path
                    {
                        Stroke = brush,
                        StrokeThickness = strokeWidth,
                        Fill = fillBrush,
                        Data = CloudPath.ForRectangle(new Rect(0, 0, 1, 1)),
                    };
                }
                else
                {
                    _activeShape = new Rectangle
                    {
                        Stroke = brush,
                        StrokeThickness = strokeWidth,
                        RadiusX = 2, RadiusY = 2,
                        Fill = fillBrush,
                    };
                    AnnotationFactory.ApplyDash(_activeShape, _lineStyle);
                }
                Canvas.SetLeft(_activeShape, pos.X);
                Canvas.SetTop(_activeShape, pos.Y);
                break;
            case EditorTool.Ellipse:
                _activeShape = new Ellipse
                {
                    Stroke = brush,
                    StrokeThickness = strokeWidth,
                    Fill = fillBrush,
                };
                AnnotationFactory.ApplyDash(_activeShape, _lineStyle == LineBorderStyle.Cloud ? LineBorderStyle.Solid : _lineStyle);
                Canvas.SetLeft(_activeShape, pos.X);
                Canvas.SetTop(_activeShape, pos.Y);
                break;
            case EditorTool.Freehand:
                _activeShape = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = AnnotationStyle.ClampSize(_penSize),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Points = new PointCollection { pos },
                };
                break;
            case EditorTool.Highlighter:
                _activeShape = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = AnnotationStyle.HighlighterWidth(_highlighterSize),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Points = new PointCollection { pos },
                };
                break;
            case EditorTool.Callout:
                ShowDragRect(new Rect(_dragStart, _dragStart), dim: false);
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
        _eraserPoints.Clear();
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
        // A sampled color rarely matches a preset swatch, so it lands in the persistent
        // custom swatch (and current-color indicator) instead of clearing the selection.
        var sampled = EyedropperSampler.SampleClamped(_source, pos);
        ApplyCustomColor(sampled);
        if (_colorPickerBuilt) SyncColorPickerInputs(sampled);
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
        // Double-click any text annotation (Plain/Bold/Huge TextBlock, Outline Path, or
        // Pill Border) to reopen it for editing; the owning element carries its style.
        if (e.ClickCount == 2 && hit is FrameworkElement fe &&
            fe.Tag is AnnotationData tm && tm.Type == AnnotationData.TypeText)
        {
            BeginTextReEdit(fe);
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
        // Selecting a different element (or deselecting) ends any in-progress opacity drag,
        // committing it as one undo entry before the selection moves on.
        if (_opacityElement is not null && !ReferenceEquals(_opacityElement, element))
            CommitOpacityGesture();
        _selected = element;
        UpdateSelectionVisual();
    }

    private void UpdateSelectionVisual()
    {
        // The crop-adjust overlay owns the handles while a pending crop is being fine-tuned;
        // don't let the selection refresh stomp it.
        if (_adjustingCrop || (_handleKind == HandleKind.Crop && _pendingCrop is not null))
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            UpdateCropAdjustHandles();
            return;
        }
        if (_selected is null || !AnnotationCanvas.Children.Contains(_selected))
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            HideHandles();
            return;
        }
        Rect b = GetCanvasBounds(_selected);
        if (b.IsEmpty || (b.Width < 0.01 && b.Height < 0.01))
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            HideHandles();
            return;
        }
        double pad = 3 / _zoom;
        b.Inflate(pad, pad);
        Canvas.SetLeft(SelectionRect, b.X);
        Canvas.SetTop(SelectionRect, b.Y);
        SelectionRect.Width = b.Width;
        SelectionRect.Height = b.Height;
        SelectionRect.Visibility = Visibility.Visible;

        UpdateSelectionHandles();
    }

    /// <summary>Bounds of an annotation in canvas coordinates, including its own render transform.</summary>
    private Rect GetCanvasBounds(UIElement element)
    {
        Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
        if (bounds.IsEmpty) bounds = new Rect(element.RenderSize);
        return element.TransformToAncestor(AnnotationCanvas).TransformBounds(bounds);
    }

}
