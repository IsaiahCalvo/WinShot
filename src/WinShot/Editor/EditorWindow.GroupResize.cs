using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinShot.Core;

namespace WinShot.Editor;

/// <summary>
/// Resizing several selected marks at once - Survey's group resize (utils/groupResize.js,
/// useSVGInteraction.js 'group-resize'), which follows Drawboard PDF's rules:
/// <list type="bullet">
/// <item>The frame scales about the grabber opposite the one dragged, or about its centre
/// with Alt. Shift keeps its proportions, following the bigger move.</item>
/// <item>A photo, text dragged by anything but a side, a mark turned off a right angle, or
/// a line turned 90 / 270 keeps the proportions too, as if Shift were held.</item>
/// <item>A group never flips, and no member gets thinner than 4 units (or its own size).</item>
/// <item>A mark turned 90 / 270 takes the two scales swapped.</item>
/// <item>Text scales its font from a corner, the top or the bottom; from a side only the
/// box width changes and the words rewrap. Line widths never change.</item>
/// </list>
/// </summary>
public partial class EditorWindow
{
    /// <summary>Survey MIN_RESIZE_PAGE_UNITS (resizeMinimum.js).</summary>
    private const double MinResizePageUnits = 4;

    /// <summary>Survey MIN_GROUP_SCALE: a zero scale would make ink's matrix singular.</summary>
    private const double MinGroupScale = 1e-6;

    /// <summary>Survey's frame padding around the union (SVGSelectionOverlay, 2 units).</summary>
    private const double GroupFramePadding = 2;

    private sealed class GroupMember
    {
        public required FrameworkElement Element { get; init; }
        public required AnnotationData Meta { get; init; }
        public required ResizeSnapshot Before { get; init; }
        /// <summary>The unrotated canvas box; the mark turns about its centre.</summary>
        public Rect Box { get; init; }
        public double Angle { get; init; }
        /// <summary>A callout's layout in canvas space (its move offset folded in).</summary>
        public CalloutLayout? Callout { get; init; }
        public Vector Offset { get; init; }
        public double FontSize { get; init; }
    }

    private List<GroupMember>? _groupResize;
    private Rect _groupFrame;            // the padded union the grabbers sit on, at press
    private double _groupMinScaleX, _groupMinScaleY;
    private bool _groupForcesProportions;

    /// <summary>Survey canGroupResizeMember plus WinShot's own marks that have no box resize.</summary>
    private static bool CanGroupResizeType(string? type) => type is
        AnnotationData.TypeRectangle or AnnotationData.TypeEllipse or AnnotationData.TypeImage
        or AnnotationData.TypeText or AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter
        or AnnotationData.TypeStep or AnnotationData.TypeLine or AnnotationData.TypeArrow
        or AnnotationData.TypeCurvedArrow or AnnotationData.TypePolygon or AnnotationData.TypePolyline
        or AnnotationData.TypeCallout;

    /// <summary>
    /// Survey groupCanResize (SVGAnnotationLayer.jsx:9615-9625): the frame shows its eight
    /// grabbers when every member can take a resize; a locked member keeps it move-only.
    /// </summary>
    private bool GroupCanResize()
    {
        if (_selection.Count < 2) return false;
        foreach (UIElement el in _selection)
        {
            if (el is not FrameworkElement { Tag: AnnotationData meta }) return false;
            if (meta.Locked == true || !CanGroupResizeType(meta.Type)) return false;
        }
        return true;
    }

    private static bool IsSideways(double angle)
    {
        double a = ((angle % 180) + 180) % 180;
        return Math.Abs(a - 90) < 0.01;
    }

    private static bool IsRightAngle(double angle)
    {
        double a = ((angle % 90) + 90) % 90;
        return a < 0.01 || 90 - a < 0.01;
    }

    private static bool HandleAffectsX(int handle) => handle is not (4 or 6);
    private static bool HandleAffectsY(int handle) => handle is not (5 or 7);

    /// <summary>Survey textScalesFontFor: every grabber but the left / right side.</summary>
    private static bool TextScalesFontFor(int handle) => handle is not (5 or 7);

    /// <summary>Survey markNeedsUniformGroupScale.</summary>
    private static bool MemberNeedsUniformScale(AnnotationData meta, double angle, int handle)
    {
        if (!IsRightAngle(angle)) return true;
        if (meta.Type is AnnotationData.TypeLine or AnnotationData.TypeArrow or AnnotationData.TypeCurvedArrow
            && IsSideways(angle)) return true;
        if (meta.Type == AnnotationData.TypeImage) return true;
        if (meta.Type == AnnotationData.TypeText && TextScalesFontFor(handle)) return true;
        return false;
    }

