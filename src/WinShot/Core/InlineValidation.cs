using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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
    /// <summary>Each field's own HelpText, parked while an error is standing in for it.</summary>
    private static readonly ConditionalWeakTable<FrameworkElement, string> ParkedHelpText = new();

    /// <summary>Shows (or replaces) the message under <paramref name="target"/>.</summary>
    public static void Show(FrameworkElement target, string message)
    {
        // The adorner is a raw visual with no automation peer, so drawing the message and
        // deleting the ToolTip took it off UIA entirely — a screen reader used to be read
        // the tooltip as HelpText and now heard nothing, on the one field it matters most
        // (a hotkey conflict is why Done refuses to close). The field carries it instead.
        if (!ParkedHelpText.TryGetValue(target, out _))
            ParkedHelpText.Add(target, AutomationProperties.GetHelpText(target) ?? string.Empty);
        AutomationProperties.SetHelpText(target, message);

        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null)
            return; // not in a live visual tree (collapsed pane before first layout)

        Remove(target, layer);
        layer.Add(new InlineValidationAdorner(target, message));
    }

    public static void Clear(FrameworkElement target)
    {
        if (ParkedHelpText.TryGetValue(target, out string? original))
        {
            AutomationProperties.SetHelpText(target, original);
            ParkedHelpText.Remove(target);
        }
        Fade(target, AdornerLayer.GetAdornerLayer(target));
    }

    /// <summary>A message that eased in has to ease out: Remove() alone made the one thing
    /// on the pane that arrives over MotionQuick vanish in a frame.</summary>
    private static void Fade(FrameworkElement target, AdornerLayer? layer)
    {
        if (layer is null)
            return;
        foreach (Adorner adorner in layer.GetAdorners(target) ?? [])
        {
            if (adorner is InlineValidationAdorner message)
                message.FadeOutAndRemove(layer);
        }
    }

    private static void Remove(FrameworkElement target, AdornerLayer layer)
    {
        foreach (Adorner adorner in layer.GetAdorners(target) ?? [])
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
        // Announced when it appears, not only when the field is focused again.
        AutomationProperties.SetLiveSetting(_label, AutomationLiveSetting.Polite);
        _label.SetResourceReference(TextBlock.FontSizeProperty, "TextCaption");
        _label.SetResourceReference(TextBlock.ForegroundProperty, "TextErrorBrush");
        _children = new VisualCollection(this) { _label };

        if (Motion.AnimationsEnabled())
        {
            _label.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(Motion.QuickMs)))
                {
                    // The exit ease, not the entrance one: the message leaves the way it arrived,
            // reversed. Same curve as Theme.xaml's EaseInCubic and Motion.EaseInCubic.
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                });
        }
        else
        {
            _label.Opacity = 1;
        }

        UIElementAutomationPeer.CreatePeerForElement(_label)
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>Eases the message away and takes the adorner off the layer when it is gone,
    /// so appearing and disappearing are the same transition in both directions.</summary>
    internal void FadeOutAndRemove(AdornerLayer layer)
    {
        if (!Motion.AnimationsEnabled())
        {
            layer.Remove(this);
            return;
        }

        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(Motion.QuickMs)))
        {
            // The exit ease, not the entrance one: the message leaves the way it arrived,
            // reversed. Same curve as Theme.xaml's EaseInCubic and Motion.EaseInCubic.
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => layer.Remove(this);
        _label.BeginAnimation(UIElement.OpacityProperty, fade);
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
