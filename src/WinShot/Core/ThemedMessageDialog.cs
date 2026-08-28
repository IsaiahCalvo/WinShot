using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinShot.Core;

/// <summary>
/// The app's themed replacement for MessageBox.Show: a dark card with pill-button
/// footer, so confirmations and errors stop popping light-gray Win32 chrome over
/// the dark shell. One OK-style message and one two-button confirm cover every
/// current call site; destructive confirms get the red fill.
/// </summary>
public static class ThemedMessageDialog
{
    /// <summary>Shows a message with a single OK button.</summary>
    public static void Show(Window owner, string title, string message)
    {
        var (dialog, footer) = Build(owner, title, message);
        var ok = MakeButton("OK", "PillButtonPrimary");
        ok.IsDefault = true;
        ok.IsCancel = true;
        ok.Click += (_, _) => dialog.DialogResult = true;
        footer.Children.Add(ok);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Shows a confirm/cancel prompt. Returns true when the user confirms.
    /// Destructive confirms render the confirm button with the red fill.
    /// </summary>
    public static bool Confirm(Window owner, string title, string message, string confirmLabel, bool destructive = false)
    {
        var (dialog, footer) = Build(owner, title, message);

        var confirm = MakeButton(confirmLabel, destructive ? "PillButtonDestructive" : "PillButtonPrimary");
        confirm.IsDefault = true;
        confirm.Click += (_, _) => dialog.DialogResult = true;
        footer.Children.Add(confirm);

        var cancel = MakeButton("Cancel", "PillButtonSecondary");
        cancel.IsCancel = true;
        footer.Children.Add(cancel);

        return dialog.ShowDialog() == true;
    }

    internal static (Window Dialog, StackPanel Footer) Build(Window owner, string title, string message)
    {
        ThemeResources.EnsureLoaded();
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Owner = owner,
            UseLayoutRounding = true,
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "ToolbarBgBrush");
        DarkTitleBar.Apply(dialog);
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        dialog.Content = panel;

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(titleText);

        var body = new TextBlock
        {
            Text = message,
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 18),
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(body);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        panel.Children.Add(footer);
        return (dialog, footer);
    }

    private static Button MakeButton(string label, string styleKey)
    {
        var button = new Button { Content = label, MinWidth = 84 };
        button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return button;
    }
}
