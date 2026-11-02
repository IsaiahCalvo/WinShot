using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinShot.Editor;

/// <summary>
/// Survey's selection quick bar (SelectionQuickBar.jsx) on WinShot's own chrome: pick a mark and
/// a small calm bar appears just above it with Colour, Width, Line style, Duplicate, Delete and
/// More. Every control is a shortcut onto what already exists — the toolbar's own colour, width
/// and line-style writes, the Ctrl+D duplicate, the Delete key and the right-click menu — so a
/// change made here is the same restyle and the same Undo step as the same change made in the
/// toolbar. The layout rules are in <see cref="SelectionQuickBarLayout"/>.
/// </summary>
public partial class EditorWindow
{
    // Survey's quick colour discs (quickStylePresets.js QUICK_COLOURS): red, blue, black.
    private static readonly Color[] QuickBarColours =
    {
        Color.FromRgb(0xFF, 0x00, 0x00),
        Color.FromRgb(0x00, 0x00, 0xFF),
        Color.FromRgb(0x00, 0x00, 0x00),
    };

    // Survey QUICK_BAR_WIDTHS.
    internal static readonly int[] QuickBarWidths = { 1, 2, 3, 4, 6, 8 };

    private Border? _quickBar;
    private StackPanel? _quickBarColourGroup;
    private Button? _quickBarPaintBtn;
    private Ellipse? _quickBarPaintDisc;
    private Button? _quickBarWidthBtn;
    private TextBlock? _quickBarWidthText;
    private Button? _quickBarStyleBtn;
    private Button? _quickBarDuplicateBtn;
    private Button? _quickBarDeleteBtn;
    private Button? _quickBarMoreBtn;
    private Rectangle? _quickBarDividerA;
    private Rectangle? _quickBarDividerB;
    private bool _quickBarQueued;
    private IInputElement? _colorPickerHomeTarget;

    private void WireSelectionQuickBar()
    {
        _quickBar = BuildQuickBar();
        OverlayCanvas.Children.Add(_quickBar);
        // Zoom and scroll move the selection on screen without changing it.
        ViewTranslate.Changed += (_, _) => QueueQuickBarUpdate();
        ViewScale.Changed += (_, _) => QueueQuickBarUpdate();
        Viewport.SizeChanged += (_, _) => QueueQuickBarUpdate();
        // The bar hides at once while a selection is moved, resized or rotated, and comes back
        // as soon as the gesture ends (Drawboard's timing: no fade).
        Viewport.PreviewMouseUp += (_, _) => QueueQuickBarUpdate();
        ColorPickerPopup.Closed += (_, _) =>
        {
            if (_colorPickerHomeTarget is UIElement home)
            {
                ColorPickerPopup.PlacementTarget = home;
                _colorPickerHomeTarget = null;
            }
        };
    }

    private bool IsQuickBarSource(object? source) =>
        _quickBar is not null && source is DependencyObject d && (ReferenceEquals(_quickBar, d) || _quickBar.IsAncestorOf(d));

