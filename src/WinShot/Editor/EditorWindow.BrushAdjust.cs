using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinShot.Editor;

/// <summary>
/// Survey's on-canvas brush drag (useBrushAdjustGesture.js) on the editor canvas: with a drawing
/// tool armed and nothing selected, hold Alt and drag with the right button (or Ctrl+Alt with
/// the left). Sideways = width (size for the eraser), up / down = opacity. A dot at the true
/// on-screen size stays where the press began, in the tool's colour at the new opacity, with the
/// values written under it. Let go = the values are set exactly as if picked in the toolbar;
/// Escape or a lost mouse = nothing changes. The maths is in <see cref="BrushAdjust"/>.
/// </summary>
public partial class EditorWindow
{
    private sealed class BrushAdjustState
    {
        public required MouseButton Button { get; init; }
        public required Point Origin { get; init; }
        public required double StartWidth { get; init; }
        public required double StartOpacity { get; init; }
        public required bool CanOpacity { get; init; }
        public required bool Eraser { get; init; }
        public required int MinWidth { get; init; }
        public required int MaxWidth { get; init; }
        public BrushAdjustAxis Axis { get; set; }
        public int Width { get; set; }
        public double Opacity { get; set; }
    }

    private BrushAdjustState? _brushAdjust;
    private Ellipse? _brushAdjustDot;
    private Border? _brushAdjustLabel;
    private TextBlock? _brushAdjustText;

    private void WireBrushAdjust()
    {
        Viewport.PreviewMouseDown += OnBrushAdjustPreviewDown;
        Viewport.PreviewMouseMove += OnBrushAdjustPreviewMove;
        Viewport.PreviewMouseUp += OnBrushAdjustPreviewUp;
    }

