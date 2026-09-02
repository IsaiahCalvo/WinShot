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
    /// <summary>Survey's transparent leader hit stroke: a fixed 12 units, never thickness-driven.</summary>
    private const double LeaderHitThickness = 12;

    // Survey hover / multi-select glow (SVGAnnotationLayer.jsx:3060-3190): box outline inset
    // -2/+4 at stroke 3, both leader segments at stroke 5, and an arrowhead-shaped glow at
    // max(3, lineThickness + 2) - all #4a90e2 at 0.45 opacity, never hit-testable.
    private static readonly Color GlowColor = Color.FromRgb(0x4A, 0x90, 0xE2);

    private readonly Path _leaderGlow = new()
    {
        Fill = null,
        StrokeThickness = 5,
        Opacity = 0.45,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    private readonly Path _headGlow = new()
    {
        Opacity = 0.45,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    private readonly Rectangle _boxGlow = new()
    {
        Fill = null,
        StrokeThickness = 3,
        Opacity = 0.45,
        RadiusX = 0,
        RadiusY = 0,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    private readonly Path _hit = new()
    {
        Stroke = Brushes.Transparent,
        Fill = null,
        StrokeThickness = LeaderHitThickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        // Survey gives line1/line2 cursor:'move'; the text box itself has no cursor rule.
        Cursor = System.Windows.Input.Cursors.SizeAll,
    };

    private readonly Path _leader = new()
    {
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Fill = null,
    };

    private readonly Path _head = new()
    {
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };

    // Survey draws the text box as a <rect> with rx/ry 0 and its own dash pattern; a WPF
    // Border cannot dash, so the visible box is a Rectangle and the text rides in a
    // chromeless host above it.
    private readonly Rectangle _box = new()
    {
        RadiusX = 0,
        RadiusY = 0,
        SnapsToDevicePixels = true,
    };

    private readonly Border _textHost = new()
    {
        // Survey insets the text horizontally only (TEXT_PADDING = 6); the full buffered
        // box height is given to the content and the text is centred inside it.
        Padding = new Thickness(CalloutLayout.TextPadding, 0, CalloutLayout.TextPadding, 0),
        Background = null,
        BorderThickness = new Thickness(0),
    };

    private readonly TextBlock _label = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily(CalloutLayout.DefaultFontFamily),
        FontWeight = FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public CalloutAnnotation()
    {
        IsHitTestVisible = true;
        Background = null;
        ClipToBounds = false;
        _textHost.Child = _label;
        Children.Add(_leaderGlow);
        Children.Add(_headGlow);
        Children.Add(_boxGlow);
        Children.Add(_hit);
        Children.Add(_leader);
        Children.Add(_head);
        Children.Add(_box);
        Children.Add(_textHost);
    }

    public TextBlock Label => _label;
    public Rectangle Box => _box;
    public CalloutLayout Layout { get; private set; }

    /// <summary>
    /// Survey paints the blue glow instead of handles while a callout is hovered (or selected
    /// as part of a multi-selection).
    /// </summary>
    public bool ShowGlow
    {
        get => _boxGlow.Visibility == Visibility.Visible;
        set
        {
            var v = value ? Visibility.Visible : Visibility.Collapsed;
            _boxGlow.Visibility = v;
            _leaderGlow.Visibility = v;
            _headGlow.Visibility = _head.Visibility == Visibility.Visible ? v : Visibility.Collapsed;
        }
    }

    public void Apply(
        CalloutLayout layout, string text, Color stroke, Color fill, double thickness,
        ArrowheadStyle head, LineBorderStyle lineStyle, double fontSize, string? fontFamily = null,
        Color? fontColor = null)
    {
        double descender = fontSize * CalloutLayout.DescenderRatio;
        if (Math.Abs(layout.Descender - descender) > 0.001)
            layout = layout with { Descender = descender };
        Layout = layout;
        var rect = layout.RenderBox;
        var strokeBrush = new SolidColorBrush(stroke);
        var fillBrush = fill.A == 0 ? Brushes.Transparent : new SolidColorBrush(fill);

        var dir = layout.Tip - layout.Knee;
        if (dir.Length < 0.5) dir = layout.Tip - layout.Join;
        if (dir.Length < 0.5) dir = new Vector(1, 0);
        dir.Normalize();
        // Survey: headSize = max(8, lineThickness * 3); only the triangle heads pull the
        // leader back into the head, the rest run all the way to the tip.
        double headSize = Math.Max(8, thickness * 3);
        double trim = head is ArrowheadStyle.SolidTriangle or ArrowheadStyle.OpenTriangle
            ? headSize / 3
            : 0;
        Point shaftTip = layout.Tip - dir * trim;

        var leader = new PathGeometry();
        var fig = new PathFigure { StartPoint = shaftTip, IsFilled = false };
        fig.Segments.Add(new LineSegment(layout.Knee, true));
        fig.Segments.Add(new LineSegment(layout.Join, true));
        leader.Figures.Add(fig);
        _leader.Data = leader;
        var glowBrush = new SolidColorBrush(GlowColor);
        _leaderGlow.Data = leader;
        _leaderGlow.Stroke = glowBrush;
        _leader.Fill = null;
        _leader.Stroke = strokeBrush;
        _leader.StrokeThickness = Math.Max(1, thickness);
        var dashStyle = lineStyle == LineBorderStyle.Cloud ? LineBorderStyle.Solid : lineStyle;
        AnnotationFactory.ApplyDash(_leader, dashStyle);
        _hit.Data = leader;
        _hit.StrokeThickness = LeaderHitThickness;

        var v = layout.Tip - layout.Knee;
        if (v.Length < 0.5) v = layout.Tip - layout.Join;
        if (v.Length < 0.5) v = new Vector(1, 0);
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
        _head.StrokeDashArray = null;   // Survey keeps the arrowhead solid at any line style
        _head.Visibility = head == ArrowheadStyle.None ? Visibility.Collapsed : Visibility.Visible;
        _headGlow.Data = _head.Data;
        _headGlow.Stroke = glowBrush;
        _headGlow.Fill = head is ArrowheadStyle.SolidTriangle ? glowBrush : Brushes.Transparent;
        _headGlow.StrokeThickness = Math.Max(3, thickness + 2);
        if (_head.Visibility != Visibility.Visible) _headGlow.Visibility = Visibility.Collapsed;

        _box.Stroke = strokeBrush;
        // Survey: the box border is deliberately lighter than the leader at any zoom.
        _box.StrokeThickness = Math.Max(1, thickness * 0.7);
        AnnotationFactory.ApplyDash(_box, dashStyle);
        _box.Fill = fillBrush;
        _box.IsHitTestVisible = true;
        _box.Width = rect.Width;
        _box.Height = rect.Height;
        SetLeft(_box, rect.X);
        SetTop(_box, rect.Y);

        _boxGlow.Stroke = glowBrush;
        _boxGlow.Width = rect.Width + 4;
        _boxGlow.Height = rect.Height + 4;
        SetLeft(_boxGlow, rect.X - 2);
        SetTop(_boxGlow, rect.Y - 2);

        _textHost.Width = rect.Width;
        _textHost.Height = rect.Height;
        SetLeft(_textHost, rect.X);
        SetTop(_textHost, rect.Y);

        _label.Text = text;
        _label.FontSize = fontSize;
        _label.FontFamily = new FontFamily(
            string.IsNullOrWhiteSpace(fontFamily) ? CalloutLayout.DefaultFontFamily : fontFamily);
        _label.Foreground = fontColor is Color fc ? new SolidColorBrush(fc) : strokeBrush;
        _label.TextWrapping = TextWrapping.Wrap;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.IsHitTestVisible = true;
        _label.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Survey's creation ghost (SVGAnnotationLayer.jsx:4855-4949): the whole group at 0.6
    /// opacity with 5,5 dashes on the box and both leader segments, so the committed callout
    /// (solid, full opacity) is visually distinct from the preview.
    /// </summary>
    public void ApplyGhost()
    {
        Opacity = 0.6;
        _leader.StrokeDashArray = new DoubleCollection { 5, 5 };
        _box.StrokeDashArray = new DoubleCollection { 5, 5 };
        _head.StrokeDashArray = null;
    }

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }
}
