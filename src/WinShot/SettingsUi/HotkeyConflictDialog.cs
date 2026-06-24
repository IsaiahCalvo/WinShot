using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinShot.Core;

namespace WinShot.SettingsUi;

internal enum HotkeyConflictChoice
{
    Keep,
    Change,
    FindApp,
}

internal sealed class HotkeyConflictDialog : Window
{
    private HotkeyConflictChoice _choice = HotkeyConflictChoice.Keep;

    private HotkeyConflictDialog(string actionLabel, string gesture, HotkeyConflictSource source)
    {
        Title = "Hotkey already used";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        Foreground = Brushes.White;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(18) };
        Content = panel;

        panel.Children.Add(new TextBlock
        {
            Text = $"{gesture} is already used",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10),
        });

        string ownerText = source.IsExactMatch
            ? $"{gesture} is used by {source.DisplayName}."
            : $"{gesture} is taken. Best match: {source.DisplayName}.";

        panel.Children.Add(new TextBlock
        {
            Text = $"{ownerText}\n\nFind app lets you press the hotkey once while WinShot watches what comes forward. Change opens that app or its settings. Keep means {actionLabel} needs a different WinShot hotkey.",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        panel.Children.Add(buttons);

        buttons.Children.Add(CreateButton("Find app", isPrimary: true, () =>
        {
            _choice = HotkeyConflictChoice.FindApp;
            DialogResult = true;
        }));

        buttons.Children.Add(CreateButton("Change", isPrimary: false, () =>
        {
            _choice = HotkeyConflictChoice.Change;
            DialogResult = true;
        }));

        buttons.Children.Add(CreateButton("Keep", isPrimary: false, () =>
        {
            _choice = HotkeyConflictChoice.Keep;
            DialogResult = false;
        }));
    }

    public static HotkeyConflictChoice Show(Window owner, string actionLabel, string gesture, HotkeyConflictSource source)
    {
        var dialog = new HotkeyConflictDialog(actionLabel, gesture, source) { Owner = owner };
        dialog.ShowDialog();
        return dialog._choice;
    }

    private static Button CreateButton(string text, bool isPrimary, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 86,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(isPrimary ? Color.FromRgb(0x2D, 0x7D, 0xFF) : Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Foreground = Brushes.White,
            BorderBrush = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
