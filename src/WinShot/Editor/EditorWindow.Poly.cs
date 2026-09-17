using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinShot.Editor;

/// <summary>
/// The Polygon and Polyline tools: Survey's click-to-place drafting, wired to WPF.
///
/// <see cref="PolyDraft"/> owns every rule (when a run may be finished, when it may be closed,
/// where the magnet is, what a finish produces); this file owns the pixels and the pointer
/// plumbing, which is the same split Survey has between polyDraft.js, SVGAnnotationLayer.jsx
/// and useSVGInteraction.js.
///
/// The one thing worth keeping straight: finishing is ALWAYS explicit. A click never ends the
/// shape — it only adds a vertex, or closes the run when it lands on the first one's magnet.
/// Anything else would make a mis-click destroy the corners already placed.
/// </summary>
public partial class EditorWindow
{
    private PolyDraft? _polyDraft;

    /// <summary>The chrome for the live draft. One layer, rebuilt per frame, hung in
    /// <c>InteractionCanvas</c> so nothing in it is ever mistaken for an annotation:
    /// <c>AnnotationCanvas</c>'s child list IS the annotation list to serialisation, marquee,
    /// z-order and export.</summary>
    private Canvas? _polyDraftLayer;

    /// <summary>Survey's drawn chrome colours (SVGAnnotationLayer.jsx:5772-5866) mapped onto
    /// WinShot's own selection blue — the ring every affordance in this editor is drawn in.</summary>
    private static Brush PolyChromeBrush => new SolidColorBrush(AnnotationStyle.SelectionRingColor);

    /// <summary>
    /// Screen constants become content units by dividing by the zoom, so the magnet and the
    /// checkmarks stay the same size on screen while the geometry scales with the page.
    /// </summary>
    private double PolyFirstPointSnapRadius => PolyDraft.FirstPointSnapScreenRadius / _zoom;
    private double PolyFinishHitRadius => PolyDraft.FinishControlScreenHitRadius / _zoom;

    /// <summary>
    /// A left click while a poly tool is armed. Returns true when it was consumed.
    ///
    /// Order matters: a click inside the FIRST vertex's magnet closes the shape and wins over
    /// "place another point here" (SVGAnnotationLayer.jsx:5387-5393), and a click on either
    /// finish checkmark finishes there — otherwise it is one more vertex.
    /// </summary>
    private bool PolyPointerDown(Point pos, bool shift, int clickCount)
    {
        if (!PolyDraft.IsPolyTool(_tool)) return false;

        if (_polyDraft is { } draft)
        {
            // The checkmark rings are drawn into a non-hit-testable layer, so they are
            // hit-tested here rather than by WPF. Same two controls, same radii.
            var (first, last) = draft.FinishControls;
            double r = PolyFinishHitRadius;
            if (draft.CanClose && (pos - first).Length <= r) { FinishPolyDraft(close: true); return true; }
            if (draft.CanFinish && (pos - last).Length <= r) { FinishPolyDraft(close: false); return true; }
            if (draft.IsNearFirstPoint(pos, PolyFirstPointSnapRadius)) { FinishPolyDraft(close: true); return true; }

            // WinShot addition: the CAD gesture. Survey finishes with the checkmark, Enter or
            // a click on the first point and has no double-click finish — but its own
            // compactPolyDraftPoints exists precisely to make a double-click's duplicate
            // trailing vertex harmless, so the gesture costs nothing and is what a hand
            // reaches for. The first click of the pair has already placed its vertex.
            if (clickCount >= 2) { FinishPolyDraft(close: false); return true; }

            _polyDraft = draft.AddPoint(pos, shift);
        }
        else
        {
            _polyDraft = PolyDraft.Create(PolyDraft.TypeFor(_tool), pos);
        }
        PaintPolyDraft();
        return true;
    }

    /// <summary>The rubber band follows the cursor; near the first vertex it snaps onto it so
    /// the user SEES the shape close before committing to the click.</summary>
    private bool PolyPointerMove(Point pos, bool shift)
    {
        if (_polyDraft is not { } draft) return false;
        _polyDraft = draft.UpdatePreview(pos, shift, PolyFirstPointSnapRadius);
        PaintPolyDraft();
        return true;
    }

