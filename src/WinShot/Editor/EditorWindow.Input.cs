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

    /// <summary>
    /// In-process pointer seam. The WPF handlers below wrap the real event into this so a
    /// harness can drive every tool with plain points on the hidden test desktop — no real
    /// mouse, no visible window. GetPosition mirrors MouseEventArgs for the two elements
    /// the tools ask about (the canvas in content px, the viewport in screen px).
    /// </summary>
    internal sealed class PointerArgs
    {
        public PointerArgs(Point canvas, Point viewport, MouseButton button = MouseButton.Left,
            int clickCount = 1, object? source = null, IReadOnlyList<Point>? canvasSamples = null)
        {
            Canvas = canvas; Viewport = viewport; ChangedButton = button; ClickCount = clickCount; OriginalSource = source;
            CanvasSamples = canvasSamples is { Count: > 0 } ? canvasSamples : new[] { canvas };
        }
        public Point Canvas { get; }

        /// <summary>
        /// Every sub-frame sample this event carried, in canvas coordinates. Survey feeds its
        /// ink tools getCoalescedEvents() (SVGAnnotationLayer.jsx:733-741) so a 120-240Hz pen
        /// contributes all of its points; WPF's equivalent is the stylus packet buffer. A plain
        /// mouse move has no history API, so it contributes exactly one sample.
        /// </summary>
        public IReadOnlyList<Point> CanvasSamples { get; }
        public Point Viewport { get; }
        public MouseButton ChangedButton { get; }
        public int ClickCount { get; }
        public object? OriginalSource { get; }
        public bool Handled { get; set; }
        public Point GetPosition(IInputElement relativeTo) => relativeTo is Canvas ? Canvas : Viewport;
    }

    private PointerArgs Wrap(MouseEventArgs e, MouseButton button, int clickCount) =>
        new(e.GetPosition(AnnotationCanvas), e.GetPosition(Viewport), button, clickCount, e.OriginalSource,
            CoalescedSamples(e));

    /// <summary>Stylus/touch packets behind this move, if any - WPF's coalesced events.</summary>
    private IReadOnlyList<Point>? CoalescedSamples(MouseEventArgs e)
    {
        if (e.StylusDevice is null) return null;
        try
        {
            var packets = e.StylusDevice.GetStylusPoints(AnnotationCanvas);
            if (packets is null || packets.Count == 0) return null;
            var pts = new List<Point>(packets.Count);
            foreach (var sp in packets) pts.Add(sp.ToPoint());
            return pts;
        }
        catch (InvalidOperationException) { return null; }
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = Wrap(e, e.ChangedButton, e.ClickCount);
        PointerDown(p);
        if (p.Handled) e.Handled = true;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e) => PointerMove(Wrap(e, MouseButton.Left, 1));

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        var p = Wrap(e, e.ChangedButton, 1);
        PointerUp(p);
        if (p.Handled) e.Handled = true;
    }

    internal void PointerDown(PointerArgs e)
    {
        if (_sourceOperationActive) return;
        if (IsRotationPillSource(e.OriginalSource)) return; // the pill is its own control
        // Survey keeps the fit mode across annotation clicks; only an explicit zoom
        // (ZoomAt -> ViewFitMode.Free, Survey's MANUAL) stops the view tracking the viewport.
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
                BeginResize(handle, pos);
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

    internal void PointerMove(PointerArgs e)
    {
        if (_sourceOperationActive) return;
        if (_counterDrag is not null)
        {
            ApplyCounterDragMove(e.GetPosition(AnnotationCanvas));
            return;
        }
        if (_panning)
        {
            var p = e.GetPosition(Viewport);
            ViewTranslate.X += p.X - _panLast.X;
            ViewTranslate.Y += p.Y - _panLast.Y;
            _panLast = p;
            ClampView();
            return;
        }
        if (_activeHandle < 0 && !_dragging && !_movingSelection && !_marqueeSelecting && _handleKind != HandleKind.None)
        {
            var hoverPos = e.GetPosition(AnnotationCanvas);
            UpdateRotationPillHover(hoverPos);
            // Survey paints a per-handle cursor (rotation-aware resize arrows, crosshair on
            // the rotation grip, grab on endpoints/tip). Restore the tool cursor on exit.
            if (!UpdateHandleCursor(hoverPos)) UpdateCursor();
        }
        UpdateCalloutHover(e.GetPosition(AnnotationCanvas));
        UpdateHoverHalo(e.GetPosition(AnnotationCanvas));
        UpdateEraserCursor(e.GetPosition(Viewport));
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
        if (_marqueeSelecting)
        {
            var marqueePos = e.GetPosition(AnnotationCanvas);
            // Survey latches `active` once the drag clears MIN_DRAG_PX and never clears it
            // (useSVGInteraction.js:1159), and draws no band below the threshold.
            _marqueeArmed |= MarqueeSelection.IsDrag(_marqueeStart, marqueePos);
            if (_marqueeArmed) ShowMarquee(marqueePos);
            return;
        }
        if (_movingCalloutBox && _selected is CalloutAnnotation boxCallout)
        {
            var posMove = e.GetPosition(AnnotationCanvas);
            var d = posMove - _moveLast;
            if (d.X != 0 || d.Y != 0)
            {
                NudgeCalloutBox(boxCallout, d.X, d.Y);
                _moveTotal += d;
                _moveLast = posMove;
                UpdateSelectionVisual();
            }
            return;
        }
        if (_movingSelection && _selected is not null)
        {
            // Survey 2026-04-20: mid-drag Shift on a counter swaps the move for an orbit around
            // the frozen nub tip, matching the creation-time gesture (useSVGInteraction.js:1199-1240).
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _selection.Count == 1
                && _selected is Grid stepBadge
                && stepBadge.Tag is AnnotationData { Type: AnnotationData.TypeStep } stepMeta)
            {
                _movingSelection = false;
                BeginCounterOrbit(stepBadge, stepMeta, e.GetPosition(AnnotationCanvas), shift: true);
                return;
            }
            var posMove = e.GetPosition(AnnotationCanvas);
            // Survey drags freely and applies constrainToPage on release, so an off-page
            // drag snaps back inside on drop rather than sticking at the edge mid-gesture.
            var d = posMove - _moveLast;
            if (d.X != 0 || d.Y != 0)
            {
                foreach (UIElement el in SelectionList())
                    MoveElement(el, d.X, d.Y);
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
                // Survey's in-progress guide is shaft-only: no arrowhead until commit
                // (SVGAnnotationLayer.jsx:4992-5005).
                arrow.Data = LineCurve.Stroke(_dragStart, pos, null);
                break;
            case EditorTool.Callout:
                UpdateCalloutPreview(_dragStart, CalloutLayout.PushBoxOffTip(_dragStart, pos));
                break;
            case EditorTool.Text:
                // Survey's preview only appears once the pointer has moved past 10 SCREEN px
                // on either axis (PDFViewer.jsx:31477-31487); below that nothing is drawn.
                if (TextDragExceedsThreshold(pos))
                {
                    SetTextPreviewStyle();
                    ShowDragRect(ShapeConstraint.BoxFromDrag(_dragStart, pos, square: false), dim: false);
                }
                else
                {
                    HideDragRect();
                }
                break;
            case EditorTool.CurvedArrow when _activeShape is Path curve:
                curve.Data = AnnotationFactory.CurvedArrowGeometry(
                    _dragStart, AnnotationFactory.DefaultCurveControl(_dragStart, pos), pos, _thickness);
                break;
            case EditorTool.Line when _activeShape is Path linePath:
                linePath.Data = LineCurve.Stroke(_dragStart, pos, null);
                break;
            case EditorTool.Rectangle or EditorTool.Ellipse when _activeShape is not null:
                var r = ShapeConstraint.BoxFromDrag(_dragStart, pos, square: false);
                Canvas.SetLeft(_activeShape, r.X);
                Canvas.SetTop(_activeShape, r.Y);
                _activeShape.Width = Math.Max(1, r.Width);
                _activeShape.Height = Math.Max(1, r.Height);
                if (_activeShape is Path cloudPath && _lineStyle == LineBorderStyle.Cloud)
                    cloudPath.Data = CloudPath.ForRectangle(new Rect(0, 0, cloudPath.Width, cloudPath.Height),
                        _cloudIntensity, cloudPath.StrokeThickness);
                break;
            case EditorTool.Freehand or EditorTool.Highlighter when _activeShape is Polyline stroke:
                // Survey appends every coalesced sample clearing the 0.2 page-px dedupe and
                // draws a plain constant-width polyline preview (SVGAnnotationLayer.jsx:5021-5040).
                foreach (var sample in e.CanvasSamples)
                    AppendInkSample(stroke, sample);
                break;
            case EditorTool.Eraser:
                // Same 0.2 gate the ink tools use (FabricEraserCanvas.jsx:2197-2205), over
                // coalesced samples, so erase coverage tracks the pointer at the same density.
                foreach (var sample in e.CanvasSamples)
                {
                    if (_eraserPoints.Count > 0 &&
                        (sample - _eraserPoints[^1]).Length < PaperInk.LiveMinDistance) continue;
                    if (_eraserPoints.Count > 0) EraseLive(_eraserPoints[^1], sample);
                    _eraserPoints.Add(sample);
                }
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

    internal void PointerUp(PointerArgs e)
    {
        if (_sourceOperationActive) return;
        if (_counterDrag is not null && e.ChangedButton == MouseButton.Left)
        {
            ApplyCounterDragMove(e.GetPosition(AnnotationCanvas));
            CommitCounterDrag();
            e.Handled = true;
            return;
        }
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
        if (_marqueeSelecting)
        {
            EndMarquee(e.GetPosition(AnnotationCanvas));
            return;
        }
        if (_movingSelection || _movingCalloutBox)
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
                pos = ShapeConstraint.SnapLineEnd(_dragStart, pos, snap: false);
                if ((pos - _dragStart).Length < 3)
                {
                    AnnotationCanvas.Children.Remove(shape);
                }
                else
                {
                    if (shape is Path livePath)
                    {
                        EndLinePreviewStyle(livePath, _tool == EditorTool.Arrow ? livePath.Stroke : null);
                        livePath.Data = _tool == EditorTool.Arrow
                            ? AnnotationFactory.ArrowGeometry(_dragStart, pos, livePath.StrokeThickness, _arrowhead, _startArrowhead)
                            : LineCurve.Stroke(_dragStart, pos, null);
                    }
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
                ClearCalloutPreview();
                // Survey: a drag shorter than 4 page-px is a cancel, not a default callout
                // (SVGAnnotationLayer.jsx:1113-1117) - no stray 0x0 callout is committed.
                if ((pos - _dragStart).Length < CalloutLayout.MinDrag) return;
                Point boxOrigin = CalloutLayout.PushBoxOffTip(_dragStart, pos);
                // Survey switches out of the creation tool as edit mode opens, so the next
                // page click commits the editor instead of starting another callout. Done
                // BEFORE the editor exists, or OnToolChecked's CommitText would delete it.
                CheckToolButton(EditorTool.Select);
                CommitCallout(_dragStart, boxOrigin);
                return;
            case EditorTool.CurvedArrow:
                if (shape is not Path curve) return;
                if ((pos - _dragStart).Length < 3) AnnotationCanvas.Children.Remove(curve);
                else
                {
                    EndLinePreviewStyle(curve, curve.Stroke);
                    BeginCurveEdit(curve, _dragStart, pos);
                }
                return;
            case EditorTool.Text:
                HideDragRect();
                ResetDragRectStyle();
                if (!inImageAt(_dragStart)) return;
                // Survey decides click-vs-drag on pointer UP with the same 10 screen px test
                // (PDFViewer.jsx:31502 `isDrag = dx > 10 || dy > 10`).
                if (!TextDragExceedsThreshold(pos))
                {
                    // With the text tool armed, a plain click INSIDE an existing textbox
                    // re-opens it instead of creating a new one (PDFViewer.jsx:31502-31519).
                    if (HitTestAnnotation(_dragStart) is FrameworkElement hit &&
                        hit.Tag is AnnotationData { Type: AnnotationData.TypeText })
                    {
                        BeginTextReEdit(hit);
                        return;
                    }
                    PlaceText(_dragStart);
                }
                else
                {
                    PlaceTextInBox(ShapeConstraint.BoxFromDrag(_dragStart, pos, square: false));
                }
                return;
            case EditorTool.Rectangle or EditorTool.Ellipse:
                if (shape is null) return;
                var r = ShapeConstraint.BoxFromDrag(_dragStart, pos, square: false);
                // Survey rejects the commit unless BOTH axes exceed 2 px
                // (annotationCreationCommit.js:150), so no 1x200 sliver survives.
                if (r.Width <= 2 || r.Height <= 2)
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
                        bounds, strokeColor, shape.StrokeThickness, _fillMode, fillColor, lineStyle,
                        _cloudIntensity);
                    PushAddElement(shape);
                }
                return;
            case EditorTool.Freehand or EditorTool.Highlighter:
                if (shape is Polyline stroke && stroke.Points.Count >= 1)
                {
                    // Survey appends the release sample on pointerup before committing
                    // (SVGAnnotationLayer.jsx:1342-1348), then commits the raw samples
                    // (0.2 live dedupe, then 0.35 compaction that always keeps the true
                    // last point) and lets the swept-disk outline do the rest.
                    AppendInkSample(stroke, pos);
                    var pts = PaperInk.Compact(stroke.Points);
                    AnnotationCanvas.Children.Remove(stroke);
                    bool highlighter = _tool == EditorTool.Highlighter;
                    double width = highlighter
                        ? AnnotationStyle.HighlighterWidth(_penSize)
                        : AnnotationStyle.ClampSize(_penSize);
                    Color inkColor = highlighter ? AnnotationStyle.HighlighterColor : AnnotationStyle.Opaque(_color);
                    var ink = AnnotationFactory.CreateInk(pts, inkColor, width, highlighter);
                    ink.Tag = AnnotationData.ForStroke(
                        highlighter ? AnnotationData.TypeHighlighter : AnnotationData.TypeFreehand,
                        pts, inkColor, width);
                    AnnotationCanvas.Children.Add(ink);
                    if (highlighter)
                        RefreshHighlighterFill(ink);
                    // Survey never auto-selects a freehand commit: "Do NOT auto-select pen
                    // strokes or highlighter paths" (PageAnnotationLayer.jsx:5246-5247).
                    PushAddElement(ink, select: false);
                }
                else if (shape is not null) AnnotationCanvas.Children.Remove(shape);
                return;
            case EditorTool.Eraser:
                ApplyEraserStroke(pos);
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

    private void DrawMouseDown(Point pos, PointerArgs e)
    {
        bool hadOpenText = _activeText is not null;
        CommitText();
        bool inImage = pos.X >= 0 && pos.Y >= 0 && pos.X <= _source.Width && pos.Y <= _source.Height;

        // Clicking an existing mark selects it instead of stacking a new one on top.
        // Region effects act on the image under the drag; they never pick up a mark.
        // The Text tool is excluded: Survey's text overlay owns the whole page, so a press
        // over an existing mark starts the drag-to-size gesture and only the RELEASE decides
        // between re-editing that textbox and creating a new one (PDFViewer.jsx:31491-31521).
        if (_tool is not (EditorTool.Eraser or EditorTool.Eyedropper or EditorTool.Crop or EditorTool.Pan
                or EditorTool.Blur or EditorTool.Pixelate or EditorTool.Spotlight or EditorTool.Text)
            && HitTestAnnotation(pos) is { } existing)
        {
            Select(existing);
            if (e.ClickCount == 2 && existing is FrameworkElement fe && fe.Tag is AnnotationData tm)
            {
                if (tm.Type == AnnotationData.TypeText)
                {
                    BeginTextReEdit(fe);
                    e.Handled = true;
                    return;
                }
                if (tm.Type == AnnotationData.TypeCallout && fe is CalloutAnnotation callout)
                {
                    BeginCalloutReEdit(callout);
                    e.Handled = true;
                    return;
                }
            }
            if (existing is CalloutAnnotation existingCallout && CalloutBoxContains(existingCallout, pos))
            {
                _movingCalloutBox = true;
                _calloutBoxBefore = existingCallout.Layout;
            }
            else
                _movingSelection = true;
            _moveLast = pos;
            _moveTotal = new Vector();
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == EditorTool.Text)
        {
            // Survey: click places a default box; drag sizes the box. A click that
            // just committed an open editor must not immediately open another.
            if (!hadOpenText && inImage)
            {
                _dragStart = pos;
                _dragging = true;
                Viewport.CaptureMouse();
                // Nothing is shown until the 10 screen px deadband is crossed.
            }
            e.Handled = true;
            return;
        }
        if (_tool == EditorTool.Step)
        {
            // Survey places a counter by press-drag-release, not by click
            // (PDFViewer.jsx:31727-31790); PointerUp commits it.
            if (inImage) BeginStepPlacement(pos);
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
            // Survey snapshots mode + radius into pointer.gestureConfig at pointer-down:
            // "Gesture behavior is immutable after pointer-down. Toolbar updates affect the
            // next gesture only" (FabricEraserCanvas.jsx:2146-2152).
            _gestureEraserSize = AnnotationStyle.ClampEraserSize(_eraserSize);
            _gestureEraserPartial = _eraserPartial;
            _dragStart = pos;
            _dragging = true;
            Viewport.CaptureMouse();
            // A stationary press already carves under the ring (FabricEraserCanvas.jsx:2174).
            EraseLive(pos, pos);
            e.Handled = true;
            return;
        }

        _dragStart = _tool is EditorTool.Blur or EditorTool.Crop or EditorTool.Pixelate or EditorTool.Spotlight
            ? ClampToSurface(pos) : pos;
        _dragging = true;
        Viewport.CaptureMouse();
        _thickness = ActiveSize();
        double strokeWidth = _tool == EditorTool.Highlighter
            ? AnnotationStyle.HighlighterWidth(_penSize)
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
                    Data = LineCurve.Stroke(_dragStart, _dragStart, null),
                };
                ApplyLinePreviewStyle(_activeShape, strokeWidth);
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
                ApplyLinePreviewStyle(_activeShape, strokeWidth);
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
                ApplyLinePreviewStyle(_activeShape, strokeWidth);
                break;
            case EditorTool.Rectangle:
                if (_lineStyle == LineBorderStyle.Cloud)
                {
                    _activeShape = new Path
                    {
                        Stroke = brush,
                        StrokeThickness = strokeWidth,
                        Fill = fillBrush,
                        Data = CloudPath.ForRectangle(new Rect(0, 0, 1, 1), _cloudIntensity, strokeWidth),
                    };
                }
                else
                {
                    // Survey commits rx:0/ry:0 and renders <rect> with no rx — square corners
                    // during the drag and after release (annotationCreationCommit.js:180-181).
                    _activeShape = new Rectangle
                    {
                        Stroke = brush,
                        StrokeThickness = strokeWidth,
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
            case EditorTool.Highlighter:
                // Survey's in-flight ink is a plain constant-width <polyline> with round caps
                // and joins (SVGAnnotationLayer.jsx:5021-5040); the swept-disk fill only shows
                // up at commit, so nothing is rebuilt per move. The pen paints the picker's RAW
                // colour - buildFreehandCommitJSON never composes pen with strokeOpacity
                // (annotationCreationCommit.js:288-292) - and the highlighter paints the fixed
                // rgba(255,193,7,.3) multiplied against the page, exactly as the commit will.
                bool blend = _tool == EditorTool.Highlighter;
                _activeShape = new Polyline
                {
                    Stroke = blend
                        ? HighlighterPreviewBrush()
                        : new SolidColorBrush(AnnotationStyle.Opaque(_color)),
                    StrokeThickness = blend
                        ? AnnotationStyle.HighlighterWidth(_penSize)
                        : AnnotationStyle.ClampSize(_penSize),
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

    private bool inImageAt(Point pos) =>
        pos.X >= 0 && pos.Y >= 0 && pos.X <= _source.Width && pos.Y <= _source.Height;

    /// <summary>
    /// Survey's live dedupe: keep a sample only once it is >= 0.2 page px from the last kept
    /// one (SVGAnnotationLayer.jsx:737-740) - inclusive, matching Math.hypot(...) >= 0.2.
    /// </summary>
    private static void AppendInkSample(Polyline stroke, Point sample)
    {
        if (stroke.Points.Count == 0 || (sample - stroke.Points[^1]).Length >= PaperInk.LiveMinDistance)
            stroke.Points.Add(sample);
    }

    /// <summary>
    /// Survey's eraser ring (FabricEraserCanvas.jsx:2435-2450): a 1px rgba(17,24,39,.9)
    /// border over an rgba(255,255,255,.18) fill with a 1px rgba(255,255,255,.72) halo, so
    /// it reads on any background. Only the diameter scales with zoom - updateEraserCursor
    /// (:487-500) scales width/height/transform and nothing else, so both rings stay
    /// hairlines. WPF strokes straddle the geometry, so the dark ring is drawn at d-1 and
    /// the halo at d+1 to land where the CSS border-box border and its box-shadow do.
    /// </summary>
    private void UpdateEraserCursor(Point viewportPos)
    {
        if (EraserCursor is null) return;
        if (_tool != EditorTool.Eraser || _panning || _spaceDown)
        {
            EraserCursor.Visibility = Visibility.Collapsed;
            if (EraserCursorHalo is not null) EraserCursorHalo.Visibility = Visibility.Collapsed;
            return;
        }
        double d = AnnotationStyle.ClampEraserSize(_eraserSize) * _zoom;
        double ring = Math.Max(1, d - 1);
        EraserCursor.Width = EraserCursor.Height = ring;
        Canvas.SetLeft(EraserCursor, viewportPos.X - ring / 2);
        Canvas.SetTop(EraserCursor, viewportPos.Y - ring / 2);
        EraserCursor.Visibility = Visibility.Visible;
        if (EraserCursorHalo is not null)
        {
            double halo = Math.Max(1, d + 1);
            EraserCursorHalo.Width = EraserCursorHalo.Height = halo;
            Canvas.SetLeft(EraserCursorHalo, viewportPos.X - halo / 2);
            Canvas.SetTop(EraserCursorHalo, viewportPos.Y - halo / 2);
            EraserCursorHalo.Visibility = Visibility.Visible;
        }
    }

    private static Color StrokeColorOf(Shape shape) =>
        shape.Stroke is SolidColorBrush brush ? brush.Color : Colors.White;

    /// <summary>
    /// Survey commitInterruptedPointer (FabricEraserCanvas.jsx:2081-2098): a gesture cut short
    /// by pointercancel, a lost capture or a mid-swipe zoom COMMITS what it already carved,
    /// because - in Survey's own words - "silently un-erasing that work reads as 'the eraser
    /// randomly doesn't take'". It routes through the normal release path (queueEraserCommit ->
    /// applyEraserAndCommit), so the whole-gesture carve and its single undo entry both happen,
    /// and pointerRef is nulled FIRST so nothing leaks into the next gesture.
    /// Dropping the carve was worse than losing it: the preview mutation stayed painted with no
    /// undo step, and the stale _eraseBefore snapshots made the NEXT gesture recompute those
    /// strokes from their pristine originals - visibly restoring erased ink.
    /// </summary>
    private void CommitInterruptedEraser()
    {
        if (_tool == EditorTool.Eraser && _eraserPoints.Count > 0)
            ApplyEraserStroke(_eraserPoints[^1]);
        _eraserPoints.Clear();
        _eraseBefore.Clear();
        _eraseRemoved.Clear();
    }

    private void AbortDrag()
    {
        if (!_dragging) return;
        CommitInterruptedEraser();
        _dragging = false;
        _eraserPoints.Clear();
        if (_activeShape is not null)
        {
            AnnotationCanvas.Children.Remove(_activeShape);
            _activeShape = null;
        }
        ClearCalloutPreview();
        if (_pendingCrop is null) HideDragRect();
    }

    /// <summary>
    /// Survey's creation ghost (SVGAnnotationLayer.jsx:4855-4949): fixed slate chrome at 0.6
    /// opacity with 5,5 dashes and a solid triangle head - deliberately NOT the toolbar style,
    /// so the committed callout (solid, full opacity, user style) is visually distinct.
    /// </summary>
    private void UpdateCalloutPreview(Point tip, Point boxOrigin)
    {
        var layout = CalloutLayout.FromDrag(tip, boxOrigin,
            descender: CalloutLayout.DefaultFontSize * CalloutLayout.DescenderRatio);
        var ghost = Color.FromRgb(0x1E, 0x29, 0x3B);
        var clear = Color.FromArgb(0, 0, 0, 0);
        if (_previewCallout is null)
        {
            _previewCallout = AnnotationFactory.CreateCallout(
                layout, "", ghost, clear, 2, ArrowheadStyle.SolidTriangle,
                LineBorderStyle.Dashed, CalloutLayout.DefaultFontSize, CalloutLayout.DefaultFontFamily);
            _previewCallout.IsHitTestVisible = false;
            AnnotationCanvas.Children.Add(_previewCallout);
        }
        else
        {
            _previewCallout.Apply(layout, "", ghost, clear, 2, ArrowheadStyle.SolidTriangle,
                LineBorderStyle.Dashed, CalloutLayout.DefaultFontSize, CalloutLayout.DefaultFontFamily);
        }
        _previewCallout.ApplyGhost();
    }

    private void ClearCalloutPreview()
    {
        if (_previewCallout is null) return;
        AnnotationCanvas.Children.Remove(_previewCallout);
        _previewCallout = null;
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

    private bool _marqueeSelecting;
    private bool _marqueeArmed;
    private Point _marqueeStart;

    /// <summary>
    /// Survey's post-drag dblclick guard (useSVGInteraction.js:250-257, 770-776): a
    /// click-to-select followed by a click-and-drag emits a spurious dblclick that must not
    /// open edit mode. Stamped at every drag-commit exit.
    /// </summary>
    private long _justDraggedAt;
    private const long DoubleClickGuardMs = 400;
    private const long EditModeCooldownMs = 300;

    private void SelectMouseDown(Point pos, PointerArgs e)
    {
        CommitText();
        var hit = HitTestAnnotation(pos);
        if (hit is null)
        {
            // Survey does NOT deselect at pointer-down (useSVGInteraction.js:1131-1132) — the
            // release decides, so a Shift-marquee can add to the existing selection.
            _marqueeSelecting = true;
            _marqueeArmed = false;
            _marqueeStart = pos;
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }
        // Double-click any text annotation (Plain/Bold/Huge TextBlock, Outline Path, or
        // Pill Border) to reopen it for editing; the owning element carries its style.
        if (e.ClickCount == 2 && Environment.TickCount64 - _justDraggedAt >= DoubleClickGuardMs
            && hit is FrameworkElement fe && fe.Tag is AnnotationData tm)
        {
            if (tm.Type == AnnotationData.TypeText)
            {
                BeginTextReEdit(fe);
                e.Handled = true;
                return;
            }
            if (tm.Type == AnnotationData.TypeCallout && fe is CalloutAnnotation callout)
            {
                BeginCalloutReEdit(callout);
                e.Handled = true;
                return;
            }
            // Survey's bbox edit mode covers counter, line and arrow (and WinShot's curved
            // arrow, which rides on Survey's 'line'); rect/ellipse/ink are explicit no-ops.
            if (tm.Type is AnnotationData.TypeStep or AnnotationData.TypeLine
                    or AnnotationData.TypeArrow or AnnotationData.TypeCurvedArrow
                && Environment.TickCount64 - _editModeLeftAt >= EditModeCooldownMs)
            {
                Select(hit);
                _bboxEditTarget = fe;
                UpdateSelectionVisual();
                e.Handled = true;
                return;
            }
        }
        // Survey gates the multi-select toggle on Shift alone (useSVGInteraction.js:536);
        // Ctrl/Cmd is reserved (it means "move the whole callout" there).
        bool additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        // Survey UX 2026-04-20: Shift-pointerdown on a single-selected counter enters orbit
        // BEFORE the multi-select toggle, or the toggle would always win and the orbit drag
        // could never fire (useSVGInteraction.js:488-533). A movement under ~3px still commits
        // as a plain toggle, so a pure Shift-click is unchanged.
        if (additive && hit is Grid orbitBadge
            && orbitBadge.Tag is AnnotationData { Type: AnnotationData.TypeStep } orbitMeta
            && !(_selection.Count > 1 && _selection.Contains(hit)))
        {
            BeginCounterOrbit(orbitBadge, orbitMeta, pos, shift: true);
            e.Handled = true;
            return;
        }
        if (additive)
            ToggleSelection(hit);
        else if (!(_selection.Count > 1 && _selection.Contains(hit)))
            Select(hit);
        _movingCalloutBox = false;
        _calloutBoxBefore = null;
        if (!additive && hit is CalloutAnnotation calloutHit && CalloutBoxContains(calloutHit, pos))
        {
            _movingCalloutBox = true;
            _calloutBoxBefore = calloutHit.Layout;
        }
        else
            _movingSelection = _selection.Contains(hit);
        _moveLast = pos;
        _moveTotal = new Vector();
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// Live rubber band, styled by drag direction: blue and solid dragging right (Window,
    /// takes only what is wholly inside), green and dashed dragging left (Crossing, takes
    /// anything it touches). The AutoCAD convention Survey uses — no modifier to remember.
    /// </summary>
    private void ShowMarquee(Point pos)
    {
        // Survey clamps the live end point to the page viewBox (useSVGInteraction.js:1148-1151)
        // so the band never leaves the image, and strokes it at 1 screen px.
        pos = ClampToSurface(pos);
        DragRect.StrokeThickness = 1 / _zoom;
        bool window = MarqueeSelection.ModeFor(_marqueeStart, pos) == MarqueeMode.Window;
        DragRect.Stroke = new SolidColorBrush(window
            ? Color.FromArgb(0xCC, 0x00, 0x64, 0xFF)
            : Color.FromArgb(0xCC, 0x00, 0xC8, 0x64));
        DragRect.Fill = new SolidColorBrush(window
            ? Color.FromArgb(0x26, 0x00, 0x64, 0xFF)
            : Color.FromArgb(0x26, 0x00, 0xC8, 0x64));
        DragRect.StrokeDashArray = window ? null : new DoubleCollection { 5, 5 };
        ShowDragRect(MarqueeSelection.RectFor(_marqueeStart, pos), dim: false);
    }

    /// <summary>
    /// Survey's line/arrow draw-time guide (SVGAnnotationLayer.jsx:4992-5005): translucent,
    /// dashed 5/5, round caps, no fill and no arrowhead. The solid stroke, the user's own line
    /// style and the head all appear only on commit.
    /// </summary>
    private static void ApplyLinePreviewStyle(Shape shape, double strokeWidth)
    {
        shape.Fill = null;
        shape.Opacity = 0.6;
        // WPF dash units are multiples of StrokeThickness, so divide to land on 5 px.
        double t = strokeWidth > 0 ? strokeWidth : 1;
        shape.StrokeDashArray = new DoubleCollection { 5 / t, 5 / t };
        shape.StrokeDashCap = PenLineCap.Round;
    }

    /// <summary>Swaps a line/arrow preview back to its committed look.</summary>
    private void EndLinePreviewStyle(Shape shape, Brush? fill)
    {
        shape.Opacity = 1;
        shape.Fill = fill;
        AnnotationFactory.ApplyDash(shape, _lineStyle);
    }

    /// <summary>Puts the drag rectangle back to its ordinary look after a marquee or text drag.</summary>
    private void ResetDragRectStyle()
    {
        DragRect.Stroke = (Brush)FindResource("InfoBrush");
        DragRect.Fill = new SolidColorBrush(Color.FromArgb(0x11, 0x4D, 0xA3, 0xFF));
        DragRect.StrokeDashArray = new DoubleCollection { 4, 3 };
        DragRect.StrokeThickness = 1.5 / _zoom;
        DragRect.RadiusX = DragRect.RadiusY = 0;
    }

    /// <summary>
    /// Survey's text-tool creation preview: <c>2px dashed rgba(59,130,246,0.5)</c> over
    /// <c>rgba(59,130,246,0.05)</c> with a 2px corner radius (PDFViewer.jsx:31537-31543).
    /// DragRect is shared with crop / marquee, so <see cref="ResetDragRectStyle"/> undoes this.
    /// </summary>
    private void SetTextPreviewStyle()
    {
        DragRect.Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0x3B, 0x82, 0xF6));
        DragRect.Fill = new SolidColorBrush(Color.FromArgb(0x0D, 0x3B, 0x82, 0xF6));
        DragRect.StrokeThickness = 2 / _zoom;
        // WPF dash units are multiples of StrokeThickness; CSS `2px dashed` draws ~2:2 px.
        DragRect.StrokeDashArray = new DoubleCollection { 1, 1 };
        DragRect.RadiusX = DragRect.RadiusY = 2 / _zoom;
    }

    /// <summary>Survey's text drag gate: 10 SCREEN px on either axis (PDFViewer.jsx:31502).</summary>
    private bool TextDragExceedsThreshold(Point pos) =>
        Math.Abs(pos.X - _dragStart.X) * _zoom > 10 || Math.Abs(pos.Y - _dragStart.Y) * _zoom > 10;

    private void EndMarquee(Point pos)
    {
        bool armed = _marqueeArmed;
        _marqueeSelecting = false;
        _marqueeArmed = false;
        HideDragRect();
        ResetDragRectStyle();
        Viewport.ReleaseMouseCapture();
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        if (!armed)
        {
            // Sub-threshold release is a click: Survey deselects unless Shift is held
            // (useSVGInteraction.js:2714-2722).
            if (!shift) Select(null);
            return;
        }
        pos = ClampToSurface(pos);
        var box = MarqueeSelection.RectFor(_marqueeStart, pos);
        MarqueeMode mode = MarqueeSelection.ModeFor(_marqueeStart, pos);
        var hits = new List<UIElement>();
        foreach (UIElement child in AnnotationCanvas.Children)
        {
            if (child is TextBox) continue;
            if (child is not FrameworkElement fe || fe.Tag is not AnnotationData) continue;
            if (MarqueeSelection.Catches(box, GetCanvasBounds(fe), mode, CanvasGeometryOf(fe)))
                hits.Add(fe);
        }

        // Survey applies the result by modifier — Alt subtracts (and wins over Shift), Shift
        // unions, neither replaces (useSVGInteraction.js:2782-2828). An empty hit set is a
        // no-op under a modifier and a full clear without one.
        if (alt || shift)
        {
            if (hits.Count == 0) return;
            var next = SelectionList().ToList();
            foreach (UIElement hit in hits)
            {
                if (alt) next.Remove(hit);
                else if (!next.Contains(hit)) next.Add(hit);
            }
            if (next.Count == 0) Select(null);
            else if (next.Count == 1) Select(next[0]);
            else SelectMany(next);
            return;
        }
        if (hits.Count == 0) Select(null);
        else if (hits.Count == 1) Select(hits[0]);
        else SelectMany(hits);
    }

    /// <summary>
    /// The annotation's own outline in canvas space, widened by its stroke so an unfilled
    /// ring or a diagonal line is probed as the shape it draws, not as its bounding box.
    /// Null for elements with no vector geometry (text, images), which fall back to bounds.
    /// </summary>
    private static Geometry? CanvasGeometryOf(FrameworkElement fe)
    {
        if (fe is not Shape shape || shape.RenderedGeometry is not { } geo || geo.IsEmpty()) return null;
        double t = Math.Max(1, shape.StrokeThickness);
        // Survey's doesRectIntersectEllipse honours hasVisiblePaint(fill): an unfilled shape
        // is tested as a ring, a filled one as a disk (geometryHitTest.js:2135).
        Geometry probe = HasVisiblePaint(shape.Fill)
            ? geo.GetFlattenedPathGeometry()
            : geo.GetWidenedPathGeometry(new Pen(Brushes.Black, t));
        return ToCanvasSpace(fe, probe);
    }

    private static bool HasVisiblePaint(Brush? brush) =>
        brush is not null && brush is not SolidColorBrush { Color.A: 0 };

    /// <summary>Clones <paramref name="geo"/> with the element→canvas transform baked in.</summary>
    private static Geometry ToCanvasSpace(FrameworkElement fe, Geometry geo)
    {
        var clone = geo.Clone();
        var toCanvas = new TranslateTransform(
            double.IsNaN(Canvas.GetLeft(fe)) ? 0 : Canvas.GetLeft(fe),
            double.IsNaN(Canvas.GetTop(fe)) ? 0 : Canvas.GetTop(fe));
        clone.Transform = new MatrixTransform(
            Matrix.Multiply((fe.RenderTransform ?? Transform.Identity).Value, toCanvas.Value));
        return clone;
    }

    /// <summary>
    /// Survey's hover halo (SVGAnnotationLayer.jsx:3839-3840 and every shape branch): the
    /// hovered-but-unselected mark is traced in #4a90e2 at 0.4 opacity, using its OWN geometry
    /// and rotation rather than a bounding box, so a click's target is obvious before it lands.
    /// </summary>
    private void UpdateHoverHalo(Point pos)
    {
        // Survey paints the same glow from the pan-mode `pendingHover` path
        // (SVGAnnotationLayer.jsx:598-633): "one glow implementation serves both modes."
        bool hoverMode = _tool is EditorTool.Select or EditorTool.Pan || _spaceDown;
        UIElement? hovered = hoverMode && _activeHandle < 0 && !_dragging
            && !_marqueeSelecting && !_movingSelection && !_panning
            ? HitTestAnnotation(pos)
            : null;
        // A member of a multi-selection already carries its halo from UpdateSelectionHalos;
        // a single-selected mark shows its own overlay instead (SVGAnnotationLayer.jsx:3839-3840).
        if (hovered is not null && _selection.Contains(hovered))
            hovered = null;
        if (hovered is not FrameworkElement fe || !PaintHalo(HoverHalo, fe))
            HoverHalo.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Traces <paramref name="fe"/>'s own geometry (and rotation) into <paramref name="halo"/>
    /// with Survey's per-type halo weights. False when the mark has no vector outline to trace.
    /// </summary>
    private bool PaintHalo(Path halo, FrameworkElement fe)
    {
        if (fe.Tag is not AnnotationData meta || fe is CalloutAnnotation) return false;
        Geometry? geo = fe is Shape s && s.RenderedGeometry is { } rg && !rg.IsEmpty()
            ? ToCanvasSpace(fe, rg)
            : meta.Type == AnnotationData.TypeStep
                ? StepPinGeometry(fe)
                : null;
        if (geo is null) return false;

        double sw = meta.Thickness ?? (fe is Shape sh ? sh.StrokeThickness : 1);
        double inv = 1 / _zoom;
        bool ink = meta.Type is AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter;
        halo.Data = geo;
        if (ink)
        {
            // Survey's filled-outline ink halo: a thin trace plus a 12% wash inside it.
            halo.StrokeThickness = Math.Max(1.25 * inv, Math.Min(2 * inv, sw + 0.5));
            halo.Fill = new SolidColorBrush(Color.FromArgb(0x1F, 0x4A, 0x90, 0xE2));
        }
        else
        {
            // Counters hard-code 6; every other shape uses max(6, strokeWidth + 4).
            halo.StrokeThickness = meta.Type == AnnotationData.TypeStep ? 6 : Math.Max(6, sw + 4);
            halo.Fill = null;
        }
        halo.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>
    /// Survey drops the per-item dashed boxes in a multi-selection and gives every member the
    /// SAME blue glow the cursor hover paints — the union frame is the "this is a group"
    /// affordance (SVGAnnotationLayer.jsx:3826-3840). A single selection keeps its own overlay.
    /// </summary>
    private readonly List<Path> _selectionHalos = new();

    private void UpdateSelectionHalos()
    {
        if (HoverHalo is null) return;
        int wanted = _selection.Count > 1 ? _selection.Count : 0;
        while (_selectionHalos.Count < wanted)
        {
            var halo = new Path
            {
                Stroke = HoverHalo.Stroke,
                Opacity = HoverHalo.Opacity,
                Fill = null,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            // Directly beneath HoverHalo, i.e. still under the selection chrome and handles.
            InteractionCanvas.Children.Insert(
                InteractionCanvas.Children.IndexOf(HoverHalo), halo);
            _selectionHalos.Add(halo);
        }
        for (int i = 0; i < _selectionHalos.Count; i++)
        {
            bool painted = i < wanted
                && _selection[i] is FrameworkElement fe
                && AnnotationCanvas.Children.Contains(_selection[i])
                && PaintHalo(_selectionHalos[i], fe);
            if (!painted) _selectionHalos[i].Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The counter's pin outline in canvas space (its badge Grid holds the Path).</summary>
    private static Geometry? StepPinGeometry(FrameworkElement fe) =>
        fe is Grid badge && badge.Children.OfType<Path>().FirstOrDefault()?.Data is { } pin
            ? ToCanvasSpace(fe, pin)
            : null;

    private void EndMove()
    {
        if (_movingCalloutBox)
        {
            _movingCalloutBox = false;
            Viewport.ReleaseMouseCapture();
            if (_selected is CalloutAnnotation c && _calloutBoxBefore is CalloutLayout before)
            {
                // Survey: a drop that breaks the callout rules restores the pre-drag snapshot
                // whole rather than clamping the box into a legal spot. A safe drop is put
                // back through the connection rules the free drag skipped.
                bool safe = CalloutLayout.IsDropSafe(c.Layout, CalloutLayout.HandleRadius);
                var after = safe
                    ? CalloutLayout.FromParts(c.Layout.Tip, c.Layout.Knee, c.Layout.Box, c.Layout.Descender)
                    : before;
                ApplyCalloutLayout(c, after);
                if (safe && _moveTotal.Length >= 0.5)
                    Push(new EditorAction(
                        undo: () => ApplyCalloutLayout(c, before),
                        redo: () => ApplyCalloutLayout(c, after)), apply: false);
            }
            _calloutBoxBefore = null;
            _moveTotal = new Vector();
            UpdateSelectionVisual();
            return;
        }
        if (!_movingSelection) return;
        _movingSelection = false;
        Viewport.ReleaseMouseCapture();
        if (_selected is not null)
        {
            var moved = SelectionList().ToArray();
            // Survey's commit gate is per-axis abs > 2 (useSVGInteraction.js:2850). Below it
            // the drag was applied live but is not a move, so put it back.
            if (Math.Abs(_moveTotal.X) <= 2 && Math.Abs(_moveTotal.Y) <= 2)
            {
                foreach (UIElement el in moved) MoveElement(el, -_moveTotal.X, -_moveTotal.Y);
                UpdateSelectionVisual();
            }
            else
            {
                // Survey constrainToPage (svgTransformMath.js:51-56): pull the selection's
                // absolute bbox back inside the page before the commit is recorded.
                Vector fix = ClampSelectionToSurface(moved);
                if (fix.X != 0 || fix.Y != 0)
                {
                    foreach (UIElement el in moved) MoveElement(el, fix.X, fix.Y);
                    _moveTotal += fix;
                    UpdateSelectionVisual();
                }
                double dx = _moveTotal.X, dy = _moveTotal.Y;
                // The move was applied live during the drag, so record it without re-applying.
                Push(new EditorAction(
                    undo: () => { foreach (UIElement el in moved) MoveElement(el, -dx, -dy); },
                    redo: () => { foreach (UIElement el in moved) MoveElement(el, dx, dy); }), apply: false);
            }
        }
        _moveTotal = new Vector();
        _justDraggedAt = Environment.TickCount64;
        foreach (UIElement el in SelectionList())
            RefreshHighlighterFill(el);
    }

    private void RefreshHighlighterFill(UIElement? el)
    {
        if (el is Path ink && ink.Tag is AnnotationData meta &&
            meta.Type == AnnotationData.TypeHighlighter &&
            meta.Color is string hex && TryParseColor(hex, out var color))
            RefreshHighlighterFill(ink, color);
    }

    /// <summary>
    /// Repaints one highlighter multiplied against everything painted below it. Survey's
    /// mix-blend-mode:multiply blends against the page AND every earlier sibling in the same
    /// SVG (svgAnnotationRenderers.jsx:272), not just the screenshot.
    /// </summary>
    private void RefreshHighlighterFill(Path ink, Color color)
    {
        using var backdrop = HighlighterBlend.BackdropUnder(
            CanvasHost, BaseTiles, AnnotationCanvas, ink, _source.Width, _source.Height);
        HighlighterBlend.ApplyLiveFill(ink, backdrop, color);
    }

    /// <summary>
    /// The in-flight highlighter's stroke paint. Survey's drag preview already carries
    /// mixBlendMode:'multiply' at the colour the commit will use
    /// (SVGAnnotationLayer.jsx:5028-5039), and that blend sees the page AND every earlier
    /// sibling - so the preview has to multiply against the SAME backdrop the commit does
    /// (BackdropUnder), not the bare screenshot. Otherwise a stroke drawn over an existing
    /// annotation visibly changes on release. Composited once here, at pointer-down; growing
    /// the stroke only re-clips the finished brush.
    /// </summary>
    private Brush HighlighterPreviewBrush()
    {
        // The stroke is not on the canvas yet, so nothing is hidden: this renders the page
        // plus every committed annotation, which is exactly what the new ink will sit on.
        using var backdrop = HighlighterBlend.BackdropUnder(
            CanvasHost, BaseTiles, AnnotationCanvas, target: null!, _source.Width, _source.Height);
        return HighlighterBlend.PreviewBrush(backdrop, AnnotationStyle.HighlighterColor);
    }

    /// <summary>
    /// Rebuilds every highlighter's multiply fill, bottom-up, so a change to anything under
    /// one is reflected the way Survey's SVG blend is. Free when the canvas holds no
    /// highlighter; otherwise one canvas render each, and only on an undoable mutation.
    /// ponytail: refreshes all of them instead of intersect-testing - the count is tiny.
    /// </summary>
    private void RefreshHighlighters()
    {
        if (AnnotationCanvas is null) return;
        var children = AnnotationCanvas.Children.Cast<UIElement>().ToList();
        if (!children.Any(HighlighterBlend.IsHighlighter)) return;
        foreach (UIElement el in children)
            RefreshHighlighterFill(el);
    }

    private void AbortMove()
    {
        if (_movingCalloutBox)
        {
            _movingCalloutBox = false;
            if (_selected is CalloutAnnotation c && _calloutBoxBefore is CalloutLayout before)
                ApplyCalloutLayout(c, before);
            _calloutBoxBefore = null;
            _moveTotal = new Vector();
            UpdateSelectionVisual();
            return;
        }
        if (!_movingSelection) return;
        _movingSelection = false;
        if (_selected is not null && (_moveTotal.X != 0 || _moveTotal.Y != 0))
        {
            foreach (UIElement el in SelectionList())
                MoveElement(el, -_moveTotal.X, -_moveTotal.Y);
        }
        _moveTotal = new Vector();
        UpdateSelectionVisual();
    }

    /// <summary>
    /// Survey shows the glow when a callout is hovered while unselected, or selected as part
    /// of a multi-selection (SVGAnnotationLayer.jsx:3416).
    /// </summary>
    private void UpdateCalloutHover(Point pos)
    {
        if (!AnnotationCanvas.Children.OfType<CalloutAnnotation>().Any()) return;
        var hovered = _tool == EditorTool.Select && _activeHandle < 0 && !_dragging && !_marqueeSelecting
            ? HitTestAnnotation(pos) as CalloutAnnotation
            : null;
        foreach (UIElement child in AnnotationCanvas.Children)
        {
            if (child is not CalloutAnnotation c) continue;
            bool selected = ReferenceEquals(c, _selected) || _selection.Contains(c);
            c.ShowGlow = selected
                ? _selection.Count > 1
                : ReferenceEquals(c, hovered);
        }
    }

    private static bool CalloutBoxContains(CalloutAnnotation callout, Point pos)
    {
        Vector off = ElementOffset(callout);
        var b = callout.Layout.RenderBox;
        b.Offset(off.X, off.Y);
        return b.Contains(pos);
    }

    /// <summary>
    /// Live text-box drag. Survey moves the box freely while the pointer is down (no
    /// re-routing), then accepts or rolls the whole callout back on release.
    /// </summary>
    private void NudgeCalloutBox(CalloutAnnotation callout, double dx, double dy)
    {
        var l = callout.Layout;
        ApplyCalloutLayout(callout, CalloutLayout.Raw(l.Tip, l.Knee,
            new Rect(l.Box.X + dx, l.Box.Y + dy, l.Box.Width, l.Box.Height), l.Descender));
    }

    private void ApplyCalloutLayout(CalloutAnnotation callout, CalloutLayout layout)
    {
        if (callout.Tag is not AnnotationData meta) return;
        // A re-layout must not rewrite the text: mid-edit the label already carries what the
        // open editor shows, while meta.Text is still the pre-edit string until the commit.
        ApplyCalloutMeta(callout, layout, meta, callout.Text);
        callout.Tag = CalloutDataFrom(callout.Layout, meta);
    }

    /// <summary>
    /// Rebuilds a callout's AnnotationData for a new layout while carrying over every style
    /// field the geometry patch does not own - Survey's `{ ...c, ...patch }` spread.
    /// </summary>
    private static AnnotationData CalloutDataFrom(CalloutLayout layout, AnnotationData meta)
    {
        var (head, _) = AnnotationStyle.HeadsFrom(meta);
        var stroke = meta.Color is string hex && TryParseColor(hex, out var sc) ? sc : Colors.White;
        var data = AnnotationData.ForCallout(layout, meta.Text ?? "", stroke,
            AnnotationStyle.FillColorFrom(meta, stroke), meta.Thickness ?? 2, head,
            AnnotationStyle.LineStyleFrom(meta), meta.FontSize ?? CalloutLayout.DefaultFontSize,
            meta.FontFamily, CalloutFontColor(meta));
        data.Tx = meta.Tx;
        data.Ty = meta.Ty;
        data.Bold = meta.Bold;
        data.Italic = meta.Italic;
        data.Underline = meta.Underline;
        data.Strike = meta.Strike;
        data.Align = meta.Align;
        data.VerticalAlign = meta.VerticalAlign;
        return data;
    }

    private IReadOnlyList<UIElement> SelectionList()
    {
        if (_selection.Count > 0) return _selection;
        if (_selected is null) return Array.Empty<UIElement>();
        return new[] { _selected };
    }

    private void Select(UIElement? element)
    {
        // Selecting a different element (or deselecting) ends any in-progress opacity drag,
        // committing it as one undo entry before the selection moves on.
        if (_opacityElement is not null && !ReferenceEquals(_opacityElement, element))
            CommitOpacityGesture();
        LeaveBboxEditMode(element);
        _selection.Clear();
        _selected = element;
        if (element is not null)
            _selection.Add(element);
        UpdateSelectionVisual();
        if (IsLoaded)
        {
            SyncChromeFromSelection();
            UpdateContextPanels();
        }
    }

    /// <summary>
    /// Drops bbox edit mode when the selection moves off its target, stamping Survey's 300 ms
    /// re-entry cooldown so the dismissing click cannot immediately reopen it.
    /// </summary>
    private void LeaveBboxEditMode(UIElement? nextSelection)
    {
        if (_bboxEditTarget is null || ReferenceEquals(_bboxEditTarget, nextSelection)) return;
        _bboxEditTarget = null;
        _editModeLeftAt = Environment.TickCount64;
    }

    private void SelectMany(IReadOnlyList<UIElement> elements)
    {
        if (_opacityElement is not null)
            CommitOpacityGesture();
        LeaveBboxEditMode(null);
        _selection.Clear();
        foreach (UIElement el in elements)
            if (!_selection.Contains(el))
                _selection.Add(el);
        _selected = _selection.Count > 0 ? _selection[^1] : null;
        UpdateSelectionVisual();
        if (IsLoaded)
        {
            SyncChromeFromSelection();
            UpdateContextPanels();
        }
    }

    private void ToggleSelection(UIElement element)
    {
        if (_opacityElement is not null && !ReferenceEquals(_opacityElement, element))
            CommitOpacityGesture();
        if (_selection.Contains(element))
        {
            _selection.Remove(element);
            _selected = _selection.Count > 0 ? _selection[^1] : null;
        }
        else
        {
            _selection.Add(element);
            _selected = element;
        }
        UpdateSelectionVisual();
        if (IsLoaded)
        {
            SyncChromeFromSelection();
            UpdateContextPanels();
        }
    }

    private void UpdateSelectionVisual()
    {
        // Survey's halo is hovered-AND-unselected, so any selection change clears it; the next
        // pointer move re-derives it.
        if (HoverHalo is not null) HoverHalo.Visibility = Visibility.Collapsed;
        UpdateSelectionHalos();
        // The crop-adjust overlay owns the handles while a pending crop is being fine-tuned;
        // don't let the selection refresh stomp it.
        if (_adjustingCrop || (_handleKind == HandleKind.Crop && _pendingCrop is not null))
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            UpdateCropAdjustHandles();
            return;
        }
        // Survey's group union frame is the same overlay at strokeOpacity 0.6, a deliberate
        // contrast with the opaque single-selection frame (SVGAnnotationLayer.jsx:5875-5891).
        SelectionRect.Opacity = _selection.Count > 1 ? 0.6 : 1.0;
        if (_selection.Count > 1)
        {
            Rect union = Rect.Empty;
            foreach (UIElement el in _selection)
            {
                if (!AnnotationCanvas.Children.Contains(el)) continue;
                Rect bEl = GetCanvasBounds(el);
                union = union.IsEmpty ? bEl : Rect.Union(union, bEl);
            }
            if (union.IsEmpty)
            {
                SelectionRect.Visibility = Visibility.Collapsed;
                HideHandles();
                return;
            }
            // Survey's overlay padding is 2 page units, so it scales with zoom like the mark.
            const double padMany = 2;
            union.Inflate(padMany, padMany);
            Canvas.SetLeft(SelectionRect, union.X);
            Canvas.SetTop(SelectionRect, union.Y);
            SelectionRect.Width = union.Width;
            SelectionRect.Height = union.Height;
            SelectionRect.Visibility = Visibility.Visible;
            HideHandles();
            return;
        }
        if (_selected is null || !AnnotationCanvas.Children.Contains(_selected))
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            HideHandles();
            return;
        }
        if (_selected is CalloutAnnotation callout)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            UpdateSelectionHandles();
            _ = callout;
            return;
        }
        // Drawn in the annotation's UNROTATED box and turned by the same angle, so the frame
        // hugs a rotated mark instead of ballooning out to its axis-aligned bounds.
        double selAngle = _selected is FrameworkElement selFe ? AnnotationTransform.AngleOf(selFe) : 0;
        Rect b = _selected is FrameworkElement boxFe
            ? UnrotatedCanvasBounds(boxFe)
            : GetCanvasBounds(_selected);
        if (b.IsEmpty || (b.Width < 0.01 && b.Height < 0.01))
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            HideHandles();
            return;
        }
        // Survey isBorderFlush (SVGAnnotationLayer.jsx:5469-5470, :5659-5660): rect and text
        // render NO dashed frame — their handles sit on the shape's own stroke — while every
        // other type keeps the 2-unit padding so the handles land ON the dashes.
        string? selType = (_selected as FrameworkElement)?.Tag is AnnotationData sd ? sd.Type : null;
        bool borderFlush = selType is AnnotationData.TypeRectangle or AnnotationData.TypeText;
        double pad = borderFlush ? 0 : 2;
        b.Inflate(pad, pad);
        Canvas.SetLeft(SelectionRect, b.X);
        Canvas.SetTop(SelectionRect, b.Y);
        SelectionRect.Width = b.Width;
        SelectionRect.Height = b.Height;
        SelectionRect.RenderTransform = selAngle == 0
            ? Transform.Identity
            : new RotateTransform(selAngle, b.Width / 2, b.Height / 2);
        // Survey draws no box around a line, arrow or curve — the two endpoint handles ARE
        // the selection. A dashed rectangle around a diagonal line just looked like a hit box.
        // A selected counter is the same: one dot on the nubbin tip and nothing else
        // (SVGAnnotationLayer.jsx:5106-5164).
        bool lineLike = !ReferenceEquals(_bboxEditTarget, _selected)
            && selType is AnnotationData.TypeLine or AnnotationData.TypeArrow
                or AnnotationData.TypeCurvedArrow or AnnotationData.TypeStep;
        // borderFlush carries no edit-mode guard: Survey hides the rect/text box unconditionally.
        SelectionRect.Visibility = lineLike || borderFlush ? Visibility.Collapsed : Visibility.Visible;

        UpdateSelectionHandles();
    }

    /// <summary>
    /// Survey constrainToPage (svgTransformMath.js:51-56): the correction that brings the
    /// union of <paramref name="elements"/> back inside the screenshot, or zero.
    /// </summary>
    private Vector ClampSelectionToSurface(IReadOnlyList<UIElement> elements)
    {
        Rect union = Rect.Empty;
        foreach (UIElement el in elements)
        {
            if (!AnnotationCanvas.Children.Contains(el)) continue;
            Rect b = GetCanvasBounds(el);
            if (b.IsEmpty) continue;
            union = union.IsEmpty ? b : Rect.Union(union, b);
        }
        if (union.IsEmpty) return default;
        double w = _source.Width, h = _source.Height;
        double dx = union.Left < 0 ? -union.Left : (union.Right > w ? w - union.Right : 0);
        double dy = union.Top < 0 ? -union.Top : (union.Bottom > h ? h - union.Bottom : 0);
        return new Vector(dx, dy);
    }

    /// <summary>Bounds of an annotation in canvas coordinates, including its own render transform.</summary>
    private Rect GetCanvasBounds(UIElement element)
    {
        Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
        if (bounds.IsEmpty) bounds = new Rect(element.RenderSize);
        return element.TransformToAncestor(AnnotationCanvas).TransformBounds(bounds);
    }

}
