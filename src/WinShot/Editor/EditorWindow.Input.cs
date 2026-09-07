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

        // Z is held: this click is a zoom, whatever tool is armed.
        if (_zoomKeyDown)
        {
            _zoomDragging = true;
            _zoomDragStart = pos;
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }

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

        // The focus ring beats the box handles: it sits outside the bubble, so the two
        // never overlap, and it is the smaller target of the pair.
        if (MagnifierFocusHandle.Visibility == Visibility.Visible &&
            (pos - CurrentMagnifierFocus()).Length <= 10 / _zoom)
        {
            _draggingMagnifierFocus = true;
            _magnifierFocusBefore = CurrentMagnifierFocus();
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
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
            UpdateScrollOverlay();
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
        if (_draggingMagnifierFocus)
        {
            SetMagnifierFocus(e.GetPosition(AnnotationCanvas));
            return;
        }
        if (_activeHandle >= 0)
        {
            DragResize(e.GetPosition(AnnotationCanvas));
            return;
        }
        if (_zoomDragging)
        {
            // The same dashed band the crop uses, so "I am choosing a region" looks the
            // same wherever the editor asks for one.
            ShowDragRect(new Rect(_zoomDragStart, e.GetPosition(AnnotationCanvas)), dim: false);
            return;
        }
        if (_marqueeActive)
        {
            UpdateMarquee(e.GetPosition(AnnotationCanvas));
            return;
        }
        if (_movingSelection && _multiSelected.Count > 1)
        {
            Vector step = MoveStep(e.GetPosition(AnnotationCanvas));
            if (step.X != 0 || step.Y != 0)
            {
                foreach (UIElement member in _multiSelected)
                    MoveElement(member, step.X, step.Y);
                UpdateMultiSelectionVisual();
            }
            return;
        }
        if (_movingSelection && _selected is not null)
        {
            Vector step = MoveStep(e.GetPosition(AnnotationCanvas));
            if (step.X != 0 || step.Y != 0)
            {
                MoveElement(_selected, step.X, step.Y);
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
            {
                var (tail, head) = ArrowEnds(_dragStart, ConstrainDraw(pos));
                arrow.Data = AnnotationFactory.ArrowGeometry(tail, head, _thickness, _arrowStyle);
                break;
            }
            case EditorTool.CurvedArrow when _activeShape is Path curve:
                Point curveEnd = ConstrainDraw(pos);
                curve.Data = AnnotationFactory.CurvedArrowGeometry(
                    _dragStart, AnnotationFactory.DefaultCurveControl(_dragStart, curveEnd), curveEnd, _thickness);
                break;
            case EditorTool.Line when _activeShape is Line line:
                Point lineEnd = ConstrainDraw(pos);
                line.X2 = lineEnd.X;
                line.Y2 = lineEnd.Y;
                break;
            case EditorTool.Rectangle or EditorTool.Ellipse when _activeShape is not null:
                var r = new Rect(_dragStart, ConstrainDraw(pos));
                Canvas.SetLeft(_activeShape, r.X);
                Canvas.SetTop(_activeShape, r.Y);
                _activeShape.Width = r.Width;
                _activeShape.Height = r.Height;
                break;
            case EditorTool.Freehand or EditorTool.Highlighter when _activeShape is Polyline stroke:
                if (stroke.Points.Count == 0 || (pos - stroke.Points[^1]).Length > 1.2)
                    stroke.Points.Add(pos);
                break;
            case EditorTool.Redact when _activeShape is System.Windows.Shapes.Rectangle mark:
                // The mark itself IS the preview: the effect runs on the pixels under the
                // rubber band as it grows, instead of a dashed box that only becomes a
                // redaction after the mouse comes up.
                UpdateRedactionPreview(mark, new Rect(_dragStart, ConstrainDrawClamped(pos)), force: false);
                break;
            case EditorTool.Magnifier when _activeShape is System.Windows.Shapes.Rectangle bubble:
                UpdateMagnifierPreview(bubble, new Rect(_dragStart, ConstrainDrawClamped(pos)), force: false);
                break;
            case EditorTool.Spotlight:
                ShowDragRect(new Rect(_dragStart, ConstrainDrawClamped(pos)), dim: true);
                break;
            case EditorTool.Crop:
                Rect cropArea = CropSelectionRect(pos);
                ShowDragRect(cropArea, dim: true);
                ShowThirdsGuides(cropArea);
                break;
        }
    }

    /// <summary>
    /// Rule-of-thirds guides inside the crop rectangle. Drawn at a constant screen weight,
    /// so they stay hairlines however far the canvas is zoomed.
    /// </summary>
    private void ShowThirdsGuides(Rect area)
    {
        if (area.Width < 12 || area.Height < 12)
        {
            CropGuides.Visibility = Visibility.Collapsed;
            return;
        }

        double t = 1 / _zoom;
        void Place(Line line, Point from, Point to)
        {
            line.X1 = from.X; line.Y1 = from.Y;
            line.X2 = to.X; line.Y2 = to.Y;
            line.StrokeThickness = t;
        }

        double x1 = area.Left + area.Width / 3, x2 = area.Left + area.Width * 2 / 3;
        double y1 = area.Top + area.Height / 3, y2 = area.Top + area.Height * 2 / 3;
        Place(ThirdV1, new Point(x1, area.Top), new Point(x1, area.Bottom));
        Place(ThirdV2, new Point(x2, area.Top), new Point(x2, area.Bottom));
        Place(ThirdH1, new Point(area.Left, y1), new Point(area.Right, y1));
        Place(ThirdH2, new Point(area.Left, y2), new Point(area.Right, y2));
        CropGuides.Visibility = Visibility.Visible;
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
        if (_draggingMagnifierFocus)
        {
            EndMagnifierFocusDrag();
            return;
        }
        if (_zoomDragging)
        {
            EndZoomGesture(e.GetPosition(AnnotationCanvas));
            return;
        }
        if (_draggingCurveHandle)
        {
            // The handle stays live (and re-draggable) until a click elsewhere commits.
            _draggingCurveHandle = false;
            Viewport.ReleaseMouseCapture();
            return;
        }
        if (_marqueeActive)
        {
            EndMarquee(e.GetPosition(AnnotationCanvas));
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
        // The release has to land on the SAME point the last preview frame drew, or a
        // Shift-constrained mark snaps back to the raw pointer the instant it is committed.
        var pos = ConstrainDraw(e.GetPosition(AnnotationCanvas));
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
                    // The tool's weight, not the visual's: a Fancy arrow draws with no
                    // stroke at all, and reading 0 back would make the mark unloadable.
                    // An arrow points wherever it was previewed pointing; a line has no
                    // direction to invert, so it keeps the raw drag.
                    var (tail, head) = _tool == EditorTool.Arrow
                        ? ArrowEnds(_dragStart, pos)
                        : (_dragStart, pos);
                    var strokeData = AnnotationData.ForStroke(
                        _tool == EditorTool.Arrow ? AnnotationData.TypeArrow : AnnotationData.TypeLine,
                        new[] { tail, head }, StrokeColorOf(shape), _thickness);
                    // Persist the head style so the resize/endpoint-handle code (and an
                    // in-session re-edit) rebuilds the right geometry.
                    if (_tool == EditorTool.Arrow) strokeData.Style = _arrowStyle.ToString();
                    shape.Tag = strokeData;
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
                    var shapeMeta = AnnotationData.ForShape(
                        _tool == EditorTool.Rectangle ? AnnotationData.TypeRectangle : AnnotationData.TypeEllipse,
                        bounds, StrokeColorOf(shape), shape.StrokeThickness, _fillMode, _fillColor);
                    shapeMeta.Dash = _strokeDash.ToString();
                    shapeMeta.Sketch = _strokeRender.ToString();
                    shapeMeta.Seed = _sketchSeed;
                    shape.Tag = shapeMeta;
                    PushAddElement(shape);
                }
                return;
            case EditorTool.Freehand or EditorTool.Highlighter:
                if (shape is Polyline stroke && stroke.Points.Count >= 2)
                {
                    // The pencil captures raw points during the drag for responsiveness;
                    // on release, smooth them so the committed stroke looks polished.
                    // "Smooth pencil" off keeps the raw path — some people are drawing a
                    // signature, and a smoothed signature is somebody else's.
                    if (_tool == EditorTool.Freehand && _settings.Current.SmoothPencil)
                        stroke.Points = AnnotationFactory.SmoothFreehandPoints(stroke.Points);
                    stroke.Tag = AnnotationData.ForStroke(
                        _tool == EditorTool.Freehand ? AnnotationData.TypeFreehand : AnnotationData.TypeHighlighter,
                        stroke.Points, StrokeColorOf(stroke), stroke.StrokeThickness);
                    PushAddElement(shape);
                }
                else if (shape is not null) AnnotationCanvas.Children.Remove(shape);
                return;
            case EditorTool.Magnifier:
            {
                if (shape is not System.Windows.Shapes.Rectangle bubble) return;
                var bounds = new Rect(_dragStart, ClampToSurface(pos));
                if (bounds.Width < 12 || bounds.Height < 12)
                {
                    // A callout smaller than its own border is not a callout.
                    AnnotationCanvas.Children.Remove(bubble);
                    return;
                }
                UpdateMagnifierPreview(bubble, bounds, force: true);
                Point focus = MagnifierLayout.ClampFocus(
                    MagnifierLayout.DefaultFocus(bounds), new Size(_sourceWidth, _sourceHeight));
                bubble.Tag = AnnotationData.ForMagnifier(
                    bounds, focus, _magnification, StrokeColorOf(bubble), bubble.StrokeThickness);
                PushAddElement(bubble);
                return;
            }
            case EditorTool.Redact:
                if (shape is not System.Windows.Shapes.Rectangle mark) return;
                var area = new Rect(_dragStart, ClampToSurface(pos));
                if (area.Width < 3 || area.Height < 3)
                {
                    AnnotationCanvas.Children.Remove(mark);
                    return;
                }
                if (RedactTextOnlyCheck.IsChecked == true)
                {
                    // The dragged rectangle was only ever the SEARCH area: what lands is one
                    // mark per word, so photos and graphics inside it stay visible.
                    AnnotationCanvas.Children.Remove(mark);
                    _ = RedactTextInRegionAsync(area);
                    return;
                }
                UpdateRedactionPreview(mark, area, force: true);
                mark.Tag = AnnotationData.ForRedaction(area, _redactStyle, _redactStrength, _activeRedactSeed);
                PushAddElement(mark);
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
                ShowThirdsGuides(sel);
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

        _dragStart = _tool is EditorTool.Redact or EditorTool.Crop or EditorTool.Spotlight
            or EditorTool.Magnifier
            ? ClampToSurface(pos) : pos;
        _dragging = true;
        Viewport.CaptureMouse();

        // New shapes/arrows/lines pick up the current opacity by baking it into the brush alpha.
        var brush = new SolidColorBrush(WithOpacity(_color, _opacity));
        switch (_tool)
        {
            case EditorTool.Arrow:
                _activeShape = new Path
                {
                    Stroke = brush,
                    Fill = brush,
                    // Fancy is one filled silhouette; stroking it would fatten the tail.
                    StrokeThickness = _arrowStyle == ArrowStyle.Fancy ? 0 : _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = AnnotationFactory.ArrowGeometry(_dragStart, _dragStart, _thickness, _arrowStyle),
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
            case EditorTool.Ellipse:
                // The seed is drawn once per mark, here, so a sketchy outline never
                // re-scribbles itself as the drag grows.
                _sketchSeed = Random.Shared.Next();
                var box = AnnotationFactory.CreateShape(
                    _tool == EditorTool.Ellipse, _strokeRender, _sketchSeed);
                box.Stroke = brush;
                box.StrokeThickness = _thickness;
                box.Fill = ShapeFillBrush.Create(_fillMode, WithOpacity(_color, _opacity), _fillColor);
                AnnotationFactory.ApplyDash(box, _strokeDash);
                _activeShape = box;
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
                // Highlighter has a translucent base (~0x59); opacity scales it further.
                highlight.A = (byte)Math.Clamp(Math.Round(0x59 * _opacity), 0, 255);
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
            case EditorTool.Redact:
                _activeShape = BeginRedaction(new Rect(_dragStart, _dragStart));
                break;
            case EditorTool.Magnifier:
                _activeShape = BeginMagnifier(new Rect(_dragStart, _dragStart));
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

    // -------------------------------------------------------- Shift constraints

    /// <summary>
    /// Where the pointer counts as being for the tool currently drawing, with Shift folded
    /// in: shapes square, lines and arrows snap to 45°, and everything else is unchanged.
    /// Both the live preview and the mouse-up commit go through here, so a constrained mark
    /// cannot snap back to the raw pointer the instant it is committed.
    /// </summary>
    private Point ConstrainDraw(Point pos)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return pos;
        return _tool switch
        {
            EditorTool.Line or EditorTool.Arrow or EditorTool.CurvedArrow =>
                DragConstraints.QuantizeBearing(_dragStart, pos),
            EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Redact or EditorTool.Spotlight
                or EditorTool.Magnifier =>
                DragConstraints.Square(_dragStart, pos),
            _ => pos,
        };
    }

    /// <summary>The same, then clamped into the image — for the tools that never draw off it.</summary>
    private Point ConstrainDrawClamped(Point pos) => ClampToSurface(ConstrainDraw(pos));

    /// <summary>
    /// How far the selection should move THIS frame. The raw drag is accumulated
    /// separately from what has been applied, so holding Shift locks the move to one axis
    /// and letting go picks the free move back up from where the pointer actually is —
    /// locking each frame's delta on its own would let a diagonal drag creep in both axes.
    /// </summary>
    private Vector MoveStep(Point pos)
    {
        _moveRaw += pos - _moveLast;
        _moveLast = pos;
        Vector want = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
            ? DragConstraints.LockAxis(_moveRaw)
            : _moveRaw + SnapNudge(_moveRaw);
        Vector step = want - _moveTotal;
        _moveTotal = want;
        return step;
    }

    /// <summary>
    /// How far the moving mark has to shift to line its edges or its middle up with a
    /// neighbour's. Zero when snapping is off, when Ctrl is held for the one move that has
    /// to land a pixel off a guide, or when Shift is already constraining the drag — two
    /// rules pulling on one drag is a fight the user cannot win.
    ///
    /// The snap is computed against the RAW drag rather than the snapped position, so a
    /// mark that has stuck to a guide can be pulled off it by keeping going, instead of
    /// being dragged along by its own correction.
    /// </summary>
    private Vector SnapNudge(Vector rawMove)
    {
        if (!_objectSnap) return default;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return default;

        UIElement? moving = _multiSelected.Count > 1 ? null : _selected;
        if (moving is null) return default;

        Rect start = GetCanvasBounds(moving);
        if (start.IsEmpty) return default;
        // Where the mark would be with the raw drag applied, minus what has already been
        // applied to it — GetCanvasBounds reports it mid-drag, not where it started.
        Rect at = Rect.Offset(start, rawMove.X - _moveTotal.X, rawMove.Y - _moveTotal.Y);

        var others = new List<Rect>();
        foreach (UIElement child in AnnotationCanvas.Children)
        {
            if (ReferenceEquals(child, moving)) continue;
            if (child is not FrameworkElement fe || fe.Tag is not AnnotationData) continue;
            Rect b = GetCanvasBounds(child);
            if (!b.IsEmpty) others.Add(b);
        }
        if (others.Count == 0) return default;

        return ObjectSnap.Adjust(at, others, ObjectSnap.Tolerance / _zoom);
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
            EdgeSnapDistance);

    /// <summary>
    /// How close a crop edge has to get before it sticks to the image edge. The toggle
    /// turns it off for good; holding Ctrl turns it off for one drag, which is the escape
    /// hatch for the one crop that has to end a pixel short of the edge.
    /// </summary>
    private double EdgeSnapDistance =>
        SnapToEdgesCheck?.IsChecked == true && (Keyboard.Modifiers & ModifierKeys.Control) == 0
            ? CropSnapPx
            : 0;

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
    // ------------------------------------------------------- magnifier focus

    /// <summary>The point of the picture the selected callout is looking at.</summary>
    private Point CurrentMagnifierFocus() =>
        _selected is FrameworkElement fe && fe.Tag is AnnotationData meta &&
        meta.Type == AnnotationData.TypeMagnifier
            ? MagnifierFocusOf(meta)
            : default;

    private static Point MagnifierFocusOf(AnnotationData meta) =>
        meta.Points is { Length: >= 1 } pts && pts[0] is { Length: >= 2 } p
            ? new Point(p[0], p[1])
            : default;

    /// <summary>
    /// Aims the selected callout at a new point, live. The bubble does not move: dragging
    /// the ring is how you point a callout at something OTHER than what it is sitting on,
    /// which is the whole reason the two are separate.
    /// </summary>
    private void SetMagnifierFocus(Point focus)
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta ||
            meta.Type != AnnotationData.TypeMagnifier)
            return;

        Point clamped = MagnifierLayout.ClampFocus(focus, new Size(_sourceWidth, _sourceHeight));
        meta.Points = new[] { new[] { clamped.X, clamped.Y } };
        RefreshRedaction(fe);
        UpdateMagnifierFocusVisual();
    }

    /// <summary>Commits the aim as one undo step; a ring put back where it started records nothing.</summary>
    private void EndMagnifierFocusDrag()
    {
        _draggingMagnifierFocus = false;
        Viewport.ReleaseMouseCapture();

        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta ||
            meta.Type != AnnotationData.TypeMagnifier)
            return;

        Point after = MagnifierFocusOf(meta);
        Point before = _magnifierFocusBefore;
        if ((after - before).Length < 0.5) return;

        void Aim(Point p)
        {
            if (fe.Tag is not AnnotationData m) return;
            m.Points = new[] { new[] { p.X, p.Y } };
            RefreshRedaction(fe);
            if (ReferenceEquals(_selected, fe)) UpdateMagnifierFocusVisual();
        }

        Push(new EditorAction(undo: () => Aim(before), redo: () => Aim(after)), apply: false);
    }

    /// <summary>
    /// Draws the focus ring and the leader joining it to the bubble, at constant SCREEN
    /// size so both stay grabbable however far the canvas is zoomed. Hidden for every other
    /// selection, and for none.
    /// </summary>
    private void UpdateMagnifierFocusVisual()
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta ||
            meta.Type != AnnotationData.TypeMagnifier)
        {
            MagnifierFocusHandle.Visibility = Visibility.Collapsed;
            MagnifierLeader.Visibility = Visibility.Collapsed;
            return;
        }

        Point focus = MagnifierFocusOf(meta);
        double d = 12 / _zoom;
        MagnifierFocusHandle.Width = d;
        MagnifierFocusHandle.Height = d;
        MagnifierFocusHandle.StrokeThickness = 2 / _zoom;
        Canvas.SetLeft(MagnifierFocusHandle, focus.X - d / 2);
        Canvas.SetTop(MagnifierFocusHandle, focus.Y - d / 2);
        MagnifierFocusHandle.Visibility = Visibility.Visible;

        Rect bubble = GetCanvasBounds(fe);
        var centre = new Point(bubble.Left + bubble.Width / 2, bubble.Top + bubble.Height / 2);
        MagnifierLeader.X1 = focus.X;
        MagnifierLeader.Y1 = focus.Y;
        MagnifierLeader.X2 = centre.X;
        MagnifierLeader.Y2 = centre.Y;
        MagnifierLeader.StrokeThickness = 1 / _zoom;
        // A leader from a bubble to a ring underneath it is noise; it only earns its place
        // once the callout has actually been moved off what it is showing.
        MagnifierLeader.Visibility = bubble.Contains(focus) ? Visibility.Collapsed : Visibility.Visible;
    }

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
        bool reopenPicker = _reopenPickerAfterSample;
        _reopenPickerAfterSample = false;

        if (reopenPicker)
        {
            // Armed from inside the picker: the sample IS the picker's colour, and the
            // picker comes back so choosing a colour off the image is part of choosing one
            // rather than a detour that loses the popup.
            BuildColorPickerPalette();
            SyncColorPickerInputs(sampled);
            CommitPickerColor();
        }
        else
        {
            ApplyCustomColor(sampled);
            if (_colorPickerBuilt) SyncColorPickerInputs(sampled);
        }

        HideEyedropperChrome();
        CheckToolButton(_toolBeforeEyedropper);
        if (reopenPicker) ColorPickerPopup.IsOpen = true;
    }

    private void HideEyedropperChrome()
    {
        EyedropSwatch.Visibility = Visibility.Collapsed;
        EyedropLoupe.Visibility = Visibility.Collapsed;
    }

    // The loupe window, in source pixels, and how big it is drawn on screen. Odd, so there
    // is a true centre cell for the pixel actually under the cursor.
    private const int LoupePixels = 15;
    private const double LoupeScreenSize = 132;

    /// <summary>
    /// The magnified pixel grid, pointed at the source bitmap instead of the screen. The
    /// eyedropper without it is a one-pixel guess: at fit-to-view zoom a single source pixel
    /// is a fraction of a screen pixel, so the swatch alone could not say WHICH pixel.
    /// </summary>
    private void UpdateEyedropperLoupe(Point pos)
    {
        int cx = (int)Math.Floor(pos.X), cy = (int)Math.Floor(pos.Y);
        if (cx < 0 || cy < 0 || cx >= _source.Width || cy >= _source.Height)
        {
            EyedropLoupe.Visibility = Visibility.Collapsed;
            return;
        }

        int side = Math.Min(LoupePixels, Math.Min(_source.Width, _source.Height));
        int half = side / 2;
        // Near an edge the window slides rather than padding, so the grid is always full.
        int ox = Math.Clamp(cx - half, 0, _source.Width - side);
        int oy = Math.Clamp(cy - half, 0, _source.Height - side);

        BitmapSource tile;
        try
        {
            using SD.Bitmap crop = CaptureService.Crop(_source, new SD.Rectangle(ox, oy, side, side));
            tile = CaptureService.ToBitmapSource(crop);
        }
        catch (Exception ex)
        {
            Log.Error("Eyedropper loupe crop failed", ex);
            EyedropLoupe.Visibility = Visibility.Collapsed;
            return;
        }

        // Everything inside the loupe is sized in SCREEN pixels, so the whole thing is
        // scaled by 1/zoom and stays the same size however far the canvas is zoomed.
        double scale = 1 / _zoom;
        double cell = LoupeScreenSize / side;

        if (EyedropLoupe.Children.Count == 0)
            BuildEyedropperLoupe();

        var image = (Image)EyedropLoupe.Children[0];
        var target = (System.Windows.Shapes.Rectangle)EyedropLoupe.Children[1];
        image.Source = tile;
        image.Width = LoupeScreenSize;
        image.Height = LoupeScreenSize;
        target.Width = cell;
        target.Height = cell;
        target.StrokeThickness = 1.5;
        Canvas.SetLeft(target, (cx - ox) * cell);
        Canvas.SetTop(target, (cy - oy) * cell);

        // Above-right of the cursor by default, flipping when that would run off the image.
        double gap = 14 * scale;
        double sizeContent = LoupeScreenSize * scale;
        double left = pos.X + gap;
        double top = pos.Y - gap - sizeContent;
        if (left + sizeContent > _source.Width) left = pos.X - gap - sizeContent;
        if (top < 0) top = pos.Y + gap;

        EyedropLoupe.RenderTransform = new ScaleTransform(scale, scale);
        EyedropLoupe.RenderTransformOrigin = new Point(0, 0);
        Canvas.SetLeft(EyedropLoupe, left);
        Canvas.SetTop(EyedropLoupe, top);
        EyedropLoupe.Visibility = Visibility.Visible;
    }

    private void BuildEyedropperLoupe()
    {
        var image = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(image, EdgeMode.Aliased);
        EyedropLoupe.Children.Add(image);

        EyedropLoupe.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Stroke = (Brush)FindResource("InfoBrush"),
            StrokeThickness = 1.5,
            Fill = null,
        });

        EyedropLoupe.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = LoupeScreenSize,
            Height = LoupeScreenSize,
            RadiusX = 6,
            RadiusY = 6,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Fill = null,
            IsHitTestVisible = false,
        });
    }

    /// <summary>Hover preview: a small swatch + hex readout near the cursor while the eyedropper is active.</summary>
    private void UpdateEyedropperSwatch(Point pos)
    {
        EyedropperPreview preview = EyedropperSampler.Preview(_source, pos, _zoom);
        if (!preview.Visible)
        {
            HideEyedropperChrome();
            return;
        }
        UpdateEyedropperLoupe(pos);

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
            // Empty canvas starts a rubber band. A drag that never moves far enough is
            // still just a click, and MarqueeMouseUp deselects for it.
            Select(null);
            ClearMultiSelection();
            _marqueeActive = true;
            _marqueeStart = pos;
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }

        // Clicking a member of the group keeps the group and moves the whole set.
        if (_multiSelected.Count > 1 && _multiSelected.Contains(hit))
        {
            _movingSelection = true;
            _moveLast = pos;
            _moveTotal = new Vector();
            _moveRaw = new Vector();
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }
        ClearMultiSelection();
        // Double-click any text annotation (Plain/Bold/Huge TextBlock, Outline Path, or
        // Pill Border) to reopen it for editing; the owning element carries its style.
        if (e.ClickCount == 2 && hit is FrameworkElement fe &&
            fe.Tag is AnnotationData tm && tm.Type == AnnotationData.TypeText)
        {
            BeginTextReEdit(fe);
            e.Handled = true;
            return;
        }
        // Double-click an arrow to step it to the next style — "not that one, the next
        // one" without opening the style menu five times. It has to select first: the
        // cycle acts on the selection, and a double-click's first press may have landed
        // on a different mark.
        if (e.ClickCount == 2 && hit is FrameworkElement arrowHit &&
            arrowHit.Tag is AnnotationData am &&
            am.Type is AnnotationData.TypeArrow or AnnotationData.TypeCurvedArrow)
        {
            Select(hit);
            CycleSelectedArrowStyle();
            e.Handled = true;
            return;
        }
        // Alt-drag leaves a copy behind and drags the copy — the original stays where it is.
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && CloneForAltDrag(hit) is UIElement clone)
            hit = clone;
        Select(hit);
        _movingSelection = true;
        _moveLast = pos;
        _moveTotal = new Vector();
        _moveRaw = new Vector();
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    // ------------------------------------------------------------ marquee selection

    /// <summary>Live rubber band: blue solid dragging right, green dashed dragging left.</summary>
    private void UpdateMarquee(Point pos)
    {
        _marqueeMode = MarqueeSelection.ModeFor(_marqueeStart, pos, _marqueePreference);
        Rect r = MarqueeSelection.RectFor(_marqueeStart, pos);

        bool window = _marqueeMode == MarqueeMode.Window;
        MarqueeRect.Stroke = new SolidColorBrush(window
            ? Color.FromArgb(0xCC, 0x00, 0x64, 0xFF)
            : Color.FromArgb(0xCC, 0x00, 0xC8, 0x64));
        MarqueeRect.Fill = new SolidColorBrush(window
            ? Color.FromArgb(0x26, 0x00, 0x64, 0xFF)
            : Color.FromArgb(0x26, 0x00, 0xC8, 0x64));
        MarqueeRect.StrokeDashArray = window ? null : new DoubleCollection { 5, 5 };
        MarqueeRect.StrokeThickness = 1 / _zoom;

        Canvas.SetLeft(MarqueeRect, r.X);
        Canvas.SetTop(MarqueeRect, r.Y);
        MarqueeRect.Width = r.Width;
        MarqueeRect.Height = r.Height;
        MarqueeRect.Visibility = Visibility.Visible;
    }

    /// <summary>Resolves the band into a selection: one mark selects normally, several form a group.</summary>
    private void EndMarquee(Point pos)
    {
        _marqueeActive = false;
        Viewport.ReleaseMouseCapture();
        MarqueeRect.Visibility = Visibility.Collapsed;

        if (!MarqueeSelection.IsDrag(_marqueeStart, pos))
            return; // a click on empty canvas: the deselect already happened on mouse-down

        Rect band = MarqueeSelection.RectFor(_marqueeStart, pos);
        var caught = new List<UIElement>();
        foreach (UIElement child in AnnotationCanvas.Children)
        {
            if (child is not FrameworkElement fe || fe.Tag is not AnnotationData) continue;
            if (MarqueeSelection.Catches(band, GetCanvasBounds(child), _marqueeMode))
                caught.Add(child);
        }

        if (caught.Count == 0) { Select(null); return; }
        if (caught.Count == 1) { Select(caught[0]); return; }

        Select(null);
        _multiSelected.Clear();
        _multiSelected.AddRange(caught);
        UpdateMultiSelectionVisual();
    }

    /// <summary>The group's union frame — no handles, because a group is move-only for now.</summary>
    private void UpdateMultiSelectionVisual()
    {
        if (_multiSelected.Count < 2) { GroupRect.Visibility = Visibility.Collapsed; return; }

        Rect union = MarqueeSelection.Union(_multiSelected.Select(GetCanvasBounds));
        if (union.IsEmpty) { GroupRect.Visibility = Visibility.Collapsed; return; }

        double pad = 3 / _zoom;
        union.Inflate(pad, pad);
        Canvas.SetLeft(GroupRect, union.X);
        Canvas.SetTop(GroupRect, union.Y);
        GroupRect.Width = union.Width;
        GroupRect.Height = union.Height;
        GroupRect.StrokeThickness = 1.5 / _zoom;
        GroupRect.Visibility = Visibility.Visible;
    }

    private void ClearMultiSelection()
    {
        if (_multiSelected.Count == 0) return;
        _multiSelected.Clear();
        GroupRect.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Drops an in-progress rubber band without selecting anything — Escape, or a lost
    /// mouse capture (alt-tab, a right-click). Without this the band would stay painted
    /// on the canvas with no way to clear it.
    /// </summary>
    private void AbortMarquee()
    {
        if (!_marqueeActive) return;
        _marqueeActive = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------- Z zoom gesture

    /// <summary>
    /// Finishes a Z gesture: a click zooms in about the point, a drag fits the rectangle.
    /// The threshold is what separates the two, and it is in CONTENT pixels scaled by the
    /// zoom — at 800 % a three-pixel wobble on screen is a sub-pixel rectangle, and treating
    /// that as a drag would zoom to nothing.
    /// </summary>
    private void EndZoomGesture(Point pos)
    {
        _zoomDragging = false;
        Viewport.ReleaseMouseCapture();
        DragRect.Visibility = Visibility.Collapsed;

        if (_zoomArmedByMenu)
        {
            _zoomArmedByMenu = false;
            _zoomKeyDown = false;
            UpdateCursor();
        }

        var region = new Rect(_zoomDragStart, pos);
        double screenSpan = Math.Max(region.Width, region.Height) * _zoom;
        if (screenSpan < 6) ZoomToPoint(pos);
        else ZoomToRect(region);
    }

    /// <summary>
    /// Arms the zoom gesture from the menu, for people who found it there rather than on
    /// the keyboard. It lasts exactly one gesture — the key version stays armed only while
    /// Z is held, and a menu row that left the pointer in a mode until it was found again
    /// would be a trap.
    /// </summary>
    private void ArmZoomGesture()
    {
        _zoomKeyDown = true;
        _zoomArmedByMenu = true;
        UpdateCursor();
    }

    /// <summary>Drops an in-progress Z drag without zooming — Z released, or capture lost.</summary>
    private void AbortZoomGesture()
    {
        if (!_zoomDragging) return;
        _zoomDragging = false;
        Viewport.ReleaseMouseCapture();
        DragRect.Visibility = Visibility.Collapsed;
    }

    private void EndMove()
    {
        if (!_movingSelection) return;
        _movingSelection = false;
        Viewport.ReleaseMouseCapture();

        // A group move is one undo step that puts every member back.
        if (_multiSelected.Count > 1 && _moveTotal.Length >= 0.5)
        {
            var members = _multiSelected.ToArray();
            double gdx = _moveTotal.X, gdy = _moveTotal.Y;
            Push(new EditorAction(
                undo: () => { foreach (var m in members) MoveElement(m, -gdx, -gdy); UpdateMultiSelectionVisual(); },
                redo: () => { foreach (var m in members) MoveElement(m, gdx, gdy); UpdateMultiSelectionVisual(); }),
                apply: false);
            _moveTotal = new Vector();
            _moveRaw = new Vector();
            return;
        }

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
        _moveRaw = new Vector();
    }

    private void AbortMove()
    {
        if (!_movingSelection) return;
        _movingSelection = false;
        if (_moveTotal.X != 0 || _moveTotal.Y != 0)
        {
            if (_multiSelected.Count > 1)
            {
                foreach (UIElement member in _multiSelected)
                    MoveElement(member, -_moveTotal.X, -_moveTotal.Y);
                UpdateMultiSelectionVisual();
            }
            else if (_selected is not null)
            {
                MoveElement(_selected, -_moveTotal.X, -_moveTotal.Y);
            }
        }
        _moveTotal = new Vector();
        _moveRaw = new Vector();
        UpdateSelectionVisual();
    }

    private void Select(UIElement? element)
    {
        // Selecting a different element (or deselecting) ends any in-progress opacity drag
        // or counter re-numbering, committing each as one undo entry before moving on.
        if (_opacityElement is not null && !ReferenceEquals(_opacityElement, element))
            CommitOpacityGesture();
        if (_stepRetypeElement is not null && !ReferenceEquals(_stepRetypeElement, element))
            CommitStepRetype();
        // Picking one mark replaces any marquee group.
        if (element is not null) ClearMultiSelection();
        _selected = element;
        // Selecting a redaction pulls its own style and strength into the toolbar, so the
        // controls describe the mark you are looking at rather than the last one you drew.
        if (element is FrameworkElement fe && fe.Tag is AnnotationData meta && IsLoaded)
        {
            if (meta.Type == AnnotationData.TypeRedact)
            {
                _redactStyle = Redaction.Parse(meta.Style);
                _redactStrength = meta.Strength ?? Redaction.DefaultStrength;
                UpdateContextPanels();
            }
            else if (meta.Type == AnnotationData.TypeMagnifier)
            {
                // Same reason: the dial should read the callout you are looking at, not the
                // last one you drew.
                _magnification = MagnifierLayout.ClampScale(meta.Zoom ?? MagnifierLayout.DefaultScale);
                UpdateContextPanels();
            }
        }
        UpdateSelectionVisual();
    }

    private void UpdateSelectionVisual()
    {
        // The focus ring belongs to the selection, so it follows it everywhere the
        // selection chrome goes rather than being remembered at each call site.
        UpdateMagnifierFocusVisual();

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
            ArrowSelectionGlow.Visibility = Visibility.Collapsed;
            HideHandles();
            return;
        }
        // A dashed box around a diagonal arrow selects mostly empty space and says nothing
        // about which mark is picked. An arrow shows as a wide translucent copy of ITSELF.
        if (_selected is FrameworkElement stroke && stroke.Tag is AnnotationData strokeMeta &&
            strokeMeta.Type is AnnotationData.TypeArrow or AnnotationData.TypeCurvedArrow
                or AnnotationData.TypeLine)
        {
            ShowStrokeSelectionGlow(stroke, strokeMeta);
            SelectionRect.Visibility = Visibility.Collapsed;
            UpdateSelectionHandles();
            return;
        }
        ArrowSelectionGlow.Visibility = Visibility.Collapsed;

        // The frame is drawn in the annotation's UNROTATED box and then turned by the same
        // angle, so it hugs a rotated mark instead of ballooning into its axis-aligned box.
        double angle = _selected is FrameworkElement sel ? AnnotationTransform.AngleOf(sel) : 0;
        Rect b = _selected is FrameworkElement fe2 ? UnrotatedCanvasBounds(fe2) : GetCanvasBounds(_selected);
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
        SelectionRect.RenderTransform = angle == 0
            ? Transform.Identity
            : new RotateTransform(angle, b.Width / 2, b.Height / 2);
        SelectionRect.Visibility = Visibility.Visible;

        UpdateSelectionHandles();
    }

    /// <summary>
    /// Paints the selected arrow / line as a wide translucent copy of its own geometry.
    /// The copy rides the element's own move offset, so it stays glued to the mark while
    /// the mark is dragged.
    /// </summary>
    private void ShowStrokeSelectionGlow(FrameworkElement element, AnnotationData meta)
    {
        Geometry? data = element switch
        {
            Path path => path.Data,
            Line line => new LineGeometry(new Point(line.X1, line.Y1), new Point(line.X2, line.Y2)),
            _ => null,
        };
        if (data is null)
        {
            ArrowSelectionGlow.Visibility = Visibility.Collapsed;
            return;
        }

        var ink = (Brush)FindResource("InfoBrush");
        ArrowSelectionGlow.Data = data;
        ArrowSelectionGlow.Stroke = ink;
        // Only a filled head wants a fill; a bare line would swell into a blob.
        ArrowSelectionGlow.Fill = meta.Type == AnnotationData.TypeLine ? null : ink;
        ArrowSelectionGlow.StrokeThickness = (meta.Thickness ?? 4) + 7 / _zoom;
        ArrowSelectionGlow.StrokeLineJoin = PenLineJoin.Round;
        ArrowSelectionGlow.StrokeStartLineCap = PenLineCap.Round;
        ArrowSelectionGlow.StrokeEndLineCap = PenLineCap.Round;
        ArrowSelectionGlow.Opacity = 0.35;
        Vector offset = AnnotationTransform.OffsetOf(element);
        ArrowSelectionGlow.RenderTransform = offset.Length < 0.001
            ? Transform.Identity
            : new TranslateTransform(offset.X, offset.Y);
        ArrowSelectionGlow.Visibility = Visibility.Visible;
    }

    /// <summary>Bounds of an annotation in canvas coordinates, including its own render transform.</summary>
    private Rect GetCanvasBounds(UIElement element)
    {
        Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
        if (bounds.IsEmpty) bounds = new Rect(element.RenderSize);
        return element.TransformToAncestor(AnnotationCanvas).TransformBounds(bounds);
    }

}
