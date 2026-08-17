using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinShot.Core;

/// <summary>
/// The destructive confirm card (design 4h): borderless 230px #232326 card,
/// 12px radius, 13/600 title, 11.5 secondary body, secondary Cancel and the
/// app's ONLY solid-red button. Replaces MessageBox for destructive actions.
/// </summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string body, string confirmLabel)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var card = new Border
        {
            Width = 250,
            CornerRadius = new CornerRadius(12),
            Background = (Brush)TryFindResource("CardGroupBrush") ?? new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Margin = new Thickness(24),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 48, ShadowDepth = 20, Direction = 270, Opacity = 0.65, Color = Colors.Black,
            },
        };
        Content = card;

        var panel = new StackPanel();
        card.Child = panel;

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)TryFindResource("TextPrimaryBrush") ?? Brushes.White,
        });
        panel.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 11.5,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)TryFindResource("TextSecondaryBrush") ?? Brushes.Gainsboro,
            Margin = new Thickness(0, 8, 0, 12),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(buttons);

        var cancel = new Button
        {
            Content = "Cancel",
            Height = 28,
            MinWidth = 0,
            IsCancel = true,
            Style = (Style)TryFindResource("PillButtonSecondary"),
        };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);

        var confirm = new Button
        {
            Content = confirmLabel,
            Height = 28,
            MinWidth = 0,
            IsDefault = true,
            Style = (Style)TryFindResource("PillButtonDestructive"),
        };
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(confirm);

        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    public static bool Show(Window owner, string title, string body, string confirmLabel)
    {
        var dialog = new ConfirmDialog(title, body, confirmLabel) { Owner = owner };
        return dialog.ShowDialog() == true;
    }
}
