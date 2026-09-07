using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinShot.Core;

/// <summary>
/// A one-field prompt on the same dark card as every other dialog. Built on
/// <see cref="ThemedMessageDialog.Build"/> so the chrome, the button order and the dark
/// title bar are shared rather than re-created.
/// </summary>
public static class ThemedInputDialog
{
    /// <summary>Returns the entered text, or null if the user cancelled.</summary>
    public static string? Ask(
        Window? owner, string title, string message, string initialValue, string confirmLabel = "Save")
    {
        ThemeResources.EnsureLoaded();
        var (dialog, body, footer) = ThemedMessageDialog.Build(owner, title, message);

        var box = new TextBox
        {
            Text = initialValue,
            Margin = new Thickness(0, 0, 0, 16),
        };
        box.SetResourceReference(FrameworkElement.StyleProperty, "DarkTextBox");
        box.SetResourceReference(Control.FontSizeProperty, "TextBodyLg");
        box.MinHeight = 30;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.Padding = new Thickness(8, 0, 8, 0);
        AutomationName(box, title, message);

        // The footer is the last child; the field belongs above it.
        body.Children.Insert(body.Children.Count - 1, box);

        ThemedMessageDialog.AddButton(footer, "Cancel", primary: false, () => { }).IsCancel = true;
        Button confirm = ThemedMessageDialog.AddButton(
            footer, confirmLabel, primary: true, () => dialog.DialogResult = true);
        confirm.IsDefault = true;

        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                dialog.DialogResult = true;
            }
        };

        return dialog.ShowDialog() == true ? box.Text : null;
    }

    private static void AutomationName(DependencyObject element, string name, string help)
    {
        System.Windows.Automation.AutomationProperties.SetName(element, name);
        System.Windows.Automation.AutomationProperties.SetHelpText(element, help);
    }

    /// <summary>
    /// Makes a typed answer safe to write. Invalid characters become '-' rather than being
    /// dropped, so "12/06" reads as "12-06" instead of silently becoming "1206", and an
    /// answer that sanitizes down to nothing falls back to what was offered.
    /// </summary>
    public static string SanitizeFileName(string? entered, string fallback)
    {
        string text = (entered ?? string.Empty).Trim();
        if (text.Length == 0)
            return fallback;

        char[] invalid = Path.GetInvalidFileNameChars();
        var cleaned = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            cleaned.Append(Array.IndexOf(invalid, c) >= 0 ? '-' : c);

        // Trailing dots and spaces are legal to type and illegal to create on Windows.
        // An answer made only of characters we had to replace ("///" -> "---") is not a name
        // the user chose, so it falls back too rather than saving a file called "---".
        string result = cleaned.ToString().TrimEnd(' ', '.');
        return result.Trim('-', ' ', '.').Length == 0 ? fallback : result;
    }
}
