using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WinShot.Core;

namespace WinShot.Editor;

/// <summary>
/// The parts of Survey's settings row (row 2) that WinShot did not have:
///
/// <list type="bullet">
/// <item>The colour cluster for single-colour tools (Survey QuickStyleControls.jsx
/// QuickColourDots): red, blue and black discs, then a rainbow custom disc that opens the
/// picker. The chosen disc wears a ring in its own colour with a white check. Shapes, text,
/// callouts and counters keep the one paint swatch (QuickPaintSwatch).</item>
/// <item>The eraser's kind as a Partial / Whole segmented toggle (Survey PASS 7 board 13),
/// in place of the chevron menu inside the eraser button.</item>
/// <item>The rule between the colour cluster and the width (toolbarColourGroup.js
/// showsColourRule).</item>
/// </list>
/// </summary>
public partial class EditorWindow
{
    /// <summary>Survey utils/quickStylePresets.js QUICK_COLOURS.</summary>
    private static readonly (Color Colour, string Name)[] QuickColourDots =
    {
        (Color.FromRgb(0xFF, 0x00, 0x00), "Red"),
        (Color.FromRgb(0x00, 0x00, 0xFF), "Blue"),
        (Color.FromRgb(0x00, 0x00, 0x00), "Black"),
    };

    private StackPanel? _colourDots;
    private readonly List<(Color Colour, Grid Face)> _colourDotFaces = new();
    private Button? _customDot;
    private Grid? _customDotFace;
    private Rectangle? _colourRule;
    private StackPanel? _eraserToggle;
    private RadioButton? _eraserPartialSeg;
    private RadioButton? _eraserWholeSeg;
    private Rectangle? _eraserRule;

    private void WireSurveyRow()
    {
        if (ColorPanel is null || SecondaryToolPanel is null || EditorColorBar is null) return;

        _colourDots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetName(_colourDots, "Color");
        foreach ((Color colour, string name) in QuickColourDots)
        {
            Color c = colour;
            var face = new Grid { Width = 22, Height = 22 };
            _colourDotFaces.Add((c, face));
            var btn = DotButton(face, name, () => SetCurrentColor(c));
            _colourDots.Children.Add(btn);
        }
        _customDotFace = new Grid { Width = 22, Height = 22 };
        _customDot = DotButton(_customDotFace, "Custom color", OpenCustomColourFromDot);
        _colourDots.Children.Add(_customDot);
        ColorPanel.Children.Insert(0, _colourDots);

        _colourRule = new Rectangle { Style = (Style)FindResource("ToolbarDivider"), Margin = new Thickness(6, 0, 6, 0), Visibility = Visibility.Collapsed };
        SecondaryToolPanel.Children.Insert(SecondaryToolPanel.Children.IndexOf(EditorColorBar) + 1, _colourRule);

        _eraserToggle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetName(_eraserToggle, "Eraser type");
        _eraserPartialSeg = Segment("Partial", "Partial erase", partial: true);
        _eraserWholeSeg = Segment("Whole", "Full stroke erase", partial: false);
        _eraserToggle.Children.Add(_eraserPartialSeg);
        _eraserToggle.Children.Add(_eraserWholeSeg);
        _eraserRule = new Rectangle { Style = (Style)FindResource("ToolbarDivider"), Margin = new Thickness(6, 0, 6, 0), Visibility = Visibility.Collapsed };
        SecondaryToolPanel.Children.Insert(0, _eraserRule);
        SecondaryToolPanel.Children.Insert(0, _eraserToggle);

        ColorPickerPopup.Closed += (_, _) => SyncSurveyRow();

        // Survey's line-style menu shows each style as a drawn stroke (StrokeStyleCards
        // StrokeSample, 28 x 14), not as a word alone.
        if (LineStyleBox is not null)
        {
            var sample = (DataTemplate)System.Windows.Markup.XamlReader.Parse(LineSampleTemplate);
            foreach (ComboBoxItem item in LineStyleBox.Items.OfType<ComboBoxItem>())
                item.ContentTemplate = sample;
        }

        // Survey's arrowhead menu draws each row's arrow (StrokeSample 28 x 14, endHead).
        if (ArrowStyleBox is not null)
        {
            foreach (ComboBoxItem item in ArrowStyleBox.Items.OfType<ComboBoxItem>())
                if (item.Tag is string tag && ArrowheadSample(tag) is { } head)
                    item.ContentTemplate = head;
            ArrowStyleBox.DropDownOpened += (_, _) => SyncImportedArrowheads();
            MoveBothEndsIntoArrowMenu();
        }
    }

