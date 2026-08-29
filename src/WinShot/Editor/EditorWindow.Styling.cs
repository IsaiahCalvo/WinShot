using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Editor;

public partial class EditorWindow : Window
{
    // ----------------------------------------------------- live restyle (Gap 1)

    /// <summary>
    /// Re-styles the currently selected annotation in place when the user changes color,
    /// thickness, or fill, and records one undoable <see cref="EditorAction"/>. Only the
    /// non-null arguments are applied; the rest keep the element's current look. Captures a
    /// before/after snapshot of the affected visual properties and the stored
    /// <see cref="AnnotationData"/> so undo/redo simply replay the appropriate snapshot.
    /// </summary>
    private void RestyleSelected(Color? color, double? thickness, ShapeFillMode? fill, double? opacity = null, Color? fillColor = null)
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
            return;
        // Color is irrelevant to a few annotation kinds (spotlight/emoji/image); thickness
        // and fill only make sense for the shapes that carry them. Skip no-op restyles so
        // those annotations don't push empty undo entries.
        if (!CanRestyle(_selected, color, thickness, fill) &&
            !(opacity is not null && CanSetOpacity(_selected)) &&
            fillColor is null)
            return;

        // A color pick (swatch / eyedropper) is opaque, but the annotation may carry a
        // reduced alpha — from the highlighter's translucent base or an earlier opacity
        // change. Preserve that existing alpha so changing the hue never silently resets
        // opacity. An explicit opacity change (opacity != null) overrides this below.
        if (color is Color picked && opacity is null && picked.A == 255)
        {
            byte fallback = meta.Type == AnnotationData.TypeHighlighter ? (byte)0x80 : (byte)0xFF;
            byte alpha = meta.Color is string hex0 && TryParseColor(hex0, out var cur) ? cur.A : fallback;
            color = Color.FromArgb(alpha, picked.R, picked.G, picked.B);
        }

        var before = SnapshotStyle(_selected, meta);
        var after = before.With(color, thickness, fill, opacity, fillColor);
        ApplyStyleSnapshot(_selected, after);

