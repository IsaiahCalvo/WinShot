using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinShot.Core;

namespace WinShot.Editor;

/// <summary>
/// The shortcut list ("?" or Ctrl+/) and the key in every tool's tooltip (Survey
/// toolShortcuts.js: "every tool button answers two questions on hover — what is this, and how
/// do I get back here without the mouse"). Both read <see cref="EditorToolKeys"/>.
/// </summary>
public partial class EditorWindow
{
    private Window? _shortcutSheet;

    /// <summary>Opens the list, or closes it when it is already open ("?" again closes it).</summary>
    private void ShowShortcutSheet()
    {
        if (_shortcutSheet is { IsVisible: true })
        {
            _shortcutSheet.Close();
            return;
        }
        var (dialog, body, footer) = ThemedMessageDialog.Build(this, "Keyboard shortcuts",
            "Press ? or Ctrl+/ any time to open this list.");
        dialog.Width = 560;
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.Height = Math.Min(720, SystemParameters.WorkArea.Height * 0.85);
        dialog.ResizeMode = ResizeMode.CanResize;

        var list = new StackPanel();
        // Survey KeyboardShortcutsOverlay: "Click a key field, then press your new shortcut."
        var hint = new TextBlock
        {
            Text = "Click a tool's key, then press the new one (a letter, alone or with Shift).",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        list.Children.Add(hint);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "TextErrorBrush");
        list.Children.Add(error);
        _keyFieldEditing = null;
        foreach (ShortcutSection section in EditorShortcutSheet.Sections())
        {
            var heading = new TextBlock
            {
                Text = section.Title.ToUpperInvariant(),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 12, 0, 6),
            };
            heading.SetResourceReference(TextBlock.FontSizeProperty, "TextCaption");
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            list.Children.Add(heading);
            foreach (ShortcutRow row in section.Rows)
            {
                var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock { Text = row.Label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                grid.Children.Add(label);
                FrameworkElement keys = row.Assignable ? KeyField(row, error) : KeyCap(row.Keys);
                keys.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(keys, 1);
                grid.Children.Add(keys);
                list.Children.Add(grid);
            }
        }
        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = dialog.Height - 170,
            Margin = new Thickness(0, 0, 0, 12),
        };
        body.Children.Insert(body.Children.IndexOf(footer), scroll);
        dialog.SizeChanged += (_, _) => scroll.Height = Math.Max(120, dialog.ActualHeight - 170);
        var close = ThemedMessageDialog.AddButton(footer, "Close", primary: true, dialog.Close);
        close.IsDefault = true;
        close.IsCancel = true;
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (_keyFieldEditing is not null) return; // the key field owns the keys while it listens
            bool question = e.Key == Key.OemQuestion
                && (Keyboard.Modifiers == ModifierKeys.Shift || Keyboard.Modifiers == ModifierKeys.Control);
            if (question || e.Key == Key.Escape)
            {
                dialog.Close();
                e.Handled = true;
            }
        };
        dialog.Closed += (_, _) => { if (ReferenceEquals(_shortcutSheet, dialog)) _shortcutSheet = null; };
        _shortcutSheet = dialog;
        dialog.Show();
    }

    private Button? _keyFieldEditing;

    /// <summary>
    /// A tool's key as a field (Survey KeyboardShortcutsOverlay): click it, press a letter (or
    /// Shift+letter) and the tool answers to that key from now on, in the handler, the tooltips
    /// and this list. Esc or a click elsewhere leaves it as it was. A changed key gets a Reset.
    /// </summary>
    private FrameworkElement KeyField(ShortcutRow row, TextBlock error)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var field = new Button
        {
            Content = row.Keys,
            MinWidth = 96,
            Padding = new Thickness(8, 2, 8, 3),
            ToolTip = $"Change the key for {row.Label}",
            Focusable = true,
        };
        if (TryFindResource("PillButtonSecondary") is Style style) field.Style = style;
        System.Windows.Automation.AutomationProperties.SetName(field, $"Shortcut for {row.Label}");
        var reset = new Button
        {
            Content = "Reset",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 3),
            Visibility = Overridden(row.Label) ? Visibility.Visible : Visibility.Collapsed,
        };
        if (TryFindResource("PillButtonSecondary") is Style ghost) reset.Style = ghost;
        System.Windows.Automation.AutomationProperties.SetName(reset, $"Reset shortcut for {row.Label}");

        void ShowError(string? text)
        {
            error.Text = text ?? "";
            error.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
        void Stop()
        {
            if (ReferenceEquals(_keyFieldEditing, field)) _keyFieldEditing = null;
            field.Content = EditorToolKeys.All.First(k => k.Label == row.Label).Badge;
        }
        void Changed()
        {
            _settings?.Save();
            AddKeysToToolTips();
            reset.Visibility = Overridden(row.Label) ? Visibility.Visible : Visibility.Collapsed;
            Stop();
        }
        field.Click += (_, _) =>
        {
            _keyFieldEditing = field;
            field.Content = "Press a key…";
            ShowError(null);
        };
        field.LostKeyboardFocus += (_, _) => Stop();
        field.PreviewKeyDown += (_, e) =>
        {
            if (!ReferenceEquals(_keyFieldEditing, field)) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            e.Handled = true;
            if (key == Key.Escape) { Stop(); return; }
            if (_settings is null) { Stop(); return; }
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            {
                ShowError("Press a letter, alone or with Shift.");
                return;
            }
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            string? problem = EditorToolKeys.Assign(_settings.Current.EditorToolKeyBindings, row.Label, key, shift);
            ShowError(problem);
            if (problem is null) Changed();
        };
        reset.Click += (_, _) =>
        {
            if (_settings is null) return;
            string? problem = EditorToolKeys.Reset(_settings.Current.EditorToolKeyBindings, row.Label);
            ShowError(problem);
            if (problem is null) Changed();
        };
        panel.Children.Add(field);
        panel.Children.Add(reset);
        return panel;
    }

    private bool Overridden(string label) =>
        _settings?.Current.EditorToolKeyBindings.ContainsKey(label) == true;

    /// <summary>A keycap chip: the key text on a raised rounded box.</summary>
    private static Border KeyCap(string keys)
    {
        var text = new TextBlock { Text = keys, FontWeight = FontWeights.SemiBold };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        text.SetResourceReference(TextBlock.FontSizeProperty, "TextCaption");
        var cap = new Border
        {
            Child = text,
            Padding = new Thickness(6, 1, 6, 2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cap.SetResourceReference(Border.BackgroundProperty, "ChromeControlBrush");
        cap.SetResourceReference(Border.BorderBrushProperty, "ChromeHairlineBrush");
        return cap;
    }

    /// <summary>A tooltip that reads "Rectangle  [R]", the way Drawboard and Survey print it.</summary>
    internal static object ToolTipWithKey(string label, string? badge)
    {
        if (string.IsNullOrEmpty(badge)) return label;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var cap = KeyCap(badge);
        cap.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(cap);
        return row;
    }

    /// <summary>
    /// Puts each tool's key into its button's tooltip. Runs once the window is loaded, from the
    /// labels the XAML already gives (so the words stay the toolbar's own).
    /// </summary>
    private readonly Dictionary<RadioButton, string> _toolTipLabels = new();

    private void AddKeysToToolTips()
    {
        foreach (RadioButton rb in ToolButtons())
        {
            if (rb.Tag is not string tag || !Enum.TryParse(tag, out EditorTool tool)) continue;
            // The XAML's own words are kept on the first pass, so a changed key can redo the tip.
            if (!_toolTipLabels.TryGetValue(rb, out string? label))
            {
                if (rb.ToolTip is not string fromXaml) continue;
                _toolTipLabels[rb] = label = fromXaml;
            }
            rb.ToolTip = ToolTipWithKey(label, EditorToolKeys.BadgeFor(tool));
        }
        if (BtnUndo?.ToolTip is string undo) BtnUndo.ToolTip = ToolTipWithKey(undo, "Ctrl+Z");
        if (BtnRedo?.ToolTip is string redo) BtnRedo.ToolTip = ToolTipWithKey(redo, "Ctrl+Y");
        KeyTip(CropUtilityBtn, EditorToolKeys.BadgeFor(EditorTool.Crop));
        KeyTip(AddImageBtn, ActionBadge(EditorToolKeys.ActionAddImage));
        KeyTip(AddBackgroundBtn, ActionBadge(EditorToolKeys.ActionBackground));
    }

    private readonly Dictionary<FrameworkElement, string> _actionTipLabels = new();

    private void KeyTip(FrameworkElement? button, string? badge)
    {
        if (button is null) return;
        if (!_actionTipLabels.TryGetValue(button, out string? label))
        {
            if (button.ToolTip is not string fromXaml) return;
            _actionTipLabels[button] = label = fromXaml;
        }
        button.ToolTip = ToolTipWithKey(label, badge);
    }

    private static string? ActionBadge(string action) =>
        EditorToolKeys.All.FirstOrDefault(k => k.Action == action)?.Badge;
}