    /// <summary>Alt + right button, or Ctrl + Alt + left button (Survey isBrushAdjustPress).</summary>
    internal static bool IsBrushAdjustPress(MouseButton button, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Alt) == 0) return false;
        if (button == MouseButton.Right) return true;
        return button == MouseButton.Left && (modifiers & ModifierKeys.Control) != 0;
    }

    private void OnBrushAdjustPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (_brushAdjust is not null)
        {
            // A second button while the drag runs belongs to the drag.
            e.Handled = true;
            return;
        }
        if (!IsBrushAdjustPress(e.ChangedButton, Keyboard.Modifiers)) return;
        if (_sourceOperationActive || _activeText is not null || _panning || _dragging || _movingSelection) return;
        if (!BrushAdjust.AppliesTo(_tool)) return;
        // Only while the toolbar is working for the tool: a picked mark would be restyled instead.
        if (SelectionList().Count > 0) return;

        Point origin = e.GetPosition(OverlayCanvas);
        _brushAdjust = new BrushAdjustState
        {
            Button = e.ChangedButton,
            Origin = origin,
            StartWidth = ActiveSize(),
            StartOpacity = _opacity * 100,
            CanOpacity = BrushAdjust.HasOpacity(_tool),
            Eraser = _tool == EditorTool.Eraser,
            MinWidth = ClampActiveSize(0),
            MaxWidth = ClampActiveSize(100000),
            Width = (int)Math.Round(ActiveSize()),
            Opacity = _opacity * 100,
        };
        Viewport.CaptureMouse();
        ShowBrushAdjustPreview();
        e.Handled = true;
    }

    private void OnBrushAdjustPreviewMove(object sender, MouseEventArgs e)
    {
        if (_brushAdjust is not { } s) return;
        Vector d = e.GetPosition(OverlayCanvas) - s.Origin;
        var (width, opacity, axis) = BrushAdjust.Resolve(
            s.StartWidth, s.StartOpacity, d.X, d.Y, s.Axis, s.MinWidth, s.MaxWidth, s.CanOpacity);
        s.Width = width;
        s.Opacity = opacity;
        s.Axis = axis;
        ShowBrushAdjustPreview();
        e.Handled = true;
    }

    private void OnBrushAdjustPreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (_brushAdjust is not { } s) return;
        e.Handled = true;
        if (e.ChangedButton != s.Button) return;
        EndBrushAdjust(commit: true);
    }

    /// <summary>Ends the drag. Commit sets the values as the toolbar would; otherwise nothing changes.</summary>
    private void EndBrushAdjust(bool commit)
    {
        if (_brushAdjust is not { } s) return;
        _brushAdjust = null;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        if (_brushAdjustDot is not null) _brushAdjustDot.Visibility = Visibility.Collapsed;
        if (_brushAdjustLabel is not null) _brushAdjustLabel.Visibility = Visibility.Collapsed;
        if (!commit) return;
        if (s.Width != (int)Math.Round(s.StartWidth)) ApplySize(s.Width);
        if (s.CanOpacity && Math.Abs(s.Opacity - s.StartOpacity) > 0.01) SetStrokeOpacityPercent(s.Opacity);
    }

    /// <summary>
    /// The stroke opacity, set the way the Opacity slider sets it for the stroke well. The drag
    /// always means the line, never a shape's fill, whichever colour tab happens to be open.
    /// </summary>
    private void SetStrokeOpacityPercent(double percent)
    {
        double pct = Math.Clamp(percent, 0, 100);
        _opacity = pct / 100.0;
        bool fillWell = _colorWell == ColorWell.Fill
            && EditorShellContract.ContextFor(ChromeTool(), ChromeFilledRectangle()).HasFlag(EditorContextControls.FillStrokeTabs);
        if (!fillWell)
        {
            if (OpacitySlider is not null && Math.Abs(OpacitySlider.Value - pct) > 0.5) OpacitySlider.Value = pct;
            if (OpacityValue is not null) OpacityValue.Text = $"{Math.Round(pct)}%";
        }
        SyncColorWellIndicator();
    }

    private void ShowBrushAdjustPreview()
    {
        if (_brushAdjust is not { } s) return;
        if (_brushAdjustDot is null)
        {
            _brushAdjustDot = new Ellipse
            {
                IsHitTestVisible = false,
                StrokeThickness = 1,
                Stroke = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
            };
            _brushAdjustText = new TextBlock
            {
                Foreground = (Brush)FindResource("ChromeTextBrush"),
                FontSize = 12,
            };
            _brushAdjustLabel = new Border
            {
                IsHitTestVisible = false,
                Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Background = (Brush)FindResource("ChromeBgBrush"),
                BorderBrush = (Brush)FindResource("ChromeHairlineBrush"),
                Child = _brushAdjustText,
            };
            OverlayCanvas.Children.Add(_brushAdjustDot);
            OverlayCanvas.Children.Add(_brushAdjustLabel);
        }

        double drawn = _tool == EditorTool.Highlighter ? AnnotationStyle.HighlighterWidth(s.Width) : s.Width;
        double diameter = Math.Max(2, drawn * _zoom);
        _brushAdjustDot.Width = diameter;
        _brushAdjustDot.Height = diameter;
        _brushAdjustDot.Fill = s.Eraser
            ? new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(AnnotationStyle.Compose(_color, s.Opacity / 100.0));
        Canvas.SetLeft(_brushAdjustDot, s.Origin.X - diameter / 2);
        Canvas.SetTop(_brushAdjustDot, s.Origin.Y - diameter / 2);
        _brushAdjustDot.Visibility = Visibility.Visible;

        _brushAdjustText!.Text = BrushAdjust.Label(s.Width, s.Opacity, s.Eraser);
        _brushAdjustLabel!.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size label = _brushAdjustLabel.DesiredSize;
        Canvas.SetLeft(_brushAdjustLabel, s.Origin.X - label.Width / 2);
        Canvas.SetTop(_brushAdjustLabel, s.Origin.Y + diameter / 2 + 8);
        _brushAdjustLabel.Visibility = Visibility.Visible;
    }
}