    /// <summary>
    /// Survey AppShell.jsx:4455-4473: "Both ends" is the arrowhead menu's footer, under a rule,
    /// not a button beside it: a 28 x 14 sample with a head at each end, the words, and a
    /// check while it is on. Arrow only (a callout's leader has one end), so the footer
    /// follows the toggle's own visibility.
    /// </summary>
    private void MoveBothEndsIntoArrowMenu()
    {
        if (ArrowStyleBox is null || ArrowBothEndsBtn is null) return;
        ArrowStyleBox.ApplyTemplate();
        if (ArrowStyleBox.Template?.FindName("PART_Popup", ArrowStyleBox) is not Popup { Child: Border { Child: StackPanel card } })
            return;
        (ArrowBothEndsBtn.Parent as Panel)?.Children.Remove(ArrowBothEndsBtn);
        ArrowBothEndsBtn.Style = null;
        ArrowBothEndsBtn.Margin = new Thickness(0);
        ArrowBothEndsBtn.Content = null;
        ArrowBothEndsBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        ArrowBothEndsBtn.Focusable = false;
        ArrowBothEndsBtn.Cursor = Cursors.Hand;
        ArrowBothEndsBtn.ToolTip = null;
        ArrowBothEndsBtn.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(BothEndsRowTemplate);

        var rule = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        rule.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = ArrowBothEndsBtn });
        card.Children.Add(rule);
        card.Children.Add(ArrowBothEndsBtn);
    }

    /// <summary>The footer row, dressed like the menu's own rows (SurveyDropdownItem).</summary>
    private const string BothEndsRowTemplate =
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ToggleButton'>" +
        "<Border x:Name='Bg' Background='Transparent' CornerRadius='4' Padding='9,4' MinHeight='34'" +
        " TextElement.FontSize='{DynamicResource TextBody}' TextElement.FontWeight='SemiBold' TextElement.Foreground='{DynamicResource TextSecondaryBrush}'>" +
        "<Grid VerticalAlignment='Center'>" +
        "<Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>" +
        "<Canvas Width='28' Height='14' VerticalAlignment='Center'>" +
        "<Line X1='6' Y1='7' X2='22' Y2='7' StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Stroke='{Binding (TextElement.Foreground), ElementName=Bg}'/>" +
        "<Path Data='M6,3 L1,7 L6,11 M22,3 L27,7 L22,11' StrokeThickness='1.5' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Stroke='{Binding (TextElement.Foreground), ElementName=Bg}'/>" +
        "</Canvas>" +
        "<TextBlock Grid.Column='1' Text='Both ends' Margin='8,0,0,0' VerticalAlignment='Center'/>" +
        "<Path x:Name='Check' Grid.Column='2' Visibility='Collapsed' Margin='10,0,0,0' Width='12' Height='12' Stretch='Uniform' VerticalAlignment='Center'" +
        " Data='M2,6.5 L5,9.5 L10,3' StrokeThickness='1.75' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Stroke='{Binding (TextElement.Foreground), ElementName=Bg}'/>" +
        "</Grid></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsChecked' Value='True'><Setter TargetName='Check' Property='Visibility' Value='Visible'/>" +
        "<Setter TargetName='Bg' Property='TextElement.Foreground' Value='{DynamicResource TextPrimaryBrush}'/></Trigger>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bg' Property='Background' Value='{DynamicResource AccentDeepBrush}'/>" +
        "<Setter TargetName='Bg' Property='TextElement.Foreground' Value='White'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>";

    /// <summary>Tags of the heads a user can't pick, only meet on an existing mark.</summary>
    private static readonly HashSet<string> ImportedArrowheads = new(StringComparer.Ordinal)
    {
        "openTriangle", "diamond", "slash",
    };

    /// <summary>
    /// Survey AppShell.jsx:4386-4389: the menu lists the six heads, and prepends the live head
    /// only when it is one of the three that arrive from an existing mark.
    /// </summary>
    private void SyncImportedArrowheads()
    {
        if (ArrowStyleBox is null) return;
        foreach (ComboBoxItem item in ArrowStyleBox.Items.OfType<ComboBoxItem>())
            if (item.Tag is string tag && ImportedArrowheads.Contains(tag))
                item.Visibility = ReferenceEquals(ArrowStyleBox.SelectedItem, item)
                    ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// A 28 x 14 arrow with the head <paramref name="tag"/> at its right end, then the word.
    /// Heads follow Survey's menu drawings (Icons.jsx arrowheadNone ... arrowheadBar) on a
    /// 1.5px shaft; the three imported heads are drawn the same way.
    /// </summary>
    private static DataTemplate? ArrowheadSample(string tag)
    {
        // (shaft end x, stroked head data, filled head data)
        (double shaftTo, string? stroked, string? filled)? spec = tag switch
        {
            "none" => (27, null, null),
            "solidTriangle" => (22, null, "M21,3.5 L27,7 L21,10.5 Z"),
            "vShape" => (27, "M22,3 L27,7 L22,11", null),
            "openCircle" => (21, "M21,7 A3,3 0 1 1 27,7 A3,3 0 1 1 21,7 Z", null),
            "square" => (21, "M21,4 L27,4 L27,10 L21,10 Z", null),
            "horizontalLine" => (26, "M26,3 L26,11", null),
            "openTriangle" => (21, "M21,3.5 L27,7 L21,10.5 Z", null),
            "diamond" => (20, "M20,7 L23.5,3.5 L27,7 L23.5,10.5 Z", null),
            "slash" => (27, "M23,11 L27,3", null),
            _ => null,
        };
        if (spec is not { } h) return null;
        const string ink = "{Binding Foreground, RelativeSource={RelativeSource AncestorType=Control}}";
        var xaml = new System.Text.StringBuilder(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<StackPanel Orientation='Horizontal' VerticalAlignment='Center'>" +
            "<Canvas Width='28' Height='14' VerticalAlignment='Center'>");
        xaml.Append(System.FormattableString.Invariant(
            $"<Line X1='1' Y1='7' X2='{h.shaftTo}' Y2='7' StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Stroke='{ink}'/>"));
        if (h.stroked is not null)
            xaml.Append($"<Path Data='{h.stroked}' StrokeThickness='1.5' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Stroke='{ink}'/>");
        if (h.filled is not null)
            xaml.Append($"<Path Data='{h.filled}' StrokeThickness='1.5' StrokeLineJoin='Round' Stroke='{ink}' Fill='{ink}'/>");
        xaml.Append("</Canvas><TextBlock Text='{Binding}' Margin='6,0,0,0' VerticalAlignment='Center'/></StackPanel></DataTemplate>");
        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml.ToString());
    }

    /// <summary>A 28 x 14 stroke sample, then the style's name. Keyed on the item's own word.</summary>
    private const string LineSampleTemplate =
        "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
        "<StackPanel Orientation='Horizontal' VerticalAlignment='Center'>" +
        "<Grid Width='28' Height='14' VerticalAlignment='Center'>" +
        "<Line x:Name='L' X1='1' Y1='7' X2='27' Y2='7' StrokeThickness='1.75' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeDashCap='Round'" +
        " Stroke='{Binding Foreground, RelativeSource={RelativeSource AncestorType=Control}}'/>" +
        "<Path x:Name='C' Visibility='Collapsed' StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round'" +
        " Data='M1,9 A3.25,3.25 0 0 1 7.5,9 A3.25,3.25 0 0 1 14,9 A3.25,3.25 0 0 1 20.5,9 A3.25,3.25 0 0 1 27,9'" +
        " Stroke='{Binding Foreground, RelativeSource={RelativeSource AncestorType=Control}}'/>" +
        "</Grid>" +
        "<TextBlock Text='{Binding}' Margin='6,0,0,0' VerticalAlignment='Center'/>" +
        "</StackPanel>" +
        "<DataTemplate.Triggers>" +
        "<DataTrigger Binding='{Binding}' Value='Dashed'><Setter TargetName='L' Property='StrokeDashArray' Value='2.4 1.6'/><Setter TargetName='L' Property='StrokeDashCap' Value='Flat'/></DataTrigger>" +
        "<DataTrigger Binding='{Binding}' Value='Dotted'><Setter TargetName='L' Property='StrokeDashArray' Value='0.01 1.8'/></DataTrigger>" +
        "<DataTrigger Binding='{Binding}' Value='Cloud'><Setter TargetName='L' Property='Visibility' Value='Collapsed'/><Setter TargetName='C' Property='Visibility' Value='Visible'/></DataTrigger>" +
        "</DataTemplate.Triggers>" +
        "</DataTemplate>";

    private Button DotButton(UIElement face, string name, Action click)
    {
        var btn = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = name,
            Content = face,
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
                "<Border Background='Transparent'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                "</ControlTemplate>"),
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, name);
        btn.Click += (_, e) => { e.Handled = true; click(); SyncSurveyRow(); };
        return btn;
    }

    private RadioButton Segment(string text, string name, bool partial)
    {
        var seg = new RadioButton
        {
            GroupName = "EraserKind",
            Content = text,
            ToolTip = name,
            Focusable = false,
            Cursor = Cursors.Hand,
            Height = 24,
            MinWidth = 48,
            Padding = new Thickness(6, 0, 6, 0),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='RadioButton'>" +
                "<Border x:Name='Bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='6' Background='Transparent' Padding='{TemplateBinding Padding}'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'>" +
                "<Setter TargetName='Bd' Property='Background' Value='#14FFFFFF'/></Trigger></ControlTemplate.Triggers>" +
                "</ControlTemplate>"),
        };
        System.Windows.Automation.AutomationProperties.SetName(seg, name);
        seg.Checked += (_, _) =>
        {
            if (_syncingEraserSegments) return;
            SetEraserPartial(partial);
            SyncSurveyRow();
        };
        return seg;
    }

    private bool _syncingEraserSegments;

    private void OpenCustomColourFromDot()
    {
        if (_customDot is null) return;
        _colorPickerHomeTarget ??= ColorPickerPopup.PlacementTarget;
        ColorPickerPopup.PlacementTarget = _customDot;
        OnCustomSwatchClick(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Survey toolbarColourGroup.js showsQuickColourDots: pen, highlighter, arrow, line and
    /// polyline take the three discs and the custom disc; every other coloured tool keeps the
    /// one paint swatch.
    /// </summary>
    internal static bool UsesQuickColourDots(EditorTool tool) =>
        tool is EditorTool.Freehand or EditorTool.Highlighter or EditorTool.Arrow
            or EditorTool.CurvedArrow or EditorTool.Line or EditorTool.Polyline;

    /// <summary>Brings the colour cluster, its rule and the eraser toggle up to date.</summary>
    private void SyncSurveyRow()
    {
        if (_colourDots is null || ColorWellBtn is null) return;
        EditorTool chrome = ChromeTool();
        bool colour = ColorPanel.Visibility == Visibility.Visible;
        bool dots = colour && UsesQuickColourDots(chrome);
        _colourDots.Visibility = dots ? Visibility.Visible : Visibility.Collapsed;
        ColorWellBtn.Visibility = colour && !dots ? Visibility.Visible : Visibility.Collapsed;
        if (_colourRule is not null)
            _colourRule.Visibility = colour && ThicknessPanel.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;

        if (dots)
        {
            Color current = AnnotationStyle.Opaque(CurrentStrokeBrush());
            bool preset = false;
            foreach ((Color c, Grid face) in _colourDotFaces)
            {
                bool on = c == current;
                preset |= on;
                PaintDot(face, c, on, custom: false);
            }
            if (_customDotFace is not null) PaintDot(_customDotFace, current, !preset, custom: true);
        }

        SyncImportedArrowheads();

        bool eraser = _tool == EditorTool.Eraser;
        if (_eraserToggle is not null) _eraserToggle.Visibility = eraser ? Visibility.Visible : Visibility.Collapsed;
        if (_eraserRule is not null) _eraserRule.Visibility = eraser ? Visibility.Visible : Visibility.Collapsed;
        if (_eraserPartialSeg is not null && _eraserWholeSeg is not null)
        {
            _syncingEraserSegments = true;
            try
            {
                _eraserPartialSeg.IsChecked = _eraserPartial;
                _eraserWholeSeg.IsChecked = !_eraserPartial;
            }
            finally { _syncingEraserSegments = false; }
            // Gold word for the live kind, and nothing else (Survey .chrome-segmented).
            var accent = (Brush)FindResource("ChromeAccentBrush");
            var text = (Brush)FindResource("ChromeTextMutedBrush");
            _eraserPartialSeg.Foreground = _eraserPartial ? accent : text;
            _eraserWholeSeg.Foreground = _eraserPartial ? text : accent;
        }
    }

    /// <summary>
    /// One 16px disc in a 22px cell. Chosen: a 1.5px gap in the bar's own colour, then a 1.5px
    /// ring in the disc's own colour, and a check — dark ink on a light disc, white otherwise.
    /// The custom disc is a rainbow ring with a plus, or the current colour with the check
    /// when no preset is the current colour.
    /// </summary>
    private void PaintDot(Grid face, Color colour, bool chosen, bool custom)
    {
        face.Children.Clear();
        var ringColour = SwatchRingColour(colour);
        if (chosen)
            face.Children.Add(new Ellipse { Width = 22, Height = 22, Stroke = new SolidColorBrush(ringColour), StrokeThickness = 1.5 });
        if (custom && !chosen)
        {
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            Color[] stops = { Color.FromRgb(0xF4, 0x3F, 0x5E), Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0x0E, 0xA5, 0xE9), Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xF4, 0x3F, 0x5E) };
            for (int i = 0; i < stops.Length; i++) rainbow.GradientStops.Add(new GradientStop(stops[i], i / (double)(stops.Length - 1)));
            face.Children.Add(new Ellipse { Width = 16, Height = 16, Stroke = rainbow, StrokeThickness = 3 });
            face.Children.Add(new Path
            {
                Data = Geometry.Parse("M5,2.5 V7.5 M2.5,5 H7.5"),
                Stroke = (Brush)FindResource("ChromeTextBrush"),
                StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 10,
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }
        var disc = new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(colour) };
        if (!chosen && NeedsHairline(colour))
        {
            disc.Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            disc.StrokeThickness = 1;
        }
        face.Children.Add(disc);
        if (chosen)
        {
            face.Children.Add(new Path
            {
                Data = Geometry.Parse("M2.1,5.2 L4,7.1 L7.9,3.1"),
                Stroke = new SolidColorBrush(Luminance(colour) > 0.45 ? Color.FromRgb(0x0B, 0x0D, 0x12) : Colors.White),
                StrokeThickness = 1.1,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 10,
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    private static double Luminance(Color c)
    {
        static double Lin(byte v) { double x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>Survey quickStylePresets.js: a disc too dark to show against the bar gets a light ring and a hairline.</summary>
    private static bool NeedsHairline(Color c)
    {
        double l = Luminance(c), panel = 0.0107;
        double contrast = (Math.Max(l, panel) + 0.05) / (Math.Min(l, panel) + 0.05);
        return contrast < 1.6;
    }

    private static Color SwatchRingColour(Color c) =>
        NeedsHairline(c) ? Color.FromRgb(0xD7, 0xDC, 0xE5) : c;
}
