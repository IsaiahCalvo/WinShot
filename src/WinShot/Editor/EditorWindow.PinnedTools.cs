using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WinShot.Core;

namespace WinShot.Editor;

/// <summary>
/// Survey's pinned tools dock (PinnedToolsDock.jsx) on the editor canvas, laid out as Survey's
/// is: the pins, then "+" (Pin a tool), a rule, "…" (the dock menu), and the grip last. "+"
/// opens a menu that pins the tool in use with its current colour, size, fill and line style,
/// or any tool from the Draw / Shapes / Text lists; clicking a pin arms that tool with those
/// settings; right-click a pin to duplicate it, move it to the start or unpin it. "…" docks it
/// left, right or bottom, and so does dragging the grip. Pins are saved for every editor
/// window. The pure rules are in <see cref="PinnedTools"/>.
/// </summary>
public partial class EditorWindow
{
    private Border? _pinDock;
    private StackPanel? _pinDockRow;
    private bool _pinGripDragging;

    private PinDockSide PinSide => PinnedTools.ParseSide(_settings?.Current.EditorPinDock);

    private List<PinnedToolSetting> Pins => _settings?.Current.EditorPinnedTools ?? new List<PinnedToolSetting>();

    private void WirePinnedDock()
    {
        _pinDockRow = new StackPanel();
        _pinDock = new Border
        {
            Padding = new Thickness(3),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = (Brush)FindResource("ChromeBgBrush"),
            BorderBrush = (Brush)FindResource("ChromeHairlineBrush"),
            Child = _pinDockRow,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 4, Direction = 270, Opacity = 0.3 },
        };
        System.Windows.Automation.AutomationProperties.SetName(_pinDock, "Pinned tools");
        _pinDock.MouseDown += (_, e) => e.Handled = true;
        _pinDock.PreviewMouseRightButtonUp += (_, e) =>
        {
            // A pin's own menu opens on its button; anywhere else on the dock does nothing.
            if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is null) e.Handled = true;
        };
        OverlayCanvas.Children.Add(_pinDock);
        RebuildPinDock();
        Viewport.SizeChanged += (_, _) => PlacePinDock();
    }

    private bool IsPinDockSource(object? source) =>
        _pinDock is not null && source is DependencyObject d && (ReferenceEquals(_pinDock, d) || _pinDock.IsAncestorOf(d));

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T hit) return hit;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void RebuildPinDock()
    {
        if (_pinDock is null || _pinDockRow is null) return;
        _pinDockRow.Children.Clear();
        bool horizontal = PinSide == PinDockSide.Bottom;
        _pinDockRow.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;

        foreach (PinnedToolSetting pin in Pins.ToList())
            if (PinnedTools.Kind(pin.Kind) is PinKind kind)
                _pinDockRow.Children.Add(BuildPinButton(pin, kind));

        Button add = DockButton("SurveyIcon.Plus", "Pin a tool");
        add.Click += (_, e) => { e.Handled = true; OpenPinAddMenu(add); };
        _pinDockRow.Children.Add(add);

        _pinDockRow.Children.Add(new Border
        {
            Background = (Brush)FindResource("ChromeHairlineBrush"),
            Width = horizontal ? 1 : 18,
            Height = horizontal ? 18 : 1,
            Margin = horizontal ? new Thickness(3, 0, 3, 0) : new Thickness(0, 3, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Button more = DockButton("SurveyIcon.MoreHorizontal", "Pinned tools menu");
        more.Click += (_, e) => { e.Handled = true; OpenPinDockMenu(more); };
        _pinDockRow.Children.Add(more);

        _pinDockRow.Children.Add(BuildPinGrip());
        PlacePinDock();
    }

    private Button DockButton(string icon, string name)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("CompactToolIconButton"),
            Content = new ContentControl { Content = FindResource(icon), IsHitTestVisible = false },
            ToolTip = name,
            Focusable = false,
            Margin = new Thickness(1),
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, name);
        return btn;
    }

    private ContextMenu DockMenu(UIElement anchor)
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu"),
            PlacementTarget = anchor,
            Placement = PinSide switch
            {
                PinDockSide.Left => System.Windows.Controls.Primitives.PlacementMode.Right,
                PinDockSide.Right => System.Windows.Controls.Primitives.PlacementMode.Left,
                _ => System.Windows.Controls.Primitives.PlacementMode.Top,
            },
        };
        return menu;
    }

    private MenuItem DockItem(string header, Action click, string? icon = null, bool? check = null)
    {
        var item = new MenuItem { Header = header, Style = (Style)FindResource("DarkMenuItem") };
        if (check is bool on) { item.IsCheckable = true; item.IsChecked = on; }
        else if (icon is not null) item.Icon = new ContentControl { Content = FindResource(icon), Width = 14, Height = 14 };
        item.Click += (_, _) => click();
        return item;
    }

    private static MenuItem DockHeading(string text) => new()
    {
        Header = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold, Opacity = 0.6 },
        IsEnabled = false,
        Focusable = false,
    };

    /// <summary>Survey's "Pin a tool" menu: the tool in use first, then every pinnable tool by group.</summary>
    private void OpenPinAddMenu(UIElement anchor)
    {
        ContextMenu menu = DockMenu(anchor);
        if (CurrentToolAsPin() is PinnedToolSetting current)
        {
            menu.Items.Add(DockItem($"Pin the tool in use ({PinnedTools.Describe(current)})", PinCurrentTool, "SurveyIcon.Pin"));
            menu.Items.Add(new Separator());
        }
        foreach (string group in PinnedTools.MenuGroups)
        {
            menu.Items.Add(DockHeading(group));
            foreach (PinKind kind in PinnedTools.Kinds.Where(k => PinnedTools.Group(k) == group))
            {
                PinKind k = kind;
                menu.Items.Add(DockItem(kind.Label, () => AddPin(MakePinFor(k)), kind.Icon));
            }
        }
        menu.IsOpen = true;
    }

    /// <summary>Survey's dock menu: where it docks.</summary>
    private void OpenPinDockMenu(UIElement anchor)
    {
        ContextMenu menu = DockMenu(anchor);
        menu.Items.Add(DockHeading("Dock"));
        foreach (PinDockSide side in new[] { PinDockSide.Left, PinDockSide.Bottom, PinDockSide.Right })
        {
            PinDockSide target = side;
            menu.Items.Add(DockItem(side.ToString(), () => MovePinDock(target), check: PinSide == side));
        }
        menu.IsOpen = true;
    }

    private void MovePinDock(PinDockSide side)
    {
        if (_settings is null || side == PinSide) return;
        _settings.Current.EditorPinDock = side.ToString();
        _settings.Save();
        RebuildPinDock();
    }

    /// <summary>A pin of the given kind with the toolbar's colour, fill and line style and that tool's own size.</summary>
    private PinnedToolSetting MakePinFor(PinKind kind)
    {
        Color fill = CurrentFillBrush();
        string? fillHex = _fillMode == ShapeFillMode.None || fill.A == 0 ? null : AnnotationStyle.ToRgbHex(fill);
        return PinnedTools.Make(kind, AnnotationStyle.ToRgbHex(_color), _opacity * 100, SizeForTool(kind.Tool),
            fillHex, fill.A / 2.55, _lineStyle);
    }

    private void AddPin(PinnedToolSetting pin)
    {
        if (_settings is null) return;
        if (Pins.Any(p => PinnedTools.SameAs(p, pin))) return;
        _settings.Current.EditorPinnedTools.Add(pin);
        _settings.Save();
        RebuildPinDock();
    }

    private UIElement BuildPinGrip()
    {
        bool horizontal = PinSide == PinDockSide.Bottom;
        var grip = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Drag to dock left, right or bottom",
            Child = new ContentControl
            {
                Content = FindResource("SurveyIcon.GripDots"),
                IsHitTestVisible = false,
                Foreground = (Brush)FindResource("ChromeTextMutedBrush"),
                // Survey GripIcon: two columns of three dots on a side dock, three columns of
                // two along the bottom.
                LayoutTransform = horizontal ? new RotateTransform(90) : Transform.Identity,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(grip, "Move pinned tools");
        grip.MouseLeftButtonDown += (_, e) =>
        {
            _pinGripDragging = grip.CaptureMouse();
            e.Handled = true;
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!_pinGripDragging) return;
            _pinGripDragging = false;
            grip.ReleaseMouseCapture();
            Point p = e.GetPosition(Viewport);
            MovePinDock(PinnedTools.NearestSide(p.X, p.Y, Viewport.ActualWidth, Viewport.ActualHeight));
            e.Handled = true;
        };
        grip.LostMouseCapture += (_, _) => _pinGripDragging = false;
        return grip;
    }

    private Button BuildPinButton(PinnedToolSetting pin, PinKind kind)
    {
        Brush face = pin.StrokeColor is string hex && TryParseColor(hex, out Color c)
            ? new SolidColorBrush(kind.Tool == EditorTool.Step && pin.FillColor is string f && TryParseColor(f, out Color fc) ? fc : c)
            : (Brush)FindResource("ChromeTextBrush");
        var glyph = new Grid { Width = 22, Height = 22 };
        glyph.Children.Add(new ContentControl
        {
            Content = FindResource(kind.Icon),
            Foreground = face,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        string badge = PinnedTools.Badge(pin);
        if (badge.Length > 0)
        {
            glyph.Children.Add(new TextBlock
            {
                Text = badge,
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("ChromeTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -3, -3),
                IsHitTestVisible = false,
            });
        }
        string description = PinnedTools.Describe(pin);
        var btn = new Button
        {
            Style = (Style)FindResource("CompactToolIconButton"),
            Content = glyph,
            ToolTip = description,
            Focusable = false,
            Margin = new Thickness(1),
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, description);
        btn.Click += (_, e) => { e.Handled = true; ArmPin(pin); };

        // Survey's pin menu: Duplicate, Move to start (not on the first pin), Unpin.
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add(DockItem("Duplicate", () => DuplicatePin(pin), "SurveyIcon.Duplicate"));
            if (Pins.IndexOf(pin) > 0)
                menu.Items.Add(DockItem("Move to start", () => MovePinToStart(pin), "SurveyIcon.ChevronUp"));
            menu.Items.Add(DockItem("Unpin", () => RemovePin(pin), "SurveyIcon.Close"));
        };
        menu.Items.Add(new MenuItem { Style = (Style)FindResource("DarkMenuItem") }); // placeholder so the menu opens; Opened refills it
        btn.ContextMenu = menu;
        return btn;
    }

    private void PlacePinDock()
    {
        if (_pinDock is null) return;
        _pinDock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = _pinDock.DesiredSize;
        double w = Viewport.ActualWidth, h = Viewport.ActualHeight;
        const double margin = 10;
        // Clear of the group strip that overlays the top of the canvas.
        double topInset = EditorGroupBar is { Visibility: Visibility.Visible } bar ? bar.ActualHeight : 0;
        (double left, double top) = PinSide switch
        {
            PinDockSide.Right => (w - size.Width - margin, Math.Max(topInset + margin, (h - size.Height) / 2)),
            PinDockSide.Bottom => ((w - size.Width) / 2, h - size.Height - margin),
            _ => (margin, Math.Max(topInset + margin, (h - size.Height) / 2)),
        };
        Canvas.SetLeft(_pinDock, Math.Max(0, left));
        Canvas.SetTop(_pinDock, Math.Max(0, top));
    }

    /// <summary>A pin from what the toolbar shows for the armed tool right now; null for tools that can't be pinned.</summary>
    private PinnedToolSetting? CurrentToolAsPin()
    {
        if (PinnedTools.KindForTool(_tool, _lineStyle) is not PinKind kind) return null;
        Color fill = CurrentFillBrush();
        string? fillHex = _fillMode == ShapeFillMode.None || fill.A == 0 ? null : AnnotationStyle.ToRgbHex(fill);
        return PinnedTools.Make(kind, AnnotationStyle.ToRgbHex(_color), _opacity * 100, ActiveSize(),
            fillHex, fill.A / 2.55, _lineStyle);
    }

    private void PinCurrentTool()
    {
        if (_settings is null) return;
        if (CurrentToolAsPin() is not PinnedToolSetting pin)
        {
            App.Toast("Pick a drawing tool first",
                "Arm a pen, shape, line, counter, text box or callout, set it up, then press + to pin it.");
            return;
        }
        if (Pins.Any(p => PinnedTools.SameAs(p, pin))) return;
        _settings.Current.EditorPinnedTools.Add(pin);
        _settings.Save();
        RebuildPinDock();
    }

    /// <summary>Survey: a copy of the pin, right after it (a second pen to restyle).</summary>
    private void DuplicatePin(PinnedToolSetting pin)
    {
        if (_settings is null) return;
        var list = _settings.Current.EditorPinnedTools;
        int i = list.IndexOf(pin);
        if (i < 0) return;
        list.Insert(i + 1, new PinnedToolSetting
        {
            Kind = pin.Kind,
            StrokeColor = pin.StrokeColor,
            StrokeOpacity = pin.StrokeOpacity,
            StrokeWidth = pin.StrokeWidth,
            FillColor = pin.FillColor,
            FillOpacity = pin.FillOpacity,
            LineStyle = pin.LineStyle,
        });
        _settings.Save();
        RebuildPinDock();
    }

    private void MovePinToStart(PinnedToolSetting pin)
    {
        if (_settings is null) return;
        var list = _settings.Current.EditorPinnedTools;
        if (!list.Remove(pin)) return;
        list.Insert(0, pin);
        _settings.Save();
        RebuildPinDock();
    }

    private void RemovePin(PinnedToolSetting pin)
    {
        if (_settings is null) return;
        _settings.Current.EditorPinnedTools.Remove(pin);
        _settings.Save();
        RebuildPinDock();
    }

    /// <summary>Arms the pin's tool and sets every setting it holds, as if picked in the toolbar.</summary>
    private void ArmPin(PinnedToolSetting pin)
    {
        if (PinnedTools.Kind(pin.Kind) is not PinKind kind) return;
        Select(null);
        CheckToolButton(kind.Tool);
        if (pin.StrokeColor is string hex && TryParseColor(hex, out Color stroke))
            _color = AnnotationStyle.Opaque(stroke);
        if (pin.StrokeOpacity is double opacity)
            SetStrokeOpacityPercent(opacity);
        if (pin.StrokeWidth is double width)
            ApplySize(width);
        if (pin.FillColor is string fillHex && TryParseColor(fillHex, out Color fill))
        {
            _fillColor = AnnotationStyle.Opaque(fill);
            _fillOpacity = (pin.FillOpacity ?? 100) / 100.0;
            _fillMode = _fillOpacity <= 0 ? ShapeFillMode.None
                : Math.Abs(_fillOpacity - 0.25) < 0.02 ? ShapeFillMode.Quarter
                : ShapeFillMode.Solid;
        }
        else if (kind.Tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Polygon or EditorTool.Callout)
        {
            _fillMode = ShapeFillMode.None;
            _fillOpacity = 0;
        }
        if (pin.LineStyle is string style)
            ApplyQuickBarLineStyle(AnnotationStyle.ParseLineStyle(style));
        SyncColorWellIndicator();
        UpdateContextPanels();
    }
}