        UIElement el = _selected;
        Push(new EditorAction(
            undo: () => ApplyStyleSnapshot(el, before),
            redo: () => ApplyStyleSnapshot(el, after)), apply: false);
        UpdateSelectionVisual();
    }

    private static bool CanRestyle(UIElement element, Color? color, double? thickness, ShapeFillMode? fill)
    {
        if (element is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
            return false;
        return meta.Type switch
        {
            AnnotationData.TypeArrow or AnnotationData.TypeCurvedArrow or AnnotationData.TypeLine
                or AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter =>
                color is not null || thickness is not null,
            AnnotationData.TypeRectangle or AnnotationData.TypeEllipse or AnnotationData.TypeCallout => true,
            AnnotationData.TypeText => color is not null,
            AnnotationData.TypeStep => color is not null || thickness is not null,
            _ => false, // emoji / spotlight / image carry no editable stroke style
        };
    }

    /// <summary>Whether an opacity change has a visible effect on this annotation kind (the color-bearing ones).</summary>
    private static bool CanSetOpacity(UIElement element) =>
        element is FrameworkElement fe && fe.Tag is AnnotationData meta && meta.Type switch
        {
            AnnotationData.TypeArrow or AnnotationData.TypeCurvedArrow or AnnotationData.TypeLine
                or AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter
                or AnnotationData.TypeRectangle or AnnotationData.TypeEllipse
                or AnnotationData.TypeText or AnnotationData.TypeStep or AnnotationData.TypeCallout => true,
            _ => false,
        };

    /// <summary>
    /// Immutable snapshot of every property a restyle can touch, plus a fresh
    /// <see cref="AnnotationData"/> for the element's Tag, so save round-trips stay correct.
    /// </summary>
    private sealed record StyleSnapshot(
        Color StrokeColor,
        double Thickness,
        ShapeFillMode Fill,
        Color FillColor,
        AnnotationData Meta)
    {
        public StyleSnapshot With(Color? color, double? thickness, ShapeFillMode? fill, double? opacity = null, Color? fillColor = null)
        {
            Color c = color ?? StrokeColor;
            double t = thickness ?? Thickness;
            ShapeFillMode f = fill ?? Fill;
            Color fc = fillColor ?? FillColor;

            // Opacity rewrites stroke alpha from the slider (0–1). Highlighter used to
            // multiply a baked 0x80 base, so a 50% arm + 50% slider became A=64.
            if (opacity is double op)
            {
                byte alpha = (byte)Math.Clamp(Math.Round(255 * op), 0, 255);
                c = Color.FromArgb(alpha, c.R, c.G, c.B);
            }

            if (Meta.Type is AnnotationData.TypeRectangle or AnnotationData.TypeEllipse or AnnotationData.TypeCallout)
                (c, fc) = AnnotationStyle.EnforceOneVisible(c, fc);

            var meta = Meta.Clone();
            meta.Color = ToHex(c);
            if (thickness is not null) meta.Thickness = t;
            if (fill is not null) meta.Fill = f.ToString();
            if (fillColor is not null || Meta.Type is AnnotationData.TypeRectangle or AnnotationData.TypeEllipse)
                meta.FillColor = ToHex(fc);
            return new StyleSnapshot(c, t, f, fc, meta);
        }
    }

    private static StyleSnapshot SnapshotStyle(UIElement element, AnnotationData meta)
    {
        Color stroke = meta.Color is string hex && TryParseColor(hex, out var c) ? c : Colors.White;
        double thickness = meta.Thickness ?? 4;
        ShapeFillMode fill = Enum.TryParse(meta.Fill, out ShapeFillMode f) ? f : ShapeFillMode.None;
        Color fillColor = AnnotationStyle.FillColorFrom(meta, stroke);
        return new StyleSnapshot(stroke, thickness, fill, fillColor, meta);
    }

    /// <summary>Applies a snapshot to the live element and refreshes its stored AnnotationData.</summary>
    private void ApplyStyleSnapshot(UIElement element, StyleSnapshot snap)
    {
        if (element is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
            return;
        var stroke = new SolidColorBrush(snap.StrokeColor);

        switch (meta.Type)
        {
            case AnnotationData.TypeArrow:
            case AnnotationData.TypeCurvedArrow:
                if (element is Path arrow)
                {
                    arrow.Stroke = stroke;
                    arrow.Fill = stroke;
                    arrow.StrokeThickness = snap.Thickness;
                    AnnotationFactory.ApplyDash(arrow, AnnotationStyle.LineStyleFrom(snap.Meta));
                    // The triangular head scales with thickness, so rebuild geometry.
                    if (snap.Meta.Points is { } pts && pts.Length >= 2)
                    {
                        var p = pts.Select(q => new Point(q[0], q[1])).ToArray();
                        var (end, start) = AnnotationStyle.HeadsFrom(snap.Meta);
                        arrow.Data = meta.Type == AnnotationData.TypeCurvedArrow && p.Length >= 3
                            ? AnnotationFactory.CurvedArrowGeometry(p[0], p[1], p[2], snap.Thickness)
                            : AnnotationFactory.ArrowGeometry(p[0], p[1], snap.Thickness, end, start,
                                mid: LineCurve.ParseMid(snap.Meta.Mid));
                    }
                }
                break;
            case AnnotationData.TypeLine:
                if (element is Shape lineLike)
                {
                    lineLike.Stroke = stroke;
                    lineLike.StrokeThickness = snap.Thickness;
                    AnnotationFactory.ApplyDash(lineLike, AnnotationStyle.LineStyleFrom(snap.Meta));
                    if (lineLike is Path linePath && snap.Meta.Points is { Length: >= 2 } lp)
                        linePath.Data = LineCurve.Stroke(
                            new Point(lp[0][0], lp[0][1]), new Point(lp[1][0], lp[1][1]),
                            LineCurve.ParseMid(snap.Meta.Mid));
                }
                break;
            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
                if (element is Path ink && snap.Meta.Points is { Length: >= 1 } ip)
                {
                    var pts = ip.Select(q => new Point(q[0], q[1])).ToList();
                    ink.Data = PaperInk.Outline(pts, snap.Thickness);
                    ink.Fill = stroke;
                    ink.Stroke = Brushes.Transparent;
                    ink.StrokeThickness = 0;
                }
                break;
            case AnnotationData.TypeRectangle:
            case AnnotationData.TypeEllipse:
                if (element is Shape boxLike)
                {
                    boxLike.Stroke = stroke;
                    boxLike.StrokeThickness = snap.Thickness;
                    boxLike.Fill = snap.FillColor.A == 0
                        ? ShapeFillBrush.Create(snap.Fill, snap.StrokeColor)
                        : new SolidColorBrush(snap.FillColor);
                    var lineStyle = AnnotationStyle.LineStyleFrom(snap.Meta);
                    if (boxLike is Path cloud && lineStyle == LineBorderStyle.Cloud)
                    {
                        cloud.Data = CloudPath.ForRectangle(new Rect(0, 0, Math.Max(1, boxLike.Width), Math.Max(1, boxLike.Height)));
                        cloud.StrokeDashArray = null;
                    }
                    else
                        AnnotationFactory.ApplyDash(boxLike, lineStyle == LineBorderStyle.Cloud ? LineBorderStyle.Solid : lineStyle);
                }
                break;
            case AnnotationData.TypeCallout:
                if (element is CalloutAnnotation callout && snap.Meta.Points is { Length: >= 3 } cp)
                {
                    var box = snap.Meta.Rect is { Length: >= 4 } r
                        ? new Rect(r[0], r[1], r[2], r[3])
                        : new Rect(cp[2][0], cp[2][1], CalloutLayout.DefaultBoxWidth, CalloutLayout.DefaultBoxHeight);
                    var layout = CalloutLayout.FromParts(new Point(cp[0][0], cp[0][1]), new Point(cp[1][0], cp[1][1]), box);
                    var (head, _) = AnnotationStyle.HeadsFrom(snap.Meta);
                    callout.Apply(layout, snap.Meta.Text ?? callout.Text, snap.StrokeColor, snap.FillColor,
                        snap.Thickness, head, AnnotationStyle.LineStyleFrom(snap.Meta),
                        snap.Meta.FontSize ?? 16);
                }
                break;
            case AnnotationData.TypeText:
                ApplyTextColor(element, stroke);
                break;
            case AnnotationData.TypeStep:
                if (element is Grid badge)
                {
                    bool lightFill = 0.299 * snap.StrokeColor.R + 0.587 * snap.StrokeColor.G +
                                     0.114 * snap.StrokeColor.B > 160;
                    var contrast = lightFill ? Colors.Black : Colors.White;
                    foreach (var child in badge.Children)
                    {
                        if (child is Ellipse ring)
                        {
                            ring.Fill = stroke;
                            ring.Stroke = new SolidColorBrush(contrast) { Opacity = 0.85 };
                        }
                        else if (child is TextBlock digit)
                        {
                            digit.Foreground = new SolidColorBrush(contrast);
                        }
                    }
                }
                break;
        }

        fe.Tag = snap.Meta;
    }

    /// <summary>Sets the foreground of a styled text label, walking into the Pill border's child.</summary>
    private static void ApplyTextColor(UIElement element, Brush foreground)
    {
        switch (element)
        {
            case TextBlock tb:
                tb.Foreground = foreground;
                break;
            case Path glyph: // Outline style
                glyph.Fill = foreground;
                break;
            case Border pill when pill.Child is TextBlock inner:
                inner.Foreground = foreground;
                break;
            case Grid box:
                foreach (UIElement child in box.Children)
                    ApplyTextColor(child, foreground);
                break;
        }
    }

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private void RestyleSelectedTextFormat()
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta) return;
        if (meta.Type is not (AnnotationData.TypeText or AnnotationData.TypeCallout)) return;
        var before = meta.Clone();
        var after = meta.Clone();
        after.FontFamily = _textFontFamily;
        after.Bold = _textBold;
        after.Italic = _textItalic;
        after.Underline = _textUnderline;
        after.Strike = _textStrike;
        after.Align = _textAlign;
        after.VerticalAlign = _textVerticalAlign;
        after.FontSize = _textFontSize;
        ApplyTextFormat(fe, after);
        Push(new EditorAction(
            undo: () => ApplyTextFormat(fe, before),
            redo: () => ApplyTextFormat(fe, after)), apply: false);
        UpdateSelectionVisual();
    }

    private static void ApplyTextFormat(FrameworkElement fe, AnnotationData meta)
    {
        fe.Tag = meta;
        var align = AnnotationFactory.ParseAlign(meta.Align);
        var family = new FontFamily(string.IsNullOrWhiteSpace(meta.FontFamily) ? "Segoe UI" : meta.FontFamily);
        bool bold = meta.Bold == true;
        bool italic = meta.Italic == true;
        TextBlock? tb = fe as TextBlock
            ?? (fe as Border)?.Child as TextBlock
            ?? (fe as Grid)?.Children.OfType<TextBlock>().FirstOrDefault()
            ?? (fe as CalloutAnnotation)?.Label;
        if (tb is null) return;
        tb.VerticalAlignment = AnnotationFactory.ParseVerticalAlign(meta.VerticalAlign);
        tb.FontFamily = family;
        tb.FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold;
        tb.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
        tb.TextAlignment = align;
        if (meta.FontSize is double fs && fs > 0) tb.FontSize = fs;
        tb.TextDecorations = null;
        if (meta.Underline == true || meta.Strike == true)
        {
            tb.TextDecorations = new TextDecorationCollection();
            if (meta.Underline == true) tb.TextDecorations.Add(TextDecorations.Underline);
            if (meta.Strike == true) tb.TextDecorations.Add(TextDecorations.Strikethrough);
        }
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        if (ColorConverter.ConvertFromString(hex) is Color c)
        {
            color = c;
            return true;
        }
        color = Colors.White;
        return false;
    }

    // ----------------------------------------------------------- step mode (Gap 5)

    private void OnStepModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
            _stepLetters = string.Equals(tag, "Letter", StringComparison.Ordinal);
    }

    private void OnResetStepCounter(object sender, RoutedEventArgs e) => _nextStep = 1;

    private void OnTextStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag && Enum.TryParse(tag, out TextStyle style))
            _textStyle = style;
    }

    // -------------------------------------------------- arrow style (parity tail)

    /// <summary>
    /// Arrow-style dropdown: sets the default style for new straight arrows, and re-shapes
    /// the currently selected straight arrow in place (one undoable step) so a chosen look
    /// can be applied after the fact too.
    /// </summary>
    private void OnArrowStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag)
            return;
        if (tag == "Double")
        {
            _arrowStyle = ArrowStyle.Double;
            _arrowhead = ArrowheadStyle.SolidTriangle;
            _startArrowhead = ArrowheadStyle.SolidTriangle;
        }
        else if (Enum.TryParse(tag, out ArrowStyle legacy))
        {
            _arrowStyle = legacy;
            _arrowhead = AnnotationStyle.ParseArrowhead(legacy.ToString(), out _startArrowhead);
        }
        else
        {
            _arrowhead = AnnotationStyle.ParseArrowhead(tag, out _);
            _startArrowhead = ArrowheadStyle.None;
            _arrowStyle = ArrowStyle.Straight;
        }
        if (IsLoaded) RestyleSelectedArrowhead();
    }

    private void RestyleSelectedArrowhead()
    {
        if (_selected is not Path arrow || arrow.Tag is not AnnotationData meta ||
            meta.Type is not (AnnotationData.TypeArrow or AnnotationData.TypeCallout))
        {
            if (_selected is CalloutAnnotation callout && callout.Tag is AnnotationData calloutMeta)
            {
                var before = calloutMeta.Clone();
                var after = calloutMeta.Clone();
                after.Head = AnnotationStyle.ToStorageName(_arrowhead);
                after.StartHead = _startArrowhead == ArrowheadStyle.None ? null : AnnotationStyle.ToStorageName(_startArrowhead);
                ApplyStyleSnapshot(callout, SnapshotStyle(callout, after));
                Push(new EditorAction(
                    undo: () => { callout.Tag = before; ApplyStyleSnapshot(callout, SnapshotStyle(callout, before)); },
                    redo: () => { callout.Tag = after; ApplyStyleSnapshot(callout, SnapshotStyle(callout, after)); }),
                    apply: false);
            }
            else
                RestyleSelectedArrow(_arrowStyle);
            return;
        }
        var beforeMeta = meta.Clone();
        var afterMeta = meta.Clone();
        afterMeta.Head = AnnotationStyle.ToStorageName(_arrowhead);
        afterMeta.StartHead = _startArrowhead == ArrowheadStyle.None ? null : AnnotationStyle.ToStorageName(_startArrowhead);
        ApplyArrowStyleData(arrow, afterMeta);
        Push(new EditorAction(
            undo: () => { ApplyArrowStyleData(arrow, beforeMeta); if (ReferenceEquals(_selected, arrow)) UpdateSelectionVisual(); },
            redo: () => { ApplyArrowStyleData(arrow, afterMeta); if (ReferenceEquals(_selected, arrow)) UpdateSelectionVisual(); }),
            apply: false);
        UpdateSelectionVisual();
    }

    /// <summary>
    /// Re-shapes the selected straight arrow to a new <see cref="ArrowStyle"/>, recording one
    /// undoable action. No-op for non-arrow (and curved-arrow) selections. The endpoints stay
    /// put; only the head geometry and the stored Style change.
    /// </summary>
    private void RestyleSelectedArrow(ArrowStyle style)
    {
        if (_selected is not Path arrow || arrow.Tag is not AnnotationData meta ||
            meta.Type != AnnotationData.TypeArrow)
            return;
        var before = meta.Clone();
        if (AnnotationFactory.ParseArrowStyle(before.Style) == style) return; // nothing to do

        var after = meta.Clone();
        after.Style = style.ToString();
        ApplyArrowStyleData(arrow, after);

        Push(new EditorAction(
            undo: () => { ApplyArrowStyleData(arrow, before); if (ReferenceEquals(_selected, arrow)) UpdateSelectionVisual(); },
            redo: () => { ApplyArrowStyleData(arrow, after); if (ReferenceEquals(_selected, arrow)) UpdateSelectionVisual(); }),
            apply: false);
        UpdateSelectionVisual();
    }

    /// <summary>Rebuilds a straight arrow's geometry from its stored points + style and re-stores the metadata.</summary>
    private static void ApplyArrowStyleData(Path arrow, AnnotationData meta)
    {
        if (meta.Points is { } pts && pts.Length >= 2)
        {
            Point from = new(pts[0][0], pts[0][1]);
            Point to = new(pts[^1][0], pts[^1][1]);
            double thickness = meta.Thickness ?? arrow.StrokeThickness;
            var (end, start) = AnnotationStyle.HeadsFrom(meta);
            arrow.Data = AnnotationFactory.ArrowGeometry(from, to, thickness, end, start,
                mid: LineCurve.ParseMid(meta.Mid));
        }
        arrow.Tag = meta;
    }

    // ----------------------------------------------------- opacity (parity tail)

    /// <summary>
    /// Opacity slider (25–100%): sets the alpha multiplier used for new marks and previews it
    /// live on the selected annotation. A continuous drag updates the visual on every tick but
    /// records a SINGLE undo entry, committed when the drag ends (see
    /// <see cref="OnOpacitySliderReleased"/>). The alpha is baked into the annotation's color,
    /// so it persists.
    /// </summary>
    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double pct = Math.Clamp(e.NewValue, 0, 100);
        if (_colorWell == ColorWell.Fill &&
            EditorShellContract.ContextFor(_tool, _filledRectangleMode).HasFlag(EditorContextControls.FillStrokeTabs))
        {
            SetOpacityPercent(pct, restyle: IsLoaded);
            return;
        }
        SetOpacityPercent(pct, restyle: false);
        if (!IsLoaded) return;

        // Apply live to the selection without pushing undo, capturing a one-time "before" for
        // the whole gesture so the eventual commit is a single reversible step.
        if (_selected is FrameworkElement fe && fe.Tag is AnnotationData meta && CanSetOpacity(_selected))
        {
            if (_opacityBefore is null || !ReferenceEquals(_opacityElement, _selected))
            {
                CommitOpacityGesture(); // flush any pending gesture on a different element first
                _opacityBefore = SnapshotStyle(_selected, meta);
                _opacityElement = _selected;
            }
            var after = SnapshotStyle(_selected, meta).With(null, null, null, _opacity);
            ApplyStyleSnapshot(_selected, after);
            UpdateSelectionVisual();
        }
    }

    /// <summary>Slider drag/keyboard release: commits the pending opacity gesture as one undo entry.</summary>
    private void OnOpacitySliderReleased(object sender, RoutedEventArgs e) => CommitOpacityGesture();

    /// <summary>
    /// Finalizes an in-progress opacity drag: records one undoable action from the captured
    /// "before" snapshot to the element's current (live-previewed) state. No-op when nothing
    /// changed or no gesture is pending.
    /// </summary>
    private void CommitOpacityGesture()
    {
        if (_opacityBefore is not { } before || _opacityElement is not FrameworkElement fe ||
            fe.Tag is not AnnotationData meta)
        {
            _opacityBefore = null;
            _opacityElement = null;
            return;
        }
        UIElement el = _opacityElement;
        _opacityBefore = null;
        _opacityElement = null;

        var after = SnapshotStyle(el, meta);
        if (after.StrokeColor == before.StrokeColor) return; // alpha unchanged → no entry

        Push(new EditorAction(
            undo: () => { ApplyStyleSnapshot(el, before); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); },
            redo: () => { ApplyStyleSnapshot(el, after); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); }),
            apply: false);
    }

    /// <summary>Applies the current opacity multiplier to a base color's alpha channel.</summary>
    private static Color WithOpacity(Color c, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(c.A * opacity), 0, 255), c.R, c.G, c.B);

    // ------------------------------------------- blur/pixelate strength (parity tail)

    private void OnEffectStrengthChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag ||
            !Enum.TryParse(tag, out EffectStrength strength))
            return;
        // The same radio group drives both tools; route to whichever effect is active.
        if (_tool == EditorTool.Pixelate) _pixelateStrength = strength;
        else _blurStrength = strength;
    }

    private void OnCropRatioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item)
            return;
        if (item.Tag is not string tag || string.IsNullOrEmpty(tag))
        {
            _cropRatio = null;
            return;
        }
        string[] parts = tag.Split(':');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
            w > 0 && h > 0)
        {
            _cropRatio = w / h;
        }
    }

    // ----------------------------------------------------- custom color picker (Gap)

    /// <summary>
    /// Custom-swatch click: applies the stored custom color (so a single click reselects it)
    /// and opens the picker popup so a new color can be chosen from the grid or a hex value.
    /// </summary>
    private void OnCustomSwatchClick(object sender, RoutedEventArgs e)
    {
        BuildColorPickerPalette();
        SyncColorPickerInputs(_customColor);
        ApplyCustomColor(_customColor); // keep current selection consistent with the swatch
        ColorPickerPopup.IsOpen = true;
    }

    /// <summary>Builds the extended color grid once. A 5x8 palette of useful hues/shades.</summary>
    private void BuildColorPickerPalette()
    {
        if (_colorPickerBuilt) return;
        _colorPickerBuilt = true;

        AddPickerSwatch(Colors.Transparent, "Transparent");
        string[] swatches =
        {
            // row 1 — greys (transparent already occupies the first cell)
            "#3A3A3C", "#636366", "#8E8E93", "#AEAEB2", "#C7C7CC", "#E5E5EA", "#FFFFFF",
            "#FF3B30",
            // row 2 — reds / oranges
            "#FF6B5E", "#FF9500", "#FFB340", "#FFCC00", "#FFE066", "#D70015", "#A2231D",
            // row 3 — greens / teals
            "#34C759", "#30D158", "#00C7BE", "#63E6BE", "#5AC8FA", "#64D2FF", "#248A3D", "#0E6E4E",
            // row 4 — blues / purples
            "#007AFF", "#0A84FF", "#5856D6", "#5E5CE6", "#AF52DE", "#BF5AF2", "#0040DD", "#3634A3",
            // row 5 — pinks / browns
            "#FF2D55", "#FF375F", "#FF6482", "#A2845E", "#AC8E68", "#8B5A2B", "#D2691E", "#7D2E2E",
        };

        foreach (string hex in swatches)
        {
            if (!TryParseColor(hex, out var c)) continue;
            AddPickerSwatch(c, hex);
        }
    }

    private void AddPickerSwatch(Color c, string name)
    {
        Brush fill = c.A == 0
            ? new DrawingBrush
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 8, 8),
                ViewportUnits = BrushMappingMode.Absolute,
                Drawing = new GeometryDrawing(
                    new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                    null,
                    new RectangleGeometry(new Rect(0, 0, 4, 4))),
            }
            : new SolidColorBrush(c);
        var btn = new Button
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            ToolTip = name,
            Tag = c,
            Background = fill,
            BorderBrush = (Brush)FindResource("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetName(btn, c.A == 0 ? "Transparent" : $"Color {name}");
        AutomationProperties.SetHelpText(btn, c.A == 0
            ? "Set the current color well to fully transparent."
            : $"Use local annotation color {name}.");
        btn.Click += OnColorPickerGridPick;
        ColorPickerGrid.Children.Add(btn);
    }

    private void OnColorPickerGridPick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is Color c)
        {
            SyncColorPickerInputs(c);
            ApplyCustomColor(c);
        }
    }

    /// <summary>Hex box: live-preview a valid value as the user types (without closing the popup).</summary>
    private void OnColorPickerHexChanged(object sender, TextChangedEventArgs e)
    {
        // Fires once during XAML parse (initial Text) before the preview rect exists.
        if (_suppressHexEvents || ColorPickerPreview is null) return;
        if (ColorPickerHex.Text is string s && TryParseHexLoose(s, out var c))
            ColorPickerPreview.Fill = new SolidColorBrush(c);
    }

    private void OnColorPickerHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHexColor();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ColorPickerPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnColorPickerApply(object sender, RoutedEventArgs e) => CommitHexColor();

    private void CommitHexColor()
    {
        if (ColorPickerHex.Text is string s && TryParseHexLoose(s, out var c))
        {
            ApplyCustomColor(c);
            ColorPickerPopup.IsOpen = false;
        }
    }

    /// <summary>Refreshes the popup's preview + hex box to a color without re-triggering the live preview handler.</summary>
    private void SyncColorPickerInputs(Color c)
    {
        _suppressHexEvents = true;
        ColorPickerPreview.Fill = c.A == 0 ? Brushes.Transparent : new SolidColorBrush(c);
        ColorPickerHex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _suppressHexEvents = false;
        var (h, s, v) = AnnotationStyle.ToHsv(c);
        _pickerHue = h;
        _pickerSat = s;
        _pickerVal = v;
        if (SvHueRect is not null)
            SvHueRect.Fill = new SolidColorBrush(AnnotationStyle.FromHsv(h, 1, 1));
        if (SvCursor is not null && ColorPickerSv is not null)
        {
            Canvas.SetLeft(SvCursor, Math.Clamp(s * ColorPickerSv.Width - 5, 0, ColorPickerSv.Width - 10));
            Canvas.SetTop(SvCursor, Math.Clamp((1 - v) * ColorPickerSv.Height - 5, 0, ColorPickerSv.Height - 10));
        }
        if (HueCursor is not null && ColorPickerHue is not null)
            Canvas.SetLeft(HueCursor, Math.Clamp(h / 360.0 * ColorPickerHue.Width - 5, 0, ColorPickerHue.Width - 10));
        if (PickerOpacitySlider is not null)
            PickerOpacitySlider.Value = AnnotationStyle.OpacityOf(c) * 100;
    }

    /// <summary>
    /// Persists a custom color into the trailing swatch + current-color indicator, makes it the
    /// active stroke color, and (when an annotation is selected) restyles it via the normal path.
    /// Also used by the eyedropper so a sampled color lands in the same persistent swatch.
    /// </summary>
    private void ApplyCustomColor(Color c)
    {
        _customColor = c.A == 0 ? _customColor : c;
        CustomSwatch.Background = c.A == 0
            ? Brushes.Transparent
            : new SolidColorBrush(AnnotationStyle.Opaque(c));
        CustomSwatch.IsChecked = true;
        if (c.A == 0)
        {
            SetOpacityPercent(0, restyle: true);
            return;
        }
        SetCurrentColor(AnnotationStyle.Opaque(c));
        if (c.A < 255)
            SetOpacityPercent(c.A / 2.55, restyle: true);
    }

    private void OnPickerOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || PickerOpacityValue is null) return;
        PickerOpacityValue.Text = $"{Math.Round(e.NewValue)}%";
        SetOpacityPercent(e.NewValue, restyle: true);
    }

    private void OnColorPickerSvDown(object sender, MouseButtonEventArgs e)
    {
        _svDragging = true;
        ColorPickerSv.CaptureMouse();
        SampleSv(e.GetPosition(ColorPickerSv));
        e.Handled = true;
    }

    private void OnColorPickerSvMove(object sender, MouseEventArgs e)
    {
        if (_svDragging) SampleSv(e.GetPosition(ColorPickerSv));
    }

    private void OnColorPickerSvUp(object sender, MouseButtonEventArgs e)
    {
        _svDragging = false;
        ColorPickerSv.ReleaseMouseCapture();
    }

    private void OnColorPickerHueDown(object sender, MouseButtonEventArgs e)
    {
        _hueDragging = true;
        ColorPickerHue.CaptureMouse();
        SampleHue(e.GetPosition(ColorPickerHue));
        e.Handled = true;
    }

    private void OnColorPickerHueMove(object sender, MouseEventArgs e)
    {
        if (_hueDragging) SampleHue(e.GetPosition(ColorPickerHue));
    }

    private void OnColorPickerHueUp(object sender, MouseButtonEventArgs e)
    {
        _hueDragging = false;
        ColorPickerHue.ReleaseMouseCapture();
    }

    private void SampleSv(Point p)
    {
        if (ColorPickerSv.Width <= 0 || ColorPickerSv.Height <= 0) return;
        _pickerSat = Math.Clamp(p.X / ColorPickerSv.Width, 0, 1);
        _pickerVal = Math.Clamp(1 - p.Y / ColorPickerSv.Height, 0, 1);
        ApplyCustomColor(AnnotationStyle.FromHsv(_pickerHue, _pickerSat, _pickerVal));
        SyncColorPickerInputs(AnnotationStyle.FromHsv(_pickerHue, _pickerSat, _pickerVal));
    }

    private void SampleHue(Point p)
    {
        if (ColorPickerHue.Width <= 0) return;
        _pickerHue = Math.Clamp(p.X / ColorPickerHue.Width, 0, 1) * 360;
        ApplyCustomColor(AnnotationStyle.FromHsv(_pickerHue, _pickerSat, _pickerVal));
        SyncColorPickerInputs(AnnotationStyle.FromHsv(_pickerHue, _pickerSat, _pickerVal));
    }

    private void RestyleSelectedLineStyle(LineBorderStyle style)
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta) return;
        if (meta.Type is not (AnnotationData.TypeLine or AnnotationData.TypeArrow
            or AnnotationData.TypeRectangle or AnnotationData.TypeEllipse
            or AnnotationData.TypeText or AnnotationData.TypeCallout))
            return;
        var before = meta.Clone();
        var after = meta.Clone();
        after.LineStyle = AnnotationStyle.ToStorageName(style);

        if (meta.Type is AnnotationData.TypeRectangle or AnnotationData.TypeEllipse)
        {
            var bounds = GetCanvasBounds(fe);
            var stroke = after.Color is string hx && TryParseColor(hx, out var sc) ? sc : Colors.White;
            var fill = AnnotationStyle.FillColorFrom(after, stroke);
            double thickness = after.Thickness ?? (fe is Shape live ? live.StrokeThickness : 2);
            var rebuilt = AnnotationFactory.CreateBoxShape(
                after.Type, bounds, stroke, fill, thickness, style);
            if (fill.A == 0)
                rebuilt.Fill = ShapeFillBrush.CreateFromName(after.Fill, stroke);
            rebuilt.Tag = after;
            var original = fe;
            ReplaceCanvasElement(original, rebuilt);
            Push(new EditorAction(
                undo: () =>
                {
                    ReplaceCanvasElement(rebuilt, original);
                    original.Tag = before;
                    if (ReferenceEquals(_selected, rebuilt) || ReferenceEquals(_selected, original))
                        Select(original);
                },
                redo: () =>
                {
                    ReplaceCanvasElement(original, rebuilt);
                    rebuilt.Tag = after;
                    if (ReferenceEquals(_selected, original) || ReferenceEquals(_selected, rebuilt))
                        Select(rebuilt);
                }),
                apply: false);
            Select(rebuilt);
            return;
        }

        if (fe is Shape shape)
            AnnotationFactory.ApplyDash(shape, style == LineBorderStyle.Cloud ? LineBorderStyle.Solid : style);
        if (fe is CalloutAnnotation callout)
            ApplyStyleSnapshot(callout, SnapshotStyle(callout, after));
        fe.Tag = after;
        Push(new EditorAction(
            undo: () =>
            {
                fe.Tag = before;
                if (fe is Shape s) AnnotationFactory.ApplyDash(s, AnnotationStyle.LineStyleFrom(before));
                if (fe is CalloutAnnotation c) ApplyStyleSnapshot(c, SnapshotStyle(c, before));
            },
            redo: () =>
            {
                fe.Tag = after;
                if (fe is Shape s) AnnotationFactory.ApplyDash(s, AnnotationStyle.LineStyleFrom(after));
                if (fe is CalloutAnnotation c) ApplyStyleSnapshot(c, SnapshotStyle(c, after));
            }),
            apply: false);
    }

    private void ReplaceCanvasElement(UIElement oldEl, UIElement newEl)
    {
        if (ReferenceEquals(oldEl, newEl)) return;
        int idx = AnnotationCanvas.Children.IndexOf(oldEl);
        if (idx >= 0) AnnotationCanvas.Children.RemoveAt(idx);
        else idx = AnnotationCanvas.Children.Count;
        if (AnnotationCanvas.Children.Contains(newEl))
            AnnotationCanvas.Children.Remove(newEl);
        AnnotationCanvas.Children.Insert(Math.Min(idx, AnnotationCanvas.Children.Count), newEl);
        if (ReferenceEquals(_selected, oldEl)) _selected = newEl;
    }

    /// <summary>Parses "#RGB", "#RRGGBB", "#AARRGGBB" or bare hex; tolerates a missing leading '#'.</summary>
    private static bool TryParseHexLoose(string text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        try
        {
            if (ColorConverter.ConvertFromString(s) is Color c)
            {
                color = c;
                return true;
            }
        }
        catch
        {
            // invalid hex string — leave color as the default and report failure
        }
        return false;
    }

}
