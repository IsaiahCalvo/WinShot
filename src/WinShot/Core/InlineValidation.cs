using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinShot.Core;

/// <summary>
/// Renders a validation message under the field it belongs to. Every error in Settings used
/// to be a <c>ToolTip</c>: the copy existed and was good, and the user never saw it unless
/// they happened to hover the red border. This draws it instead — an adorner, so no row
/// layout has to make room for a message that is usually absent.
/// </summary>
internal static class InlineValidation
{
    /// <summary>Shows (or replaces) the message under <paramref name="target"/>.</summary>
    public static void Show(FrameworkElement target, string message)
    {
        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null)
            return; // not in a live visual tree (collapsed pane before first layout)

        Clear(target, layer);
        layer.Add(new InlineValidationAdorner(target, message));
    }

    public static void Clear(FrameworkElement target) =>
        Clear(target, AdornerLayer.GetAdornerLayer(target));

    private static void Clear(FrameworkElement target, AdornerLayer? layer)
    {
        Adorner[]? existing = layer?.GetAdorners(target);
        if (existing is null || layer is null)
            return;
        foreach (Adorner adorner in existing)
        {
            if (adorner is InlineValidationAdorner)
                layer.Remove(adorner);
        }
    }
}

internal sealed class InlineValidationAdorner : Adorner
{
    private const double Gap = 3;
    private const double MinWidth = 120;

    private readonly VisualCollection _children;
    private readonly TextBlock _label;

    public InlineValidationAdorner(UIElement adorned, string message) : base(adorned)
    {
        // The message is output, never a target: clicks belong to the field under it.
        IsHitTestVisible = false;

        _label = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 15,
            Opacity = 0,
        };
        _label.SetResourceReference(TextBlock.FontSizeProperty, "TextCaption");
        _label.SetResourceReference(TextBlock.ForegroundProperty, "TextErrorBrush");
        _children = new VisualCollection(this) { _label };

        if (Motion.AnimationsEnabled())
        {
            _label.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(Motion.QuickMs)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            _label.Opacity = 1;
        }
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _label.Measure(new Size(Math.Max(MinWidth, AdornedElement.RenderSize.Width), double.PositiveInfinity));
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = Math.Max(MinWidth, finalSize.Width);
        _label.Arrange(new Rect(0, finalSize.Height + Gap, width, _label.DesiredSize.Height));
        return finalSize;
    }
}
