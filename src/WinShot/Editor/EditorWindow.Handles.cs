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
    private enum HandleKind { None, Box, Endpoints, Crop, Callout }

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
            LayoutBoxHandles(UnrotatedCanvasBounds(fe), AnnotationTransform.AngleOf(fe));
            return;
        }

        switch (meta.Type)
        {
            case AnnotationData.TypeArrow:
            case AnnotationData.TypeLine:
            case AnnotationData.TypeCurvedArrow:
                LayoutEndpointHandles(meta);
                break;
            case AnnotationData.TypeCallout:
                LayoutCalloutHandles(meta);
                HideRotateThumb(); // Survey callouts have no rotation handle
                break;
            case AnnotationData.TypeRectangle:
            case AnnotationData.TypeEllipse:
            case AnnotationData.TypeText:
            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
            case AnnotationData.TypeImage:
                LayoutBoxHandles(UnrotatedCanvasBounds(fe), AnnotationTransform.AngleOf(fe));
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
    /// Eight thumbs: corners 0=TL,1=TR,2=BR,3=BL then edge midpoints 4=T,5=R,6=B,7=L, plus
    /// the rotation grip. When the annotation is rotated every thumb rides the rotated
    /// frame, so the chrome keeps wrapping the mark instead of boxing it axis-aligned.
    /// </summary>
    private void LayoutBoxHandles(Rect b, double angle)
    {
        _handleKind = HandleKind.Box;
        var centre = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        var pts = BoxHandlePoints(b);
        var spec = AdaptiveHandleSpec(b.Width, b.Height);
        EnsureThumbs(pts.Length);
        for (int i = 0; i < pts.Length; i++)
        {
            // 0-3 corners, 4=T 6=B horizontal pills, 5=R 7=L vertical pills (Survey mt/mb/ml/mr).
            if (!spec.Shows(i)) { _handleThumbs[i].Visibility = Visibility.Collapsed; continue; }
            ThumbKind kind = i < 4 ? ThumbKind.Corner : (i is 4 or 6 ? ThumbKind.PillH : ThumbKind.PillV);
            PlaceThumb(i, AnnotationRotation.RotatePoint(pts[i], centre, angle), kind, angle);
        }

        var top = new Point(centre.X, b.Top);
        var grip = new Point(centre.X, b.Top - spec.RotationOffset);
        PlaceRotateThumb(
            AnnotationRotation.RotatePoint(grip, centre, angle),
            AnnotationRotation.RotatePoint(top, centre, angle));

        HandleLayer.Visibility = Visibility.Visible;
    }

    /// <summary>Which of the 8 box thumbs fit, and how far out the rotation grip sits.</summary>
    private readonly record struct HandleSpec(bool AllFit, bool CornersFit, double RotationOffset)
    {
        /// <summary>'all' shows every thumb, 'corners' shows 0-3, 'single' shows BR (index 2) only.</summary>
        public bool Shows(int index) => AllFit || (CornersFit ? index < 4 : index == 2);
    }

    /// <summary>
    /// Survey getAdaptiveSelectionHandleSpec (selectionHandleVisibility.js:42-77): drop the
    /// pills, then everything but the bottom-right corner, once the padded box can no longer
    /// hold them. The tier maths deliberately uses the RAW inverse scale (unclamped) while
    /// visual sizing uses the clamped one, so handles keep culling as you zoom out.
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

        // Survey uses the CLAMPED metrics for the rotation offset it actually draws.
        double is_ = HandleScale;
        double topHalf = allFit ? 8 * is_ / 2 : 5.5 * is_;
        // Already in content units: Survey's metrics carry `is` and the offset is applied
        // straight to the bbox in page units (SVGSelectionOverlay.jsx:76-80).
        double offset = Math.Max(AnnotationRotation.HandleOffsetScreenPx, 10 * is_ + topHalf + 4 * is_);
        return new HandleSpec(allFit, cornersFit, offset);
    }

    /// <summary>
    /// Survey's counter chrome (SVGAnnotationLayer.jsx:5106-5164): ONE small dot sitting
    /// exactly on the nubbin tip — no stem, no rotate icon, no dashed bbox. Same white
    /// fill / #4a90e2 ring as every other handle, with the counter's own 0.25 shadow.
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
                Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)),
            };
            _rotateStem = new Line { Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)) };
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
        _rotateThumb.StrokeThickness = (counterDot ? 1.5 : 1) * is_;
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
        PlaceThumb(0, layout.Tip + off);   // Survey: every callout handle is a circle
        PlaceThumb(1, layout.Knee + off);
        PlaceThumb(2, rbox.TopLeft + off);
        PlaceThumb(3, rbox.TopRight + off);
        PlaceThumb(4, rbox.BottomRight + off);
        PlaceThumb(5, rbox.BottomLeft + off);
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
            : new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));
        for (int i = 0; i < 6; i++)
        {
            _handleThumbs[i].Cursor = cursors[i];
            _handleThumbs[i].Stroke = ring;
        }
        HandleLayer.Visibility = Visibility.Visible;
    }

    /// <summary>Thumbs: 0=from, 1=to, and for line/arrow 2=Survey midpoint-on-curve.</summary>
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
        PlaceThumb(0, from);
        PlaceThumb(1, to);
        if (surveyCurve)
        {
            Point mid = LineCurve.ParseMid(meta.Mid) is Point stored
                ? stored + off
                : LineCurve.Midpoint(from, to);
            // Survey HANDLE_RADIUS_SECONDARY: the curve-bend handle is deliberately smaller
            // than the endpoints (SVGAnnotationLayer.jsx:5450-5462).
            PlaceThumb(2, mid, ThumbKind.Secondary);
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
                Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)),
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
            _handleThumbs[i].Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));
            _handleThumbs[i].Cursor = null;
        }
    }

    private void PlaceThumb(int index, Point center, ThumbKind kind = ThumbKind.Corner, double angleDeg = 0)
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
        thumb.StrokeThickness = 1 * is_; // Survey cornerStrokeWidth
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
        HandleLayer.Visibility = Visibility.Collapsed;
        foreach (var t in _handleThumbs) t.Visibility = Visibility.Collapsed;
        HideRotateThumb();
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
            double cx = Canvas.GetLeft(thumb) + thumb.Width / 2;
            double cy = Canvas.GetTop(thumb) + thumb.Height / 2;
            // Survey 2026-05-18: the grab zone is the visible handle, not a wider disc.
            double grab = Math.Max(thumb.Width, thumb.Height) / 2;
            double d = (pos - new Point(cx, cy)).Length;
            if (d <= grab && d < bestDist) { bestDist = d; best = i; }
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
    private bool UpdateHandleCursor(Point pos)
    {
        if (_handleKind is HandleKind.None or HandleKind.Crop) return false;
        int handle = HitTestHandle(pos);
        if (handle < 0) return false;
        double angle = _selected is FrameworkElement fe ? AnnotationTransform.AngleOf(fe) : 0;
        // Survey line endpoints, the curve-bend midpoint, the counter tip and the callout
        // tip/knee are all cursor:'grab' (SVGAnnotationLayer.jsx:5379, 5396, 5149).
        Cursor cursor = _handleKind switch
        {
            HandleKind.Endpoints => Cursors.Hand,
            HandleKind.Callout => _handleThumbs[handle].Cursor ?? Cursors.Hand,
            _ when _selected is FrameworkElement { Tag: AnnotationData { Type: AnnotationData.TypeStep } }
                => Cursors.Hand,
            _ => CursorForHandle(handle, angle),
        };
        Viewport.Cursor = cursor;
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

        Rect oldBounds = _resizeBefore.Bounds;
        if (_handleKind == HandleKind.Endpoints)
        {
            // Survey's endpoint drag consults no modifier at all (useSVGInteraction.js:1874-1960).
            ApplyEndpointResize(fe, meta, pos);
        }
        else if (_handleKind == HandleKind.Callout)
            ApplyCalloutResize(fe, meta, pos);
        else
        {
            // Resize runs in the annotation's unrotated frame: map the pointer in, do the
            // ordinary axis-aligned maths, then put the rotation back about the new centre.
            double angle = AnnotationTransform.AngleOf(fe);
            Point local = ToUnrotatedFrame(fe, pos);
            var next = NewBoundsForHandle(oldBounds, _activeHandle, local);
            // Survey forces a counter resize uniform: scale = max(|sx|,|sy|) for a corner,
            // the moved axis alone otherwise, floored at 0.1 (useSVGInteraction.js:2174-2183).
            if (meta.Type == AnnotationData.TypeStep)
                next = ShapeConstraint.UniformResize(oldBounds, _activeHandle, next);
            else if (ShapeConstraint.ShiftHeld)
                next = ShapeConstraint.UniformResize(oldBounds, _activeHandle, next);
            ApplyBoxResize(fe, meta, next);
            if (angle != 0)
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
                ResizeBoxShape(fe, b, _cloudIntensity);
                break;
            case AnnotationData.TypeText:
                ResizeTextElement(fe, meta, b);
                break;
            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
                ResizeInk(fe, meta, b);
                break;
            case AnnotationData.TypeStep:
                ResizeCounter(fe, meta, b);
                break;
            case AnnotationData.TypeLine:
            case AnnotationData.TypeArrow:
            case AnnotationData.TypeCurvedArrow:
                ResizeStrokeInBox(fe, meta, b);
                break;
        }
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
        double sx = baseBounds.Width > 0.5 ? newBounds.Width / baseBounds.Width : 1;
        double sy = baseBounds.Height > 0.5 ? newBounds.Height / baseBounds.Height : 1;
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
    /// </summary>
    private void ResizeCounter(FrameworkElement fe, AnnotationData meta, Rect b)
    {
        if (fe is not Grid badge) return;
        double tipDist = Math.Max(1, Math.Max(b.Width, b.Height)) / 2;
        double radius = Math.Max(4, tipDist / 1.5); // CounterGeometry.TipDistance is radius * 1.5
        double size = CounterGeometry.TipDistance(radius) * 2;
        FlattenTransformIntoCanvas(fe);
        Canvas.SetLeft(fe, b.X);
        Canvas.SetTop(fe, b.Y);
        badge.Width = size;
        badge.Height = size;
        if (badge.Children.OfType<Path>().FirstOrDefault() is { } pin)
            pin.Data = CounterGeometry.Pin(new Point(size / 2, size / 2), radius,
                meta.PointerAngle ?? CounterGeometry.DefaultPointerAngle);
        AnnotationFactory.SetStepCaption(badge, meta.Number ?? 1, meta.Style == "Letter");
        meta.Thickness = radius;
        meta.Points = new[] { new[] { b.X, b.Y } };
        meta.Tx = 0;
        meta.Ty = 0;
    }

    /// <summary>Rectangle/Ellipse: write Canvas.Left/Top + Width/Height directly (drop any move transform into the position).</summary>
    private static void ResizeBoxShape(FrameworkElement fe, Rect b, double cloudIntensity)
    {
        FlattenTransformIntoCanvas(fe);
        Canvas.SetLeft(fe, b.X);
        Canvas.SetTop(fe, b.Y);
        fe.Width = Math.Max(1, b.Width);
        fe.Height = Math.Max(1, b.Height);
        if (fe is Path path && fe.Tag is AnnotationData meta &&
            AnnotationStyle.LineStyleFrom(meta) == LineBorderStyle.Cloud)
            path.Data = CloudPath.ForRectangle(new Rect(0, 0, fe.Width, fe.Height), cloudIntensity);
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
            fe.Width = Math.Max(1, newBounds.Width);
            fe.Height = Math.Max(1, newBounds.Height);
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

    /// <summary>Freehand / highlighter: scale every stored point about the box's top-left so the polyline fits the new box.</summary>
    private void ResizeInk(FrameworkElement fe, AnnotationData meta, Rect newBounds)
    {
        if (fe is not Path ink || _resizeBefore?.Points is not { } basePts || basePts.Length == 0) return;

        // Reconstruct the polyline from the captured (pre-drag) points so successive
        // moves accumulate from the same baseline, then scale into the new box.
        Rect baseBounds = _resizeBefore.Bounds;
        double sx = baseBounds.Width > 0.5 ? newBounds.Width / baseBounds.Width : 1;
        double sy = baseBounds.Height > 0.5 ? newBounds.Height / baseBounds.Height : 1;

        var scaled = new List<Point>(basePts.Length);
        var raw = new double[basePts.Length][];
        for (int i = 0; i < basePts.Length; i++)
        {
            double nx = newBounds.X + (basePts[i].X - baseBounds.X) * sx;
            double ny = newBounds.Y + (basePts[i].Y - baseBounds.Y) * sy;
            scaled.Add(new Point(nx, ny));
            raw[i] = new[] { nx, ny };
        }
        ink.RenderTransform = null;
        ink.Data = PaperInk.Outline(scaled, meta.Thickness ?? 4);
        meta.Points = raw;
        meta.Tx = 0;
        meta.Ty = 0;
        ink.Tag = meta;
    }

    /// <summary>Arrow / line / curved arrow: drag one endpoint, rebuilding geometry from the moved endpoints.</summary>
    private void ApplyEndpointResize(FrameworkElement fe, AnnotationData meta, Point pos)
    {
        if (_resizeBefore?.Points is not { Length: >= 2 } basePts) return;

        // basePts (canvas coords): arrow/line = [from, to]; curved = [from, control, to].
        Point from = basePts[0];
        bool curved = meta.Type == AnnotationData.TypeCurvedArrow && basePts.Length >= 3;
        Point to = curved ? basePts[2] : basePts[^1];
        Point control = curved ? basePts[1] : default;

        Point? surveyMid = LineCurve.ParseMid(meta.Mid);
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
                or AnnotationData.TypeHighlighter or AnnotationData.TypeCallout)
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
                fe.Width = Math.Max(1, snap.Bounds.Width);
                fe.Height = Math.Max(1, snap.Bounds.Height);
                if (fe is Path cloud && AnnotationStyle.LineStyleFrom(meta) == LineBorderStyle.Cloud)
                    cloud.Data = CloudPath.ForRectangle(new Rect(0, 0, fe.Width, fe.Height), _cloudIntensity);
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

            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
                if (fe is Path inkPath && snap.Points is { Length: > 0 } pp)
                {
                    inkPath.RenderTransform = null;
                    inkPath.Data = PaperInk.Outline(pp, meta.Thickness ?? 4);
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
    /// Survey's letter chain (PDFViewer.jsx:23322-23373): V, P, H, E, Shift+E, T, Q, L, A, C.
    /// E is the eraser and leaves its mode alone; Shift+E is the eraser forced to partial.
    /// Survey has an ellipse tool but no key for it, so E belongs to the eraser here too.
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
            Key.B => EditorTool.Blur,
            Key.S => EditorTool.Step,
            Key.C => EditorTool.Crop,
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
}
