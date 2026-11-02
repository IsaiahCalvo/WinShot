using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinShot.Editor;

/// <summary>
/// Survey's move snapping: while a selection is dragged, its edges and centre stick to the
/// edges and centres of the other marks and of the picture itself. Alt+drag moves without
/// snapping; Shift+S turns snapping off and on (remembered between sessions). The geometry is
/// <see cref="ObjectSnap"/>.
/// </summary>
public partial class EditorWindow
{
    /// <summary>Snap reach in screen pixels, so it feels the same at every zoom.</summary>
    private const double MoveSnapScreenPx = 6;

    // The pointer's own travel since the press. _moveTotal is what was actually applied,
    // which differs from this by the current snap.
    private Vector _moveRaw;
    private Rect? _snapStart;
    private List<Rect>? _snapOthers;

    private bool SnappingOn => _settings?.Current.EditorSnapping ?? true;

    private void ResetMoveSnap()
    {
        _moveRaw = new Vector();
        _snapStart = null;
        _snapOthers = null;
        HideSnapGuides();
    }

    /// <summary>The move to apply for a pointer travel of <paramref name="raw"/>.</summary>
    private Vector SnappedMove(Vector raw)
    {
        if (!SnappingOn || (Keyboard.Modifiers & ModifierKeys.Alt) != 0) return raw;
        if (_snapStart is null)
        {
            Rect start = Rect.Empty;
            var moving = SelectionList();
            foreach (UIElement el in moving)
                if (AnnotationCanvas.Children.Contains(el)) start.Union(GetCanvasBounds(el));
            if (start.IsEmpty) return raw;
            // The live bounds already include whatever has been applied this gesture.
            start.Offset(-_moveTotal.X, -_moveTotal.Y);
            _snapStart = start;
            _snapOthers = new List<Rect>
            {
                new(0, 0, AnnotationCanvas.ActualWidth, AnnotationCanvas.ActualHeight),
            };
            foreach (UIElement child in AnnotationCanvas.Children)
            {
                if (moving.Contains(child) || child is not FrameworkElement { Tag: AnnotationData } fe) continue;
                if (fe.Visibility != Visibility.Visible) continue;
                Rect b = GetCanvasBounds(child);
                if (!b.IsEmpty) _snapOthers.Add(b);
            }
        }
        Rect candidate = _snapStart.Value;
        candidate.Offset(raw);
        double tolerance = MoveSnapScreenPx / Math.Max(0.01, _zoom);
        return raw + ObjectSnap.Adjust(candidate, _snapOthers!, tolerance);
    }

    private void ToggleSnapping()
    {
        if (_settings is null) return;
        _settings.Current.EditorSnapping = !_settings.Current.EditorSnapping;
        _settings.Save();
        App.Toast(_settings.Current.EditorSnapping ? "Snapping on" : "Snapping off",
            "Shift+S turns it back. Alt+drag moves one mark without snapping.");
    }

    // ---- Survey SnapGuidesLayer: dashed guides while a dragged mark lines up ----

    private const double SnapGuideOverhangPx = 24, SnapGuideStrokePx = 1.9, SnapGuideDashPx = 5,
        SnapMarkHalfPx = 2.83;
    private Canvas? _snapGuideLayer;

    /// <summary>
    /// Draws Survey's guides for the selection as it now sits: a dashed selection-blue line
    /// for each edge or centre exactly on a neighbour's (24 px past both ends), and small X
    /// marks on the lined-up corners and centres. Sized in screen px at every zoom.
    /// </summary>
    private void ShowSnapGuides()
    {
        if (_snapOthers is null || !SnappingOn || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            HideSnapGuides();
            return;
        }
        Rect now = Rect.Empty;
        foreach (UIElement el in SelectionList())
            if (AnnotationCanvas.Children.Contains(el)) now.Union(GetCanvasBounds(el));
        var (lines, points) = ObjectSnap.Guides(now, _snapOthers);
        if (lines.Count == 0) { HideSnapGuides(); return; }

        if (_snapGuideLayer is null)
        {
            if (HandleLayer.Parent is not Panel host) return;
            _snapGuideLayer = new Canvas { IsHitTestVisible = false };
            host.Children.Insert(host.Children.IndexOf(HandleLayer), _snapGuideLayer);
        }
        _snapGuideLayer.Children.Clear();
        double k = HandleScale;
        var blue = new SolidColorBrush(AnnotationStyle.SelectionRingColor);
        blue.Freeze();
        double over = SnapGuideOverhangPx * k;
        foreach (var g in lines)
        {
            var line = new Line
            {
                Stroke = blue,
                StrokeThickness = SnapGuideStrokePx * k,
                // WPF dashes count in stroke widths; Survey's are 5 screen px on, 5 off.
                StrokeDashArray = new DoubleCollection { SnapGuideDashPx / SnapGuideStrokePx, SnapGuideDashPx / SnapGuideStrokePx },
            };
            if (g.AxisX) { line.X1 = line.X2 = g.Value; line.Y1 = g.Min - over; line.Y2 = g.Max + over; }
            else { line.Y1 = line.Y2 = g.Value; line.X1 = g.Min - over; line.X2 = g.Max + over; }
            _snapGuideLayer.Children.Add(line);
        }
        if (points.Count > 0)
        {
            double h = SnapMarkHalfPx * k;
            var marks = new StreamGeometry();
            using (StreamGeometryContext ctx = marks.Open())
                foreach (Point p in points)
                {
                    ctx.BeginFigure(new Point(p.X - h, p.Y - h), false, false);
                    ctx.LineTo(new Point(p.X + h, p.Y + h), true, false);
                    ctx.BeginFigure(new Point(p.X + h, p.Y - h), false, false);
                    ctx.LineTo(new Point(p.X - h, p.Y + h), true, false);
                }
            marks.Freeze();
            _snapGuideLayer.Children.Add(new Path
            {
                Data = marks, Stroke = Brushes.White, StrokeThickness = 2.6 * k,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            _snapGuideLayer.Children.Add(new Path
            {
                Data = marks, Stroke = blue, StrokeThickness = 1.3 * k,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        }
        _snapGuideLayer.Visibility = Visibility.Visible;
    }

    private void HideSnapGuides()
    {
        if (_snapGuideLayer is null) return;
        _snapGuideLayer.Children.Clear();
        _snapGuideLayer.Visibility = Visibility.Collapsed;
    }
}