    private Border BuildQuickBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _quickBarColourGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (Color c in QuickBarColours)
        {
            Color colour = c;
            var disc = new Grid { Width = 16, Height = 16 };
            disc.Children.Add(new Ellipse { Fill = new SolidColorBrush(colour) });
            disc.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 });
            string name = AnnotationStyle.ToRgbHex(colour) switch
            {
                "#FF0000" => "Red",
                "#0000FF" => "Blue",
                _ => "Black",
            };
            var btn = QuickBarButton(disc, name, () => SetCurrentColor(colour));
            _quickBarColourGroup.Children.Add(btn);
        }
        _quickBarPaintDisc = new Ellipse { Width = 18, Height = 18, StrokeThickness = 1, Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)) };
        _quickBarPaintBtn = QuickBarButton(_quickBarPaintDisc, "More colors", OpenQuickBarColourPicker);
        _quickBarColourGroup.Children.Add(_quickBarPaintBtn);
        row.Children.Add(_quickBarColourGroup);

        _quickBarDividerA = Divider();
        row.Children.Add(_quickBarDividerA);

        _quickBarWidthText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var widthContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        widthContent.Children.Add(new Rectangle { Width = 14, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Fill = (Brush)FindResource("ChromeTextBrush") });
        widthContent.Children.Add(_quickBarWidthText);
        _quickBarWidthBtn = QuickBarButton(widthContent, "Line width", OpenQuickBarWidthMenu);
        _quickBarWidthBtn.Width = double.NaN;
        _quickBarWidthBtn.Padding = new Thickness(6, 0, 6, 0);
        row.Children.Add(_quickBarWidthBtn);

        var styleGlyph = new Line { X1 = 0, Y1 = 8, X2 = 16, Y2 = 8, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 1.5 }, Stroke = (Brush)FindResource("ChromeTextBrush") };
        _quickBarStyleBtn = QuickBarButton(new Grid { Width = 16, Height = 16, Children = { styleGlyph } }, "Line style", OpenQuickBarStyleMenu);
        row.Children.Add(_quickBarStyleBtn);

        _quickBarDividerB = Divider();
        row.Children.Add(_quickBarDividerB);

        _quickBarDuplicateBtn = QuickBarButton(Icon("SurveyIcon.Copy"), "Duplicate (Ctrl+D)", DuplicateSelectedAnnotations);
        _quickBarDeleteBtn = QuickBarButton(Icon("SurveyIcon.Trash"), "Delete (Del)", DeleteSelected);
        _quickBarMoreBtn = QuickBarButton(Icon("SurveyIcon.More"), "More", OpenQuickBarMoreMenu);
        row.Children.Add(_quickBarDuplicateBtn);
        row.Children.Add(_quickBarDeleteBtn);
        row.Children.Add(_quickBarMoreBtn);

        var bar = new Border
        {
            Height = 36,
            Padding = new Thickness(2, 0, 2, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = (Brush)FindResource("ChromeBgBrush"),
            BorderBrush = (Brush)FindResource("ChromeHairlineBrush"),
            Visibility = Visibility.Collapsed,
            Child = row,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 4, Direction = 270, Opacity = 0.3 },
        };
        System.Windows.Automation.AutomationProperties.SetName(bar, "Selection actions");
        // A click on the bar must never reach the canvas underneath, where it would deselect.
        bar.PreviewMouseRightButtonUp += (_, e) => e.Handled = true;
        bar.MouseDown += (_, e) => e.Handled = true;
        return bar;

        Rectangle Divider() => new() { Style = (Style)FindResource("ToolbarDivider"), Margin = new Thickness(4, 0, 4, 0) };
        UIElement Icon(string key) => new ContentControl { Content = FindResource(key), IsHitTestVisible = false };
    }

    private Button QuickBarButton(UIElement content, string name, Action act)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("CompactToolIconButton"),
            Content = content,
            ToolTip = name,
            Focusable = false,
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, name);
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            act();
        };
        return btn;
    }

    /// <summary>Re-places the bar after the current input settles (several events can land at once).</summary>
    private void QueueQuickBarUpdate()
    {
        if (_quickBarQueued || _closed || _quickBar is null) return;
        _quickBarQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _quickBarQueued = false;
            UpdateQuickBar();
        }, DispatcherPriority.Background);
    }

    private IReadOnlyList<string?> SelectedTypes() =>
        SelectionList()
            .Where(el => AnnotationCanvas.Children.Contains(el))
            .Select(el => (el as FrameworkElement)?.Tag is AnnotationData m ? m.Type : null)
            .ToArray();

    /// <summary>Whether a pointer gesture is moving, resizing or turning the selection right now.</summary>
    private bool SelectionGestureActive() =>
        _movingSelection || _activeHandle >= 0 || _dragging || _marqueeSelecting || _panning
        || _brushAdjust is not null
        || (Viewport.IsMouseCaptured && Mouse.LeftButton == MouseButtonState.Pressed);

    private void UpdateQuickBar()
    {
        if (_quickBar is null || _closed) return;
        QuickBarPlan? plan = _tool == EditorTool.Select && _activeText is null && !_sourceOperationActive
                             && !_adjustingCrop && !SelectionGestureActive()
            ? SelectionQuickBarLayout.Plan(SelectedTypes(), SelectionLocked())
            : null;
        if (plan is not QuickBarPlan p)
        {
            HideQuickBar();
            return;
        }

        _quickBarColourGroup!.Visibility = p.Colour ? Visibility.Visible : Visibility.Collapsed;
        _quickBarWidthBtn!.Visibility = p.Width ? Visibility.Visible : Visibility.Collapsed;
        _quickBarStyleBtn!.Visibility = p.Style ? Visibility.Visible : Visibility.Collapsed;
        _quickBarDividerA!.Visibility = p.Colour && (p.Width || p.Style) ? Visibility.Visible : Visibility.Collapsed;
        _quickBarDividerB!.Visibility = p.Colour || p.Width || p.Style ? Visibility.Visible : Visibility.Collapsed;
        _quickBarDeleteBtn!.Visibility = p.Delete ? Visibility.Visible : Visibility.Collapsed;
        _quickBarPaintDisc!.Fill = new SolidColorBrush(CurrentStrokeBrush());
        _quickBarWidthText!.Text = Math.Round(ActiveSize()).ToString(System.Globalization.CultureInfo.InvariantCulture);

        Rect? frame = QuickBarSelectionFrame();
        if (frame is not Rect sel)
        {
            HideQuickBar();
            return;
        }
        var (keepOut, preferBelow) = QuickBarKeepOut(sel);

        _quickBar.Visibility = Visibility.Visible;
        _quickBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = _quickBar.DesiredSize;
        var bounds = new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight);
        var pos = SelectionQuickBarLayout.Position(sel, keepOut, preferBelow, bounds, size.Width, size.Height);
        if (pos is not var (left, top, _))
        {
            HideQuickBar();
            return;
        }
        Canvas.SetLeft(_quickBar, left);
        Canvas.SetTop(_quickBar, top);
    }

    private void HideQuickBar()
    {
        if (_quickBar is not null) _quickBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>The selection's on-screen box, handles included, in viewport pixels.</summary>
    private Rect? QuickBarSelectionFrame()
    {
        Rect union = Rect.Empty;
        foreach (UIElement el in SelectionList())
        {
            if (!AnnotationCanvas.Children.Contains(el)) continue;
            Rect b = GetCanvasBounds(el);
            if (b.IsEmpty) continue;
            union.Union(b);
        }
        if (union.IsEmpty) return null;
        Rect screen = AnnotationCanvas.TransformToVisual(OverlayCanvas).TransformBounds(union);
        // Room for the resize handles that sit on the frame.
        screen.Inflate(HandleScreenPx, HandleScreenPx);
        return screen;
    }

    /// <summary>
    /// The rotate grip and the spot its degree box opens in (selectionKeepOut): the bar never
    /// covers them, and takes the side away from the grip.
    /// </summary>
    private (IReadOnlyList<Rect> KeepOut, bool PreferBelow) QuickBarKeepOut(Rect selection)
    {
        if (_rotateThumb is not { Visibility: Visibility.Visible } thumb || !thumb.IsVisible)
            return (Array.Empty<Rect>(), false);
        Rect grip;
        try
        {
            grip = thumb.TransformToVisual(OverlayCanvas).TransformBounds(new Rect(thumb.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return (Array.Empty<Rect>(), false);
        }
        var centre = new Point(selection.X + selection.Width / 2, selection.Y + selection.Height / 2);
        var gripCentre = new Point(grip.X + grip.Width / 2, grip.Y + grip.Height / 2);
        Vector dir = gripCentre - centre;
        if (dir.Length < 0.001) dir = new Vector(0, -1);
        dir.Normalize();
        // RotationPill is 60 x 28 and sits 16 px past the grip (PlaceRotationPill).
        double reach = Math.Max(grip.Width, grip.Height) / 2 + 16 + 30;
        Point pillCentre = gripCentre + dir * reach;
        var pill = new Rect(pillCentre.X - 30, pillCentre.Y - 14, 60, 28);
        return (new[] { grip, pill }, gripCentre.Y < centre.Y - 0.5);
    }

    private void OpenQuickBarColourPicker()
    {
        if (_quickBarPaintBtn is null) return;
        _colorPickerHomeTarget ??= ColorPickerPopup.PlacementTarget;
        ColorPickerPopup.PlacementTarget = _quickBarPaintBtn;
        OnCustomSwatchClick(this, new RoutedEventArgs());
    }

    private void OpenQuickBarWidthMenu()
    {
        if (_quickBarWidthBtn is null) return;
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var itemStyle = (Style)FindResource("DarkMenuItem");
        int current = (int)Math.Round(ActiveSize());
        foreach (int w in QuickBarWidths)
        {
            int width = w;
            var bar = new Rectangle
            {
                Width = 40,
                Height = Math.Max(1, Math.Min(8, width)),
                RadiusX = 1,
                RadiusY = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)FindResource("ChromeTextBrush"),
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Grid { Width = 48, Height = 16, Children = { bar } });
            header.Children.Add(new TextBlock { Text = width.ToString(System.Globalization.CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center });
            var mi = new MenuItem { Header = header, Style = itemStyle, IsCheckable = true, IsChecked = width == current };
            mi.Click += (_, _) => ApplySize(width);
            menu.Items.Add(mi);
        }
        OpenQuickBarMenu(menu, _quickBarWidthBtn);
    }

    private void OpenQuickBarStyleMenu()
    {
        if (_quickBarStyleBtn is null) return;
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var itemStyle = (Style)FindResource("DarkMenuItem");
        bool cloud = SelectionQuickBarLayout.AllowsCloud(SelectedTypes());
        var styles = new List<(LineBorderStyle Style, string Label)>
        {
            (LineBorderStyle.Solid, "Solid"),
            (LineBorderStyle.Dashed, "Dashed"),
            (LineBorderStyle.Dotted, "Dotted"),
        };
        if (cloud) styles.Add((LineBorderStyle.Cloud, "Cloud"));
        foreach (var (style, label) in styles)
        {
            LineBorderStyle s = style;
            var mi = new MenuItem { Header = label, Style = itemStyle, IsCheckable = true, IsChecked = _lineStyle == s };
            mi.Click += (_, _) => ApplyQuickBarLineStyle(s);
            menu.Items.Add(mi);
        }
        OpenQuickBarMenu(menu, _quickBarStyleBtn);
    }

    /// <summary>The same write the toolbar's LINE STYLE dropdown makes, with the dropdown kept in step.</summary>
    private void ApplyQuickBarLineStyle(LineBorderStyle style)
    {
        _lineStyle = style;
        string tag = style.ToString().ToLowerInvariant();
        _syncingContext = true;
        try
        {
            foreach (object o in LineStyleBox.Items)
                if (o is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
                    LineStyleBox.SelectedItem = item;
        }
        finally
        {
            _syncingContext = false;
        }
        RestyleSelectedLineStyle(style);
        UpdateContextPanels();
        QueueQuickBarUpdate();
    }

    private void OpenQuickBarMoreMenu()
    {
        if (_quickBarMoreBtn is null) return;
        _annotationMenu ??= BuildAnnotationMenu();
        OpenQuickBarMenu(_annotationMenu, _quickBarMoreBtn);
    }

    private static void OpenQuickBarMenu(ContextMenu menu, UIElement anchor)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 4;
        menu.IsOpen = true;
    }
}
