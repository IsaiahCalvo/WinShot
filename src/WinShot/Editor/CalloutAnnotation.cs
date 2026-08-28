using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinShot.Editor;

/// <summary>
/// Survey-style callout: leader (tip → knee → box border) + arrowhead + text box.
/// Children live in content coordinates; the canvas itself stays at (0,0) so
/// Select-tool translates keep matching stored AnnotationData points.
/// </summary>
internal sealed class CalloutAnnotation : Canvas
{
    private readonly Path _leader = new()
    {
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Fill = Brushes.Transparent,
    };

    private readonly Path _head = new()
    {
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };

    private readonly Border _box = new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(CalloutLayout.TextPadding),
        SnapsToDevicePixels = true,
    };

    private readonly TextBlock _label = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Segoe UI"),
        FontWeight = FontWeights.SemiBold,
    };

    public CalloutAnnotation()
    {
        IsHitTestVisible = true;
        Background = Brushes.Transparent;
        _box.Child = _label;
        Children.Add(_leader);
        Children.Add(_head);
        Children.Add(_box);
    }

    public TextBlock Label => _label;
    public Border Box => _box;
    public CalloutLayout Layout { get; private set; }

    public void Apply(
        CalloutLayout layout, string text, Color stroke, Color fill, double thickness,
        ArrowheadStyle head, LineBorderStyle lineStyle, double fontSize)
    {
        Layout = layout;
        var strokeBrush = new SolidColorBrush(stroke);
        var fillBrush = fill.A == 0 ? Brushes.Transparent : new SolidColorBrush(fill);

        var leader = new PathGeometry();
        var fig = new PathFigure { StartPoint = layout.Tip, IsFilled = false };
        fig.Segments.Add(new LineSegment(layout.Knee, true));
        fig.Segments.Add(new LineSegment(layout.Join, true));
        leader.Figures.Add(fig);
        _leader.Data = leader;
        _leader.Stroke = strokeBrush;
        _leader.StrokeThickness = Math.Max(1, thickness);
        AnnotationFactory.ApplyDash(_leader, lineStyle == LineBorderStyle.Cloud ? LineBorderStyle.Solid : lineStyle);

        var v = layout.Tip - layout.Knee;
        if (v.Length < 0.5) v = layout.Tip - layout.Join;
        if (v.Length < 0.5) v = new Vector(1, 0);
        double angle = Math.Atan2(v.Y, v.X);
        var headGeom = new PathGeometry();
        // Rebuild a one-head geometry at the tip pointing along tip-from-knee.
        Point dummyFrom = layout.Tip - v;
        var full = AnnotationFactory.ArrowGeometry(
            dummyFrom, layout.Tip, thickness, head, ArrowheadStyle.None);
        if (full is PathGeometry pg && pg.Figures.Count > 1)
        {
            var onlyHead = new PathGeometry();
            for (int i = 1; i < pg.Figures.Count; i++)
                onlyHead.Figures.Add(pg.Figures[i]);
            _head.Data = onlyHead;
        }
        else
        {
            _head.Data = Geometry.Empty;
        }
        _head.Stroke = strokeBrush;
        _head.Fill = head is ArrowheadStyle.SolidTriangle ? strokeBrush : Brushes.Transparent;
        _head.StrokeThickness = Math.Max(1, thickness);
        _head.Visibility = head == ArrowheadStyle.None ? Visibility.Collapsed : Visibility.Visible;
        _ = angle;

        _box.BorderBrush = strokeBrush;
        _box.BorderThickness = new Thickness(Math.Max(1, thickness));
        _box.Background = fillBrush;
        _box.Width = layout.Box.Width;
        _box.Height = layout.Box.Height;
        SetLeft(_box, layout.Box.X);
        SetTop(_box, layout.Box.Y);

        _label.Text = text;
        _label.FontSize = fontSize;
        _label.Foreground = strokeBrush;
        _label.TextWrapping = TextWrapping.Wrap;
    }

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }
}
