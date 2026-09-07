using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
        Enter(dialog, panel);

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

    /// <summary>
    /// Fades and grows the card in. ToastWindow — the other surface this wave made the app's
    /// single feedback channel — eases in and out, so a dialog appearing at full size in one
    /// frame beside it read as two different systems. The content scales rather than the
    /// Window: a RenderTransform on the Window shrinks its content inside a frame that does
    /// not move, and the 2% band left at the edges is the same brush either way.
    ///
    /// Deliberately asymmetric, and written down here rather than left to be inferred from an
    /// absence: the dialog leaves in one frame. A modal answers a question, and a window that
    /// lingers 140 ms after the click that dismissed it reads as the app being slow to accept
    /// the answer — which is not what the toast's exit fade has to overcome, since nobody
    /// dismissed that.
    ///
    /// The duration is the theme's, so Settings &gt; Accessibility &gt; Animation effects
    /// zeroes this along with every other transition (ThemeResources zeroes the token itself).
    /// The literal is only reached by a dialog raised before the theme is merged — which is
    /// why the preference is read here as well: it was the one motion path in the app that
    /// inferred the setting from a resource instead of asking, so a missed lookup animated
    /// for a user who had turned animation off.
    /// </summary>
    private static void Enter(Window dialog, UIElement content)
    {
        if (!Motion.AnimationsEnabled())
            return;

        var scale = new ScaleTransform(0.98d, 0.98d);
        content.RenderTransform = scale;
        content.RenderTransformOrigin = new Point(0.5d, 0.5d);
        content.Opacity = 0d;

        dialog.Loaded += (_, _) =>
        {
            Duration duration = dialog.TryFindResource("MotionQuick") as Duration?
                ?? new Duration(TimeSpan.FromMilliseconds(Motion.QuickMs));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            content.BeginAnimation(
                UIElement.OpacityProperty, new DoubleAnimation(1d, duration) { EasingFunction = ease });
            var grow = new DoubleAnimation(1d, duration) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        };
    }
}