    /// <summary>Survey memberAxisScale: a mark turned 90 / 270 reads the frame's scales swapped.</summary>
    private static (double X, double Y) MemberAxisScale(double angle, double sx, double sy) =>
        IsSideways(angle) ? (sy, sx) : (sx, sy);

    /// <summary>Survey scaledTextBoxLength: only the length inside the fixed padding scales.</summary>
    private static double ScaledTextBoxLength(double length, double k, double padding) =>
        Math.Max(2 * padding, (Math.Abs(length) - 2 * padding) * k + 2 * padding);

    /// <summary>Survey resolveGroupResizeScale.</summary>
    internal static (double Sx, double Sy) ResolveGroupResizeScale(int handle, double sx, double sy,
        bool keepProportions, double minScaleX, double minScaleY)
    {
        bool affectsX = HandleAffectsX(handle), affectsY = HandleAffectsY(handle);
        double x = affectsX && double.IsFinite(sx) ? sx : 1;
        double y = affectsY && double.IsFinite(sy) ? sy : 1;
        // A group never mirrors: past the fixed side it stops at its minimum.
        x = Math.Max(x, 0);
        y = Math.Max(y, 0);
        if (keepProportions)
        {
            if (handle < 4)
            {
                // lockAspectToBiggerMove: follow whichever axis moved further from 1.
                double k = Math.Abs(x - 1) >= Math.Abs(y - 1) ? x : y;
                x = y = k;
            }
            else if (affectsX) y = x;
            else x = y;
        }
        minScaleX = Math.Max(minScaleX, MinGroupScale);
        minScaleY = Math.Max(minScaleY, MinGroupScale);
        if (keepProportions)
        {
            double k = Math.Max(x, Math.Max(minScaleX, minScaleY));
            return (k, k);
        }
        return (affectsX ? Math.Max(x, minScaleX) : 1, affectsY ? Math.Max(y, minScaleY) : 1);
    }

    /// <summary>Survey groupResizeMinimumScale.</summary>
    private static (double X, double Y) GroupMinimumScale(IEnumerable<Rect> boxes)
    {
        double mx = 0, my = 0;
        foreach (Rect b in boxes)
        {
            double w = Math.Abs(b.Width), h = Math.Abs(b.Height);
            if (w > 0) mx = Math.Max(mx, Math.Min(MinResizePageUnits, w) / w);
            if (h > 0) my = Math.Max(my, Math.Min(MinResizePageUnits, h) / h);
        }
        return (mx, my);
    }

    /// <summary>The union of the selection's drawn (turned) boxes, unpadded.</summary>
    private Rect SelectionUnion()
    {
        Rect union = Rect.Empty;
        foreach (UIElement el in _selection)
        {
            if (!AnnotationCanvas.Children.Contains(el)) continue;
            Rect b = GetCanvasBounds(el);
            union = union.IsEmpty ? b : Rect.Union(union, b);
        }
        return union;
    }

    /// <summary>The frame's grabbers: Survey shows all eight, and no rotate grip yet.</summary>
    private void LayoutGroupHandles(Rect union)
    {
        LayoutBoxHandles(union, 0, GroupFramePadding);
        HideRotateThumb();
    }

    private void BeginGroupResize(int handle)
    {
        var members = new List<GroupMember>();
        foreach (UIElement el in _selection)
        {
            if (el is not FrameworkElement { Tag: AnnotationData meta } fe) continue;
            if (!AnnotationCanvas.Children.Contains(fe)) continue;
            double angle = meta.Type == AnnotationData.TypeStep ? 0 : AnnotationTransform.AngleOf(fe);
            Vector off = ElementOffset(fe);
            CalloutLayout? callout = null;
            if (fe is CalloutAnnotation c)
            {
                var l = c.Layout;
                callout = CalloutLayout.Raw(l.Tip + off, l.Knee + off,
                    new Rect(l.Box.X + off.X, l.Box.Y + off.Y, l.Box.Width, l.Box.Height), l.Descender);
            }
            members.Add(new GroupMember
            {
                Element = fe,
                Meta = meta,
                Before = CaptureResizeSnapshot(fe, meta),
                Box = UnrotatedCanvasBounds(fe),
                Angle = angle,
                Callout = callout,
                Offset = off,
                FontSize = CurrentFontSize(fe) ?? meta.FontSize ?? CalloutLayout.DefaultFontSize,
            });
        }
        if (members.Count < 2) return;
        _groupResize = members;
        Rect union = SelectionUnion();
        union.Inflate(GroupFramePadding, GroupFramePadding);
        _groupFrame = union;
        (_groupMinScaleX, _groupMinScaleY) = GroupMinimumScale(members.Select(m => m.Box));
        _groupForcesProportions = members.Any(m => MemberNeedsUniformScale(m.Meta, m.Angle, handle));

        // Survey snaps the dragged edge to the other marks' edges (canvas audit chunk 2).
        _snapOthers = new List<Rect> { new(0, 0, AnnotationCanvas.ActualWidth, AnnotationCanvas.ActualHeight) };
        foreach (UIElement child in AnnotationCanvas.Children)
        {
            if (_selection.Contains(child) || child is not FrameworkElement { Tag: AnnotationData } other) continue;
            if (other.Visibility != Visibility.Visible) continue;
            Rect b = GetCanvasBounds(child);
            if (!b.IsEmpty) _snapOthers.Add(b);
        }
    }

