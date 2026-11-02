using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Survey's mark menu (right-click on a mark, or the quick bar's More button), laid out as
/// Survey lays it out (utils/contextMenuLayout.js, hooks/useAnnotationContextMenu.jsx,
/// styles/annotationContextMenu.css):
///
///   [Duplicate] [Cut] [Copy] [Paste] [Delete]   icon row, 28 px buttons, 16 px glyphs
///   ─────────────
///   Lock / Unlock                               14 px lock glyph
///   Arrange                               ›     hover opens Bring to front / Bring forward /
///                                               Send backward / Send to back
///
/// An item that would do nothing is greyed out and ignores clicks (Survey owner 2026-10-06):
/// Paste with an empty clipboard, Cut / Delete / Arrange on a locked mark, and an order move
/// that would not change the stacking. Paste lands centred on the right-click point.
/// </summary>
public partial class EditorWindow
{
    /// <summary>Where the mark menu was opened, in canvas units; null from the quick bar.</summary>
    private Point? _markMenuAt;
    /// <summary>The open Arrange submenu of the last mark menu built (the render harness reads it).</summary>
    private Popup? _markArrangePopup;

    private const double MarkMenuRowHeight = 28;   // Survey --menu-item-h
    private const double MarkMenuIconButton = 28;  // .annotation-menu-icon
    private const double MarkMenuRowPadX = 10;     // --menu-item-pad-x
    private const double MarkMenuRowGap = 10;      // --menu-item-gap
    private const double MarkMenuCorner = 5;       // border-radius on icons and rows
    private const double MarkMenuFontSize = 13;    // --menu-font: 400 13px/16px

    private sealed class MarkMenuEntry
    {
        public required Border Box;
        public required Func<bool> Enabled;
        public required Brush Ink;
        public bool IsOn;
        public bool KeepFill;
    }

