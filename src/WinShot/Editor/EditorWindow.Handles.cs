using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    /// <summary>
    /// Vertex: one grabber per stored point, for a polygon or polyline. Survey's own
    /// single-click chrome for those (useSVGInteraction.js:4550, handle id `vertex-N`) —
    /// the bounding box only appears in double-click bbox-edit mode, like a line's does.
    /// </summary>
    private enum HandleKind { None, Box, Endpoints, Crop, Callout, Vertex }

    private const double HandleScreenPx = 11;  // Survey HANDLE_RADIUS 5.5 -> an 11 px circle
    /// <summary>Survey HANDLE_RADIUS_SECONDARY 4 — only the line/arrow curve-bend handle.</summary>
    private const double SecondaryHandleScreenPx = 8;
    private const double RotateHandleScreenPx = 20;  // Survey rotationR 10 -> a 20 px circle
    private enum ThumbKind { Corner, PillH, PillV, Square, Secondary }
    private ContentControl? _rotateIcon;

    /// <summary>
    /// Survey's dampened handle scale (SVGSelectionOverlay.jsx:61-64):
    /// <c>is = sqrt(clampInverseScale(inverseScale))</c> with MAX_VISUAL_INVERSE_SCALE 3.
    /// Handle chrome lives in content units, so multiplying a Survey screen-px constant by
    /// this reproduces the same curve once the viewport scales it by <c>_zoom</c>.
    /// </summary>
    private double HandleScale => Math.Sqrt(Math.Min(1.0 / _zoom, 3.0));

    /// <summary>
    /// The annotation currently in Survey's double-click "bbox" edit mode — box frame with
    /// uniform resize and a rotation grip instead of its endpoint / counter-tip chrome.
    /// Cleared by selecting anything else (see <see cref="Select"/>) or by Esc.
    /// </summary>
    private FrameworkElement? _bboxEditTarget;

    /// <summary>Survey's 300 ms post-dismissal cooldown on re-entering edit mode (PDFViewer.jsx:31241).</summary>
    private long _editModeLeftAt;

    /// <summary>
    /// The selection's rotation FROZEN at pointer-down, Survey's <c>originalProps.angle</c>.
    /// A resize drag must never read the live angle: the element is being rebuilt underneath
    /// it, so a live read mixes coordinate frames and the mark walks away from the cursor.
    /// </summary>
    private double _resizeAngle;

    /// <summary>
    /// Survey resizes a counter in the SAME frame its handles are drawn in: the canonical
    /// nub-up rect, pivoting about the bubble centre (useSVGInteraction.js:3955-3972, :4109-4115,
    /// :4232). Non-null only while a counter bbox-edit resize is in flight; every other type
    /// keeps working from <c>_resizeBefore.Bounds</c>. The undo snapshot is left alone.
    /// </summary>
    private Rect? _resizeFrame;
    private Point? _resizePivot;

    /// <summary>Handle index reserved for the rotation grip (box thumbs are 0..7).</summary>
    private const int RotateHandleIndex = 8;

    private Ellipse? _rotateThumb;
    private Line? _rotateStem;

    // Callout free-drag (Survey useSVGInteraction.js:2536-2545): the pre-drag layout and the
    // grab point, so every frame is snapshot + delta and the grabbed handle keeps the offset
    // it was picked up with instead of snapping to the cursor.
    private CalloutLayout? _calloutDragStart;
    private Point _calloutGrabLocal;
    private bool _calloutDragInvalid;

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

        // Survey's double-click "bbox" edit mode (PDFViewer.jsx:31272-31292): a counter, line
        // or arrow swaps its endpoint/tip chrome for the full box frame with uniform resize
        // and a rotation grip. Rect/ellipse/ink are explicit no-ops there — their single-click
        // chrome IS the box already.
        if (ReferenceEquals(_bboxEditTarget, fe))
        {
            // Survey gives a counter in bbox mode a CANONICAL nub-up frame rotated by
            // (pointerAngle + 90) about the BUBBLE centre, so the grip sits in front of the
            // tip instead of above an axis-aligned square (SVGAnnotationLayer.jsx:5562-5588).
            if (meta.Type == AnnotationData.TypeStep)
            {
                var (frame, body, frameAngle) = CounterBboxFrame(fe, meta);
                LayoutBoxHandles(frame, frameAngle, padding: HandlePadding, rotationCentre: body);
                return;
            }
            // Survey's bbox-edit overlay is never border-flush, whatever the underlying type.
            LayoutBoxHandles(UnrotatedCanvasBounds(fe), AnnotationTransform.AngleOf(fe), HandlePadding);
            return;
        }

        // The glow tracks the selection rather than each branch below, so no branch can forget
        // to put it away.
        PaintCloudGlow(fe is Path glowPath && glowPath.Data is not null
            && AnnotationStyle.LineStyleFrom(meta) == LineBorderStyle.Cloud ? glowPath : null);

        switch (meta.Type)
        {
            case AnnotationData.TypeArrow:
            case AnnotationData.TypeLine:
            case AnnotationData.TypeCurvedArrow:
                LayoutEndpointHandles(meta);
                break;
            case AnnotationData.TypePolygon:
            case AnnotationData.TypePolyline:
                LayoutVertexHandles(meta);
                break;
            case AnnotationData.TypeCallout:
                LayoutCalloutHandles(meta);
                HideRotateThumb(); // Survey callouts have no rotation handle
                break;
            // A revision cloud is the one mark whose frame is NOT its box. The crowns are the
            // shape and the rectangle they were built on is scaffolding, so Survey rides the
            // grabbers on the crowns' hull padded by 1.5 * strokeWidth and says so in as many
            // words ("never the inner box the cloud was built from"). Padding 0 below because
            // that pad is already inside the frame CloudPath hands back.
            case AnnotationData.TypeRectangle
                when fe is Path cloudPath && cloudPath.Data is not null
                     && AnnotationStyle.LineStyleFrom(meta) == LineBorderStyle.Cloud:
                LayoutBoxHandles(
                    CloudSelectionFrameInCanvas(cloudPath), AnnotationTransform.AngleOf(fe), 0);
                break;
            // Survey isBorderFlush (SVGAnnotationLayer.jsx:5469-5470, :5659-5660): rect and
            // text put their handles ON the shape's own stroke (padding 0); everything else
            // pushes them 2 units out so they sit centred on the dashed frame.
            case AnnotationData.TypeRectangle:
            case AnnotationData.TypeText:
                LayoutBoxHandles(UnrotatedCanvasBounds(fe), AnnotationTransform.AngleOf(fe), 0);
                break;
            case AnnotationData.TypeEllipse:
            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
            case AnnotationData.TypeImage:
                LayoutBoxHandles(UnrotatedCanvasBounds(fe), AnnotationTransform.AngleOf(fe), HandlePadding);
                break;
            case AnnotationData.TypeStep:
                // Survey's counter: no scale handles, and rotation means aiming the pin's
                // pointer, not spinning the badge. The grip sits out past the tip, on the
                // pointer's own bearing, so it reads as "drag the point around".
                _handleKind = HandleKind.Box;
                EnsureThumbs(0);
                PlaceCounterGrip(fe, meta);
                HandleLayer.Visibility = Visibility.Visible;
                break;
            default:
                HideHandles(); // spotlight / emoji: move + delete only
                break;
        }
    }

    /// <summary>
    /// Survey's counter bbox (useSVGInteraction.js:3955-3972): the canonical nub-up rect
    /// <c>2r × (2r + tipExt)</c> above the bubble centre, turned by <c>pointerAngle + 90</c>
    /// about that centre. Handles, anchors, angle and pivot all come from here so the chrome
    /// and the resize maths can never drift into two different frames.
    /// </summary>
    private static (Rect Frame, Point Body, double Angle) CounterBboxFrame(FrameworkElement fe, AnnotationData meta)
    {
        // The badge Grid is 3r square (TipDistance(r) * 2) with the bubble at its centre, which
        // is exactly what UnrotatedCanvasBounds now reports for it — but read the Grid direct
        // anyway, since this frame's whole job is to be that canonical box.
        double size = fe.Width;
        if (double.IsNaN(size) || size <= 0) size = UnrotatedCanvasBounds(fe).Width;
        double r = size / 3;
        double tipExt = r * 0.5;
        double left = Canvas.GetLeft(fe); if (double.IsNaN(left)) left = 0;
        double top = Canvas.GetTop(fe); if (double.IsNaN(top)) top = 0;
        Vector off = AnnotationTransform.OffsetOf(fe);
        var body = new Point(left + off.X + size / 2, top + off.Y + size / 2);
        return (new Rect(body.X - r, body.Y - r - tipExt, 2 * r, 2 * r + tipExt), body,
            AnnotationRotation.Normalize((meta.PointerAngle ?? CounterGeometry.DefaultPointerAngle) + 90));
    }

    /// <summary>
    /// Eight thumbs: corners 0=TL,1=TR,2=BR,3=BL then edge midpoints 4=T,5=R,6=B,7=L, plus
    /// the rotation grip. When the annotation is rotated every thumb rides the rotated
    /// frame, so the chrome keeps wrapping the mark instead of boxing it axis-aligned.
    /// </summary>
    private void LayoutBoxHandles(Rect b, double angle, double padding = 0, Point? rotationCentre = null)
    {
        _handleKind = HandleKind.Box;
        var centre = rotationCentre ?? new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        var pts = BoxHandlePoints(b, padding);
        var spec = AdaptiveHandleSpec(b.Width, b.Height);
        EnsureThumbs(pts.Length);
        for (int i = 0; i < pts.Length; i++)
        {
            // 0-3 corners, 4=T 6=B horizontal pills, 5=R 7=L vertical pills (Survey mt/mb/ml/mr).
            if (!spec.Shows(i)) { _handleThumbs[i].Visibility = Visibility.Collapsed; continue; }
            ThumbKind kind = i < 4 ? ThumbKind.Corner : (i is 4 or 6 ? ThumbKind.PillH : ThumbKind.PillV);
            PlaceThumb(i, AnnotationRotation.RotatePoint(pts[i], centre, angle), kind, angle);
        }

        double boxCentreX = b.Left + b.Width / 2;
        var top = new Point(boxCentreX, b.Top - padding);
        var grip = new Point(boxCentreX, b.Top - padding - spec.RotationOffset);
        PlaceRotateThumb(
            AnnotationRotation.RotatePoint(grip, centre, angle),
            AnnotationRotation.RotatePoint(top, centre, angle));

        HandleLayer.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Survey's non-border-flush selection padding: the handles sit 2 page-units outside the
    /// bbox, exactly on the dashed frame (SVGSelectionOverlay.jsx:83-94).
    /// </summary>
    private const double HandlePadding = 2;

    /// <summary>Which of the 8 box thumbs fit, and how far out the rotation grip sits.</summary>
    private readonly record struct HandleSpec(bool AllFit, bool CornersFit, double RotationOffset)
    {
        /// <summary>'all' shows every thumb, 'corners' shows 0-3, 'single' shows BR (index 2) only.</summary>
        public bool Shows(int index) => AllFit || (CornersFit ? index < 4 : index == 2);
    }

    /// <summary>
    /// Survey getAdaptiveSelectionHandleSpec (selectionHandleVisibility.js:42-77): drop the
    /// pills, then everything but the bottom-right corner, once the padded box can no longer
    /// hold them. The tiers AND the rotation offset deliberately use the RAW inverse scale
    /// (unclamped); only the drawn handle sizes use the clamped one, so handles keep culling
    /// as you zoom out and the grip keeps clearing them.
    /// </summary>
    private HandleSpec AdaptiveHandleSpec(double width, double height)
    {
        double rawIs = Math.Sqrt(1.0 / _zoom);
        const double padding = 2;
        double boxW = Math.Max(0, width) + padding * 2;
        double boxH = Math.Max(0, height) + padding * 2;
        double cornerR = 5.5 * rawIs, minGap = 4 * rawIs;
        double hPillW = 28 * rawIs, hPillH = 8 * rawIs, vPillH = 28 * rawIs;
        bool cornersFit = boxW >= cornerR * 2 + minGap && boxH >= cornerR * 2 + minGap;
        bool allFit = cornersFit
            && boxW >= cornerR * 2 + hPillW + minGap * 2
            && boxH >= cornerR * 2 + vPillH + minGap * 2;

        // Survey computes the rotation offset inside getAdaptiveSelectionHandleSpec, which
        // receives the RAW inverseScale (SVGSelectionOverlay.jsx:68-73) — only the drawn
        // handle SIZES use the clamped one (selectionHandleVisibility.js:69-70).
        double topHalf = allFit ? hPillH / 2 : cornerR;
        // Already in content units: Survey's metrics carry `is` and the offset is applied
        // straight to the bbox in page units (SVGSelectionOverlay.jsx:76-80).
        double offset = Math.Max(AnnotationRotation.HandleOffsetScreenPx, 10 * rawIs + topHalf + minGap);
        return new HandleSpec(allFit, cornersFit, offset);
    }

    /// <summary>
    /// Survey's counter chrome (SVGAnnotationLayer.jsx:5106-5164): ONE small dot sitting
    /// exactly on the nubbin tip — no stem, no rotate icon, no dashed bbox. Same white
    /// fill / the selection ring colour ring as every other handle, with the counter's own 0.25 shadow.
    /// </summary>
    private void PlaceCounterGrip(FrameworkElement fe, AnnotationData meta)
    {
        Rect b = UnrotatedCanvasBounds(fe);
        if (b.Width < 0.5 && b.Height < 0.5) { HideRotateThumb(); return; }
        double angle = (meta.PointerAngle ?? CounterGeometry.DefaultPointerAngle) * Math.PI / 180;
        var centre = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        double tipDist = b.Width / 2;
        var tip = new Point(centre.X + Math.Cos(angle) * tipDist, centre.Y + Math.Sin(angle) * tipDist);
        PlaceRotateThumb(tip, tip, counterDot: true);
    }

    /// <summary>Puts the grip above an element that has no box frame of its own.</summary>
    private void PlaceRotateGrip(FrameworkElement fe)
    {
        Rect b = UnrotatedCanvasBounds(fe);
        if (b.Width < 0.5 && b.Height < 0.5) { HideRotateThumb(); return; }
        double angle = AnnotationTransform.AngleOf(fe);
        var centre = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        var top = new Point(centre.X, b.Top);
        var grip = new Point(centre.X, b.Top - AdaptiveHandleSpec(b.Width, b.Height).RotationOffset);
        PlaceRotateThumb(
            AnnotationRotation.RotatePoint(grip, centre, angle),
            AnnotationRotation.RotatePoint(top, centre, angle));
    }

    /// <summary>
    /// The annotation's canvas bounds as if it were NOT rotated. Rotation turns about this
    /// rect's centre, so the handle and resize maths stay in a simple axis-aligned frame and
    /// only map points in and out at the edges.
    /// </summary>
    private static Rect UnrotatedCanvasBounds(FrameworkElement fe)
    {
        Rect local = AnnotationTransform.LocalBounds(fe);
        double left = Canvas.GetLeft(fe); if (double.IsNaN(left)) left = 0;
        double top = Canvas.GetTop(fe); if (double.IsNaN(top)) top = 0;
        Vector off = AnnotationTransform.OffsetOf(fe);
        return new Rect(local.X + left + off.X, local.Y + top + off.Y, local.Width, local.Height);
    }

    /// <summary>
    /// The cloud's selection frame in canvas coordinates. <see cref="CloudPath.SelectionFrame"/>
    /// computes the padded crown hull in the shape's OWN space, so this only has to say where
    /// the shape sits. The hull is symmetric about the authored box, so the frame's centre is
    /// still the centre the render rotates about and the two cannot drift apart.
    /// </summary>
    private static Rect CloudSelectionFrameInCanvas(Path cloud)
    {
        Rect frame = CloudPath.SelectionFrame(cloud.Data, cloud.StrokeThickness);
        if (frame.IsEmpty) return UnrotatedCanvasBounds(cloud);
        double left = Canvas.GetLeft(cloud); if (double.IsNaN(left)) left = 0;
        double top = Canvas.GetTop(cloud); if (double.IsNaN(top)) top = 0;
        Vector off = AnnotationTransform.OffsetOf(cloud);
        return new Rect(frame.X + left + off.X, frame.Y + top + off.Y, frame.Width, frame.Height);
    }

    /// <summary>Selected cloud only: Survey's glow RING, or nothing.</summary>
    private Path? _cloudGlow;

    /// <summary>
    /// Survey's cloud glow (<see cref="CloudPath.HoverGlowRing"/>): the crowns widened, with the
    /// ink band knocked straight out, so a translucent cloud keeps its own colour instead of
    /// reading purple through the blue. No blur.
    /// <para>CloudPath says to paint it under the ink; this hangs it in InteractionCanvas just
    /// below HoverHalo, i.e. over the mark but under every other piece of selection chrome.
    /// That is equivalent HERE because the ring is a knockout — it shares a boundary with the
    /// ink and covers none of it, which is the whole reason the bleed-through the "under" rule
    /// guards against cannot happen either way. The alternative, splicing chrome into
    /// AnnotationCanvas, would put a non-annotation into the collection that serialisation,
    /// marquee selection, z-order and export all read as the annotation list.</para>
    /// </summary>
    private void PaintCloudGlow(Path? cloud)
    {
        if (cloud?.Data is not PathGeometry crowns || HoverHalo is null)
        {
            if (_cloudGlow is not null) _cloudGlow.Visibility = Visibility.Collapsed;
            return;
        }
        if (_cloudGlow is null)
        {
            _cloudGlow = new Path { Stroke = null, IsHitTestVisible = false };
            InteractionCanvas.Children.Insert(
                InteractionCanvas.Children.IndexOf(HoverHalo), _cloudGlow);
        }
        _cloudGlow.Fill = new SolidColorBrush(AnnotationStyle.SelectionRingColor);
        _cloudGlow.Opacity = CloudPath.HoverGlowOpacity;
        _cloudGlow.Data = ToCanvasSpace(cloud,
            CloudPath.HoverGlowRing(crowns, cloud.StrokeThickness));
        _cloudGlow.Visibility = Visibility.Visible;
    }

    private static Point RotationCentre(FrameworkElement fe)
    {
        Rect b = UnrotatedCanvasBounds(fe);
        return new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
    }

    /// <summary>
    /// The rotation grip. <paramref name="counterDot"/> renders Survey's counter chrome
    /// instead: the bare 11 px handle dot on the pin tip, no icon and no stem.
    /// </summary>
    private void PlaceRotateThumb(Point centre, Point anchor, bool counterDot = false)
    {
        if (_rotateThumb is null)
        {
            _rotateThumb = new Ellipse
            {
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(AnnotationStyle.SelectionRingColor),
            };
            // Survey draws the grip's tether in neutral grey, not the ring blue
            // (SVGSelectionOverlay.jsx:239-247).
            _rotateStem = new Line { Stroke = new SolidColorBrush(Color.FromRgb(0xD1, 0xD1, 0xD1)) };
            _rotateIcon = new ContentControl
            {
                // Survey's rotate-icon.svg is stroked #000000, not the Fabric fallback grey.
                Content = FindResource("SurveyIcon.Rotate"),
                Foreground = new SolidColorBrush(Colors.Black),
                IsHitTestVisible = false,
            };
            HandleLayer.Children.Add(_rotateStem);
            HandleLayer.Children.Add(_rotateThumb);
            HandleLayer.Children.Add(_rotateIcon);
        }

        double is_ = HandleScale;
        double s = (counterDot ? HandleScreenPx : RotateHandleScreenPx) * is_;
        _rotateThumb.Width = s;
        _rotateThumb.Height = s;
        // Survey: the counter tip dot is a non-box handle — strokeWidth 1.5 with
        // vector-effect non-scaling-stroke (SVGAnnotationLayer.jsx:5142-5143), i.e. a constant
        // 1.5 SCREEN px. The real rotation grip is box chrome, so it keeps the damped 1*is.
        _rotateThumb.StrokeThickness = counterDot ? ConstantRingWidth : 1 * is_;
        // Survey: rotation circle drop-shadow(0 2 5 rgba(0,0,0,0.1)); the counter tip
        // handle uses the heavier drop-shadow(0 1 3 rgba(0,0,0,0.25)).
        _rotateThumb.Effect = new DropShadowEffect
        {
            BlurRadius = (counterDot ? 3 : 5) * is_,
            ShadowDepth = (counterDot ? 1 : 2) * is_,
            Direction = 270,
            Opacity = counterDot ? 0.25 : 0.1,
            Color = Colors.Black,
        };
        Canvas.SetLeft(_rotateThumb, centre.X - s / 2);
        Canvas.SetTop(_rotateThumb, centre.Y - s / 2);
        _rotateThumb.Visibility = Visibility.Visible;
        double iconSize = s * 0.7; // Survey: 14 px icon in a 20 px circle
        _rotateIcon!.Width = iconSize;
        _rotateIcon.Height = iconSize;
        Canvas.SetLeft(_rotateIcon, centre.X - iconSize / 2);
        Canvas.SetTop(_rotateIcon, centre.Y - iconSize / 2);
        _rotateIcon.Visibility = counterDot ? Visibility.Collapsed : Visibility.Visible;

        _rotateStem!.X1 = anchor.X;
        _rotateStem.Y1 = anchor.Y;
        _rotateStem.X2 = centre.X;
        _rotateStem.Y2 = centre.Y;
        _rotateStem.StrokeThickness = 1 * is_;
        _rotateStem.Visibility = counterDot ? Visibility.Collapsed : Visibility.Visible;

        _gripContent = centre;
        _gripAnchorContent = anchor;
        if (_activeHandle == RotateHandleIndex || RotationPill?.Visibility == Visibility.Visible)
            PlaceRotationPill();
    }

    private void HideRotateThumb()
    {
        HideRotationPill();
        if (_rotateThumb is not null) _rotateThumb.Visibility = Visibility.Collapsed;
        if (_rotateIcon is not null) _rotateIcon.Visibility = Visibility.Collapsed;
        if (_rotateStem is not null) _rotateStem.Visibility = Visibility.Collapsed;
    }

    private static Point[] BoxHandlePoints(Rect raw, double padding = 0)
    {
        Rect b = padding == 0 ? raw : Rect.Inflate(raw, padding, padding);
        return new[]
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
    }

    /// <summary>Survey callout thumbs: 0=tip, 1=knee, 2=TL, 3=TR, 4=BR, 5=BL.</summary>
    private void LayoutCalloutHandles(AnnotationData meta)
    {
        if (_selected is not CalloutAnnotation callout) { HideHandles(); return; }
        var layout = callout.Layout;
        Vector off = ElementOffset(_selected);
        _handleKind = HandleKind.Callout;
        EnsureThumbs(6);
        // Survey places the two bottom corners on the descender-buffered bottom edge.
        var rbox = layout.RenderBox;
        // Survey: every callout handle is a circle, and all six are non-box handles, so they
        // carry the constant 1.5 px ring (SVGAnnotationLayer.jsx:3205-3206, 3218-3219, 3250).
        PlaceThumb(0, layout.Tip + off, constantRing: true);
        PlaceThumb(1, layout.Knee + off, constantRing: true);
        PlaceThumb(2, rbox.TopLeft + off, constantRing: true);
        PlaceThumb(3, rbox.TopRight + off, constantRing: true);
        PlaceThumb(4, rbox.BottomRight + off, constantRing: true);
        PlaceThumb(5, rbox.BottomLeft + off, constantRing: true);
        // Survey per-part cursors: grab on knee/tip, diagonal resize on the corners.
        var cursors = new[]
        {
            Cursors.Hand, Cursors.Hand,
            Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNWSE, Cursors.SizeNESW,
        };
        // Survey turns EVERY handle on the callout red together while the drag is heading
        // for a spot that would be rejected on release (HANDLE_RING_INVALID ef4444).
        var ring = _calloutDragInvalid
            ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
            : new SolidColorBrush(AnnotationStyle.SelectionRingColor);
        for (int i = 0; i < 6; i++)
        {
            _handleThumbs[i].Cursor = cursors[i];
            _handleThumbs[i].Stroke = ring;
        }
        HandleLayer.Visibility = Visibility.Visible;
    }

    /// <summary>Thumbs: 0=from, 1=to, and for line/arrow 2=Survey midpoint-on-curve.</summary>
    /// <summary>
    /// One grabber per vertex. Survey's poly shapes carry their OWN chrome on a single click —
    /// the points you placed, each draggable — rather than a bounding box, which is the whole
    /// reason a polygon is worth having over a rectangle. The box is still reachable: a
    /// double-click swaps in bbox-edit mode, exactly as it does for a line or a counter.
    /// </summary>
    private void LayoutVertexHandles(AnnotationData meta)
    {
        if (meta.Points is not { Length: >= 2 } pts) { HideHandles(); return; }
        Vector off = ElementOffset(_selected!);
        _handleKind = HandleKind.Vertex;
        EnsureThumbs(pts.Length);
        double angle = AnnotationTransform.AngleOf(_selected!);
        Point pivot = RotationCentre((FrameworkElement)_selected!);
        for (int i = 0; i < pts.Length; i++)
        {
            var p = new Point(pts[i][0] + off.X, pts[i][1] + off.Y);
            PlaceThumb(i, angle == 0 ? p : AnnotationRotation.RotatePoint(p, pivot, angle),
                constantRing: true);
        }
        HideRotateThumb();
        HandleLayer.Visibility = Visibility.Visible;
    }

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
        bool surveyCurve = meta.Type is AnnotationData.TypeLine or AnnotationData.TypeArrow;
        EnsureThumbs(surveyCurve ? 3 : 2);
        // The stored points are UNROTATED locals; the element turns about its bounds centre,
        // so a rotated line's handles have to ride the same rotation or they float off the
        // stroke (and the endpoint drag would then be un-grabbable).
        double angle = AnnotationTransform.AngleOf(_selected!);
        Point pivot = RotationCentre((FrameworkElement)_selected!);
        Point W(Point p) => angle == 0 ? p : AnnotationRotation.RotatePoint(p, pivot, angle);
        PlaceThumb(0, W(from), constantRing: true);
        PlaceThumb(1, W(to), constantRing: true);
        if (surveyCurve)
        {
            Point mid = LineCurve.ParseMid(meta.Mid) is Point stored
                ? stored + off
                : LineCurve.Midpoint(from, to);
            // Survey HANDLE_RADIUS_SECONDARY: the curve-bend handle is deliberately smaller
            // than the endpoints (SVGAnnotationLayer.jsx:5450-5462).
            PlaceThumb(2, W(mid), ThumbKind.Secondary, constantRing: true);
        }
        HandleLayer.Visibility = Visibility.Visible;
    }

    /// <summary>The element's accumulated TranslateTransform offset (moves + crop shifts), or zero.</summary>
    private static Vector ElementOffset(UIElement element) => AnnotationTransform.OffsetOf(element);

    private void EnsureThumbs(int count)
    {
        while (_handleThumbs.Count < count)
        {
            var thumb = new Rectangle
            {
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(AnnotationStyle.SelectionRingColor),
                RadiusX = 2,
                RadiusY = 2,
                // Survey handleStyle.js: drop-shadow(0 1px 3px rgba(0,0,0,0.15)).
                Effect = new DropShadowEffect
                {
                    BlurRadius = 3,
                    ShadowDepth = 1,
                    Direction = 270,
                    Opacity = 0.15,
                    Color = Colors.Black,
                },
            };
            _handleThumbs.Add(thumb);
            HandleLayer.Children.Add(thumb);
        }
        for (int i = 0; i < _handleThumbs.Count; i++)
        {
            _handleThumbs[i].Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
            _handleThumbs[i].Stroke = new SolidColorBrush(AnnotationStyle.SelectionRingColor);
            _handleThumbs[i].Cursor = null;
        }
    }

    /// <summary>
    /// Survey handleStyle.js:32 HANDLE_RING_WIDTH 1.5 drawn with <c>vector-effect:
    /// non-scaling-stroke</c> — a constant 1.5 SCREEN px on every shape-owned handle
    /// (endpoints, bend dot, callout parts, counter tip). Content units, so divide by zoom.
    /// </summary>
    private double ConstantRingWidth => 1.5 / _zoom;

    private void PlaceThumb(int index, Point center, ThumbKind kind = ThumbKind.Corner,
        double angleDeg = 0, bool constantRing = false)
    {
        if (index < 0 || index >= _handleThumbs.Count) return;
        var thumb = _handleThumbs[index];
        // Survey selectionHandleVisibility.js:24-40 — every metric is a screen-px base
        // times the dampened, clamped inverse scale.
        double is_ = HandleScale;
        double s = HandleScreenPx * is_;
        double pillLong = 28 * is_, pillShort = 8 * is_; // Survey hPill 28x8 / vPill 8x28
        (thumb.Width, thumb.Height) = kind switch
        {
            ThumbKind.PillH => (pillLong, pillShort),
            ThumbKind.PillV => (pillShort, pillLong),
            ThumbKind.Secondary => (SecondaryHandleScreenPx * is_, SecondaryHandleScreenPx * is_),
            _ => (s, s),
        };
        thumb.RadiusX = thumb.RadiusY = kind switch
        {
            ThumbKind.Square => 2 * is_,
            ThumbKind.PillH or ThumbKind.PillV => 4 * is_, // Survey pillRx
            _ => thumb.Width / 2,
        };
        thumb.RenderTransformOrigin = new Point(0.5, 0.5);
        thumb.RenderTransform = angleDeg == 0 ? null : new RotateTransform(angleDeg);
        // Survey: box handles use the zoom-damped cornerStrokeWidth 1*is; every non-box handle
        // is a flat 1.5 screen px (selectionHandleVisibility.js:30 vs handleStyle.js:32).
        thumb.StrokeThickness = constantRing ? ConstantRingWidth : 1 * is_;
        if (thumb.Effect is DropShadowEffect shadow)
        {
            // Survey corners drop-shadow(0 1 3 0.15); pills drop-shadow(0 2 4 0.15).
            bool pill = kind is ThumbKind.PillH or ThumbKind.PillV;
            shadow.BlurRadius = (pill ? 4 : 3) * is_;
            shadow.ShadowDepth = (pill ? 2 : 1) * is_;
        }
        Canvas.SetLeft(thumb, center.X - thumb.Width / 2);
        Canvas.SetTop(thumb, center.Y - thumb.Height / 2);
        thumb.Visibility = Visibility.Visible;
    }

    private void HideHandles()
    {
        if (_adjustingCrop) return; // an in-progress crop adjust keeps its handles
        _handleKind = HandleKind.None;
        if (_bendTip is not null) _bendTip.IsOpen = false;
        HandleLayer.Visibility = Visibility.Collapsed;
        foreach (var t in _handleThumbs) t.Visibility = Visibility.Collapsed;
        HideRotateThumb();
        PaintCloudGlow(null);
    }

    /// <summary>Index of the handle near a content point, or -1. Endpoint/box share the same thumb list.</summary>
    private int HitTestHandle(Point pos)
    {
        if (_handleKind == HandleKind.None || HandleLayer.Visibility != Visibility.Visible) return -1;
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < _handleThumbs.Count; i++)
        {
            var thumb = _handleThumbs[i];
            if (thumb.Visibility != Visibility.Visible) continue;
            var c = new Point(Canvas.GetLeft(thumb) + thumb.Width / 2, Canvas.GetTop(thumb) + thumb.Height / 2);
            // Survey: the grab zone is exactly the drawn SVG shape (pointerEvents 'auto'), so a
            // 28x8 pill must be hit-tested as a RECTANGLE. A radial test would give it a
            // 28-unit-DIAMETER disc and let it swallow clicks well outside the frame.
            // Pills are the only non-square thumbs, and they carry the annotation's rotation.
            bool pill = Math.Abs(thumb.Width - thumb.Height) > 1e-6;
            double spin = thumb.RenderTransform is RotateTransform rt ? rt.Angle : 0;
            Vector v = (spin == 0 ? pos : AnnotationRotation.RotatePoint(pos, c, -spin)) - c;
            bool hit = pill
                ? Math.Abs(v.X) <= thumb.Width / 2 && Math.Abs(v.Y) <= thumb.Height / 2
                : v.Length <= thumb.Width / 2;
            double d = v.Length;
            if (hit && d < bestDist) { bestDist = d; best = i; }
        }

        // The grip wins ties with the top-edge thumb below it: it is the outer of the two,
        // so the user reached past the frame deliberately to get to it.
        if (_rotateThumb is { Visibility: Visibility.Visible })
        {
            double cx = Canvas.GetLeft(_rotateThumb) + _rotateThumb.Width / 2;
            double cy = Canvas.GetTop(_rotateThumb) + _rotateThumb.Height / 2;
            if ((pos - new Point(cx, cy)).Length <= _rotateThumb.Width / 2)
                return RotateHandleIndex;
        }
        return best;
    }

    /// <summary>
    /// Survey getCursorForHandle (svgTransformMath.js:117-141): the 8 box thumbs walk an
    /// 8-entry cursor ring rotated by <c>round(angle / 45) % 8</c>, so the cursor follows the
    /// annotation's own rotation. 'mtr' is crosshair; endpoint/bend/callout handles grab.
    /// </summary>
    private static Cursor CursorForHandle(int index, double angleDegrees)
    {
        if (index == RotateHandleIndex) return Cursors.Cross;
        // WinShot thumb order 0=TL 1=TR 2=BR 3=BL 4=T 5=R 6=B 7=L maps onto Survey's ring
        // indices tl:0 mt:1 tr:2 mr:3 br:4 mb:5 bl:6 ml:7.
        int[] baseIndex = { 0, 2, 4, 6, 1, 3, 5, 7 };
        if (index < 0 || index >= baseIndex.Length) return Cursors.Arrow;
        Cursor[] ring =
        {
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
        };
        int offset = (int)Math.Round(((angleDegrees % 360) + 360) % 360 / 45) % 8;
        return ring[(baseIndex[index] + offset) % 8];
    }

    /// <summary>
    /// Paints the handle-specific cursor while hovering selection chrome, or returns false so
    /// the ordinary tool cursor stands. Survey drives this from the handle groups' inline
    /// cursor styles; WinShot's handle layer is not hit-testable, so it runs off the move path.
    /// </summary>
    /// <summary>
    /// Survey's curve-bend midpoint carries a native <c>&lt;title&gt;Drag to bend&lt;/title&gt;</c>
    /// (SVGAnnotationLayer.jsx:5374-5463). WinShot's handles live inside
    /// <c>InteractionCanvas</c>, which is <c>IsHitTestVisible="False"</c>, so a ToolTip set on
    /// the thumb could never fire - the chip has to be driven from the same hover hit-test
    /// that already paints the handle cursor.
    /// </summary>
    private ToolTip? _bendTip;

    private void ShowBendTip(bool show, Point canvasPos)
    {
        if (!show)
        {
            if (_bendTip is not null) _bendTip.IsOpen = false;
            return;
        }
        _bendTip ??= new ToolTip
        {
            Content = "Drag to bend",
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            PlacementTarget = Viewport,
            StaysOpen = true,
        };
        var vp = AnnotationCanvas.TranslatePoint(canvasPos, Viewport);
        _bendTip.HorizontalOffset = vp.X;
        // Survey anchors the chip below its target with a 10px gap (Tooltip.jsx:86-98).
        _bendTip.VerticalOffset = vp.Y + HandleScreenPx / 2 + 10;
        _bendTip.IsOpen = true;
    }

    private bool UpdateHandleCursor(Point pos)
    {
        if (_handleKind is HandleKind.None or HandleKind.Crop) { ShowBendTip(false, pos); return false; }
        int handle = HitTestHandle(pos);
        if (handle < 0) { ShowBendTip(false, pos); return false; }
        // Endpoint thumb 2 IS the curve-bend midpoint (see LayoutEndpointHandles).
        ShowBendTip(_handleKind == HandleKind.Endpoints && handle == 2 && _activeHandle < 0, pos);
        double angle = _selected is FrameworkElement fe ? AnnotationTransform.AngleOf(fe) : 0;
        // Survey line endpoints, the curve-bend midpoint, the counter tip and the callout
        // tip/knee are all cursor:'grab' (SVGAnnotationLayer.jsx:5379, 5396, 5149).
        Cursor cursor = _handleKind switch
        {
            HandleKind.Endpoints or HandleKind.Vertex => Cursors.Hand,
            HandleKind.Callout => _handleThumbs[handle].Cursor ?? Cursors.Hand,
            _ when _selected is FrameworkElement { Tag: AnnotationData { Type: AnnotationData.TypeStep } }
                => Cursors.Hand,
            _ => CursorForHandle(handle, angle),
        };
        SetSurfaceCursor(cursor);
        return true;
    }

    // ---- resize drag lifecycle ----

    private void BeginResize(int handle, Point pos)
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
            // Survey freezes originalProps.angle at pointer-down and never re-reads it during
            // the drag (useSVGInteraction.js:4220-4258). Reading it live mixes frames, and the
            // shape walks away under the cursor.
            _resizeAngle = AnnotationTransform.AngleOf(fe);
            _resizeFrame = null;
            _resizePivot = null;
            // A counter never carries a RotateTransform (its "rotation" aims the nub), so
            // AngleOf is always 0 for one. Survey resizes it in its OWN rendered frame
            // instead: the canonical nub-up rect at pointerAngle+90 about the bubble centre.
            if (meta.Type == AnnotationData.TypeStep)
            {
                var (frame, body, frameAngle) = CounterBboxFrame(fe, meta);
                _resizeAngle = frameAngle;
                _resizeFrame = frame;
                _resizePivot = body;
            }
            _calloutDragStart = null;
            _calloutDragInvalid = false;
            if (_handleKind == HandleKind.Callout && _selected is CalloutAnnotation dragged)
            {
                _calloutDragStart = dragged.Layout;
                _calloutGrabLocal = pos - ElementOffset(dragged);
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

        // A counter resizes in its own rendered frame (Survey :3955-3972); everything else in
        // its un-rotated bounds. The undo snapshot keeps the real badge box either way.
        Rect oldBounds = _resizeFrame ?? _resizeBefore.Bounds;
        // Every branch works in the FROZEN un-rotated frame: the pivot is the pre-drag bounds
        // centre and the angle is the pre-drag angle, so the maths never chases a shape that
        // is changing underneath it (Survey useSVGInteraction.js:4104-4129).
        double angle = _resizeAngle;
        var frozenCentre = _resizePivot
            ?? new Point(oldBounds.Left + oldBounds.Width / 2, oldBounds.Top + oldBounds.Height / 2);
        Point local = angle == 0 ? pos : AnnotationRotation.RotatePoint(pos, frozenCentre, -angle);

        if (_handleKind == HandleKind.Vertex)
        {
            ApplyVertexDrag(fe, meta, local);
            UpdateSelectionVisual();
            return;
        }

        if (_handleKind == HandleKind.Endpoints)
        {
            // Survey's endpoint drag consults no modifier at all (useSVGInteraction.js:1874-1960)
            // and it never touches obj.angle — the tilt survives the drag (:3413).
            ApplyEndpointResize(fe, meta, local);
            if (angle != 0)
            {
                // Survey's (R - I)·Δ compensation (:1940-1952): moving a control point moves
                // the bbox centre the mark pivots about, so shift the geometry back by the
                // rotation-induced part of that drift before re-applying the angle.
                fe.UpdateLayout();
                Vector d = RotationCentre(fe) - frozenCentre;
                double rad = angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
                ApplyEndpointResize(fe, meta, local,
                    new Vector((cos - 1) * d.X - sin * d.Y, sin * d.X + (cos - 1) * d.Y));
                fe.UpdateLayout();
                AnnotationTransform.SetAngle(fe, angle);
            }
        }
        else if (_handleKind == HandleKind.Callout)
            ApplyCalloutResize(fe, meta, pos);   // callouts never rotate
        else
        {
            // Survey's supportsFlip set is rect / circle / ellipse / exact ink path ONLY
            // (:2189-2197). Those take the signed 0.01 magnitude floor (ink: none at all, so
            // microscopic geometry survives); every other type — image, text, line, arrow —
            // takes `Math.max(0.1, scale)` (:2208-2210), which is BOTH a 10% floor and a
            // no-mirror clamp: dragging past the anchor bounces instead of flipping. A counter
            // is the odd one out: Survey abs()es its scale (:2174-2183), so it never mirrors
            // but does keep growing at the full drag rate past the anchor.
            bool supportsFlip = meta.Type is AnnotationData.TypeRectangle or AnnotationData.TypeEllipse
                or AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter;
            local = FloorScale(oldBounds, _activeHandle, local, meta.Type switch
            {
                AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter => 0,
                AnnotationData.TypeRectangle or AnnotationData.TypeEllipse => 0.01,
                _ => 0.1,
            // A counter keeps the signed pointer even though it never mirrors: Math.abs() of a
            // crossed scale still grows at the full drag rate on the near side (:2174-2180).
            }, allowFlip: supportsFlip || meta.Type == AnnotationData.TypeStep);
            var next = NewBoundsForHandle(oldBounds, _activeHandle, local);
            // Survey forces a counter resize uniform, but with its OWN rule: max(|sx|,|sy|) on
            // a corner and the moved axis alone on an edge, with no averaging (:2174-2183).
            // safeUniformScale is unconditionally POSITIVE there, so the "flipped" negation at
            // :2241/:2245 is dead code for a counter — hence mirror: false.
            if (meta.Type == AnnotationData.TypeStep)
                next = ShapeConstraint.UniformResize(oldBounds, _activeHandle, next, average: false, mirror: false);
            else if (ShapeConstraint.ShiftHeld)
                next = ShapeConstraint.UniformResize(oldBounds, _activeHandle, next);

            if (angle != 0)
            {
                // Survey lands the new centre as worldAnchor + R(offsetFromAnchor) (:2222-2252)
                // and NEGATES that offset on a flipped axis (:2241, :2245), so a mirrored shape
                // sits on the far side of the SAME anchor. Per driven axis the offset collapses
                // to sign(local - anchor) * halfExtent, which is correct flipped AND unflipped.
                // Take the direction from where the POINTER is, never from `next`:
                // NewBoundsForHandle normalises the rect, so after a flip `next`'s anchor edge
                // has swapped sides and AnchorPoint(next, ...) returns the wrong corner.
                var (ax, ay) = ShapeConstraint.AxisAnchors(oldBounds, _activeHandle);
                Point worldAnchor = AnnotationRotation.RotatePoint(
                    ShapeConstraint.AnchorPoint(oldBounds, _activeHandle), frozenCentre, angle);
                // An axis this handle does not drive gets offset 0, and AnchorPoint already
                // supplies the box mid-line for it — Survey's affectsX/affectsY guards. A type
                // that cannot flip takes the side from the HANDLE's own growth direction, which
                // is what a positive scale means (image/text/line are clamped to that side
                // anyway; a counter is not, so this is the line that stops it mirroring).
                int SideX(double axv) => supportsFlip
                    ? (local.X < axv ? -1 : 1)
                    : (_activeHandle is 0 or 3 or 7 ? -1 : 1);
                int SideY(double ayv) => supportsFlip
                    ? (local.Y < ayv ? -1 : 1)
                    : (_activeHandle is 0 or 1 or 4 ? -1 : 1);
                var offset = new Point(
                    ax is double axv ? SideX(axv) * next.Width / 2 : 0,
                    ay is double ayv ? SideY(ayv) * next.Height / 2 : 0);
                Point o = AnnotationRotation.RotatePoint(offset, new Point(0, 0), angle);
                next = new Rect(worldAnchor.X + o.X - next.Width / 2,
                                worldAnchor.Y + o.Y - next.Height / 2,
                                next.Width, next.Height);
            }

            ApplyBoxResize(fe, meta, next, local);
            // A counter's frame angle is the NUB's bearing, not a transform: ResizeCounter has
            // already absorbed it into the badge's position, and Survey never puts an angle on
            // a counter (its renderer draws the nub at data.pointerAngle).
            if (angle != 0 && meta.Type != AnnotationData.TypeStep)
            {
                fe.UpdateLayout();
                AnnotationTransform.SetAngle(fe, angle);
            }
        }

        UpdateSelectionVisual();
    }

    /// <summary>Live rotation from the grip: bearing, grab offset, then the snap rules.</summary>
    private void DragRotate(FrameworkElement fe, Point pos)
    {
        // Counter: aim the pointer at the cursor (Survey's counterRotateDrag). The badge
        // itself never turns, so the number stays upright.
        if (fe is Grid badge && fe.Tag is AnnotationData step && step.Type == AnnotationData.TypeStep)
        {
            double pointer = AnnotationRotation.PointerAngleFromCursor(RotationCentre(fe), pos);
            // Survey snaps in the mtr branch BEFORE the counter special-case
            // (useSVGInteraction.js:2357-2380), so the bbox-edit grip honours Shift while the
            // standalone tip dot (SVGAnnotationLayer.jsx:5183) never does. There is no soft
            // snap on this path at all, so it is Shift-only.
            if (ShapeConstraint.ShiftHeld && ReferenceEquals(_bboxEditTarget, fe))
                pointer = AnnotationRotation.Quantize(pointer);
            AnnotationFactory.SetStepPointerAngle(badge, pointer);
            step.PointerAngle = pointer;
            return;
        }

        double bearing = AnnotationRotation.AngleFromPointer(RotationCentre(fe), pos);
        double angle = AnnotationRotation.ResolveDrag(bearing, ShapeConstraint.ShiftHeld);
        AnnotationTransform.SetAngle(fe, angle);
        if (fe.Tag is AnnotationData meta) meta.Angle = angle;
    }

    private void EndResize()
    {
        _activeHandle = -1;
        Viewport.ReleaseMouseCapture();
        // The rate limiter lets a resize skip repaints; the gesture is over, so pay them.
        FlushRedactionRefresh();
        if (_selected is not null) RefreshRedaction(_selected);
        ScheduleHideRotationPill();
        _justDraggedAt = Environment.TickCount64;

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

        if (_handleKind == HandleKind.Callout && fe is CalloutAnnotation dropped
            && _calloutDragStart is CalloutLayout preDrag)
        {
            _calloutDragStart = null;
            _calloutDragInvalid = false;
            // Survey: an invalid drop rolls the WHOLE callout - tip, knee, box position AND
            // box size - back to the pre-drag snapshot rather than clamping it into shape.
            ApplyCalloutLayout(dropped, CalloutLayout.IsDropSafe(dropped.Layout, HandleScreenPx / 2)
                ? CalloutLayout.FromParts(dropped.Layout.Tip, dropped.Layout.Knee,
                    dropped.Layout.Box, dropped.Layout.Descender)
                : preDrag);
        }

        var before = _resizeBefore;
        var after = CaptureResizeSnapshot(_selected, meta);
        _resizeBefore = null;
        _resizeFrame = null;
        _resizePivot = null;

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
        _calloutDragStart = null;
        _calloutDragInvalid = false;
        _adjustingCrop = false;
        _resizeBefore = null;
        _resizeFrame = null;
        _resizePivot = null;
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

    /// <summary>
    /// Survey clamps the SCALE, not the size (useSVGInteraction.js:2198-2218), so a 400 px
    /// rectangle stops at 4 px while an 8 px mark keeps shrinking proportionally. Applying the
    /// floor to the POINTER instead of the resulting rect keeps it per-axis. A floor of 0 is
    /// Survey's exact-path rule: no visible-size floor.
    /// <para><paramref name="allowFlip"/> true is the signed magnitude floor, so a flip still
    /// flips. False is Survey's <c>Math.max(0.1, scale)</c> branch (:2208-2210), which is one
    /// clamp doing two jobs: the 10% floor AND the no-mirror guard. The side comes from the
    /// HANDLE (the box lives left of its anchor for 0/3/7, above it for 0/1/4), never from the
    /// sign of the delta — that sign is exactly what is wrong once the drag has crossed.</para>
    /// </summary>
    private static Point FloorScale(Rect old, int handle, Point local, double floor, bool allowFlip = true)
    {
        if (floor <= 0 && allowFlip) return local;
        var (ax, ay) = ShapeConstraint.AxisAnchors(old, handle);
        double x = local.X, y = local.Y;
        if (ax is double axv && old.Width > 1e-6)
        {
            double min = old.Width * floor, d = x - axv;
            if (!allowFlip)
            {
                bool leftOfAnchor = handle is 0 or 3 or 7;
                double mag = Math.Max(min, leftOfAnchor ? -d : d);
                x = axv + (leftOfAnchor ? -mag : mag);
            }
            else if (Math.Abs(d) < min) x = axv + (d < 0 ? -min : min);
        }
        if (ay is double ayv && old.Height > 1e-6)
        {
            double min = old.Height * floor, d = y - ayv;
            if (!allowFlip)
            {
                bool aboveAnchor = handle is 0 or 1 or 4;
                double mag = Math.Max(min, aboveAnchor ? -d : d);
                y = ayv + (aboveAnchor ? -mag : mag);
            }
            else if (Math.Abs(d) < min) y = ayv + (d < 0 ? -min : min);
        }
        return new Point(x, y);
    }

    // ---- per-kind apply ----

    /// <summary>Resizes a box-style element to <paramref name="b"/> (canvas coords). Text scales font size.</summary>
    private void ApplyBoxResize(FrameworkElement fe, AnnotationData meta, Rect b, Point local)
    {
        switch (meta.Type)
        {
            case AnnotationData.TypeRectangle:
            case AnnotationData.TypeEllipse:
            case AnnotationData.TypeImage:
                ResizeBoxShape(fe, b, _cloudIntensity);
                break;
            case AnnotationData.TypeText:
                ResizeTextElement(fe, meta, b);
                break;
            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
                ResizeInk(fe, meta, b, local);
                break;
            case AnnotationData.TypeStep:
                ResizeCounter(fe, meta, b);
                break;
            case AnnotationData.TypeLine:
            case AnnotationData.TypeArrow:
            case AnnotationData.TypeCurvedArrow:
                ResizeStrokeInBox(fe, meta, b);
                break;
            case AnnotationData.TypePolygon:
            case AnnotationData.TypePolyline:
                ResizePolyInBox(fe, meta, b);
                break;
        }
    }

    /// <summary>
    /// Survey's per-vertex drag (useSVGInteraction.js:4550, :2105-2111): the grabbed point is
    /// replaced and EVERY other vertex is left byte-identical, so dragging one corner cannot
    /// nudge a neighbour by a rounding wobble. The rule itself lives in
    /// <see cref="PolyDraft.MoveVertex"/>, shared with the drafting code.
    /// </summary>
    private void ApplyVertexDrag(FrameworkElement fe, AnnotationData meta, Point pos)
    {
        if (fe is not Path poly || _resizeBefore?.Points is not { Length: >= 2 } basePts) return;
        Point[] next = PolyDraft.MoveVertex(basePts, _activeHandle, pos);
        fe.RenderTransform = null;   // the points are absolute now
        RebuildPolyGeometry(poly, meta, next);
    }

    /// <summary>
    /// A polygon / polyline in bbox-edit mode: scale every stored vertex out of the pre-drag
    /// bounds into the new box, the way <see cref="ResizeStrokeInBox"/> does for a line.
    /// </summary>
    private void ResizePolyInBox(FrameworkElement fe, AnnotationData meta, Rect newBounds)
    {
        if (fe is not Path poly || _resizeBefore?.Points is not { Length: >= 2 } basePts) return;
        Rect baseBounds = _resizeBefore.Bounds;
        double sx = baseBounds.Width > 1e-6 ? newBounds.Width / baseBounds.Width : 1;
        double sy = baseBounds.Height > 1e-6 ? newBounds.Height / baseBounds.Height : 1;
        Point[] mapped = basePts.Select(p => new Point(
            newBounds.X + (p.X - baseBounds.X) * sx,
            newBounds.Y + (p.Y - baseBounds.Y) * sy)).ToArray();
        fe.RenderTransform = null;
        RebuildPolyGeometry(poly, meta, mapped);
    }

    /// <summary>
    /// Re-draws a poly shape from a new vertex run and re-stores it. One funnel, because the
    /// Cloud border style makes the geometry a function of the ring: a cloud re-built from a
    /// stale ring is a cloud around the shape's old outline.
    /// </summary>
    private void RebuildPolyGeometry(Path poly, AnnotationData meta, IReadOnlyList<Point> points)
    {
        bool closed = meta.Type == AnnotationData.TypePolygon;
        var rebuilt = AnnotationFactory.CreatePolyShape(
            points, closed, StrokeColorOf(poly), Colors.Transparent, poly.StrokeThickness,
            AnnotationStyle.LineStyleFrom(meta), meta.CloudIntensity ?? _cloudIntensity,
            StrokeStyles.ParseRender(meta.Sketch), meta.Seed ?? 0);
        poly.Data = rebuilt.Data;
        meta.Points = points.Select(p => new[] { p.X, p.Y }).ToArray();
        meta.Tx = 0;
        meta.Ty = 0;
        Canvas.SetLeft(poly, 0);
        Canvas.SetTop(poly, 0);
    }

    /// <summary>
    /// Line / arrow / curved arrow inside bbox edit mode: scale every stored point from the
    /// pre-drag bounds into the new box and rebuild the geometry, so the whole mark resizes
    /// rather than one endpoint moving.
    /// </summary>
    private void ResizeStrokeInBox(FrameworkElement fe, AnnotationData meta, Rect newBounds)
    {
        if (fe is not Path stroke || _resizeBefore?.Points is not { Length: >= 2 } basePts) return;
        Rect baseBounds = _resizeBefore.Bounds;
        // Survey's line commit floors at 0.01 and keeps scaling (:3253-3254); the old
        // "substitute 1" guard silently froze a near-degenerate axis at full size.
        double sx = baseBounds.Width > 1e-6 ? newBounds.Width / baseBounds.Width : 1;
        double sy = baseBounds.Height > 1e-6 ? newBounds.Height / baseBounds.Height : 1;
        Point Map(Point p) => new(
            newBounds.X + (p.X - baseBounds.X) * sx,
            newBounds.Y + (p.Y - baseBounds.Y) * sy);

        bool curved = meta.Type == AnnotationData.TypeCurvedArrow && basePts.Length >= 3;
        Point from = Map(basePts[0]);
        Point to = Map(curved ? basePts[2] : basePts[^1]);
        Point? mid = LineCurve.ParseMid(meta.Mid) is Point m ? Map(m) : null;
        stroke.RenderTransform = null;
        if (curved)
        {
            Point control = Map(basePts[1]);
            stroke.Data = AnnotationFactory.CurvedArrowGeometry(from, control, to, stroke.StrokeThickness);
            meta.Points = new[] { new[] { from.X, from.Y }, new[] { control.X, control.Y }, new[] { to.X, to.Y } };
        }
        else
        {
            var (head, startHead) = AnnotationStyle.HeadsFrom(meta);
            stroke.Data = meta.Type == AnnotationData.TypeArrow
                ? AnnotationFactory.ArrowGeometry(from, to, stroke.StrokeThickness, head, startHead, mid: mid)
                : LineCurve.Stroke(from, to, mid);
            meta.Points = new[] { new[] { from.X, from.Y }, new[] { to.X, to.Y } };
            meta.Mid = mid is Point mm ? new[] { mm.X, mm.Y } : null;
        }
        meta.Tx = 0;
        meta.Ty = 0;
        fe.Tag = meta;
    }

    /// <summary>
    /// Counter resize (Survey useSVGInteraction.js:2267-2272, commit at :3300-3305): the box
    /// drag drives the RADIUS, floored at 4, and the pin is rebuilt at the new size. The badge
    /// stays square because the resize path already forced a uniform scale.
    /// <para><paramref name="b"/> is the CANONICAL nub-up frame (2r wide, 2r + 0.5r tall) the
    /// handles are drawn on, not the 3r badge Grid — so map it back the way Survey's commit
    /// does (<c>obj.top = newTop + newRadius * 0.5</c>, :3300-3305).</para>
    /// </summary>
    private void ResizeCounter(FrameworkElement fe, AnnotationData meta, Rect b)
    {
        if (fe is not Grid badge) return;
        // Survey floors the counter's SCALE at 0.1 (done on the pointer, upstream) and only
        // its committed RADIUS at 4 (:2181, :3301) — never the box's absolute pixel size.
        double radius = Math.Max(4, Math.Max(b.Width / 2, b.Height / 2.5));
        double size = CounterGeometry.TipDistance(radius) * 2;
        // The bubble centre implied by the new frame: half a tip-extension below the frame's
        // own centre, in the FRAME's tilted axes (the badge itself never carries a transform —
        // its "rotation" is the nub's bearing, so the position must absorb the tilt).
        var frameCentre = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        Point body = AnnotationRotation.RotatePoint(
            new Point(frameCentre.X, frameCentre.Y + radius * 0.25), frameCentre, _resizeAngle);
        double left = body.X - size / 2, top = body.Y - size / 2;
        FlattenTransformIntoCanvas(fe);
        Canvas.SetLeft(fe, left);
        Canvas.SetTop(fe, top);
        badge.Width = size;
        badge.Height = size;
        if (badge.Children.OfType<Path>().FirstOrDefault() is { } pin)
            pin.Data = CounterGeometry.Pin(new Point(size / 2, size / 2), radius,
                meta.PointerAngle ?? CounterGeometry.DefaultPointerAngle);
        AnnotationFactory.SetStepCaption(badge, meta.Number ?? 1, meta.Style == "Letter");
        meta.Thickness = radius;
        meta.Points = new[] { new[] { left, top } };
        meta.Tx = 0;
        meta.Ty = 0;
    }

    /// <summary>Rectangle/Ellipse: write Canvas.Left/Top + Width/Height directly (drop any move transform into the position).</summary>
    private static void ResizeBoxShape(FrameworkElement fe, Rect b, double cloudIntensity)
    {
        FlattenTransformIntoCanvas(fe);
        Canvas.SetLeft(fe, b.X);
        Canvas.SetTop(fe, b.Y);
        // No absolute floor: Survey clamps the scale RATIO at 0.01, which the caller has
        // already applied, so a 400 px rect stops at 4 px and an 8 px one keeps shrinking.
        fe.Width = b.Width;
        fe.Height = b.Height;
        if (fe is Path path && fe.Tag is AnnotationData meta &&
            AnnotationStyle.LineStyleFrom(meta) == LineBorderStyle.Cloud)
            path.Data = CloudPath.ForRectangle(new Rect(0, 0, fe.Width, fe.Height),
                meta.CloudIntensity ?? cloudIntensity, path.StrokeThickness);
        if (fe.Tag is AnnotationData data &&
            data.Type is AnnotationData.TypeRectangle or AnnotationData.TypeEllipse)
        {
            data.Rect = new[] { b.X, b.Y, fe.Width, fe.Height };
            data.Tx = 0;
            data.Ty = 0;
            fe.Tag = data;
        }
    }

    /// <summary>
    /// Text label: scale FontSize proportionally to the box drag, measured from the BASELINE
    /// bounds/font captured at drag start (not the live values) so successive mouse-moves don't
    /// compound. Keeps aspect ratio by using the larger axis ratio.
    /// </summary>
    private void ResizeTextElement(FrameworkElement fe, AnnotationData meta, Rect newBounds)
    {
        if (_resizeBefore is not { } snap) return;
        if (fe is Grid || meta.Rect is { Length: >= 4 })
        {
            FlattenTransformIntoCanvas(fe);
            Canvas.SetLeft(fe, newBounds.X);
            Canvas.SetTop(fe, newBounds.Y);
            fe.Width = newBounds.Width;      // scale floored at 0.1 upstream, Survey :2207-2209
            fe.Height = newBounds.Height;
            if (fe is Grid g && g.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock tb)
                tb.Width = Math.Max(1, fe.Width - 2 * AnnotationFactory.TextPadding);
            meta.Rect = new[] { newBounds.X, newBounds.Y, fe.Width, fe.Height };
            meta.Tx = 0;
            meta.Ty = 0;
            fe.Tag = meta;
            return;
        }

        Rect baseBounds = snap.Bounds;
        if (baseBounds.Width < 0.5 || baseBounds.Height < 0.5) return;
        double ratio = Math.Max(newBounds.Width / baseBounds.Width, newBounds.Height / baseBounds.Height);
        ratio = Math.Clamp(ratio, 0.1, 20);
        double baseFont = snap.FontSize ?? meta.FontSize ?? AnnotationFactory.FontSizeFor(_thickness);
        double newFont = Math.Clamp(baseFont * ratio, 6, 1000);
        SetFontSize(fe, newFont);
        fe.UpdateLayout();
    }

    /// <summary>
    /// Freehand / highlighter. Survey commits an ink resize as a SIGNED <c>scaleX/scaleY</c> on
    /// the path with <c>vectorEffect: 'none'</c> (inkGeometryTransform.js:474-501,
    /// svgAnnotationRenderers.jsx:300-318), so the SVG transform scales the stroke WIDTH along
    /// with the geometry and a mirrored drag really mirrors the stroke. Here that is the same
    /// affine, applied to the outline built at the pre-drag thickness and baked back into
    /// <c>Data</c>, plus the equivalent scalar thickness in <c>meta</c> for the rebuild sites.
    /// </summary>
    private void ResizeInk(FrameworkElement fe, AnnotationData meta, Rect newBounds, Point local)
    {
        if (fe is not Path ink || _resizeBefore?.Points is not { } basePts || basePts.Length == 0) return;

        Rect baseBounds = _resizeBefore.Bounds;
        // Magnitudes come from the (already floored / Shift-locked) box, the SIGN from which
        // side of the anchor the pointer is on — Survey's supportsFlip path (:2186-2246).
        var (ax, ay) = ShapeConstraint.AxisAnchors(baseBounds, _activeHandle);
        double sx = Signed(baseBounds.Width, newBounds.Width, ax, local.X, baseBounds.Left, baseBounds.Right);
        double sy = Signed(baseBounds.Height, newBounds.Height, ay, local.Y, baseBounds.Top, baseBounds.Bottom);
        // An axis this handle does not drive is anchored on the box's own mid-line — which is
        // where Survey's anchorMap puts mt/mb/ml/mr, and where the Shift lock centres it.
        double anchorX = ax ?? baseBounds.Left + baseBounds.Width / 2;
        double anchorY = ay ?? baseBounds.Top + baseBounds.Height / 2;

        // Scaling about the LOCAL anchor reproduces the un-rotated box; DragResize's
        // world-anchor placement (Survey :2249-2253) lives entirely in newBounds.X/Y, and
        // dropping it is what slid a rotated stroke off the handle you were dragging. With
        // this the whole map is T(shift)·S about the local anchor + a re-rotation about the
        // new centre, which composes to exactly Survey's scaleInRotatedFrameAroundMatrix
        // about worldAnchorX/Y (inkGeometryTransform.js:669-690). At angle 0 shift is (0,0).
        double expLeft = anchorX + ((sx >= 0 ? baseBounds.Left : baseBounds.Right) - anchorX) * sx;
        double expTop = anchorY + ((sy >= 0 ? baseBounds.Top : baseBounds.Bottom) - anchorY) * sy;
        var shift = new Vector(newBounds.X - expLeft, newBounds.Y - expTop);

        var raw = new double[basePts.Length][];
        for (int i = 0; i < basePts.Length; i++)
            raw[i] = new[]
            {
                anchorX + (basePts[i].X - anchorX) * sx + shift.X,
                anchorY + (basePts[i].Y - anchorY) * sy + shift.Y,
            };

        double baseThickness = _resizeBefore.Meta.Thickness ?? 4;
        double baseAspect = _resizeBefore.Meta.PenAspect ?? 1;
        // Survey's matrix() equivalent: outline ONCE at the pre-drag pen, then let the affine
        // carry the stroke width with the geometry. Re-outlining the scaled points at a fixed
        // width is what kept a 3x-enlarged stroke at hairline weight.
        var outline = PaperInk.Outline(basePts, baseThickness, aspect: baseAspect);
        var affine = outline.Transform?.Value ?? Matrix.Identity;   // the pre-drag pen ellipse
        affine.Append(new Matrix(sx, 0, 0, sy,
            anchorX - anchorX * sx + shift.X,
            anchorY - anchorY * sy + shift.Y));
        outline.Transform = new MatrixTransform(affine);
        ink.RenderTransform = null;
        ink.Data = outline;
        meta.Points = raw;
        // The pen scales with the geometry, so a non-uniform drag leaves it ELLIPTICAL - which
        // is what Survey's signed scaleX/scaleY on a vectorEffect:'none' path does. Persist both
        // axes so the re-grab, undo/redo, restyle and reload rebuilds draw the same stroke the
        // drag committed instead of snapping it back round.
        double penX = baseThickness * Math.Abs(sx);
        double penY = baseThickness * baseAspect * Math.Abs(sy);
        meta.Thickness = penX;
        meta.PenAspect = penX > 1e-6 ? penY / penX : 1;
        meta.Tx = 0;
        meta.Ty = 0;
        ink.Tag = meta;

        // Survey's exact-path branch imposes no floor, only a nudge off an exact zero (:2198-2205).
        static double Signed(double baseExtent, double newExtent, double? anchor, double pointer,
            double lo, double hi)
        {
            if (baseExtent <= 1e-6) return 1;
            double mag = newExtent / baseExtent;
            // No anchor on this axis: the handle does not drive it, so it only ever changes
            // under the Shift uniform lock, which is positive and centred.
            if (anchor is not double a) return mag == 0 ? double.Epsilon : mag;
            double moved = Math.Abs(a - lo) < Math.Abs(a - hi) ? hi : lo;  // the edge this handle drags
            double sign = (pointer - a) * (moved - a) < 0 ? -1 : 1;        // pointer past the anchor mirrors
            return mag == 0 ? sign * double.Epsilon : sign * mag;
        }
    }

    /// <summary>
    /// Arrow / line / curved arrow: drag one endpoint, rebuilding geometry from the moved
    /// endpoints. <paramref name="pos"/> is in the FROZEN un-rotated frame, and
    /// <paramref name="shift"/> is Survey's (R - I)·Δ pivot compensation
    /// (useSVGInteraction.js:1940-1952) applied to all three control points.
    /// </summary>
    private void ApplyEndpointResize(FrameworkElement fe, AnnotationData meta, Point pos,
        Vector shift = default)
    {
        if (_resizeBefore?.Points is not { Length: >= 2 } basePts) return;

        // basePts (canvas coords): arrow/line = [from, to]; curved = [from, control, to].
        Point from = basePts[0];
        bool curved = meta.Type == AnnotationData.TypeCurvedArrow && basePts.Length >= 3;
        Point to = curved ? basePts[2] : basePts[^1];
        Point control = curved ? basePts[1] : default;

        // The PRE-DRAG mid, not the live one: this runs twice per frame for the rotation
        // compensation, and reading the value the first pass wrote would compound.
        Point? surveyMid = LineCurve.ParseMid(_resizeBefore.Meta.Mid);
        if (meta.Type is AnnotationData.TypeLine or AnnotationData.TypeArrow)
        {
            if (_activeHandle == 0) from = pos;
            else if (_activeHandle == 1) to = pos;
            else surveyMid = pos;
        }
        else
        {
            if (_activeHandle == 0) from = pos;
            else to = pos;
        }

        if (shift != default)
        {
            from += shift;
            to += shift;
            control += shift;
            if (surveyMid is Point pre) surveyMid = pre + shift;
        }

        fe.RenderTransform = null; // points are absolute now
        if (fe is not Path path) return;
        double thickness = meta.Thickness ?? path.StrokeThickness;
        if (curved)
        {
            // Keep the stored control point as-is so a previously bent curve keeps its shape.
            path.Data = AnnotationFactory.CurvedArrowGeometry(from, control, to, thickness);
            meta.Points = new[] { new[] { from.X, from.Y }, new[] { control.X, control.Y }, new[] { to.X, to.Y } };
        }
        else if (meta.Type == AnnotationData.TypeLine)
        {
            if (surveyMid is Point m && LineCurve.ShouldSnapToLinear(m, from, to))
                surveyMid = null;
            path.Data = LineCurve.Stroke(from, to, surveyMid);
            meta.Points = new[] { new[] { from.X, from.Y }, new[] { to.X, to.Y } };
            meta.Mid = surveyMid is Point sm ? LineCurve.ToArray(sm) : null;
        }
        else
        {
            var (end, start) = AnnotationStyle.HeadsFrom(meta);
            if (surveyMid is Point m && LineCurve.ShouldSnapToLinear(m, from, to))
                surveyMid = null;
            path.Data = AnnotationFactory.ArrowGeometry(from, to, thickness, end, start, mid: surveyMid);
            meta.Points = new[] { new[] { from.X, from.Y }, new[] { to.X, to.Y } };
            meta.Mid = surveyMid is Point sm ? LineCurve.ToArray(sm) : null;
        }
        // Keep the stored geometry in sync so the end-of-drag snapshot reads the live state.
        meta.Tx = 0;
        meta.Ty = 0;
        path.Tag = meta;
    }

    /// <summary>
    /// Survey free-drag model (useSVGInteraction.js:2536-2545): while the pointer is down the
    /// grabbed handle follows the cursor 1:1 from the PRE-DRAG snapshot - no mid-drag clamping,
    /// no re-routing, no cumulative drift - and each frame is validated only so the rings can
    /// go red. <see cref="EndResize"/> disciplines the drop.
    /// </summary>
    private void ApplyCalloutResize(FrameworkElement fe, AnnotationData meta, Point pos)
    {
        if (fe is not CalloutAnnotation callout || _calloutDragStart is not CalloutLayout snap) return;
        Vector d = (pos - ElementOffset(callout)) - _calloutGrabLocal;
        var rbox = snap.RenderBox;
        double buffer = snap.Descender;

        CalloutLayout Corner(int corner, Point grabbed)
        {
            var next = NewBoundsForHandle(rbox, corner, grabbed + d);
            return CalloutLayout.Raw(snap.Tip, snap.Knee,
                new Rect(next.X, next.Y, next.Width, Math.Max(0, next.Height - buffer)), buffer);
        }

        CalloutLayout proposed = _activeHandle switch
        {
            0 => CalloutLayout.Raw(snap.Tip + d, snap.Knee, snap.Box, buffer),
            1 => CalloutLayout.Raw(snap.Tip, snap.Knee + d, snap.Box, buffer),
            2 => Corner(0, rbox.TopLeft),
            3 => Corner(1, rbox.TopRight),
            4 => Corner(2, rbox.BottomRight),
            5 => Corner(3, rbox.BottomLeft),
            _ => snap,
        };
        _calloutDragInvalid = !CalloutLayout.IsDropSafe(proposed, HandleScreenPx / 2);
        ApplyCalloutMeta(callout, proposed, meta);
        callout.Tag = CalloutDataFrom(callout.Layout, meta);
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
                or AnnotationData.TypeCurvedArrow or AnnotationData.TypeFreehand
                or AnnotationData.TypeHighlighter or AnnotationData.TypeCallout
                or AnnotationData.TypePolygon or AnnotationData.TypePolyline)
        {
            pts = mp.Select(p => new Point(p[0] + off.X, p[1] + off.Y)).ToArray();
        }
        return new ResizeSnapshot
        {
            Type = meta.Type,
            // The UNROTATED bounds: rotation turns about this rect's centre, so this is the
            // frame both the resize maths and undo work in.
            Bounds = element is FrameworkElement box ? UnrotatedCanvasBounds(box) : GetCanvasBounds(element),
            Points = pts,
            FontSize = CurrentFontSize(element),
            Meta = meta.Clone(),
            Tx = off.X,
            Ty = off.Y,
            Angle = meta.Type == AnnotationData.TypeStep
                ? meta.PointerAngle ?? CounterGeometry.DefaultPointerAngle
                : AnnotationTransform.AngleOf(element),
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
                // No absolute floor here either (Survey floors the SCALE, :2214-2217): clamping
                // the restore at 1 px would make undo/redo disagree with the live geometry for
                // any mark small enough that a legal 0.01 scale lands under a pixel.
                fe.Width = snap.Bounds.Width;
                fe.Height = snap.Bounds.Height;
                if (fe is Path cloud && AnnotationStyle.LineStyleFrom(meta) == LineBorderStyle.Cloud)
                    cloud.Data = CloudPath.ForRectangle(new Rect(0, 0, fe.Width, fe.Height),
                        meta.CloudIntensity ?? _cloudIntensity, cloud.StrokeThickness);
                meta.Rect = new[] { snap.Bounds.X, snap.Bounds.Y, snap.Bounds.Width, snap.Bounds.Height };
                meta.Tx = 0;
                meta.Ty = 0;
                break;

            case AnnotationData.TypeText:
                fe.RenderTransform = new TranslateTransform(snap.Tx, snap.Ty);
                if (snap.FontSize is double fs) SetFontSize(fe, fs);
                meta.FontSize = snap.FontSize ?? meta.FontSize;
                meta.Tx = snap.Tx;
                meta.Ty = snap.Ty;
                fe.UpdateLayout();
                break;

            case AnnotationData.TypePolygon:
            case AnnotationData.TypePolyline:
                fe.RenderTransform = null;
                if (fe is Path polyPath && snap.Points is { Length: >= 2 } polyPts)
                    RebuildPolyGeometry(polyPath, meta, polyPts);
                break;

            case AnnotationData.TypeArrow:
            case AnnotationData.TypeLine:
            case AnnotationData.TypeCurvedArrow:
                fe.RenderTransform = null;
                if (fe is Path path && snap.Points is { Length: >= 2 } sp)
                {
                    double thickness = meta.Thickness ?? path.StrokeThickness;
                    bool curved = snap.Type == AnnotationData.TypeCurvedArrow && sp.Length >= 3;
                    var (end, start) = AnnotationStyle.HeadsFrom(meta);
                    if (snap.Type == AnnotationData.TypeLine)
                        path.Data = LineCurve.Stroke(sp[0], sp[^1], LineCurve.ParseMid(meta.Mid));
                    else
                        path.Data = curved
                            ? AnnotationFactory.CurvedArrowGeometry(sp[0], sp[1], sp[2], thickness)
                            : AnnotationFactory.ArrowGeometry(sp[0], sp[^1], thickness, end, start,
                                mid: LineCurve.ParseMid(meta.Mid));
                    meta.Points = sp.Select(p => new[] { p.X, p.Y }).ToArray();
                    meta.Tx = 0;
                    meta.Ty = 0;
                }
                break;
            case AnnotationData.TypeCallout:
                if (fe is CalloutAnnotation callout && snap.Meta.Points is { Length: >= 3 } cp)
                {
                    fe.RenderTransform = snap.Tx != 0 || snap.Ty != 0
                        ? new TranslateTransform(snap.Tx, snap.Ty)
                        : null;
                    var box = snap.Meta.Rect is { Length: >= 4 } r
                        ? new Rect(r[0], r[1], r[2], r[3])
                        : new Rect(cp[2][0], cp[2][1], CalloutLayout.DefaultBoxWidth, CalloutLayout.DefaultBoxHeight);
                    var layout = CalloutLayout.FromParts(
                        new Point(cp[0][0], cp[0][1]), new Point(cp[1][0], cp[1][1]), box,
                        CalloutDescender(snap.Meta));
                    ApplyCalloutMeta(callout, layout, snap.Meta);
                    meta = snap.Meta.Clone();
                }
                break;

            case AnnotationData.TypeStep:
                // SetStepPointerAngle below derives the radius from badge.Width, so the SIZE
                // has to come back first or an undone counter resize keeps the new radius.
                if (fe is Grid stepBadge)
                {
                    fe.RenderTransform = null;
                    double r = CounterGeometry.RadiusFor(meta.Thickness ?? 14);
                    double sz = CounterGeometry.TipDistance(r) * 2;
                    // snap.Bounds is the PIN's ink box, not the Grid's canvas position — the
                    // stored point is (meta.Points[0] is where ResizeCounter writes it).
                    double sl = meta.Points is { Length: > 0 } sp0 && sp0[0].Length >= 2 ? sp0[0][0] : snap.Bounds.X;
                    double st = meta.Points is { Length: > 0 } sp1 && sp1[0].Length >= 2 ? sp1[0][1] : snap.Bounds.Y;
                    Canvas.SetLeft(fe, sl);
                    Canvas.SetTop(fe, st);
                    stepBadge.Width = sz;
                    stepBadge.Height = sz;
                    AnnotationFactory.SetStepCaption(stepBadge, meta.Number ?? 1, meta.Style == "Letter");
                    meta.Points = new[] { new[] { sl, st } };
                    meta.Tx = 0;
                    meta.Ty = 0;
                }
                break;

            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
                if (fe is Path inkPath && snap.Points is { Length: > 0 } pp)
                {
                    inkPath.RenderTransform = null;
                    inkPath.Data = PaperInk.Outline(pp, meta.Thickness ?? 4, aspect: meta.PenAspect ?? 1);
                    meta.Points = pp.Select(p => new[] { p.X, p.Y }).ToArray();
                    meta.Tx = 0;
                    meta.Ty = 0;
                }
                break;
        }

        if (snap.Type == AnnotationData.TypeStep)
        {
            if (fe is Grid badge) AnnotationFactory.SetStepPointerAngle(badge, snap.Angle);
            meta.PointerAngle = snap.Angle;
        }
        else
        {
            // Every branch above rebuilt the element unrotated; put the captured angle back.
            if (Math.Abs(snap.Angle) > 0.01)
            {
                fe.UpdateLayout();
                AnnotationTransform.SetAngle(fe, snap.Angle);
            }
            meta.Angle = snap.Angle;
        }

        fe.Tag = meta;
    }

    /// <summary>Folds an element's TranslateTransform offset into its Canvas.Left/Top and clears the transform.</summary>
    private static void FlattenTransformIntoCanvas(FrameworkElement fe)
    {
        Vector flatOffset = AnnotationTransform.OffsetOf(fe);
        if (flatOffset.X != 0 || flatOffset.Y != 0)
        {
            double l = Canvas.GetLeft(fe); if (double.IsNaN(l)) l = 0;
            double tp = Canvas.GetTop(fe); if (double.IsNaN(tp)) tp = 0;
            Canvas.SetLeft(fe, l + flatOffset.X);
            Canvas.SetTop(fe, tp + flatOffset.Y);
        }
        fe.RenderTransform = null;
    }

    /// <summary>Reads the font size from any text-label kind (TextBlock, Outline Path via stored meta, Pill border child).</summary>
    private static double? CurrentFontSize(UIElement element) => element switch
    {
        TextBlock tb => tb.FontSize,
        Border pill when pill.Child is TextBlock inner => inner.FontSize,
        Grid box when box.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock boxed => boxed.FontSize,
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
            case Grid box when box.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock boxed:
                boxed.FontSize = fontSize;
                break;
            case Path glyph:
                // Outline style: rebuild the glyph geometry at the new size from the stored text/color.
                var foreground = glyph.Fill ?? Brushes.White;
                string text = meta.Text ?? string.Empty;
                var typeface = new Typeface(new FontFamily("Segoe UI"),
                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
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
        if (_selected is null || _movingSelection) return;
        var items = SelectionList()
            .Select(el => (Element: el, Index: AnnotationCanvas.Children.IndexOf(el)))
            .Where(t => t.Index >= 0)
            .OrderBy(t => t.Index)
            .ToArray();
        if (items.Length == 0) return;
        Select(null);
        foreach (var item in items)
            AnnotationCanvas.Children.Remove(item.Element);
        Push(new EditorAction(
            undo: () =>
            {
                foreach (var item in items)
                    if (!AnnotationCanvas.Children.Contains(item.Element))
                    {
                        int insert = Math.Min(item.Index, AnnotationCanvas.Children.Count);
                        AnnotationCanvas.Children.Insert(insert, item.Element);
                    }
            },
            redo: () =>
            {
                foreach (var item in items)
                    AnnotationCanvas.Children.Remove(item.Element);
            }), apply: false);
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
        if (_selection.Count > 1) { ReorderSelection(move); return; }
        var children = AnnotationCanvas.Children;
        int oldIndex = children.IndexOf(_selected);
        if (oldIndex < 0) return;
        int last = children.Count - 1;
        int newIndex = move switch
        {
            ZMove.Front => last,
            ZMove.Back => 0,
            // Survey resolveOverlapTarget (PDFViewer.jsx:25886-25918): Figma-style — land at
            // the first neighbour whose bbox actually overlaps, skipping the rest entirely.
            // Nothing overlaps => an explicit no-op, not a ±1 nudge.
            ZMove.Forward => FirstOverlapping(oldIndex, +1, oldIndex),
            ZMove.Backward => FirstOverlapping(oldIndex, -1, oldIndex),
            _ => oldIndex,
        };
        if (newIndex == oldIndex) return; // already at the requested end — nothing to do

        UIElement el = _selected;
        Push(new EditorAction(
            undo: () => { children.Remove(el); children.Insert(oldIndex, el); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); },
            redo: () => { children.Remove(el); children.Insert(newIndex, el); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); }));
    }

    /// <summary>Index of the first neighbour in <paramref name="step"/> direction whose bounds overlap.</summary>
    private int FirstOverlapping(int fromIndex, int step, int fallback)
    {
        var children = AnnotationCanvas.Children;
        Rect from = GetCanvasBounds(children[fromIndex]);
        for (int j = fromIndex + step; j >= 0 && j < children.Count; j += step)
        {
            Rect other = GetCanvasBounds(children[j]);
            if (!other.IsEmpty && !from.IsEmpty && from.IntersectsWith(other)) return j;
        }
        return fallback;
    }

    /// <summary>
    /// Survey reorderAll (useAnnotationContextMenu.jsx:501-537): a multi-selection permutes as
    /// one atomic step and is NOT overlap-aware — a plain bubble-swap past unselected siblings,
    /// committed as a single undo entry so repeated calls never read stale indices.
    /// </summary>
    private void ReorderSelection(ZMove move)
    {
        var children = AnnotationCanvas.Children;
        var before = children.Cast<UIElement>().ToArray();
        bool[] sel = before.Select(_selection.Contains).ToArray();
        if (!sel.Any(s => s) || sel.All(s => s)) return;

        var order = new List<UIElement>();
        switch (move)
        {
            case ZMove.Front:
                order.AddRange(before.Where((_, i) => !sel[i]));
                order.AddRange(before.Where((_, i) => sel[i]));
                break;
            case ZMove.Back:
                order.AddRange(before.Where((_, i) => sel[i]));
                order.AddRange(before.Where((_, i) => !sel[i]));
                break;
            default:
            {
                var work = before.ToArray();
                var flag = sel.ToArray();
                if (move == ZMove.Forward)
                {
                    for (int i = work.Length - 2; i >= 0; i--)
                        if (flag[i] && !flag[i + 1]) Swap(work, flag, i, i + 1);
                }
                else
                {
                    for (int i = 1; i < work.Length; i++)
                        if (flag[i] && !flag[i - 1]) Swap(work, flag, i, i - 1);
                }
                order.AddRange(work);
                break;
            }
        }
        if (order.SequenceEqual(before)) return;

        var after = order.ToArray();
        void Reseat(UIElement[] target)
        {
            children.Clear();
            foreach (UIElement el in target) children.Add(el);
            UpdateSelectionVisual();
        }
        Push(new EditorAction(undo: () => Reseat(before), redo: () => Reseat(after)));

        static void Swap(UIElement[] items, bool[] flags, int a, int b)
        {
            (items[a], items[b]) = (items[b], items[a]);
            (flags[a], flags[b]) = (flags[b], flags[a]);
        }
    }

    /// <summary>Right-click an annotation to select it and open the z-order / delete context menu.</summary>
    private void OnViewportRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_sourceOperationActive) return;
        var hit = HitTestAnnotation(e.GetPosition(AnnotationCanvas));
        if (hit is null) return;
        // Right-clicking inside a multi-selection keeps it whole, so the menu's z-order
        // items reach the group path (same guard the left-click path uses).
        if (!(_selection.Count > 1 && _selection.Contains(hit))) Select(hit);
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
        var moved = SelectionList().ToArray();
        foreach (UIElement el in moved)
            MoveElement(el, dx, dy);
        // Same page constraint the pointer move commit applies, so a held arrow key cannot
        // walk an annotation off the screenshot.
        Vector fix = ClampSelectionToSurface(moved);
        if (fix.X != 0 || fix.Y != 0)
        {
            foreach (UIElement el in moved) MoveElement(el, fix.X, fix.Y);
            dx += fix.X;
            dy += fix.Y;
        }
        if (dx == 0 && dy == 0) { UpdateSelectionVisual(); return; }
        UpdateSelectionVisual();
        Push(new EditorAction(
            undo: () =>
            {
                foreach (UIElement el in moved) MoveElement(el, -dx, -dy);
                UpdateSelectionVisual();
            },
            redo: () =>
            {
                foreach (UIElement el in moved) MoveElement(el, dx, dy);
                UpdateSelectionVisual();
            }),
            apply: false);
    }

    /// <summary>Maps a single-letter shortcut to a tool, mirroring the toolbar toggle. Returns false if unmapped.</summary>
    /// <summary>
    /// Survey's letter chain (PDFViewer.jsx:23322-23437): V, P, H, E, Shift+E, T, Q, L, A, C —
    /// C is the COUNTER (:23425-23437), matching its shortcuts overlay.
    /// E is the eraser and leaves its mode alone; Shift+E is the eraser forced to partial.
    /// Survey has an ellipse tool but no key for it, so E belongs to the eraser here too.
    /// R (rectangle), X (redact), K (crop) and M (magnify) are WinShot-only extensions on
    /// keys Survey leaves free; crop moves off C so C can keep Survey's counter meaning.
    /// B and I are one-shot image actions, not tools.
    /// </summary>
    private bool TrySelectToolByKey(Key key, bool shift = false)
    {
        if (shift)
        {
            if (key != Key.E) return false;
            CheckToolButton(EditorTool.Eraser);
            SetEraserPartial(true);
            return true;
        }
        // B and I are not tools at all: they run the two image ACTIONS. B is where the
        // background composer has always lived in WinShot, which is what freed it when the
        // redaction rework took blur off it.
        if (key == Key.B) { OnAddBackground(this, new RoutedEventArgs()); return true; }
        if (key == Key.I) { OnAddImage(this, new RoutedEventArgs()); return true; }

        EditorTool? tool = key switch
        {
            Key.V => EditorTool.Select,
            Key.A => EditorTool.Arrow,
            Key.R => EditorTool.Rectangle,
            Key.E => EditorTool.Eraser,
            Key.L => EditorTool.Line,
            Key.T => EditorTool.Text,
            Key.P => EditorTool.Freehand, // Pen / Draw
            Key.H => EditorTool.Highlighter,
            Key.C => EditorTool.Step,   // Survey's counter key
            // Survey's B was Blur; blur is a redaction STYLE now, so the letter goes back to
            // WinShot's background composer (handled with the other one-shot keys) and the
            // redaction takes X. Crop moves to K, the only free letter next to it.
            Key.X => EditorTool.Redact,
            Key.K => EditorTool.Crop,
            Key.M => EditorTool.Magnifier,
            Key.Q => EditorTool.Callout,
            _ => null,
        };
        if (tool is not EditorTool t) return false;
        CheckToolButton(t);
        return true;
    }

    /// <summary>Moves an annotation by composing into its TranslateTransform (same channel crop shifting uses).</summary>
    private static void MoveElement(UIElement element, double dx, double dy)
    {
        if (element is not FrameworkElement fe)
        {
            if (element.RenderTransform is TranslateTransform plain) { plain.X += dx; plain.Y += dy; }
            else element.RenderTransform = new TranslateTransform(dx, dy);
            return;
        }

        // Carries any rotation through untouched.
        Vector offset = AnnotationTransform.OffsetOf(fe);
        AnnotationTransform.SetOffset(fe, new Vector(offset.X + dx, offset.Y + dy));
    }


    // ---- Survey's RotationInputField: a 60x28 pill 16px radially past the grip. ----
    // Shows while the grip is hovered or dragged, hides 500ms after the cursor leaves
    // unless it has keyboard focus. Enter commits, Esc reverts, Up/Down step 1 (Shift 15).
    private Point _gripContent, _gripAnchorContent;
    private DispatcherTimer? _pillHideTimer;
    private DispatcherTimer? _pillShowTimer;
    private double _pillStartAngle;

    /// <summary>Survey/Fabric show angles as 0–359, never negative.</summary>
    private static double DisplayAngle(double deg) => Math.Round(((deg % 360) + 360) % 360) % 360;

    private double SelectedRotationAngle()
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta) return 0;
        return meta.Type == AnnotationData.TypeStep
            ? meta.PointerAngle ?? CounterGeometry.DefaultPointerAngle
            : AnnotationTransform.AngleOf(fe);
    }

    private void SetSelectedRotationAngle(double deg)
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta) return;
        deg = AnnotationRotation.Normalize(deg);
        if (fe is Grid badge && meta.Type == AnnotationData.TypeStep)
        {
            AnnotationFactory.SetStepPointerAngle(badge, deg);
            meta.PointerAngle = deg;
        }
        else
        {
            AnnotationTransform.SetAngle(fe, deg);
            meta.Angle = deg;
        }
    }

    private void PlaceRotationPill()
    {
        if (RotationPill is null || _rotateThumb is not { Visibility: Visibility.Visible }) return;
        Vector dir = _gripContent - _gripAnchorContent;
        if (dir.Length < 0.001) dir = new Vector(0, -1);
        dir.Normalize();
        // Grip in viewport pixels, then 16px past its edge along the outward direction.
        double gx = _gripContent.X * _zoom + ViewTranslate.X;
        double gy = _gripContent.Y * _zoom + ViewTranslate.Y;
        // Survey computeInputPosition (rotationInputHelpers.js:133-144): the pill centre sits
        // handleHalf + GAP_ABOVE(16) + pillHalf out along the outward direction, so it clears
        // the grip by ~32 px edge-to-edge instead of overlapping it.
        double reach = RotateHandleScreenPx / 2
            + 16
            + Math.Max(RotationPill.Width, RotationPill.Height) / 2;
        double left = gx + dir.X * reach - RotationPill.Width / 2;
        double top = gy + dir.Y * reach - RotationPill.Height / 2;
        // ...then clamp into the host with EDGE_MARGIN 4 (rotationInputHelpers.js:159-166).
        // OverlayCanvas is clipped to the viewport, so without this the pill is cut in half.
        const double edgeMargin = 4;
        double maxLeft = Math.Max(edgeMargin, Viewport.ActualWidth - RotationPill.Width - edgeMargin);
        double maxTop = Math.Max(edgeMargin, Viewport.ActualHeight - RotationPill.Height - edgeMargin);
        Canvas.SetLeft(RotationPill, Math.Clamp(left, edgeMargin, maxLeft));
        Canvas.SetTop(RotationPill, Math.Clamp(top, edgeMargin, maxTop));
        if (!RotationInput.IsKeyboardFocused)
            RotationInput.Text = DisplayAngle(SelectedRotationAngle()).ToString();
        _pillHideTimer?.Stop();
        _pillShowTimer?.Stop();
        RotationPill.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Hover over the grip shows the pill after Survey's 150 ms intent dwell
    /// (SVGAnnotationLayer.jsx:1481-1490); leaving it starts the 500 ms grace. Once the pill
    /// is up, hover repositions it immediately so it keeps tracking the grip.
    /// </summary>
    private void UpdateRotationPillHover(Point pos)
    {
        if (_rotateThumb is not { Visibility: Visibility.Visible })
        {
            _pillShowTimer?.Stop();
            ScheduleHideRotationPill();
            return;
        }
        if (HitTestHandle(pos) != RotateHandleIndex)
        {
            _pillShowTimer?.Stop();
            ScheduleHideRotationPill();
            return;
        }
        if (RotationPill?.Visibility == Visibility.Visible) { PlaceRotationPill(); return; }
        _pillShowTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _pillShowTimer.Tick -= OnPillShowTick;
        _pillShowTimer.Tick += OnPillShowTick;
        if (!_pillShowTimer.IsEnabled) _pillShowTimer.Start();
    }

    private void OnPillShowTick(object? sender, EventArgs e)
    {
        _pillShowTimer!.Stop();
        // A DispatcherTimer armed when the editor closed still fires, and what it fires into
        // is a torn-down template. The exception that follows is raised from the dispatcher
        // with nothing to catch it, so it takes the process rather than the editor — which is
        // how a 500 ms hide timer became an intermittent, position-shifting crash in the test
        // run. Both ticks check, and Closed stops them.
        if (_closed) return;
        PlaceRotationPill();
    }

    private void ScheduleHideRotationPill()
    {
        if (RotationPill is null || RotationPill.Visibility != Visibility.Visible) return;
        _pillHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pillHideTimer.Tick -= OnPillHideTick;
        _pillHideTimer.Tick += OnPillHideTick;
        _pillHideTimer.Stop();
        _pillHideTimer.Start();
    }

    private void OnPillHideTick(object? sender, EventArgs e)
    {
        _pillHideTimer!.Stop();
        if (_closed || RotationPill is null || RotationInput is null) return;
        if (RotationInput.IsKeyboardFocused || RotationPill.IsMouseOver || _activeHandle == RotateHandleIndex) return;
        RotationPill.Visibility = Visibility.Collapsed;
    }

    private void HideRotationPill()
    {
        _pillHideTimer?.Stop();
        _pillShowTimer?.Stop();
        if (RotationPill is null) return;
        if (RotationInput.IsKeyboardFocused) Viewport.Focus();
        RotationPill.Visibility = Visibility.Collapsed;
    }

    private bool IsRotationPillSource(object? source) =>
        RotationPill is not null && source is DependencyObject d && RotationPill.IsAncestorOf(d);

    // Survey blocks every printable character that is not 0-9 — no letters, no spaces, no
    // periods or minus signs (RotationInputField.jsx:370).
    private void OnRotationInputText(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void OnRotationInputFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _pillStartAngle = SelectedRotationAngle();
        _pillHideTimer?.Stop();
        RotationInput.Dispatcher.BeginInvoke(RotationInput.SelectAll, DispatcherPriority.Input);
    }

    private void OnRotationInputKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitRotationInput();
                Viewport.Focus();
                break;
            case Key.Escape:
                // Survey reverts to the pre-edit angle and blurs, but leaves the pill's
                // visibility to the 500 ms grace timer (RotationInputField.jsx:341-352).
                SetSelectedRotationAngle(_pillStartAngle);
                UpdateSelectionVisual();
                RotationInput.Text = DisplayAngle(SelectedRotationAngle()).ToString();
                Viewport.Focus();
                ScheduleHideRotationPill();
                break;
            case Key.Up or Key.Down:
                double step = ShapeConstraint.ShiftHeld ? AnnotationRotation.SnapIncrementDegrees : 1;
                if (e.Key == Key.Down) step = -step;
                SetSelectedRotationAngle(SelectedRotationAngle() + step);
                RotationInput.Text = DisplayAngle(SelectedRotationAngle()).ToString();
                RotationInput.CaretIndex = RotationInput.Text.Length;
                UpdateSelectionVisual();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnRotationInputBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitRotationInput();
        ScheduleHideRotationPill();
    }

    /// <summary>Applies the typed angle as one undoable step from the angle at focus time.</summary>
    private void CommitRotationInput()
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta) return;
        double target = double.TryParse(RotationInput.Text, out double typed) ? typed : SelectedRotationAngle();
        SetSelectedRotationAngle(_pillStartAngle);
        var before = CaptureResizeSnapshot(fe, meta);
        SetSelectedRotationAngle(target);
        var after = CaptureResizeSnapshot(fe, meta);
        _pillStartAngle = SelectedRotationAngle();
        if (!after.NearlyEquals(before))
        {
            UIElement el = fe;
            Push(new EditorAction(
                undo: () => { ApplyResizeSnapshot(el, before); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); },
                redo: () => { ApplyResizeSnapshot(el, after); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); }),
                apply: false);
        }
        UpdateSelectionVisual();
    }

    // Counter re-numbering run: digits typed against one selected badge, committed as one
    // undo entry when the selection changes or a non-digit key arrives.
    private UIElement? _stepRetypeElement;
    private AnnotationData? _stepRetypeBefore;
    private int _stepRetypeValue;

    /// <summary>Maps a single-letter shortcut to a tool, mirroring the toolbar toggle. Returns false if unmapped.</summary>
    /// <summary>
    /// A committed counter can be re-numbered: select it and type. Subsequent counters
    /// continue from the value you typed, which is the whole point — renumbering one badge
    /// without moving the sequence would just make the next one collide.
    ///
    /// Digits accumulate while the same badge stays selected (so "1","2" is 12, not 2) and
    /// the whole run commits as ONE undo entry. There is no timer: the run ends when the
    /// selection changes or a non-digit key arrives, which are the only things that can
    /// mean "done".
    /// </summary>
    private bool TryRetypeStepNumber(Key key)
    {
        if (_selected is not Grid badge || badge.Tag is not AnnotationData meta ||
            meta.Type != AnnotationData.TypeStep)
            return false;

        int digit = key switch
        {
            >= Key.D0 and <= Key.D9 => key - Key.D0,
            >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
            _ => -1,
        };
        if (digit < 0) return false;

        if (!ReferenceEquals(_stepRetypeElement, _selected))
        {
            CommitStepRetype();
            _stepRetypeElement = _selected;
            _stepRetypeBefore = meta.Clone();
            _stepRetypeValue = 0;
        }

        // 0-99. A third digit starts over rather than silently clamping to 99, which is
        // what a typo feels like it should do.
        int next = _stepRetypeValue * 10 + digit;
        _stepRetypeValue = next > 99 ? digit : next;
        ApplyStepNumber(badge, _stepRetypeValue);
        _nextStep = _stepRetypeValue + 1;
        return true;
    }

    /// <summary>Writes a number into a live counter badge and its metadata.</summary>
    private static void ApplyStepNumber(Grid badge, int number)
    {
        if (badge.Tag is not AnnotationData meta) return;
        bool letters = string.Equals(meta.Style, "Letter", StringComparison.Ordinal);
        foreach (object child in badge.Children)
            if (child is TextBlock caption)
                caption.Text = letters
                    ? AnnotationFactory.StepLetterLabel(number)
                    : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        meta.Number = number;
    }

    /// <summary>Ends a re-numbering run, recording it as one reversible step.</summary>
    private void CommitStepRetype()
    {
        if (_stepRetypeElement is not Grid badge || _stepRetypeBefore is not { } before)
        {
            _stepRetypeElement = null;
            _stepRetypeBefore = null;
            return;
        }
        _stepRetypeElement = null;
        _stepRetypeBefore = null;
        if (badge.Tag is not AnnotationData after || before.Number == after.Number) return;

        int beforeNumber = before.Number ?? 1;
        int afterNumber = after.Number ?? 1;
        Push(new EditorAction(
            undo: () => { ApplyStepNumber(badge, beforeNumber); _nextStep = beforeNumber + 1; },
            redo: () => { ApplyStepNumber(badge, afterNumber); _nextStep = afterNumber + 1; }),
            apply: false);
    }

}
