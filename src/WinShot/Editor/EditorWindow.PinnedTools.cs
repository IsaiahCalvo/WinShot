using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WinShot.Core;

namespace WinShot.Editor;

/// <summary>
/// Survey's pinned tools dock (PinnedToolsDock.jsx) on the editor canvas. "+" pins the armed tool
/// with its current colour, size, fill and line style; clicking a pin arms that tool with those
/// settings. Right-click a pin to replace or remove it. The dock sits on the left, right or bottom
/// of the canvas and is dragged between them by its grip. Pins are saved for every editor window.
/// The pure rules are in <see cref="PinnedTools"/>.
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
        _pinDockRow.Orientation = PinSide == PinDockSide.Bottom ? Orientation.Horizontal : Orientation.Vertical;

        _pinDockRow.Children.Add(BuildPinGrip());
        foreach (PinnedToolSetting pin in Pins.ToList())
            if (PinnedTools.Kind(pin.Kind) is PinKind kind)
                _pinDockRow.Children.Add(BuildPinButton(pin, kind));

        var add = new Button
        {
            Style = (Style)FindResource("CompactToolIconButton"),
            Content = new ContentControl { Content = FindResource("SurveyIcon.Plus"), IsHitTestVisible = false },
            ToolTip = "Pin the current tool with its settings",
            Focusable = false,
            Margin = new Thickness(1),
        };
        System.Windows.Automation.AutomationProperties.SetName(add, "Pin the current tool");
        add.Click += (_, e) => { e.Handled = true; PinCurrentTool(); };
        _pinDockRow.Children.Add(add);
        PlacePinDock();
    }

    private UIElement BuildPinGrip()
    {
        var grip = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Drag to move the dock to the left, right or bottom",
            Child = new ContentControl
            {
                Content = FindResource("SurveyIcon.Grip"),
                IsHitTestVisible = false,
                Foreground = (Brush)FindResource("ChromeTextMutedBrush"),
                LayoutTransform = PinSide == PinDockSide.Bottom ? new RotateTransform(90) : Transform.Identity,
            },
        };
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
            PinDockSide side = PinnedTools.NearestSide(p.X, p.Y, Viewport.ActualWidth, Viewport.ActualHeight);
            if (side != PinSide && _settings is not null)
            {
                _settings.Current.EditorPinDock = side.ToString();
                _settings.Save();
                RebuildPinDock();
            }
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

        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var itemStyle = (Style)FindResource("DarkMenuItem");
        var replace = new MenuItem { Header = "Replace with current settings", Style = itemStyle };
        replace.Click += (_, _) => ReplacePin(pin);
        var remove = new MenuItem { Header = "Remove pin", Style = itemStyle };
        remove.Click += (_, _) => RemovePin(pin);
        menu.Items.Add(replace);
        menu.Items.Add(remove);
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

    private void ReplacePin(PinnedToolSetting old)
    {
        if (_settings is null) return;
        if (CurrentToolAsPin() is not PinnedToolSetting pin) return;
        int i = _settings.Current.EditorPinnedTools.IndexOf(old);
        if (i < 0) return;
        _settings.Current.EditorPinnedTools[i] = pin;
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