    private void DragGroupResize(Point pos)
    {
        if (_groupResize is not { } members) return;
        Rect u = _groupFrame;
        int handle = _activeHandle;
        bool fromCentre = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        Point centre = new(u.Left + u.Width / 2, u.Top + u.Height / 2);
        Point anchor = fromCentre ? centre : ShapeConstraint.AnchorPoint(u, handle);
        Point grip = BoxHandlePoints(u)[handle];
        double origDx = grip.X - anchor.X; if (origDx == 0) origDx = 1;
        double origDy = grip.Y - anchor.Y; if (origDy == 0) origDy = 1;
        bool keep = ShapeConstraint.ShiftHeld || _groupForcesProportions;
        double liveDx = pos.X - anchor.X, liveDy = pos.Y - anchor.Y;

        if (SnappingOn && !keep && !fromCentre && _snapOthers is { } others)
        {
            double tol = MoveSnapScreenPx / Math.Max(0.01, _zoom);
            if (HandleAffectsX(handle)) liveDx += ObjectSnap.EdgeOffset(anchor.X + liveDx, others, axisX: true, tol);
            if (HandleAffectsY(handle)) liveDy += ObjectSnap.EdgeOffset(anchor.Y + liveDy, others, axisX: false, tol);
        }

        var (sx, sy) = ResolveGroupResizeScale(handle, liveDx / origDx, liveDy / origDy, keep,
            _groupMinScaleX, _groupMinScaleY);
        Point Scale(Point p) => new(anchor.X + (p.X - anchor.X) * sx, anchor.Y + (p.Y - anchor.Y) * sy);

        int savedHandle = _activeHandle;
        var savedBefore = _resizeBefore;
        double savedAngle = _resizeAngle;
        try
        {
            foreach (var m in members) ApplyGroupMember(m, handle, sx, sy, Scale);
        }
        finally
        {
            _activeHandle = savedHandle;
            _resizeBefore = savedBefore;
            _resizeAngle = savedAngle;
        }
        UpdateSelectionVisual();
        ShowSnapGuides();
    }

    private void ApplyGroupMember(GroupMember m, int handle, double sx, double sy, Func<Point, Point> scale)
    {
        FrameworkElement fe = m.Element;
        AnnotationData meta = m.Meta;
        Rect box = m.Box;
        var (ax, ay) = MemberAxisScale(m.Angle, sx, sy);
        Point c = new(box.Left + box.Width / 2, box.Top + box.Height / 2);
        Point nc = scale(c);

        if (m.Callout is CalloutLayout cl)
        {
            ApplyGroupCallout((CalloutAnnotation)fe, m, cl, handle, sx, sy, scale);
            return;
        }

        if (meta.Type == AnnotationData.TypeStep)
        {
            // Survey: a counter's centre moves with the frame and its radius takes the
            // average of the two scales, so the bubble stays round; the nub keeps its bearing.
            double r0 = (double.IsNaN(fe.Width) || fe.Width <= 0 ? box.Width : fe.Width) / 3;
            double r = r0 * Math.Max(0.05, (sx + sy) / 2);
            _resizeBefore = m.Before;
            _resizeAngle = 0;
            ResizeCounter(fe, meta, new Rect(nc.X - r, nc.Y - 1.5 * r, 2 * r, 2.5 * r));
            return;
        }

        if (meta.Type == AnnotationData.TypeText)
        {
            bool scalesFont = TextScalesFontFor(handle);
            double k = Math.Min(ax, ay);
            if (fe is Grid || meta.Rect is { Length: >= 4 })
            {
                double pad = AnnotationFactory.TextPadding;
                double w = scalesFont ? ScaledTextBoxLength(box.Width, ax, pad) : box.Width * ax;
                double h = scalesFont ? ScaledTextBoxLength(box.Height, ay, pad) : box.Height;
                if (scalesFont) SetFontSize(fe, Math.Round(Math.Max(1, m.FontSize * k) * 10) / 10);
                _resizeBefore = m.Before;
                ResizeTextElement(fe, meta, new Rect(nc.X - w / 2, nc.Y - h / 2, w, h));
                if (!scalesFont && fe is Grid g && g.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock tb)
                {
                    // Drawboard: words rewrapped by a side drag grow the box downward so no
                    // line is cut off (textBoxFit.js) - the top stays put.
                    tb.Measure(new Size(Math.Max(1, w - 2 * pad), double.PositiveInfinity));
                    double need = tb.DesiredSize.Height + 2 * pad;
                    if (need > h)
                    {
                        fe.Height = need;
                        meta.Rect = new[] { nc.X - w / 2, nc.Y - h / 2, w, need };
                    }
                }
            }
            else
            {
                // A label without a box: the font scales and the label re-centres.
                if (scalesFont) SetFontSize(fe, m.FontSize * k);
                FlattenTransformIntoCanvas(fe);
                fe.UpdateLayout();
                Rect now = UnrotatedCanvasBounds(fe);
                Canvas.SetLeft(fe, Canvas.GetLeft(fe) + nc.X - (now.Left + now.Width / 2));
                Canvas.SetTop(fe, Canvas.GetTop(fe) + nc.Y - (now.Top + now.Height / 2));
            }
            RestoreGroupAngle(fe, meta, m.Angle);
            return;
        }

        double nw = box.Width * ax, nh = box.Height * ay;
        var next = new Rect(nc.X - nw / 2, nc.Y - nh / 2, nw, nh);
        _resizeBefore = m.Before;
        _resizeAngle = m.Angle;
        // Handle 2 (bottom-right) with the pointer placed past the box's own top-left by the
        // new size: the per-type resizers then scale about that corner and never mirror, and
        // ResizeInk's shift lands the stroke on `next`.
        _activeHandle = 2;
        Point local = new(m.Before.Bounds.Left + nw, m.Before.Bounds.Top + nh);
        ApplyBoxResize(fe, meta, next, local);
        RestoreGroupAngle(fe, meta, m.Angle);
    }

