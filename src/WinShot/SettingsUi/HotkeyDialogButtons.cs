using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinShot.SettingsUi;

/// <summary>Shared button factory for the hotkey conflict / owner-probe dialogs.</summary>
internal static class HotkeyDialogButtons
{
    public static Button CreateDialogButton(string text, bool isPrimary, Action onClick)
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