    private ContextMenu BuildAnnotationMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        // Each body element is its own item: the container is a bare presenter, so the
        // MenuItem chrome never draws and a click inside never closes the menu by itself.
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        var container = new Style(typeof(MenuItem));
        container.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(MenuItem)) { VisualTree = presenter }));
        container.Setters.Add(new Setter(MenuItem.StaysOpenOnClickProperty, true));
        container.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        menu.ItemContainerStyle = container;

        Brush primary = (Brush)FindResource("TextPrimaryBrush");
        Brush muted = (Brush)FindResource("TextSecondaryBrush");
        Brush danger = (Brush)FindResource("TextErrorBrush");
        Brush off = (Brush)FindResource("TextDisabledBrush");
        Brush hover = (Brush)FindResource("HoverFillBrush");
        var entries = new List<MarkMenuEntry>();

        var body = new StackPanel { MinWidth = 150 };
        TextElement.SetFontSize(body, MarkMenuFontSize);
        menu.Items.Add(body);

        void Paint(MarkMenuEntry e, bool hot)
        {
            e.Box.Background = e.IsOn && (hot || e.KeepFill) ? hover : Brushes.Transparent;
            e.Box.Cursor = e.IsOn ? Cursors.Hand : Cursors.No;
        }

        Popup? arrangePopup = null;
        MarkMenuEntry? arrangeEntry = null;
        void CloseArrange()
        {
            if (arrangePopup is not null) arrangePopup.IsOpen = false;
            if (arrangeEntry is not null) { arrangeEntry.KeepFill = false; Paint(arrangeEntry, arrangeEntry.Box.IsMouseOver); }
        }

        MarkMenuEntry Wire(Border box, Func<bool> enabled, Brush ink, Action? act, bool closesArrange = true)
        {
            var e = new MarkMenuEntry { Box = box, Enabled = enabled, Ink = ink };
            box.Background = Brushes.Transparent;
            box.MouseEnter += (_, _) => { if (closesArrange) CloseArrange(); Paint(e, true); };
            box.MouseLeave += (_, _) => Paint(e, false);
            box.MouseLeftButtonDown += (_, a) => a.Handled = true;
            box.MouseLeftButtonUp += (_, a) =>
            {
                a.Handled = true;
                if (!e.IsOn || act is null) return;
                menu.IsOpen = false;
                act();
            };
            entries.Add(e);
            return e;
        }

        ContentControl Glyph(string key, double size)
        {
            var icon = (FrameworkElement)FindResource(key);
            icon.Width = size;
            icon.Height = size;
            return new ContentControl { Content = icon, Focusable = false, IsTabStop = false, VerticalAlignment = VerticalAlignment.Center };
        }

        // ---- icon row (Survey QUICK_ACTION_KEYS order) ----
        var quick = new Grid();
        void Quick(string label, string icon, bool isDanger, Func<bool> enabled, Action act)
        {
            if (quick.ColumnDefinitions.Count > 0)
                quick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 2 });
            quick.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var glyph = Glyph(icon, 16);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            var box = new Border
            {
                Width = MarkMenuIconButton, Height = MarkMenuIconButton,
                CornerRadius = new CornerRadius(MarkMenuCorner), Child = glyph, ToolTip = label,
            };
            ToolTipService.SetPlacement(box, PlacementMode.Top);
            AutomationProperties(box, label);
            Grid.SetColumn(box, quick.ColumnDefinitions.Count - 1);
            quick.Children.Add(box);
            Wire(box, enabled, isDanger ? danger : primary, act);
        }
        bool Editable() => SelectionList().Any() && !SelectionLocked();
        Quick("Duplicate", "SurveyIcon.Duplicate", false, () => SelectionList().Any(), DuplicateSelectedAnnotations);
        Quick("Cut", "SurveyIcon.Scissors", false, Editable, () => { if (CopySelectedAnnotations()) DeleteSelected(); });
        Quick("Copy", "SurveyIcon.Copy", false, () => SelectionList().Any(), () => CopySelectedAnnotations());
        Quick("Paste", "SurveyIcon.Paste", false, () => AnnotationClipboard.Count > 0, () =>
        {
            if (_markMenuAt is Point at) PasteAnnotationsAt(at); else PasteAnnotations();
        });
        Quick("Delete", "SurveyIcon.Trash", true, Editable, DeleteSelected);
        body.Children.Add(quick);

        // ---- separator (1 px, margin 4 0) ----
        body.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), Background = (Brush)FindResource("BorderBrush") });

        // ---- rows ----
        (Border Box, TextBlock Label, DockPanel Line) Row(string label, string? icon)
        {
            var line = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            if (icon is not null)
            {
                var glyph = Glyph(icon, 14);
                glyph.Margin = new Thickness(0, 0, MarkMenuRowGap, 0);
                glyph.Foreground = muted;
                glyph.Tag = "row-icon";
                DockPanel.SetDock(glyph, Dock.Left);
                line.Children.Add(glyph);
            }
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var box = new Border
            {
                MinHeight = MarkMenuRowHeight, Padding = new Thickness(MarkMenuRowPadX, 0, MarkMenuRowPadX, 0),
                CornerRadius = new CornerRadius(MarkMenuCorner), Child = line,
            };
            AutomationProperties(box, label);
            return (box, text, line);
        }

        var (lockBox, lockText, lockLine) = Row("Lock", "SurveyIcon.Lock");
        lockLine.Children.Add(lockText);
        body.Children.Add(lockBox);
        Wire(lockBox, () => SelectionList().Any(), primary, ToggleSelectionLock);

        var (arrangeBox, arrangeText, arrangeLine) = Row("Arrange", "SurveyIcon.Layers");
        var chevron = Glyph("SurveyIcon.ChevronRight", 14);
        chevron.Foreground = muted;
        chevron.Tag = "row-icon";
        chevron.Margin = new Thickness(MarkMenuRowGap, 0, 0, 0);
        DockPanel.SetDock(chevron, Dock.Right);
        arrangeLine.Children.Add(chevron);
        arrangeLine.Children.Add(arrangeText);
        body.Children.Add(arrangeBox);

        // ---- Arrange submenu: top level with the menu body, 2 px to its right ----
        var subBody = new StackPanel { MinWidth = 140 };
        TextElement.SetFontSize(subBody, MarkMenuFontSize);
        var subCard = new Border { Style = (Style)FindResource("PopoverCard"), Padding = new Thickness(4), Child = subBody };
        arrangePopup = new Popup
        {
            Child = subCard, AllowsTransparency = true, StaysOpen = true,
            PlacementTarget = body, Placement = PlacementMode.Right,
            HorizontalOffset = 2, VerticalOffset = -subCard.Margin.Top,
        };
        _markArrangePopup = arrangePopup;
        var subEntries = new List<MarkMenuEntry>();
        void Sub(string label, ZMove move)
        {
            var (box, text, line) = Row(label, null);
            line.Children.Add(text);
            subBody.Children.Add(box);
            subEntries.Add(Wire(box, () => !SelectionLocked() && ReorderCore(move, apply: false), primary,
                () => ReorderSelected(move), closesArrange: false));
        }
        Sub("Bring to front", ZMove.Front);
        Sub("Bring forward", ZMove.Forward);
        Sub("Send backward", ZMove.Backward);
        Sub("Send to back", ZMove.Back);
        arrangeEntry = Wire(arrangeBox, () => subEntries.Any(s => s.Enabled()), primary, null, closesArrange: false);
        void OpenArrange()
        {
            if (!arrangeEntry.IsOn) return;
            arrangeEntry.KeepFill = true;
            Paint(arrangeEntry, true);
            arrangePopup.IsOpen = true;
        }
        arrangeBox.MouseEnter += (_, _) => OpenArrange();
        arrangeBox.MouseLeftButtonUp += (_, _) => OpenArrange();

        void Refresh()
        {
            lockText.Text = SelectionLocked() ? "Unlock" : "Lock";
            foreach (var e in entries)
            {
                e.IsOn = e.Enabled();
                Brush ink = e.IsOn ? e.Ink : off;
                // The words take the row ink; a row's own glyphs stay muted unless it is off.
                if (e.Box.Child is ContentControl cc) cc.Foreground = ink;
                else if (e.Box.Child is Panel p)
                    foreach (UIElement child in p.Children)
                    {
                        if (child is TextBlock t) t.Foreground = ink;
                        else if (child is ContentControl g) g.Foreground = e.IsOn ? muted : off;
                    }
                e.KeepFill = false;
                Paint(e, false);
            }
        }
        menu.Opened += (_, _) => Refresh();
        menu.Closed += (_, _) => { CloseArrange(); _markMenuAt = null; };
        Refresh();
        return menu;

        static void AutomationProperties(FrameworkElement el, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(el, name);
    }

    /// <summary>Survey pasteAnnotationAt: the copies land centred on <paramref name="at"/>.</summary>
    private void PasteAnnotationsAt(Point at)
    {
        if (AnnotationClipboard.Count == 0) return;
        if (AnnotationClipboardBounds.IsEmpty) { PasteAnnotations(); return; }
        Point centre = new(
            AnnotationClipboardBounds.X + AnnotationClipboardBounds.Width / 2,
            AnnotationClipboardBounds.Y + AnnotationClipboardBounds.Height / 2);
        PlaceCopies(AnnotationClipboard, at - centre);
    }
}
