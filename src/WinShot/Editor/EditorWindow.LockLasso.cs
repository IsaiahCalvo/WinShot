using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinShot.Editor;

/// <summary>
/// Two of Survey's selection tools: Lock (right-click → Lock / Unlock) and Lasso select (Alt+V,
/// or right-click the Select button). The lasso catches the marks lying wholly inside the loop
/// you draw; Shift adds to the selection and Alt takes away, as with the box.
/// </summary>
public partial class EditorWindow
{
    private bool _lassoSelect;
    private readonly List<Point> _lassoPoints = new();
    private Path? _lassoPath;

    private static bool IsLocked(UIElement el) =>
        el is FrameworkElement { Tag: AnnotationData { Locked: true } };

    /// <summary>Whether any picked mark is locked (Survey: a locked mark in the pick stops the move).</summary>
    private bool SelectionLocked() => SelectionList().Any(IsLocked);

    /// <summary>Locks every picked mark, or unlocks them all when any is locked. One undo step.</summary>
    private void ToggleSelectionLock()
    {
        var picked = SelectionList().OfType<FrameworkElement>().Where(fe => fe.Tag is AnnotationData).ToArray();
        if (picked.Length == 0) return;
        bool lockIt = !picked.Any(IsLocked);
        var before = picked.Select(fe => ((AnnotationData)fe.Tag).Locked).ToArray();
        void Apply(bool? value)
        {
            foreach (FrameworkElement fe in picked) ((AnnotationData)fe.Tag).Locked = value;
            UpdateSelectionVisual();
            UpdateContextPanels();
        }
        Push(new EditorAction(
            undo: () =>
            {
                for (int i = 0; i < picked.Length; i++) ((AnnotationData)picked[i].Tag).Locked = before[i];
                UpdateSelectionVisual();
                UpdateContextPanels();
            },
            redo: () => Apply(lockIt ? true : null)));
    }

    /// <summary>Box select (V) or lasso select (Alt+V). Both arm the Select tool.</summary>
    private void SetLassoSelect(bool lasso)
    {
        _lassoSelect = lasso;
        SyncSelectModeButtons();
        CheckToolButton(EditorTool.Select);
        string label = lasso ? "Lasso select" : "Select";
        SelectToolBtn.ToolTip = ToolTipWithKey(label, lasso ? "Alt+V" : "V");
        System.Windows.Automation.AutomationProperties.SetName(SelectToolBtn, label);
    }

    private void WireSelectModeMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var itemStyle = (Style)FindResource("DarkMenuItem");
        var box = new MenuItem { Header = "Box select", InputGestureText = "V", Style = itemStyle, IsCheckable = true };
        var lasso = new MenuItem { Header = "Lasso select", InputGestureText = "Alt+V", Style = itemStyle, IsCheckable = true };
        box.Click += (_, _) => SetLassoSelect(false);
        lasso.Click += (_, _) => SetLassoSelect(true);
        menu.Opened += (_, _) =>
        {
            box.IsChecked = !_lassoSelect;
            lasso.IsChecked = _lassoSelect;
        };
        menu.Items.Add(box);
        menu.Items.Add(lasso);
        SelectToolBtn.ContextMenu = menu;
    }

    private void BeginLasso(Point start)
    {
        _lassoPoints.Clear();
        _lassoPoints.Add(start);
    }

    private void AddLassoPoint(Point p)
    {
        if (_lassoPoints.Count > 0 && (p - _lassoPoints[^1]).Length * _zoom < 2) return;
        _lassoPoints.Add(p);
        if (!_marqueeArmed || _lassoPoints.Count < 2) return;
        if (_lassoPath is null)
        {
            _lassoPath = new Path
            {
                IsHitTestVisible = false,
                Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x64, 0xFF)),
                Fill = new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x64, 0xFF)),
            };
            InteractionCanvas.Children.Add(_lassoPath);
        }
        _lassoPath.StrokeThickness = 1 / _zoom;
        _lassoPath.StrokeDashArray = new DoubleCollection { 4, 3 };
        _lassoPath.Data = LassoGeometry(_lassoPoints);
        _lassoPath.Visibility = Visibility.Visible;
    }

    private void HideLasso()
    {
        if (_lassoPath is not null) _lassoPath.Visibility = Visibility.Collapsed;
    }

    /// <summary>The loop as a closed, filled figure.</summary>
    internal static Geometry LassoGeometry(IReadOnlyList<Point> points)
    {
        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
        if (points.Count < 2) return geo;
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(points[0], isFilled: true, isClosed: true);
            ctx.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: false);
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>
    /// Whether a mark lies wholly inside the loop. Uses the mark's own outline where it has one
    /// (so a diagonal line inside the loop counts even when its box pokes out), else its box.
    /// </summary>
    internal static bool LassoCatches(Geometry lasso, Rect bounds, Geometry? outline)
    {
        if (bounds.IsEmpty) return false;
        if (!lasso.Bounds.IntersectsWith(bounds)) return false;
        Geometry probe = outline ?? new RectangleGeometry(bounds);
        return lasso.FillContainsWithDetail(probe) == IntersectionDetail.FullyContains;
    }

    private List<UIElement> LassoHits(Point release)
    {
        var hits = new List<UIElement>();
        _lassoPoints.Add(release);
        if (_lassoPoints.Count < 3) return hits;
        Geometry lasso = LassoGeometry(_lassoPoints);
        foreach (UIElement child in AnnotationCanvas.Children)
        {
            if (child is TextBox) continue;
            if (child is not FrameworkElement fe || fe.Tag is not AnnotationData) continue;
            if (LassoCatches(lasso, GetCanvasBounds(fe), CanvasGeometryOf(fe)))
                hits.Add(fe);
        }
        _lassoPoints.Clear();
        return hits;
    }
}
