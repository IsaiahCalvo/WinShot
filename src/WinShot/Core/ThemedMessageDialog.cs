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
    /// <param name="cancelLabel">The way out. Name the outcome where "Cancel" would be vague —
    /// "Keep both" says what happens, which is what makes the safe choice the obvious one.</param>
    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string confirmLabel,
        bool destructive = false,
        string cancelLabel = "Cancel")
    {
        var (dialog, _, footer) = Build(owner, title, message);

        Button cancel = AddButton(footer, cancelLabel, primary: false, () => { });
        cancel.IsCancel = true;

        Button confirm = AddButton(
            footer, confirmLabel, primary: true, () => dialog.DialogResult = true,
            styleKey: destructive ? "PillButtonDestructive" : "PillButtonPrimary");
        // Enter never commits something that cannot be undone: on a destructive confirm the
        // safe button is the default and the red one has to be aimed at.
        if (destructive)
            cancel.IsDefault = true;
        else
            confirm.IsDefault = true;

        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// Shows a message with several named choices and returns the index of the one picked, or
    /// -1 when the user cancelled. The LAST choice is the primary, so it is both the rightmost
    /// button and the default — the same order Confirm uses.
    /// </summary>
    public static int Choose(
        Window? owner,
        string title,
        string message,
        IReadOnlyList<string> choices,
        string cancelLabel = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
            return -1;

        var (dialog, _, footer) = Build(owner, title, message);
        int picked = -1;

        AddButton(footer, cancelLabel, primary: false, () => { }).IsCancel = true;
        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            bool primary = i == choices.Count - 1;
            Button button = AddButton(
                footer,
                choices[i],
                primary,
                () =>
                {
                    picked = index;
                    dialog.DialogResult = true;
                });
            if (primary)
                button.IsDefault = true;
        }

        return dialog.ShowDialog() == true ? picked : -1;
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
