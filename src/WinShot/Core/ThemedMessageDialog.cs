using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinShot.Core;

/// <summary>
/// The app's themed replacement for MessageBox.Show: a dark card with pill-button
/// footer, so confirmations and errors stop popping light-gray Win32 chrome over
/// the dark shell. One OK-style message and one two-button confirm cover most call
/// sites; the hotkey dialogs build their own body and footer on <see cref="Build"/>.
/// Buttons run left to right with the primary LAST, so the default action is always
/// the rightmost one.
/// </summary>
public static class ThemedMessageDialog
{
    /// <summary>Shows a message with a single OK button.</summary>
    public static void Show(Window? owner, string title, string message)
    {
        var (dialog, _, footer) = Build(owner, title, message);
        Button ok = AddButton(footer, "OK", primary: true, () => dialog.DialogResult = true);
        ok.IsDefault = true;
        ok.IsCancel = true;
        dialog.ShowDialog();
    }

    /// <summary>
    /// Shows a confirm/cancel prompt. Returns true when the user confirms.
    /// Destructive confirms render the confirm button with the red fill.
    /// </summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmLabel, bool destructive = false)
    {
        var (dialog, _, footer) = Build(owner, title, message);

        AddButton(footer, "Cancel", primary: false, () => { }).IsCancel = true;

        Button confirm = AddButton(
            footer, confirmLabel, primary: true, () => dialog.DialogResult = true,
            styleKey: destructive ? "PillButtonDestructive" : "PillButtonPrimary");
        confirm.IsDefault = true;

        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// Asks for one line of text. Returns null when the user cancels, so an empty answer and
    /// a cancelled prompt stay distinguishable. Same card and footer as the other two, so a
    /// rename prompt does not have to be a second dialog style.
    /// </summary>
    public static string? Prompt(
        Window? owner, string title, string message, string initialValue, string confirmLabel)
    {
        var (dialog, body, footer) = Build(owner, title, message);

        var input = new TextBox { Text = initialValue, Margin = new Thickness(0, 0, 0, 18) };
        input.SetResourceReference(FrameworkElement.StyleProperty, "DarkTextBox");
        // Build hands back the whole card, footer included, so the field goes in above it.
        body.Children.Insert(body.Children.IndexOf(footer), input);
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        AddButton(footer, "Cancel", primary: false, () => { }).IsCancel = true;
        Button confirm = AddButton(footer, confirmLabel, primary: true, () => dialog.DialogResult = true);
        confirm.IsDefault = true;

        return dialog.ShowDialog() == true ? input.Text : null;
    }

    /// <summary>
    /// Appends a themed footer button. Every dialog in the app gets its buttons from
    /// here, so nothing has to hand-roll a Background/Foreground pair again.
    /// </summary>
    internal static Button AddButton(
        StackPanel footer, string label, bool primary, Action onClick, string? styleKey = null)
    {
        var button = new Button { Content = label, MinWidth = 84 };
        button.SetResourceReference(
            FrameworkElement.StyleProperty,
            styleKey ?? (primary ? "PillButtonPrimary" : "PillButtonSecondary"));
        button.Click += (_, _) => onClick();
        footer.Children.Add(button);
        return button;
    }

    internal static (Window Dialog, StackPanel Body, StackPanel Footer) Build(Window? owner, string title, string message)
    {
        ThemeResources.EnsureLoaded();
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            // An owner-less modal with no taskbar button can end up behind another app with
            // nothing to click back to — the Win32 MessageBox these replaced at least
            // activated itself. Owned dialogs stay off the taskbar, as a dialog should.
            ShowInTaskbar = owner is null,
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
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        titleText.SetResourceReference(TextBlock.FontSizeProperty, "TextTitle");
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(titleText);

        var body = new TextBlock
        {
            Text = message,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 18),
        };
        body.SetResourceReference(TextBlock.FontSizeProperty, "TextBody");
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(body);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        panel.Children.Add(footer);
        return (dialog, panel, footer);
    }
}
