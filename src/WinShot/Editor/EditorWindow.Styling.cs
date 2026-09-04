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
        var targets = RestyleTargets().OfType<FrameworkElement>()
            .Where(el => el.Tag is AnnotationData)
            .Where(el => CanRestyle(el, color, thickness, fill) ||
                         (opacity is not null && CanSetOpacity(el)) ||
                         fillColor is not null)
            .ToList();
        if (targets.Count == 0) return;

        var steps = new List<(UIElement Element, StyleSnapshot Before, StyleSnapshot After)>();
        foreach (FrameworkElement fe in targets)
        {
            var meta = (AnnotationData)fe.Tag!;
            Color? useColor = color;
            // A color pick (swatch / eyedropper) is opaque, but the annotation may carry a
            // reduced alpha — from the highlighter's translucent base or an earlier opacity
            // change. Preserve that existing alpha so changing the hue never silently resets
            // opacity. An explicit opacity change (opacity != null) overrides this below.
            if (useColor is Color picked && opacity is null && picked.A == 255)
            {
                byte fallback = meta.Type == AnnotationData.TypeHighlighter ? (byte)0x80 : (byte)0xFF;
                byte alpha = meta.Color is string hex0 && TryParseColor(hex0, out var cur) ? cur.A : fallback;
                useColor = Color.FromArgb(alpha, picked.R, picked.G, picked.B);
            }

            var before = SnapshotStyle(fe, meta);
            var after = before.With(useColor, thickness, fill, opacity, fillColor);
            ApplyStyleSnapshot(fe, after);
            steps.Add((fe, before, after));
        }

        Push(new EditorAction(
            undo: () => { foreach (var step in steps) ApplyStyleSnapshot(step.Element, step.Before); },
            redo: () => { foreach (var step in steps) ApplyStyleSnapshot(step.Element, step.After); }), apply: false);
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
            AnnotationData.TypeText => true,
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

            if (Meta.Type is AnnotationData.TypeRectangle or AnnotationData.TypeEllipse
                or AnnotationData.TypeCallout or AnnotationData.TypeText)
                (c, fc) = AnnotationStyle.EnforceOneVisible(c, fc);

            var meta = Meta.Clone();
            meta.Color = ToHex(c);
            if (thickness is not null) meta.Thickness = t;
            if (fill is not null) meta.Fill = f.ToString();
            if (fillColor is not null || Meta.Type is AnnotationData.TypeRectangle
                or AnnotationData.TypeEllipse or AnnotationData.TypeText or AnnotationData.TypeCallout)
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
                    ink.Stroke = Brushes.Transparent;
                    ink.StrokeThickness = 0;
                    if (meta.Type == AnnotationData.TypeHighlighter)
                        RefreshHighlighterFill(ink, snap.StrokeColor);
                    else
                        ink.Fill = stroke;
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
                        cloud.Data = CloudPath.ForRectangle(
                            new Rect(0, 0, Math.Max(1, boxLike.Width), Math.Max(1, boxLike.Height)),
                            snap.Meta.CloudIntensity ?? _cloudIntensity, snap.Thickness);
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
                    var layout = CalloutLayout.FromParts(
                        new Point(cp[0][0], cp[0][1]), new Point(cp[1][0], cp[1][1]), box,
                        CalloutDescender(snap.Meta));
                    var (head, _) = AnnotationStyle.HeadsFrom(snap.Meta);
                    callout.Apply(layout, snap.Meta.Text ?? callout.Text, snap.StrokeColor, snap.FillColor,
                        snap.Thickness, head, AnnotationStyle.LineStyleFrom(snap.Meta),
                        snap.Meta.FontSize ?? CalloutLayout.DefaultFontSize, snap.Meta.FontFamily,
                        CalloutFontColor(snap.Meta));
                    ApplyTextFormat(callout, snap.Meta);
                }
                break;
            case AnnotationData.TypeText:
                // Survey: the stroke swatch patches only `stroke` (the border rect); the
                // letters are `fill`, written solely by the rich-text font-colour swatch
                // (PDFViewer.jsx:7445-7467, TextEditOverlay.jsx:302).
                ApplyTextColor(element, new SolidColorBrush(
                    AnnotationStyle.TextGlyphColor(snap.Meta, snap.StrokeColor)));
                if (element is Grid textBox)
                {
                    foreach (var child in textBox.Children)
                    {
                        if (child is Shape chrome)
                        {
                            chrome.Fill = snap.FillColor.A == 0
                                ? Brushes.Transparent
                                : new SolidColorBrush(snap.FillColor);
                            chrome.Stroke = snap.Thickness > 0 ? stroke : Brushes.Transparent;
                            chrome.StrokeThickness = Math.Max(0, snap.Thickness);
                            AnnotationFactory.ApplyDash(chrome,
                                AnnotationStyle.LineStyleFrom(snap.Meta) == LineBorderStyle.Cloud
                                    ? LineBorderStyle.Solid
                                    : AnnotationStyle.LineStyleFrom(snap.Meta));
                        }
                    }
                }
                break;
            case AnnotationData.TypeStep:
                if (element is Grid badge)
                {
                    // Survey's counter is a filled path with stroke="none" and a label that is
                    // always numberColor (default white), never luminance-derived.
                    foreach (var child in badge.Children)
                    {
                        if (child is Shape pin)
                        {
                            pin.Fill = stroke;
                            pin.Stroke = null;
                        }
                        else if (child is TextBlock digit)
                        {
                            digit.Foreground = new SolidColorBrush(AnnotationFactory.StepNumberColor);
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
                    if (child is not Shape)
                        ApplyTextColor(child, foreground);
                break;
        }
    }

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// With a text/callout editor open Survey keeps the object SELECTED, so the top-row
    /// stroke/width/fill/line-style controls keep patching it (PDFViewer.jsx:7498-7521,
    /// SVGAnnotationLayer.jsx:1051-1055). WinShot clears the selection to hide the handles,
    /// so hand the restyle path the edit target explicitly.
    /// </summary>
    private UIElement? EditTarget() => _activeText is null
        ? null
        : (_activeText.Tag as UIElement) ?? _reeditSource;

    /// <summary>
    /// What a style control patches. The selection, or — while a text/callout editor is open,
    /// which is when WinShot alone drops the selection — the object being edited. Only the
    /// STYLE paths take that fallback; move/delete/group keep reading SelectionList().
    /// </summary>
    private IReadOnlyList<UIElement> RestyleTargets()
    {
        var selected = SelectionList();
        if (selected.Count > 0) return selected;
        return EditTarget() is { } editing ? new[] { editing } : Array.Empty<UIElement>();
    }

    private void RestyleSelectedTextFormat(Action<AnnotationData> patch)
    {
        // Survey's toolbar mutates the OPEN editor through richTextEditor.api.* — it never
        // commits first (AppShell.jsx:1756-1975). With an editor up, that is the only target.
        if (_activeText is { } editor)
        {
            RestyleOpenEditor(editor);
            // A callout's style is written through to the stored object AT CLICK TIME, with
            // its own undo checkpoint (TextEditOverlay.jsx:280-303 applyStyle ->
            // onCalloutTextStyleChange, PDFViewer.jsx:3504-3519). Plain text has no such
            // write-back in Survey — it rides the single commit CommitText already pushes.
            if (editor.Tag is CalloutAnnotation live)
            {
                // ApplyCalloutMeta renders meta.Text, which is still the PRE-edit string while
                // the overlay owns the text — hand it what is actually on screen instead, or a
                // style click silently reverts everything typed since the editor opened.
                RestyleOneTextFormat(live, patch, editor.Text ?? string.Empty);
                // A bigger font needs more lines: Survey re-measures scrollHeight on the same
                // click (applyStyle -> rAF(broadcastLiveBounds)) rather than waiting for a
                // keystroke, so the box grows under the new metrics right away.
                GrowCalloutEditor(editor, live);
                // CalloutAnnotation.Apply re-shows the label; the editor sits on top of it
                // for as long as the edit is open.
                live.Label.Visibility = Visibility.Collapsed;
                // A font-size change also moves the box's buffered bottom edge, so the editor
                // has to follow it (Survey re-broadcasts live bounds on the same click).
                editor.Height = live.Layout.RenderBox.Height;
                UpdateSelectionVisual();
            }
            return;
        }
        foreach (UIElement el in RestyleTargets().ToArray())
            RestyleOneTextFormat(el, patch);
        UpdateSelectionVisual();
    }

    /// <summary>Applies the current text-format state live to the in-place editor.</summary>
    private void RestyleOpenEditor(TextBox tb)
    {
        bool callout = tb.Tag is CalloutAnnotation;
        // setFontFamily / setFontSize are plain members of the same rich-text api and apply to
        // callouts exactly as to plain text (TextEditOverlay.jsx:297, :301, :76-77).
        tb.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(_textFontFamily)
            ? (callout ? CalloutLayout.DefaultFontFamily : AnnotationFactory.DefaultTextFontFamily)
            : _textFontFamily);
        tb.FontSize = AnnotationStyle.ClampTextSize(_textFontSize);
        // Survey's callout line height is (lineHeight || 1) * 1.13, plain text's 1.16 * 1.13.
        tb.WithLineHeight(tb.FontSize * (callout ? 1.13 : AnnotationFactory.TextLineHeightFactor));
        // Callout text is always vertically centred (buildCalloutTextContentStyle).
        if (!callout)
            tb.VerticalContentAlignment = AnnotationFactory.ParseVerticalAlign(_textVerticalAlign);
        if (_textFontColor is Color fc)
        {
            tb.Foreground = new SolidColorBrush(fc);
            // The committed callout label lives on the visual, not the editor, so the live
            // preview underneath has to be recoloured with it. The meta write-back (and its
            // undo entry) belongs to RestyleSelectedTextFormat's callout branch.
            if (tb.Tag is CalloutAnnotation live) live.Label.Foreground = tb.Foreground;
        }
        tb.FontWeight = _textBold ? FontWeights.Bold : FontWeights.Normal;
        tb.FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal;
        tb.TextAlignment = AnnotationFactory.ParseAlign(_textAlign);
        tb.TextDecorations = null;
        if (_textUnderline || _textStrike)
        {
            var decorations = new TextDecorationCollection();
            if (_textUnderline) decorations.Add(TextDecorations.Underline);
            if (_textStrike) decorations.Add(TextDecorations.Strikethrough);
            tb.TextDecorations = decorations;
        }
        // Survey re-measures and re-broadcasts the live bounds on the style click itself
        // (applyStyle -> rAF(broadcastLiveBounds), TextEditOverlay.jsx:286-288), so the box
        // and its chrome grow immediately instead of waiting for the next keystroke.
        if (!callout) GrowTextEditor(tb, _textEditorFloorH);
    }

    /// <param name="liveText">
    /// Text currently in the open editor, when there is one. It is only rendered, never stored:
    /// the commit owns the text, so undo/redo of this style change replay the stored string.
    /// </param>
    /// <param name="patch">
    /// The ONE field this toolbar action owns. Survey's applyStyle spreads a single key onto a
    /// single object's styleRef and calloutStylePatch maps one action to one field
    /// (TextEditOverlay.jsx:70-82, :281-291) — stamping every format global onto every
    /// selected object is what silently shrank a 48pt box on a Bold click.
    /// </param>
    private void RestyleOneTextFormat(UIElement element, Action<AnnotationData> patch, string? liveText = null)
    {
        if (element is not FrameworkElement fe || fe.Tag is not AnnotationData meta) return;
        if (meta.Type is not (AnnotationData.TypeText or AnnotationData.TypeCallout)) return;
        var before = meta.Clone();
        var after = meta.Clone();
        patch(after);
        if (meta.Type == AnnotationData.TypeText)
        {
            var beforeEl = fe;
            var afterEl = CreateTextVisual(after);
            CopyPlacement(beforeEl, afterEl);
            ReplaceAnnotation(beforeEl, afterEl);
            Push(new EditorAction(
                undo: () => ReplaceAnnotation(afterEl, beforeEl),
                redo: () => ReplaceAnnotation(beforeEl, afterEl)), apply: false);
        }
        else if (fe is CalloutAnnotation callout)
        {
            ApplyCalloutMeta(callout, callout.Layout, after, liveText);
            fe.Tag = after;
            Push(new EditorAction(
                undo: () =>
                {
                    if (fe is CalloutAnnotation c0)
                    {
                        ApplyCalloutMeta(c0, c0.Layout, before);
                        fe.Tag = before;
                    }
                },
                redo: () =>
                {
                    if (fe is CalloutAnnotation c1)
                    {
                        ApplyCalloutMeta(c1, c1.Layout, after);
                        fe.Tag = after;
                    }
                }), apply: false);
        }
        else
        {
            ApplyTextFormat(fe, after);
            Push(new EditorAction(
                undo: () => ApplyTextFormat(fe, before),
                redo: () => ApplyTextFormat(fe, after)), apply: false);
        }
    }

    private FrameworkElement CreateTextVisual(AnnotationData a)
    {
        Enum.TryParse(a.Style, out TextStyle style);
        Color stroke = a.Color is string hex && TryParseColor(hex, out var c) ? c : Colors.White;
        Color fill = AnnotationStyle.FillColorFrom(a, stroke);
        double fontSize = a.FontSize is double fs && fs > 0 ? fs : 16;
        double? boxW = a.Rect is { Length: >= 4 } rect ? rect[2] : null;
        double? boxH = a.Rect is { Length: >= 4 } box ? box[3] : null;
        var label = AnnotationFactory.CreateStyledTextLabel(
            a.Text ?? "", new SolidColorBrush(AnnotationStyle.TextGlyphColor(a, stroke)), fontSize, style,
            a.FontFamily ?? AnnotationFactory.DefaultTextFontFamily, a.Bold == true,
            a.Italic == true, a.Underline == true, a.Strike == true,
            AnnotationFactory.ParseAlign(a.Align), a.VerticalAlign,
            boxW, boxH, fill, a.Thickness ?? 0, AnnotationStyle.LineStyleFrom(a),
            new SolidColorBrush(stroke));
        label.Tag = a;
        return label;
    }

    private static void CopyPlacement(FrameworkElement from, FrameworkElement to)
    {
        Canvas.SetLeft(to, Canvas.GetLeft(from));
        Canvas.SetTop(to, Canvas.GetTop(from));
        to.RenderTransform = from.RenderTransform is TranslateTransform t
            ? new TranslateTransform(t.X, t.Y)
            : from.RenderTransform;
    }

    private void ReplaceAnnotation(UIElement oldEl, UIElement newEl)
    {
        int i = AnnotationCanvas.Children.IndexOf(oldEl);
        AnnotationCanvas.Children.Remove(oldEl);
        if (i < 0) AnnotationCanvas.Children.Add(newEl);
        else AnnotationCanvas.Children.Insert(Math.Min(i, AnnotationCanvas.Children.Count), newEl);
        if (ReferenceEquals(_selected, oldEl)) _selected = newEl;
        int si = _selection.IndexOf(oldEl);
        if (si >= 0) _selection[si] = newEl;
    }

    /// <summary>Survey descenderBuffer for a callout's stored font size.</summary>
    private static double CalloutDescender(AnnotationData meta) =>
        (meta.FontSize ?? CalloutLayout.DefaultFontSize) * CalloutLayout.DescenderRatio;

    private static Color? CalloutFontColor(AnnotationData meta) =>
        meta.FontColor is string hex && TryParseColor(hex, out var c) ? c : null;

    /// <summary>
    /// Survey spreads the whole callout on every geometry mutation ({...c, ...patch}), so
    /// style survives every drag. This is the one funnel that redraws a callout from its
    /// stored AnnotationData - colours, dash, font AND the text format flags.
    /// </summary>
    private static void ApplyCalloutMeta(
        CalloutAnnotation callout, CalloutLayout layout, AnnotationData meta, string? liveText = null)
    {
        var (head, _) = AnnotationStyle.HeadsFrom(meta);
        var stroke = meta.Color is string hex && TryParseColor(hex, out var sc) ? sc : Colors.White;
        // A font-size change moves the buffered bottom edge, so the join has to be re-derived;
        // an unchanged buffer leaves a free-drag (raw) layout untouched.
        double buffer = CalloutDescender(meta);
        if (Math.Abs(layout.Descender - buffer) > 0.001) layout = layout.WithDescender(buffer);
        callout.Apply(layout, liveText ?? meta.Text ?? callout.Text, stroke,
            AnnotationStyle.FillColorFrom(meta, stroke), meta.Thickness ?? 2, head,
            AnnotationStyle.LineStyleFrom(meta), meta.FontSize ?? CalloutLayout.DefaultFontSize,
            meta.FontFamily, CalloutFontColor(meta));
        ApplyTextFormat(callout, meta);
    }

    private static void ApplyTextFormat(FrameworkElement fe, AnnotationData meta)
    {
        fe.Tag = meta;
        var align = AnnotationFactory.ParseAlign(meta.Align);
        var family = new FontFamily(string.IsNullOrWhiteSpace(meta.FontFamily)
            ? (fe is CalloutAnnotation ? CalloutLayout.DefaultFontFamily : AnnotationFactory.DefaultTextFontFamily)
            : meta.FontFamily);
        bool bold = meta.Bold == true;
        bool italic = meta.Italic == true;
        TextBlock? tb = fe as TextBlock
            ?? (fe as Border)?.Child as TextBlock
            ?? (fe as Grid)?.Children.OfType<TextBlock>().FirstOrDefault()
            ?? (fe as CalloutAnnotation)?.Label;
        if (tb is null) return;
        // Survey's callout text is ALWAYS vertically centred (buildCalloutTextContentStyle
        // hardcodes justifyContent 'center'); only plain text honours a stored vertical align.
        tb.VerticalAlignment = fe is CalloutAnnotation
            ? VerticalAlignment.Center
            : AnnotationFactory.ParseVerticalAlign(meta.VerticalAlign);
        tb.FontFamily = family;
        tb.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
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

    private void OnStepModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            _stepLetters = string.Equals(tag, "Letter", StringComparison.Ordinal);
    }

    private void OnResetStepCounter(object sender, RoutedEventArgs e) => _nextStep = 1;

    private void OnTextStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        // Plain/Bold/Outline/Pill/Huge chrome was removed to match Survey (font + B/I/U/S).
        if (_syncingContext) return;
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag && Enum.TryParse(tag, out TextStyle style))
        {
            _textStyle = style;
            if (IsLoaded) RestyleSelectedTextFormat(m => m.Style = style.ToString());
        }
    }

    // -------------------------------------------------- arrow style (parity tail)

    /// <summary>
    /// Arrow-style dropdown: sets the default style for new straight arrows, and re-shapes
    /// the currently selected straight arrow in place (one undoable step) so a chosen look
    /// can be applied after the fact too.
    /// </summary>
    private void OnArrowStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag)
            return;
        if (Enum.TryParse(tag, out ArrowStyle legacy))
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
            EditorShellContract.ContextFor(_tool, false).HasFlag(EditorContextControls.FillStrokeTabs))
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

    private void OnEffectStrengthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingContext) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag || !Enum.TryParse(tag, out EffectStrength strength))
            return;
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
        // Survey's useState('grid') resets on every mount (CompactColorPicker.jsx:86).
        PickerGridModeBtn.IsChecked = true;
        ColorPickerPopup.IsOpen = true;
    }

    /// <summary>
    /// Survey's picker is MODAL between the preset grid and the spectrum — `mode === 'grid' ?
    /// ... : ...` (CompactColorPicker.jsx:367, :430). Only one is ever on screen.
    /// </summary>
    private void OnPickerModeChecked(object sender, RoutedEventArgs e)
    {
        if (ColorPickerGrid is null || ColorPickerSpectrum is null) return;
        bool grid = PickerGridModeBtn.IsChecked == true;
        ColorPickerGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        ColorPickerSpectrum.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Builds Survey's shared preset grid once: PRESET_COLORS is 16 cells over 8 columns — a
    /// transparent cell, a full hue wheel and three greys (CompactColorPicker.jsx:13-18, rendered
    /// at :367-369). There is exactly one preset palette in the app, so the font-colour grid
    /// (BuildFontColorPalette) fills itself from the same list through the same swatch factory.
    /// </summary>
    private void BuildColorPickerPalette()
    {
        if (_colorPickerBuilt) return;
        _colorPickerBuilt = true;

        foreach (Color c in AnnotationStyle.PickerPresets)
        {
            var btn = MakePickerSwatch(c, "Color");
            btn.Click += OnColorPickerGridPick;
            ColorPickerGrid.Children.Add(btn);
        }
        // The ring is set by SyncColorPickerInputs, which the caller runs straight after this.
    }

    /// <summary>
    /// One preset cell. The transparent cell is Survey's white ground under a #cfcfcf checker of
    /// 4px squares (CompactColorPicker.jsx:386-396) — not a dark checker on nothing.
    /// </summary>
    private Button MakePickerSwatch(Color c, string role)
    {
        string hex = AnnotationStyle.ToRgbHex(c);
        Brush fill;
        if (c.A == 0)
        {
            var checker = new DrawingGroup();
            checker.Children.Add(new GeometryDrawing(
                Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            var grey = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xCF));
            checker.Children.Add(new GeometryDrawing(
                grey, null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
            checker.Children.Add(new GeometryDrawing(
                grey, null, new RectangleGeometry(new Rect(4, 4, 4, 4))));
            fill = new DrawingBrush
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 8, 8),
                ViewportUnits = BrushMappingMode.Absolute,
                Drawing = checker,
            };
        }
        else
        {
            fill = new SolidColorBrush(c);
        }

        var btn = new Button
        {
            Style = (Style)FindResource("PickerSwatchButton"),
            ToolTip = c.A == 0 ? "Transparent" : hex,
            Tag = c,
            Background = fill,
        };
        AutomationProperties.SetName(btn, c.A == 0 ? "Transparent" : $"{role} {hex}");
        AutomationProperties.SetHelpText(btn, c.A == 0
            ? "Set the current color well to fully transparent."
            : $"Use {role.ToLowerInvariant()} {hex}.");
        return btn;
    }

    /// <summary>
    /// Rings the applied colour: `border: isSelected ? '2px solid white' : '1px solid #3a4252'`
    /// (CompactColorPicker.jsx:411). Without it the popover never shows which swatch is live.
    /// </summary>
    private static void SyncPickerSelection(Panel? grid, Color? current)
    {
        if (grid is null) return;
        foreach (Button b in grid.Children.OfType<Button>())
        {
            bool selected = b.Tag is Color c && current is Color cur &&
                            (c.A == 0 ? cur.A == 0 : cur.A != 0 && AnnotationStyle.ToRgbHex(c) == AnnotationStyle.ToRgbHex(cur));
            b.BorderThickness = new Thickness(selected ? 2 : 1);
            b.BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x52));
        }
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
        {
            ColorPickerPreview.Fill = new SolidColorBrush(c);
            ApplyCustomColor(c);
        }
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
        // Every path that moves the picker's colour (grid, hex, spectrum, hue, opacity) lands
        // here, so this is the one place the selected-swatch ring has to be re-derived.
        SyncPickerSelection(ColorPickerGrid, c);
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
        {
            double knob = HueCursor.Width;
            Canvas.SetLeft(HueCursor, Math.Clamp(h / 360.0 * ColorPickerHue.Width - knob / 2,
                0, ColorPickerHue.Width - knob));
        }
        // Survey's right-aligned `{Math.round(hue)}°` readout (CompactColorPicker.jsx:526).
        if (PickerHueValue is not null)
            PickerHueValue.Text = $"{Math.Round(h)}°";
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
        if (RestyleTargets().FirstOrDefault() is not FrameworkElement fe ||
            fe.Tag is not AnnotationData meta) return;
        if (meta.Type is not (AnnotationData.TypeLine or AnnotationData.TypeArrow
            or AnnotationData.TypeRectangle or AnnotationData.TypeEllipse
            or AnnotationData.TypeText or AnnotationData.TypeCallout))
            return;
        var before = meta.Clone();
        var after = meta.Clone();
        after.LineStyle = AnnotationStyle.ToStorageName(style);
        // The Bump lives with the annotation, not the tool, so switching a rect INTO cloud
        // stamps the current toolbar value and switching out of it drops the field.
        after.CloudIntensity = style == LineBorderStyle.Cloud
            ? meta.CloudIntensity ?? _cloudIntensity
            : null;

        if (meta.Type is AnnotationData.TypeRectangle or AnnotationData.TypeEllipse)
        {
            var bounds = GetCanvasBounds(fe);
            var stroke = after.Color is string hx && TryParseColor(hx, out var sc) ? sc : Colors.White;
            var fill = AnnotationStyle.FillColorFrom(after, stroke);
            double thickness = after.Thickness ?? (fe is Shape live ? live.StrokeThickness : 2);
            var rebuilt = AnnotationFactory.CreateBoxShape(
                after.Type, bounds, stroke, fill, thickness, style,
                after.CloudIntensity ?? _cloudIntensity);
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
        if (fe is Grid textGrid && meta.Type == AnnotationData.TypeText)
        {
            foreach (var child in textGrid.Children)
                if (child is Shape chrome)
                    AnnotationFactory.ApplyDash(chrome, style == LineBorderStyle.Cloud ? LineBorderStyle.Solid : style);
        }
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