    private static void RestoreGroupAngle(FrameworkElement fe, AnnotationData meta, double angle)
    {
        if (Math.Abs(angle) < 0.01) return;
        fe.UpdateLayout();
        AnnotationTransform.SetAngle(fe, angle);
        meta.Angle = angle;
    }

    /// <summary>
    /// Survey scaleCalloutForGroup: the tip and the bend move with the frame; the text box
    /// acts like a text mark - from a corner, the top or the bottom its font scales (the box
    /// inside its fixed padding); from a side only the width changes and the words rewrap.
    /// </summary>
    private void ApplyGroupCallout(CalloutAnnotation callout, GroupMember m, CalloutLayout cl, int handle,
        double sx, double sy, Func<Point, Point> scale)
    {
        Point tip = scale(cl.Tip) - m.Offset;
        Point knee = scale(cl.Knee) - m.Offset;
        Rect b = cl.Box;
        Point centre = scale(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2)) - m.Offset;
        bool scalesFont = TextScalesFontFor(handle);
        double pad = CalloutLayout.TextPadding;
        double w = scalesFont ? ScaledTextBoxLength(b.Width, sx, pad) : b.Width * sx;
        double h = b.Height * sy;
        var meta = m.Before.Meta.Clone();
        if (scalesFont)
            meta.FontSize = Math.Round(Math.Max(1, m.FontSize * Math.Min(sx, sy)) * 10) / 10;
        var layout = CalloutLayout.Raw(tip, knee, new Rect(centre.X - w / 2, centre.Y - h / 2, w, h),
            CalloutDescender(meta));
        ApplyCalloutMeta(callout, layout, meta, callout.Text);
        callout.Tag = CalloutDataFrom(callout.Layout, meta);
    }

    private void EndGroupResize()
    {
        var members = _groupResize;
        _groupResize = null;
        _resizeBefore = null;
        _snapOthers = null;
        HideSnapGuides();
        if (members is null) return;
        var changes = new List<(UIElement El, ResizeSnapshot Before, ResizeSnapshot After)>();
        foreach (var m in members)
        {
            if (m.Element.Tag is not AnnotationData meta) continue;
            var after = CaptureResizeSnapshot(m.Element, meta);
            if (!after.NearlyEquals(m.Before)) changes.Add((m.Element, m.Before, after));
        }
        if (changes.Count == 0)
        {
            UpdateSelectionVisual();
            return;
        }
        _justDraggedAt = Environment.TickCount64;
        Push(new EditorAction(
            undo: () => { foreach (var c in changes) ApplyResizeSnapshot(c.El, c.Before); UpdateSelectionVisual(); },
            redo: () => { foreach (var c in changes) ApplyResizeSnapshot(c.El, c.After); UpdateSelectionVisual(); }),
            apply: false);
        UpdateSelectionVisual();
    }

    private void AbortGroupResize()
    {
        var members = _groupResize;
        _groupResize = null;
        _resizeBefore = null;
        _snapOthers = null;
        HideSnapGuides();
        if (members is null) return;
        foreach (var m in members) ApplyResizeSnapshot(m.Element, m.Before);
    }
}
