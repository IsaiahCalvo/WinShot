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
                var keys = KeyCap(row.Keys);
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
    private void AddKeysToToolTips()
    {
        foreach (RadioButton rb in ToolButtons())
        {
            if (rb.Tag is not string tag || !Enum.TryParse(tag, out EditorTool tool)) continue;
            if (rb.ToolTip is not string label) continue;
            rb.ToolTip = ToolTipWithKey(label, EditorToolKeys.BadgeFor(tool));
        }
        if (BtnUndo?.ToolTip is string undo) BtnUndo.ToolTip = ToolTipWithKey(undo, "Ctrl+Z");
        if (BtnRedo?.ToolTip is string redo) BtnRedo.ToolTip = ToolTipWithKey(redo, "Ctrl+Y");
        if (CropUtilityBtn?.ToolTip is string crop) CropUtilityBtn.ToolTip = ToolTipWithKey(crop, "Shift+C");
        if (AddImageBtn?.ToolTip is string image) AddImageBtn.ToolTip = ToolTipWithKey(image, "I");
        if (AddBackgroundBtn?.ToolTip is string bg) AddBackgroundBtn.ToolTip = ToolTipWithKey(bg, "B");
    }
}