    /// <summary>
    /// Enter finishes, Escape cancels (SVGAnnotationLayer.jsx:1757-1777). Returns true when
    /// the key was consumed, so an in-progress draft eats Escape before the editor's own
    /// "clear selection" handler sees it — cancelling the shape you are drawing is the more
    /// specific intent.
    /// </summary>
    private bool PolyKeyDown(System.Windows.Input.Key key)
    {
        if (_polyDraft is null) return false;
        switch (key)
        {
            case System.Windows.Input.Key.Enter:
                FinishPolyDraft(close: false);
                return true;
            case System.Windows.Input.Key.Escape:
                CancelPolyDraft();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Commit the draft. <paramref name="close"/> is true when the user asked to snap the run
    /// SHUT — the first-vertex checkmark, or a click back on the first point — which is how a
    /// polyline becomes a polygon. False finishes it where it stands: closed for the Polygon
    /// tool, open for the Polyline tool.
    ///
    /// Too few points is NOT a silent discard: the draft stays alive so the user simply keeps
    /// clicking instead of losing the corners already placed.
    /// </summary>
    private bool FinishPolyDraft(bool close)
    {
        if (_polyDraft is not { } draft) return false;
        PolyFinish finish = draft.Resolve(close);
        if (!finish.Ok) return false;

        _polyDraft = null;
        PaintPolyDraft();

        var path = AnnotationFactory.CreatePolyShape(
            finish.Points, finish.Closed, CurrentStrokeBrush(),
            finish.Closed ? CurrentFillBrush() : Colors.Transparent,
            _thickness, _lineStyle, _cloudIntensity, _strokeRender, _sketchSeed);
        var data = AnnotationData.ForPoly(
            finish.FinalType, finish.Points, CurrentStrokeBrush(), _thickness, _lineStyle);
        if (finish.Closed)
        {
            data.Fill = _fillMode.ToString();
            data.FillColor = AnnotationStyle.ToHex(CurrentFillBrush());
        }
        if (_lineStyle == LineBorderStyle.Cloud) data.CloudIntensity = _cloudIntensity;
        data.Sketch = _strokeRender == StrokeRender.Clean ? null : _strokeRender.ToString();
        data.Seed = _sketchSeed;
        path.Tag = data;
        AnnotationCanvas.Children.Add(path);
        // Not selected, like a pen stroke: the tool stays armed for the next run, and a
        // freshly committed shape's vertex handles would otherwise sit under the very next
        // click that was meant to start a new one.
        PushAddElement(path, select: false);
        return true;
    }

    /// <summary>Escape, or arming another tool: the draft is dropped, nothing is committed.</summary>
    private bool CancelPolyDraft()
    {
        if (_polyDraft is null) return false;
        _polyDraft = null;
        PaintPolyDraft();
        return true;
    }

    /// <summary>
    /// Redraws the draft chrome: the placed run, a dashed rubber band to the cursor, a hollow
    /// dot on every vertex, and a checkmark ring on the first vertex (closes) and the latest
    /// one (finishes) whenever those are legal (SVGAnnotationLayer.jsx:5772-5866).
    ///
    /// Everything is sized in SCREEN pixels divided by the zoom, so the chrome is a constant
    /// size on screen at any magnification while the run itself scales with the image.
    /// </summary>
    private void PaintPolyDraft()
    {
        if (_polyDraftLayer is null)
        {
            _polyDraftLayer = new Canvas { IsHitTestVisible = false };
            InteractionCanvas.Children.Add(_polyDraftLayer);
        }
        _polyDraftLayer.Children.Clear();
        if (_polyDraft is not { } draft || draft.Points.Count == 0)
        {
            _polyDraftLayer.Visibility = Visibility.Collapsed;
            return;
        }
        _polyDraftLayer.Visibility = Visibility.Visible;

        Color ink = CurrentStrokeBrush();
        double width = _thickness;
        double scale = 1.0 / _zoom;

        if (draft.Points.Count > 1)
        {
            var run = new PathGeometry();
            var figure = new PathFigure { StartPoint = draft.Points[0], IsClosed = false, IsFilled = false };
            for (int i = 1; i < draft.Points.Count; i++)
                figure.Segments.Add(new LineSegment(draft.Points[i], isStroked: true));
            run.Figures.Add(figure);
            _polyDraftLayer.Children.Add(new Path
            {
                Data = run,
                Stroke = new SolidColorBrush(ink),
                StrokeThickness = width,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }

        // The rubber band: dashed at 0.6 opacity so it reads as "not placed yet".
        Point last = draft.Points[^1];
        _polyDraftLayer.Children.Add(new Line
        {
            X1 = last.X, Y1 = last.Y, X2 = draft.Preview.X, Y2 = draft.Preview.Y,
            Stroke = new SolidColorBrush(ink),
            StrokeThickness = width,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashArray = new DoubleCollection(new[] { 5.0, 5.0 }),
            Opacity = 0.6,
        });

        double vertexR = 3 * scale;
        foreach (Point p in draft.Points)
            _polyDraftLayer.Children.Add(Disc(p, vertexR, Brushes.White, PolyChromeBrush, 1.5 * scale));

        var (first, lastPoint) = draft.FinishControls;
        if (draft.CanClose) AddFinishControl(first, scale);
        if (draft.CanFinish) AddFinishControl(lastPoint, scale);
    }

    /// <summary>Survey's finish control: a filled ring with a white tick through it.</summary>
    private void AddFinishControl(Point centre, double scale)
    {
        double ringR = PolyDraft.FinishControlScreenRadius * scale;
        double tickR = 4.6 * scale;
        _polyDraftLayer!.Children.Add(
            Disc(centre, ringR, PolyChromeBrush, Brushes.White, 2 * scale));

        var tick = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(centre.X - tickR, centre.Y + tickR * 0.05),
            IsClosed = false,
            IsFilled = false,
        };
        figure.Segments.Add(new LineSegment(
            new Point(centre.X - tickR * 0.26, centre.Y + tickR * 0.72), isStroked: true));
        figure.Segments.Add(new LineSegment(
            new Point(centre.X + tickR * 1.05, centre.Y - tickR * 0.85), isStroked: true));
        tick.Figures.Add(figure);
        _polyDraftLayer.Children.Add(new Path
        {
            Data = tick,
            Stroke = Brushes.White,
            StrokeThickness = 2.2 * scale,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        });
    }

    private static Ellipse Disc(Point centre, double radius, Brush fill, Brush stroke, double thickness)
    {
        var dot = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness,
        };
        Canvas.SetLeft(dot, centre.X - radius);
        Canvas.SetTop(dot, centre.Y - radius);
        return dot;
    }
}
