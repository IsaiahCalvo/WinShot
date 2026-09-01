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
    // ----------------------------------------------------- resize handles (Gap)

    /// <summary>Which family of handles is currently shown.</summary>
    private enum HandleKind { None, Box, Endpoints, Crop }

    private const double HandleScreenPx = 9;   // on-screen thumb size, kept constant via /_zoom
    private const double HandleGrabPx = 11;     // screen-px grab radius for hit testing
    private const double RotateHandleScreenPx = 13;

    /// <summary>Handle index reserved for the rotation grip (the box thumbs are 0..7).</summary>
    private const int RotateHandleIndex = 8;

    private Ellipse? _rotateThumb;
    private Line? _rotateStem;

    // Rotation drag state: the angle when the drag began plus the pointer bearing at grab
    // time, so picking the grip up off-centre does not snap the mark round.
    private double _rotateStartAngle;
    private double _rotateGrabBearing;

    /// <summary>
    /// Builds the handle layout for the current selection: an 8-thumb box frame for
    /// scalable shapes/text/freehand/image, or 2 endpoint thumbs for arrow/line/curved
    /// arrow. Spotlight/emoji/step keep the dashed rect only (no resize). The thumbs are
    /// placed in content coordinates and sized in screen px so they stay constant at any zoom.
    /// </summary>
    private void UpdateSelectionHandles()
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
        {
            HideHandles();
            return;
        }

        switch (meta.Type)
        {
            case AnnotationData.TypeArrow:
            case AnnotationData.TypeLine:
            case AnnotationData.TypeCurvedArrow:
                LayoutEndpointHandles(meta);
                break;
            case AnnotationData.TypeRectangle:
            case AnnotationData.TypeEllipse:
            case AnnotationData.TypeText:
            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
            case AnnotationData.TypeImage:
                LayoutBoxHandles(UnrotatedCanvasBounds(fe), AnnotationTransform.AngleOf(fe));
                break;
            default:
                HideHandles(); // spotlight / emoji / step: move + delete only
                break;
        }
    }

    /// <summary>
    /// Eight thumbs: corners 0=TL,1=TR,2=BR,3=BL then edge midpoints 4=T,5=R,6=B,7=L,
    /// plus a rotation handle above the top edge. When the annotation is rotated every
    /// thumb rides the rotated frame, so the chrome always wraps the mark.
    /// </summary>
    private void LayoutBoxHandles(Rect b, double angle)
    {
        _handleKind = HandleKind.Box;
        var centre = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        var pts = BoxHandlePoints(b);
        EnsureThumbs(pts.Length);
        for (int i = 0; i < pts.Length; i++)
            PlaceThumb(i, AnnotationRotation.RotatePoint(pts[i], centre, angle));

        // The rotation handle sits a constant SCREEN distance above the top edge, so it
        // never drifts away from the mark as the user zooms.
        var top = new Point(centre.X, b.Top);
        var grip = new Point(centre.X, b.Top - AnnotationRotation.HandleOffsetScreenPx / _zoom);
        PlaceRotateThumb(
            AnnotationRotation.RotatePoint(grip, centre, angle),
            AnnotationRotation.RotatePoint(top, centre, angle));

        HandleLayer.Visibility = Visibility.Visible;
    }

    private static Point[] BoxHandlePoints(Rect b) => new[]
    {
        new Point(b.Left, b.Top),                       // 0 TL
        new Point(b.Right, b.Top),                      // 1 TR
        new Point(b.Right, b.Bottom),                   // 2 BR
        new Point(b.Left, b.Bottom),                    // 3 BL
        new Point(b.Left + b.Width / 2, b.Top),         // 4 T
        new Point(b.Right, b.Top + b.Height / 2),       // 5 R
        new Point(b.Left + b.Width / 2, b.Bottom),      // 6 B
        new Point(b.Left, b.Top + b.Height / 2),        // 7 L
    };

    /// <summary>Two thumbs at the stroke endpoints (index 0 = from, 1 = to).</summary>
    private void LayoutEndpointHandles(AnnotationData meta)
    {
        if (meta.Points is not { Length: >= 2 } pts) { HideHandles(); return; }
        // For arrow/line, points are [from, to]; for curved arrow [from, control, to].
        Point from = new(pts[0][0], pts[0][1]);
        Point to = meta.Type == AnnotationData.TypeCurvedArrow && pts.Length >= 3
            ? new Point(pts[2][0], pts[2][1])
            : new Point(pts[^1][0], pts[^1][1]);
        Vector off = ElementOffset(_selected!);
        from += off;
        to += off;

        _handleKind = HandleKind.Endpoints;
        EnsureThumbs(2);
        PlaceThumb(0, from);
        PlaceThumb(1, to);
        // Arrows and lines are rotated by dragging an endpoint, so they get no rotation grip.
        HideRotateThumb();
        HandleLayer.Visibility = Visibility.Visible;
    }

    /// <summary>The element's accumulated move offset (moves + crop shifts), or zero.</summary>
    private static Vector ElementOffset(UIElement element) => AnnotationTransform.OffsetOf(element);

    private void EnsureThumbs(int count)
    {
        while (_handleThumbs.Count < count)
        {
            var thumb = new Rectangle
            {
                Fill = new SolidColorBrush(Colors.White),
                Stroke = (Brush)FindResource("InfoBrush"),
                RadiusX = 1.5,
                RadiusY = 1.5,
            };
            _handleThumbs.Add(thumb);
            HandleLayer.Children.Add(thumb);
        }
        for (int i = 0; i < _handleThumbs.Count; i++)
            _handleThumbs[i].Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlaceThumb(int index, Point center)
    {
        if (index < 0 || index >= _handleThumbs.Count) return;
        var thumb = _handleThumbs[index];
        double s = HandleScreenPx / _zoom;
        thumb.Width = s;
        thumb.Height = s;
        thumb.StrokeThickness = 1.5 / _zoom;
        Canvas.SetLeft(thumb, center.X - s / 2);
        Canvas.SetTop(thumb, center.Y - s / 2);
        thumb.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The annotation's canvas bounds as if it were NOT rotated. Rotation is applied about
    /// this rect's centre, so the whole handle/resize system can keep working in the simple
    /// axis-aligned frame and only map points in and out at the edges.
    /// </summary>
    private static Rect UnrotatedCanvasBounds(FrameworkElement fe)
    {
        Rect local = AnnotationTransform.LocalBounds(fe);
        double left = Canvas.GetLeft(fe); if (double.IsNaN(left)) left = 0;
        double top = Canvas.GetTop(fe); if (double.IsNaN(top)) top = 0;
        Vector off = AnnotationTransform.OffsetOf(fe);
        return new Rect(local.X + left + off.X, local.Y + top + off.Y, local.Width, local.Height);
    }

    /// <summary>Centre of the unrotated bounds — the pivot every rotation turns about.</summary>
    private static Point RotationCentre(FrameworkElement fe)
    {
        Rect b = UnrotatedCanvasBounds(fe);
        return new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
    }

    /// <summary>Maps a canvas point into the annotation's unrotated frame.</summary>
    private static Point ToUnrotatedFrame(FrameworkElement fe, Point canvasPoint)
    {
        double angle = AnnotationTransform.AngleOf(fe);
        return angle == 0
            ? canvasPoint
            : AnnotationRotation.RotatePoint(canvasPoint, RotationCentre(fe), -angle);
    }

    private void PlaceRotateThumb(Point centre, Point anchor)
    {
        if (_rotateThumb is null)
        {
            _rotateThumb = new Ellipse
            {
                Fill = new SolidColorBrush(Colors.White),
                Stroke = (Brush)FindResource("InfoBrush"),
            };
            _rotateStem = new Line { Stroke = (Brush)FindResource("InfoBrush") };
            HandleLayer.Children.Add(_rotateStem);
            HandleLayer.Children.Add(_rotateThumb);
        }

        double s = RotateHandleScreenPx / _zoom;
        _rotateThumb.Width = s;
        _rotateThumb.Height = s;
        _rotateThumb.StrokeThickness = 1.5 / _zoom;
        Canvas.SetLeft(_rotateThumb, centre.X - s / 2);
        Canvas.SetTop(_rotateThumb, centre.Y - s / 2);
        _rotateThumb.Visibility = Visibility.Visible;

        _rotateStem!.X1 = anchor.X;
        _rotateStem.Y1 = anchor.Y;
        _rotateStem.X2 = centre.X;
        _rotateStem.Y2 = centre.Y;
        _rotateStem.StrokeThickness = 1 / _zoom;
        _rotateStem.Visibility = Visibility.Visible;
    }

    private void HideRotateThumb()
    {
        if (_rotateThumb is not null) _rotateThumb.Visibility = Visibility.Collapsed;
        if (_rotateStem is not null) _rotateStem.Visibility = Visibility.Collapsed;
    }

    private void HideHandles()
    {
        if (_adjustingCrop) return; // an in-progress crop adjust keeps its handles
        _handleKind = HandleKind.None;
        HandleLayer.Visibility = Visibility.Collapsed;
        foreach (var t in _handleThumbs) t.Visibility = Visibility.Collapsed;
        HideRotateThumb();
    }

    /// <summary>Index of the handle near a content point, or -1. Endpoint/box share the same thumb list.</summary>
    private int HitTestHandle(Point pos)
    {
        if (_handleKind == HandleKind.None || HandleLayer.Visibility != Visibility.Visible) return -1;
        double grab = HandleGrabPx / _zoom;
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < _handleThumbs.Count; i++)
        {
            var thumb = _handleThumbs[i];
            if (thumb.Visibility != Visibility.Visible) continue;
            double cx = Canvas.GetLeft(thumb) + thumb.Width / 2;
            double cy = Canvas.GetTop(thumb) + thumb.Height / 2;
            double d = (pos - new Point(cx, cy)).Length;
            if (d <= grab && d < bestDist) { bestDist = d; best = i; }
        }

        // The rotation grip wins ties with the top-edge thumb it sits above, because it is
        // the outer of the two and the user reached past the frame to get to it.
        if (_rotateThumb is { Visibility: Visibility.Visible })
        {
            double cx = Canvas.GetLeft(_rotateThumb) + _rotateThumb.Width / 2;
            double cy = Canvas.GetTop(_rotateThumb) + _rotateThumb.Height / 2;
            if ((pos - new Point(cx, cy)).Length <= grab) return RotateHandleIndex;
        }
        return best;
    }

    // ---- resize drag lifecycle ----

    private void BeginResize(int handle)
    {
        _activeHandle = handle;
        Viewport.CaptureMouse();
        if (_adjustingCrop || _handleKind == HandleKind.Crop)
        {
            // Crop adjust: snapshot the pending rect so a cancel/abort can restore it.
            _resizeBefore = null;
            return;
        }
        if (_selected is FrameworkElement fe && fe.Tag is AnnotationData meta)
        {
            _resizeBefore = CaptureResizeSnapshot(_selected, meta);
            if (handle == RotateHandleIndex)
            {
                _rotateStartAngle = AnnotationTransform.AngleOf(fe);
                _rotateGrabBearing = AnnotationRotation.AngleFromPointer(
                    RotationCentre(fe), Mouse.GetPosition(AnnotationCanvas));
            }
        }
    }

    private void DragResize(Point pos)
    {
        if (_handleKind == HandleKind.Crop || _adjustingCrop)
        {
            DragCropHandle(pos);
            return;
        }
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta || _resizeBefore is null)
            return;

        if (_activeHandle == RotateHandleIndex)
        {
            DragRotate(fe, pos);
            UpdateSelectionVisual();
            return;
        }

        Rect oldBounds = _resizeBefore.Bounds;
        if (_handleKind == HandleKind.Endpoints)
        {
            ApplyEndpointResize(fe, meta, pos);
        }
        else
        {
            // Resize runs in the annotation's unrotated frame: map the pointer in, do the
            // ordinary axis-aligned box maths, then put the rotation back about the new centre.
            double angle = AnnotationTransform.AngleOf(fe);
            Point local = ToUnrotatedFrame(fe, pos);
            ApplyBoxResize(fe, meta, NewBoundsForHandle(oldBounds, _activeHandle, local));
            if (angle != 0)
            {
                fe.UpdateLayout();
                AnnotationTransform.SetAngle(fe, angle);
            }
        }

        UpdateSelectionVisual();
    }

    /// <summary>Live rotation from the grip: bearing, grab offset, then the 45° snap rules.</summary>
    private void DragRotate(FrameworkElement fe, Point pos)
    {
        double bearing = AnnotationRotation.AngleFromPointer(RotationCentre(fe), pos);
        double angle = AnnotationRotation.ResolveDrag(
            _rotateStartAngle,
            _rotateGrabBearing,
            bearing,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        AnnotationTransform.SetAngle(fe, angle);
        if (fe.Tag is AnnotationData meta) meta.Angle = angle;
    }

    private void EndResize()
    {
        _activeHandle = -1;
        Viewport.ReleaseMouseCapture();

        if (_adjustingCrop)
        {
            _adjustingCrop = false;
            UpdateSelectionVisual();
            return;
        }
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta || _resizeBefore is null)
        {
            _resizeBefore = null;
            return;
        }

        var before = _resizeBefore;
        var after = CaptureResizeSnapshot(_selected, meta);
        _resizeBefore = null;

        // Negligible change → no undo entry (e.g. a click that didn't move).
        if (after.NearlyEquals(before))
        {
            UpdateSelectionVisual();
            return;
        }

        UIElement el = _selected;
        Push(new EditorAction(
            undo: () => { ApplyResizeSnapshot(el, before); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); },
            redo: () => { ApplyResizeSnapshot(el, after); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); }),
            apply: false);
        UpdateSelectionVisual();
    }

    private void AbortResize()
    {
        if (_activeHandle < 0) return;
        _activeHandle = -1;
        if (!_adjustingCrop && _selected is not null && _resizeBefore is { } snap)
            ApplyResizeSnapshot(_selected, snap);
        _adjustingCrop = false;
        _resizeBefore = null;
        UpdateSelectionVisual();
    }

    /// <summary>
    /// New axis-aligned bounds after dragging <paramref name="handle"/> to <paramref name="pos"/>.
    /// Corner handles move two edges; edge-midpoint handles move one. The rect is normalized so
    /// dragging past the opposite edge flips it cleanly instead of producing negative extents.
    /// </summary>
    private static Rect NewBoundsForHandle(Rect b, int handle, Point pos)
    {
        double left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom;
        switch (handle)
        {
            case 0: left = pos.X; top = pos.Y; break;       // TL
            case 1: right = pos.X; top = pos.Y; break;      // TR
            case 2: right = pos.X; bottom = pos.Y; break;   // BR
            case 3: left = pos.X; bottom = pos.Y; break;    // BL
            case 4: top = pos.Y; break;                     // T
            case 5: right = pos.X; break;                   // R
            case 6: bottom = pos.Y; break;                  // B
            case 7: left = pos.X; break;                    // L
        }
        double x = Math.Min(left, right), y = Math.Min(top, bottom);
        return new Rect(x, y, Math.Abs(right - left), Math.Abs(bottom - top));
    }

    // ---- per-kind apply ----

    /// <summary>Resizes a box-style element to <paramref name="b"/> (canvas coords). Text scales font size.</summary>
    private void ApplyBoxResize(FrameworkElement fe, AnnotationData meta, Rect b)
    {
        switch (meta.Type)
        {
            case AnnotationData.TypeRectangle:
            case AnnotationData.TypeEllipse:
            case AnnotationData.TypeImage:
                ResizeBoxShape(fe, b);
                break;
            case AnnotationData.TypeText:
                ResizeTextElement(fe, meta, b);
                break;
            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
                ResizePolyline(fe, meta, b);
                break;
        }
    }

    /// <summary>Rectangle/Ellipse: write Canvas.Left/Top + Width/Height directly (drop any move transform into the position).</summary>
    private static void ResizeBoxShape(FrameworkElement fe, Rect b)
    {
        FlattenTransformIntoCanvas(fe);
        Canvas.SetLeft(fe, b.X);
        Canvas.SetTop(fe, b.Y);
        fe.Width = Math.Max(1, b.Width);
        fe.Height = Math.Max(1, b.Height);
    }

    /// <summary>
    /// Text label: scale FontSize proportionally to the box drag, measured from the BASELINE
    /// bounds/font captured at drag start (not the live values) so successive mouse-moves don't
    /// compound. Keeps aspect ratio by using the larger axis ratio.
    /// </summary>
    private void ResizeTextElement(FrameworkElement fe, AnnotationData meta, Rect newBounds)
    {
        if (_resizeBefore is not { } snap) return;
        Rect baseBounds = snap.Bounds;
        if (baseBounds.Width < 0.5 || baseBounds.Height < 0.5) return;
        double ratio = Math.Max(newBounds.Width / baseBounds.Width, newBounds.Height / baseBounds.Height);
        ratio = Math.Clamp(ratio, 0.1, 20);
        double baseFont = snap.FontSize ?? meta.FontSize ?? AnnotationFactory.FontSizeFor(_thickness);
        double newFont = Math.Clamp(baseFont * ratio, 6, 1000);
        SetFontSize(fe, newFont);
        fe.UpdateLayout();
    }

    /// <summary>Freehand / highlighter: scale every stored point about the box's top-left so the polyline fits the new box.</summary>
    private void ResizePolyline(FrameworkElement fe, AnnotationData meta, Rect newBounds)
    {
        if (fe is not Polyline poly || _resizeBefore?.Points is not { } basePts || basePts.Length == 0) return;

        // Reconstruct the polyline from the captured (pre-drag) points so successive
        // moves accumulate from the same baseline, then scale into the new box.
        Rect baseBounds = _resizeBefore.Bounds;
        double sx = baseBounds.Width > 0.5 ? newBounds.Width / baseBounds.Width : 1;
        double sy = baseBounds.Height > 0.5 ? newBounds.Height / baseBounds.Height : 1;

        // basePts are in canvas coords (element offset already folded in by the snapshot).
        var scaled = new PointCollection(basePts.Length);
        var raw = new double[basePts.Length][];
        for (int i = 0; i < basePts.Length; i++)
        {
            double nx = newBounds.X + (basePts[i].X - baseBounds.X) * sx;
            double ny = newBounds.Y + (basePts[i].Y - baseBounds.Y) * sy;
            scaled.Add(new Point(nx, ny));
            raw[i] = new[] { nx, ny };
        }
        // Points now hold absolute canvas coords, so neutralize any move transform.
        poly.RenderTransform = null;
        poly.Points = scaled;
        // Keep the stored geometry in sync so the end-of-drag snapshot reads the live state.
        meta.Points = raw;
        meta.Tx = 0;
        meta.Ty = 0;
        poly.Tag = meta;
    }

    /// <summary>Arrow / line / curved arrow: drag one endpoint, rebuilding geometry from the moved endpoints.</summary>
    private void ApplyEndpointResize(FrameworkElement fe, AnnotationData meta, Point pos)
    {
        if (fe is not Path path || _resizeBefore?.Points is not { Length: >= 2 } basePts) return;

        // basePts (canvas coords): arrow/line = [from, to]; curved = [from, control, to].
        Point from = basePts[0];
        bool curved = meta.Type == AnnotationData.TypeCurvedArrow && basePts.Length >= 3;
        Point to = curved ? basePts[2] : basePts[^1];
        Point control = curved ? basePts[1] : default;

        if (_activeHandle == 0) from = pos;
        else to = pos;

        double thickness = meta.Thickness ?? path.StrokeThickness;
        path.RenderTransform = null; // points are absolute now
        if (curved)
        {
            // Keep the stored control point as-is so a previously bent curve keeps its shape.
            path.Data = AnnotationFactory.CurvedArrowGeometry(from, control, to, thickness);
            meta.Points = new[] { new[] { from.X, from.Y }, new[] { control.X, control.Y }, new[] { to.X, to.Y } };
        }
        else
        {
            path.Data = AnnotationFactory.ArrowGeometry(from, to, thickness, AnnotationFactory.ParseArrowStyle(meta.Style));
            meta.Points = new[] { new[] { from.X, from.Y }, new[] { to.X, to.Y } };
        }
        // Keep the stored geometry in sync so the end-of-drag snapshot reads the live state.
        meta.Tx = 0;
        meta.Ty = 0;
        path.Tag = meta;
    }

    // ---- snapshots (undo/redo) ----

    /// <summary>
    /// Immutable capture of an element's resizable geometry plus a fresh AnnotationData,
    /// in absolute canvas coordinates (the element's move transform folded in). Replayed
    /// verbatim by undo/redo so a resize is one reversible step.
    /// </summary>
    private sealed class ResizeSnapshot
    {
        public required string Type { get; init; }
        public Rect Bounds { get; init; }
        public Point[]? Points { get; init; }   // canvas coords
        public double? FontSize { get; init; }
        public required AnnotationData Meta { get; init; }
        public double Tx { get; init; }
        public double Ty { get; init; }

        /// <summary>Rotation at capture time, so undo puts the mark back at its old angle.</summary>
        public double Angle { get; init; }

        public bool NearlyEquals(ResizeSnapshot other)
        {
            if (Math.Abs(Tx - other.Tx) > 0.01 || Math.Abs(Ty - other.Ty) > 0.01) return false;
            if (Math.Abs(Angle - other.Angle) > 0.01) return false;
            if (FontSize is double a && other.FontSize is double b && Math.Abs(a - b) > 0.01) return false;
            if (Points is { } p && other.Points is { } q)
            {
                if (p.Length != q.Length) return false;
                for (int i = 0; i < p.Length; i++)
                    if ((p[i] - q[i]).Length > 0.01) return false;
                return true;
            }
            return Math.Abs(Bounds.X - other.Bounds.X) < 0.01 && Math.Abs(Bounds.Y - other.Bounds.Y) < 0.01
                && Math.Abs(Bounds.Width - other.Bounds.Width) < 0.01 && Math.Abs(Bounds.Height - other.Bounds.Height) < 0.01;
        }
    }

    private ResizeSnapshot CaptureResizeSnapshot(UIElement element, AnnotationData meta)
    {
        Vector off = ElementOffset(element);
        Point[]? pts = null;
        if (meta.Points is { } mp && mp.Length > 0 &&
            meta.Type is AnnotationData.TypeArrow or AnnotationData.TypeLine
                or AnnotationData.TypeCurvedArrow or AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter)
        {
            pts = mp.Select(p => new Point(p[0] + off.X, p[1] + off.Y)).ToArray();
        }
        return new ResizeSnapshot
        {
            Type = meta.Type,
            // The UNROTATED bounds: rotation is applied about this rect's centre, so this is
            // the frame both the resize maths and undo work in.
            Bounds = element is FrameworkElement box ? UnrotatedCanvasBounds(box) : GetCanvasBounds(element),
            Points = pts,
            FontSize = CurrentFontSize(element),
            Meta = meta.Clone(),
            Tx = off.X,
            Ty = off.Y,
            Angle = AnnotationTransform.AngleOf(element),
        };
    }

    /// <summary>Restores an element to a captured geometry snapshot and refreshes its stored AnnotationData.</summary>
    private void ApplyResizeSnapshot(UIElement element, ResizeSnapshot snap)
    {
        if (element is not FrameworkElement fe) return;
        var meta = snap.Meta.Clone();

        switch (snap.Type)
        {
            case AnnotationData.TypeRectangle:
            case AnnotationData.TypeEllipse:
            case AnnotationData.TypeImage:
                fe.RenderTransform = null;
                Canvas.SetLeft(fe, snap.Bounds.X);
                Canvas.SetTop(fe, snap.Bounds.Y);
                fe.Width = Math.Max(1, snap.Bounds.Width);
                fe.Height = Math.Max(1, snap.Bounds.Height);
                meta.Rect = new[] { snap.Bounds.X, snap.Bounds.Y, snap.Bounds.Width, snap.Bounds.Height };
                meta.Tx = 0;
                meta.Ty = 0;
                break;

            case AnnotationData.TypeText:
                AnnotationTransform.Apply(fe, new Vector(snap.Tx, snap.Ty), 0);
                if (snap.FontSize is double fs) SetFontSize(fe, fs);
                meta.FontSize = snap.FontSize ?? meta.FontSize;
                meta.Tx = snap.Tx;
                meta.Ty = snap.Ty;
                fe.UpdateLayout();
                break;

            case AnnotationData.TypeArrow:
            case AnnotationData.TypeLine:
            case AnnotationData.TypeCurvedArrow:
                if (fe is Path path && snap.Points is { Length: >= 2 } sp)
                {
                    path.RenderTransform = null;
                    double thickness = meta.Thickness ?? path.StrokeThickness;
                    bool curved = snap.Type == AnnotationData.TypeCurvedArrow && sp.Length >= 3;
                    path.Data = curved
                        ? AnnotationFactory.CurvedArrowGeometry(sp[0], sp[1], sp[2], thickness)
                        : AnnotationFactory.ArrowGeometry(sp[0], sp[^1], thickness,
                            AnnotationFactory.ParseArrowStyle(meta.Style));
                    meta.Points = sp.Select(p => new[] { p.X, p.Y }).ToArray();
                    meta.Tx = 0;
                    meta.Ty = 0;
                }
                break;

            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
                if (fe is Polyline poly && snap.Points is { Length: > 0 } pp)
                {
                    poly.RenderTransform = null;
                    poly.Points = new PointCollection(pp);
                    meta.Points = pp.Select(p => new[] { p.X, p.Y }).ToArray();
                    meta.Tx = 0;
                    meta.Ty = 0;
                }
                break;
        }

        // Every branch above rebuilt the element unrotated; put the captured angle back.
        if (Math.Abs(snap.Angle) > 0.01)
        {
            fe.UpdateLayout();
            AnnotationTransform.SetAngle(fe, snap.Angle);
        }
        meta.Angle = snap.Angle;

        fe.Tag = meta;
    }

    /// <summary>Folds an element's TranslateTransform offset into its Canvas.Left/Top and clears the transform.</summary>
    /// <summary>
    /// Bakes the move offset into Canvas.Left/Top and clears it. A rotation is NOT
    /// flattened — it has no Canvas equivalent — so it is re-applied about the element's
    /// new centre once the caller has finished resizing.
    /// </summary>
    private static void FlattenTransformIntoCanvas(FrameworkElement fe)
    {
        Vector offset = AnnotationTransform.OffsetOf(fe);
        if (offset.X != 0 || offset.Y != 0)
        {
            double l = Canvas.GetLeft(fe); if (double.IsNaN(l)) l = 0;
            double tp = Canvas.GetTop(fe); if (double.IsNaN(tp)) tp = 0;
            Canvas.SetLeft(fe, l + offset.X);
            Canvas.SetTop(fe, tp + offset.Y);
        }
        fe.RenderTransform = null;
    }

    /// <summary>Reads the font size from any text-label kind (TextBlock, Outline Path via stored meta, Pill border child).</summary>
    private static double? CurrentFontSize(UIElement element) => element switch
    {
        TextBlock tb => tb.FontSize,
        Border pill when pill.Child is TextBlock inner => inner.FontSize,
        FrameworkElement fe when fe.Tag is AnnotationData m && m.FontSize is double f => f,
        _ => null,
    };

    /// <summary>
    /// Rebuilds a styled text label at a new font size, replacing the live element's visual
    /// while preserving its position, color, and AnnotationData (Outline rebuilds its glyph
    /// geometry; Pill rebuilds its rounded border; Plain/Bold/Huge just set FontSize).
    /// </summary>
    private void SetFontSize(FrameworkElement fe, double fontSize)
    {
        if (fe.Tag is not AnnotationData meta || meta.Type != AnnotationData.TypeText) return;
        fontSize = Math.Clamp(fontSize, 6, 1000);

        switch (fe)
        {
            case TextBlock tb:
                tb.FontSize = fontSize;
                break;
            case Border pill when pill.Child is TextBlock inner:
                inner.FontSize = fontSize;
                pill.CornerRadius = new CornerRadius(fontSize * 0.55);
                pill.Padding = new Thickness(fontSize * 0.5, fontSize * 0.2, fontSize * 0.5, fontSize * 0.2);
                break;
            case Path glyph:
                // Outline style: rebuild the glyph geometry at the new size from the stored text/color.
                var foreground = glyph.Fill ?? Brushes.White;
                string text = meta.Text ?? string.Empty;
                var typeface = new Typeface(new FontFamily("Segoe UI"),
                    FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                var formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, fontSize, foreground, pixelsPerDip: 1.0);
                glyph.Data = formatted.BuildGeometry(new Point(0, 0));
                glyph.StrokeThickness = Math.Max(1.2, fontSize / 14);
                break;
        }

        // Keep the stored size in sync so re-edit and save round-trip at the new size.
        meta.FontSize = fontSize;
        fe.Tag = meta;
    }

    // ----------------------------------------------------- crop adjust handles (Gap)

    /// <summary>Shows the 8-handle box overlay over the pending crop rect so it can be fine-tuned before Apply.</summary>
    private void ShowCropAdjustHandles()
    {
        if (_pendingCrop is null) return;
        _handleKind = HandleKind.Crop;
        Select(null);
        UpdateCropAdjustHandles();
    }

    private void UpdateCropAdjustHandles()
    {
        if (_pendingCrop is not SD.Rectangle px)
        {
            HideHandles();
            return;
        }
        var b = new Rect(px.X, px.Y, px.Width, px.Height);
        _handleKind = HandleKind.Crop;
        var pts = BoxHandlePoints(b);
        EnsureThumbs(pts.Length);
        for (int i = 0; i < pts.Length; i++) PlaceThumb(i, pts[i]);
        HandleLayer.Visibility = Visibility.Visible;
    }

    /// <summary>Live-updates the pending crop rect from a handle drag, snapping edges to the image and re-rendering the dim overlay.</summary>
    private void DragCropHandle(Point pos)
    {
        if (_pendingCrop is not SD.Rectangle px) return;
        _adjustingCrop = true;
        var cur = new Rect(px.X, px.Y, px.Width, px.Height);
        Rect b = NewBoundsForHandle(cur, _activeHandle, ClampToSurface(pos));

        var newPx = ToPixelRect(b);
        if (newPx.Width < 1 || newPx.Height < 1) return;
        _pendingCrop = newPx;

        var shown = new Rect(newPx.X, newPx.Y, newPx.Width, newPx.Height);
        ShowDragRect(shown, dim: true);
        UpdateCropAdjustHandles();
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
        if (_movingSelection) return;

        // A marquee group deletes as one undoable step.
        if (_multiSelected.Count > 1)
        {
            var members = _multiSelected.ToArray();
            ClearMultiSelection();
            Push(new EditorAction(
                undo: () =>
                {
                    foreach (var m in members)
                        if (!AnnotationCanvas.Children.Contains(m)) AnnotationCanvas.Children.Add(m);
                },
                redo: () =>
                {
                    foreach (var m in members) AnnotationCanvas.Children.Remove(m);
                }));
            return;
        }

        if (_selected is null) return;
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

    /// <summary>Where to move the selected annotation in the z-order (child index of AnnotationCanvas).</summary>
    private enum ZMove { Front, Forward, Backward, Back }

    /// <summary>
    /// Reorders the selected annotation within <c>AnnotationCanvas.Children</c>. Child index IS the
    /// z-order — it's what renders, hit-tests, AND serializes (data.Z), so reordering children needs
    /// no separate ZIndex bookkeeping. Removing then re-inserting only moves the one element, so
    /// undo just puts it back at its old index. No-op (and no undo entry) when already at the edge.
    /// </summary>
    private void ReorderSelected(ZMove move)
    {
        if (_selected is null || _movingSelection) return;
        var children = AnnotationCanvas.Children;
        int oldIndex = children.IndexOf(_selected);
        if (oldIndex < 0) return;
        int last = children.Count - 1;
        int newIndex = move switch
        {
            ZMove.Front => last,
            ZMove.Back => 0,
            ZMove.Forward => Math.Min(oldIndex + 1, last),
            ZMove.Backward => Math.Max(oldIndex - 1, 0),
            _ => oldIndex,
        };
        if (newIndex == oldIndex) return; // already at the requested end — nothing to do

        UIElement el = _selected;
        Push(new EditorAction(
            undo: () => { children.Remove(el); children.Insert(oldIndex, el); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); },
            redo: () => { children.Remove(el); children.Insert(newIndex, el); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); }));
    }

    /// <summary>Right-click an annotation to select it and open the z-order / delete context menu.</summary>
    private void OnViewportRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_sourceOperationActive) return;
        var hit = HitTestAnnotation(e.GetPosition(AnnotationCanvas));
        if (hit is null) return;
        Select(hit);
        _annotationMenu ??= BuildAnnotationMenu();
        _annotationMenu.PlacementTarget = Viewport;
        _annotationMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _annotationMenu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu? _annotationMenu;

    private ContextMenu BuildAnnotationMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var itemStyle = (Style)FindResource("DarkMenuItem");

        MenuItem Item(string header, string gesture, Action act)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture, Style = itemStyle };
            mi.Click += (_, _) => act();
            return mi;
        }

        menu.Items.Add(Item("Bring to Front", "Ctrl+Shift+]", () => ReorderSelected(ZMove.Front)));
        menu.Items.Add(Item("Bring Forward", "Ctrl+]", () => ReorderSelected(ZMove.Forward)));
        menu.Items.Add(Item("Send Backward", "Ctrl+[", () => ReorderSelected(ZMove.Backward)));
        menu.Items.Add(Item("Send to Back", "Ctrl+Shift+[", () => ReorderSelected(ZMove.Back)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", "Del", DeleteSelected));
        return menu;
    }

    /// <summary>Nudges the selected annotation by a keyboard delta as one undoable move.</summary>
    private void NudgeSelected(double dx, double dy)
    {
        if (_selected is null || _movingSelection || _draggingCurveHandle) return;
        UIElement el = _selected;
        MoveElement(el, dx, dy);
        UpdateSelectionVisual();
        Push(new EditorAction(
            undo: () => { MoveElement(el, -dx, -dy); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); },
            redo: () => { MoveElement(el, dx, dy); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); }),
            apply: false);
    }

    /// <summary>Maps a single-letter shortcut to a tool, mirroring the toolbar toggle. Returns false if unmapped.</summary>
    private bool TrySelectToolByKey(Key key)
    {
        EditorTool? tool = key switch
        {
            Key.V => EditorTool.Select,
            Key.A => EditorTool.Arrow,
            Key.R => EditorTool.Rectangle,
            Key.E => EditorTool.Ellipse,
            Key.L => EditorTool.Line,
            Key.T => EditorTool.Text,
            Key.P => EditorTool.Freehand, // Pen / Draw
            Key.H => EditorTool.Highlighter,
            Key.B => EditorTool.Blur,
            Key.S => EditorTool.Step,
            Key.C => EditorTool.Crop,
            _ => null,
        };
        if (tool is not EditorTool t) return false;
        CheckToolButton(t);
        return true;
    }

    /// <summary>
    /// Moves an annotation by composing into its move offset (the same channel crop
    /// shifting uses). A rotation on the element is carried through untouched.
    /// </summary>
    private static void MoveElement(UIElement element, double dx, double dy)
    {
        if (element is not FrameworkElement fe)
        {
            if (element.RenderTransform is TranslateTransform plain) { plain.X += dx; plain.Y += dy; }
            else element.RenderTransform = new TranslateTransform(dx, dy);
            return;
        }

        Vector offset = AnnotationTransform.OffsetOf(fe);
        AnnotationTransform.SetOffset(fe, new Vector(offset.X + dx, offset.Y + dy));
    }

}
