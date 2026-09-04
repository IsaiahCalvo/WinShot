using System.Globalization;
using System.Windows;
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
    // ------------------------------------------------------------- text tool

    private void CommitCallout(Point tip, Point boxOrigin)
    {
        // Survey's callout font is fixed at creation (defaultCalloutStyle: Arial 14) and never
        // reads the text tool's toolbar state.
        double fontSize = CalloutLayout.DefaultFontSize;
        string fontFamily = CalloutLayout.DefaultFontFamily;
        var layout = CalloutLayout.FromDrag(tip, boxOrigin,
            descender: fontSize * CalloutLayout.DescenderRatio);
        var (stroke, fill) = AnnotationStyle.EnforceOneVisible(CurrentStrokeBrush(), CurrentFillBrush());
        double thickness = AnnotationStyle.ClampSize(_shapeSize);
        var lineStyle = _lineStyle == LineBorderStyle.Cloud ? LineBorderStyle.Solid : _lineStyle;
        // Survey seeds a new callout's fontColor from the picker (Callout creation commit),
        // and the field stays independent of the border colour from then on.
        Color? fontColor = _textFontColor;
        var visual = AnnotationFactory.CreateCallout(
            layout, "", stroke, fill, thickness, _arrowhead, lineStyle, fontSize, fontFamily, fontColor);
        visual.Tag = AnnotationData.ForCallout(
            layout, "", stroke, fill, thickness, _arrowhead, lineStyle, fontSize, fontFamily, fontColor);
        AnnotationCanvas.Children.Add(visual);

        var tb = AnnotationFactory.CreateTextEditor(new SolidColorBrush(fontColor ?? stroke), fontSize);
        StyleCalloutEditor(tb, fontFamily);
        tb.Width = layout.RenderBox.Width;
        tb.Height = layout.RenderBox.Height;
        tb.Tag = visual;
        Canvas.SetLeft(tb, layout.Box.X);
        Canvas.SetTop(tb, layout.Box.Y);
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        tb.AcceptsReturn = true;
        tb.TextWrapping = TextWrapping.Wrap;
        HookCalloutGrow(tb, visual);
        tb.LostKeyboardFocus += (_, ev) =>
        {
            // Same opt-out the plain-text editor uses: the rich-text strip restyles the OPEN
            // callout editor instead of committing it (TextEditOverlay.jsx:445-447).
            if (KeepEditorOpenFor(ev.NewFocus as DependencyObject, tb)) return;
            CommitCalloutText();
        };
        tb.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape)
            {
                AnnotationCanvas.Children.Remove(tb);
                _activeText = null;
                AnnotationCanvas.Children.Remove(visual);
                ev.Handled = true;
            }
        };
        SeedTextFormatFromMeta((AnnotationData)visual.Tag);
        UpdateContextPanels(); // brings up the rich-text row for the now-open editor
        Dispatcher.InvokeAsync(() => tb.Focus());
    }

    /// <summary>
    /// Survey seeds each TextEditOverlay's styleRef from the annotation being edited, so an
    /// editor never inherits the previously formatted object's style
    /// (TextEditOverlay.jsx:143-156). WinShot's _text* state is window-level, so every
    /// editor-open path has to reload it explicitly or the globals leak across annotations.
    /// </summary>
    private void SeedTextFormatFromMeta(AnnotationData meta)
    {
        bool callout = meta.Type == AnnotationData.TypeCallout;
        _textFontFamily = string.IsNullOrWhiteSpace(meta.FontFamily)
            ? (callout ? CalloutLayout.DefaultFontFamily : NewTextFontFamily)
            : meta.FontFamily;
        _textFontSize = AnnotationStyle.ClampTextSize(meta.FontSize is double fs && fs > 0
            ? fs
            : (callout ? CalloutLayout.DefaultFontSize : NewTextFontSize));
        _textBold = meta.Bold ?? false;
        _textItalic = meta.Italic ?? false;
        _textUnderline = meta.Underline ?? false;
        _textStrike = meta.Strike ?? false;
        _textAlign = string.IsNullOrWhiteSpace(meta.Align) ? "left" : meta.Align;
        _textVerticalAlign = string.IsNullOrWhiteSpace(meta.VerticalAlign) ? "top" : meta.VerticalAlign;
        _textStyle = Enum.TryParse(meta.Style, out TextStyle st) ? st : TextStyle.Plain;
        // Absent fontColor means "never recoloured", i.e. the stroke-colour fallback.
        _textFontColor = meta.FontColor is string fcHex && TryParseColor(fcHex, out var fc) ? fc : null;
    }

    /// <summary>
    /// Survey's hard rule: no visual difference between edit mode and not edit mode
    /// (calloutEditAdapter.js:214-220 - transparent chrome, the callout's own box border is
    /// the only outline; cursorColor #007AFF).
    /// </summary>
    private static void StyleCalloutEditor(TextBox tb, string? fontFamily)
    {
        tb.BorderThickness = new Thickness(0);
        tb.BorderBrush = Brushes.Transparent;
        tb.Background = Brushes.Transparent;
        tb.FontWeight = FontWeights.Normal;
        tb.FontFamily = new FontFamily(
            string.IsNullOrWhiteSpace(fontFamily) ? CalloutLayout.DefaultFontFamily : fontFamily);
        // Survey insets the text horizontally only and centres it vertically.
        tb.Padding = new Thickness(CalloutLayout.TextPadding, 0, CalloutLayout.TextPadding, 0);
        tb.VerticalContentAlignment = VerticalAlignment.Center;
        tb.CaretBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));
        // Survey's callout line height is (lineHeight || 1) * 1.13, not plain text's 1.16 * 1.13
        // (svgAnnotationRenderers.jsx:1133).
        tb.WithLineHeight(tb.FontSize * 1.13);
    }

    /// <summary>
    /// Survey's Fabric textbox auto-grows vertically as the user types and the live bounds
    /// retract line1 to the growing edge (TextEditOverlay.jsx:239-270,
    /// svgAnnotationRenderers.jsx:1341-1352). Grow-only: never below the drag-created height.
    /// </summary>
    private void HookCalloutGrow(TextBox tb, CalloutAnnotation visual)
    {
        // Kept in a field, not a closure local, so a style click can re-run the same grow pass
        // (Survey re-measures on applyStyle's rAF too, not only on a keystroke).
        _calloutEditorFloorH = visual.Layout.Box.Height;
        tb.TextChanged += (_, _) => GrowCalloutEditor(tb, visual);
    }

    /// <summary>Re-measures the live callout text and grows the box (and editor) to fit it.</summary>
    private void GrowCalloutEditor(TextBox tb, CalloutAnnotation visual)
    {
        var layout = visual.Layout;
        double grown = Math.Max(_calloutEditorFloorH,
            MeasureCalloutText(visual, tb.Text ?? "", layout.Box.Width));
        if (Math.Abs(grown - layout.Box.Height) < 0.5) return;
        ApplyCalloutLayout(visual, layout.WithBox(
            new Rect(layout.Box.X, layout.Box.Y, layout.Box.Width, grown)));
        tb.Height = visual.Layout.RenderBox.Height;
        UpdateSelectionVisual();
    }

    /// <summary>Wrapped height of <paramref name="text"/> inside a callout box of that width.</summary>
    private static double MeasureCalloutText(CalloutAnnotation visual, string text, double width)
    {
        var probe = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = visual.Label.FontFamily,
            FontSize = visual.Label.FontSize,
            FontWeight = visual.Label.FontWeight,
            FontStyle = visual.Label.FontStyle,
        };
        probe.Measure(new Size(
            Math.Max(1, width - 2 * CalloutLayout.TextPadding), double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }

    private void CommitCalloutText()
    {
        var tb = _activeText;
        if (tb is null || tb.Tag is not CalloutAnnotation visual) return;
        _activeText = null;
        string text = tb.Text ?? "";
        AnnotationCanvas.Children.Remove(tb);
        visual.Label.Visibility = Visibility.Visible;

        if (_calloutReediting)
        {
            _calloutReediting = false;
            string previous = _calloutReeditBefore;
            var layoutBefore = _calloutReeditLayoutBefore;
            _calloutReeditLayoutBefore = null;
            if (string.IsNullOrWhiteSpace(text))
                text = previous;
            visual.Text = text;
            if (visual.Tag is AnnotationData re)
            {
                var before = re.Clone();
                re.Text = text;
                visual.Tag = re;
                // The box may have auto-grown while typing, so undo restores the layout too.
                var layoutAfter = visual.Layout;
                if (before.Text != text || (layoutBefore is CalloutLayout lb && lb.Box != layoutAfter.Box))
                {
                    Push(new EditorAction(
                        undo: () =>
                        {
                            visual.Text = previous;
                            if (visual.Tag is AnnotationData u) { u.Text = previous; visual.Tag = u; }
                            if (layoutBefore is CalloutLayout l) ApplyCalloutLayout(visual, l);
                        },
                        redo: () =>
                        {
                            visual.Text = text;
                            if (visual.Tag is AnnotationData r) { r.Text = text; visual.Tag = r; }
                            ApplyCalloutLayout(visual, layoutAfter);
                        }),
                        apply: false);
                }
            }
            UpdateSelectionVisual();
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            AnnotationCanvas.Children.Remove(visual);
            return;
        }
        visual.Text = text;
        if (visual.Tag is AnnotationData meta)
        {
            meta.Text = text;
            visual.Tag = meta;
        }
        // Survey's commit clears the edit state without selecting the result
        // (PDFViewer.jsx:31936-31942), so no handles appear on the new callout.
        PushAddElement(visual, select: false);
        UpdateSelectionVisual();
    }

    private void PlaceText(Point pos) => PlaceTextInBox(new Rect(pos.X, pos.Y, 0, 0));

    /// <summary>Survey's desktop new-text defaults (TextEditOverlay.jsx:126-137, :47).</summary>
    private const double NewTextFontSize = 16;
    private const string NewTextFontFamily = "Helvetica";

    /// <summary>New text always commits a 1px solid black border and no fill (textEditCommit.js:127-129, :44).</summary>
    private const double NewTextStrokeWidth = 1;

    /// <summary>
    /// Survey text: the drag sets the WRAP WIDTH ONLY (PDFViewer.jsx:31532-31533 passes
    /// <c>textBoxWidth</c> and no height). The box's height is font-derived at mount
    /// (<c>fontSize * 1.16 * 1.13 + 2 * pad</c>) and grows from measured content while typing.
    /// Every new editor starts from fixed defaults — on desktop <c>newTextStyle</c> is null,
    /// so nothing carries over from the previous annotation (PDFViewer.jsx:31826).
    /// </summary>
    private void PlaceTextInBox(Rect box)
    {
        _textStyle = TextStyle.Plain;
        _textFontSize = NewTextFontSize;
        _textFontFamily = NewTextFontFamily;
        _textBold = _textItalic = _textUnderline = _textStrike = false;
        _textAlign = "left";
        _textVerticalAlign = "top";

        double fontSize = NewTextFontSize;
        var tb = AnnotationFactory.CreateTextEditor(new SolidColorBrush(_color), fontSize);
        tb.FontFamily = new FontFamily(NewTextFontFamily);
        tb.TextAlignment = TextAlignment.Left;
        tb.VerticalContentAlignment = VerticalAlignment.Top;
        tb.Tag = TextStyle.Plain;
        tb.TextWrapping = TextWrapping.Wrap;
        tb.AcceptsReturn = true;

        // Survey: outerW = max(8 + 2*pad, textBoxWidth || 160); the dragged height is discarded.
        double wrapW = box.Width >= 1
            ? Math.Max(8 + 2 * AnnotationFactory.TextPadding, box.Width)
            : AnnotationFactory.DefaultTextBoxWidth;
        tb.Width = wrapW;
        tb.Height = AnnotationFactory.SingleLineBoxHeight(fontSize);
        _activeTextBox = new Rect(box.X, box.Y, tb.Width, tb.Height);
        Canvas.SetLeft(tb, box.X);
        Canvas.SetTop(tb, box.Y);

        ShowTextPreview(box.X, box.Y, tb.Width, tb.Height,
            Colors.Transparent, NewTextStrokeWidth, LineBorderStyle.Solid);
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        HookTextEditor(tb);
        UpdateContextPanels(); // brings up the rich-text row for the now-open editor
        Dispatcher.InvokeAsync(() => tb.Focus());
    }

    /// <summary>
    /// Survey paints the box chrome during creation from the live bounds with the COMMITTED
    /// look, so nothing changes appearance when the editor closes
    /// (SVGAnnotationLayer.jsx:4831-4853, hideText=true). The editor itself is chromeless.
    /// </summary>
    private void ShowTextPreview(
        double x, double y, double w, double h, Color fill, double strokeThickness, LineBorderStyle lineStyle)
    {
        HideTextPreview();
        var rect = new Rectangle
        {
            Width = Math.Max(1, w),
            Height = Math.Max(1, h),
            Fill = fill.A == 0 ? Brushes.Transparent : new SolidColorBrush(fill),
            Stroke = strokeThickness > 0 ? Brushes.Black : Brushes.Transparent,
            StrokeThickness = Math.Max(0, strokeThickness),
            IsHitTestVisible = false,
        };
        AnnotationFactory.ApplyDash(rect, lineStyle == LineBorderStyle.Cloud ? LineBorderStyle.Solid : lineStyle);
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        AnnotationCanvas.Children.Add(rect);
        _textPreview = rect;
    }

    private void HideTextPreview()
    {
        if (_textPreview is null) return;
        AnnotationCanvas.Children.Remove(_textPreview);
        _textPreview = null;
    }

    private void BeginCalloutReEdit(CalloutAnnotation visual)
    {
        if (visual.Tag is not AnnotationData meta) return;
        Select(null);
        var layout = visual.Layout;
        Vector off = visual.RenderTransform is TranslateTransform t
            ? new Vector(t.X, t.Y)
            : new Vector();
        // Survey routes creation and double-click re-edit through the same guard: opening the
        // editor leaves the callout tool, so the next page click commits instead of drawing.
        if (_tool == EditorTool.Callout) CheckToolButton(EditorTool.Select);
        var tb = AnnotationFactory.CreateTextEditor(new SolidColorBrush(
            CalloutFontColor(meta) ?? (meta.Color is string hex && TryParseColor(hex, out var c) ? c : Colors.White)),
            meta.FontSize ?? CalloutLayout.DefaultFontSize);
        StyleCalloutEditor(tb, meta.FontFamily);
        tb.Width = layout.RenderBox.Width;
        tb.Height = layout.RenderBox.Height;
        tb.Text = meta.Text ?? visual.Text;
        tb.Tag = visual;
        tb.AcceptsReturn = true;
        tb.TextWrapping = TextWrapping.Wrap;
        Canvas.SetLeft(tb, layout.Box.X + off.X);
        Canvas.SetTop(tb, layout.Box.Y + off.Y);
        visual.Label.Visibility = Visibility.Collapsed;
        _calloutReediting = true;
        _calloutReeditBefore = meta.Text ?? visual.Text ?? "";
        _calloutReeditLayoutBefore = layout;
        HookCalloutGrow(tb, visual);
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        tb.LostKeyboardFocus += (_, ev) =>
        {
            if (KeepEditorOpenFor(ev.NewFocus as DependencyObject, tb)) return;
            CommitCalloutText();
        };
        tb.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape)
            {
                AnnotationCanvas.Children.Remove(tb);
                _activeText = null;
                visual.Label.Visibility = Visibility.Visible;
                if (_calloutReeditLayoutBefore is CalloutLayout cancelled)
                    ApplyCalloutLayout(visual, cancelled);
                _calloutReeditLayoutBefore = null;
                _calloutReediting = false;
                ev.Handled = true;
            }
        };
        SeedTextFormatFromMeta(meta);
        UpdateContextPanels(); // brings up the rich-text row for the now-open editor
        Dispatcher.InvokeAsync(() =>
        {
            tb.Focus();
            tb.SelectAll();
        });
    }

    /// <summary>
    /// Reopens a committed text annotation for editing (Select tool double-click). Reads the
    /// original style, font size, text and color from the element's stored
    /// <see cref="AnnotationData"/> rather than inferring them from the visual, so
    /// Outline/Pill/Huge survive a round-trip instead of downgrading to Plain/Bold.
    /// Works for any text element kind: TextBlock (Plain/Bold/Huge), Path (Outline),
    /// or Border (Pill).
    /// </summary>
    private void BeginTextReEdit(FrameworkElement label)
    {
        if (label.Tag is not AnnotationData meta || meta.Type != AnnotationData.TypeText)
            return;
        // CommitText rebuilds the label from the window-level _text* fields, so they have to
        // carry THIS object's formatting or the re-edit both loses its own style and stamps
        // the previously formatted annotation's style onto it.
        SeedTextFormatFromMeta(meta);
        Select(null);
        double x = Canvas.GetLeft(label), y = Canvas.GetTop(label);
        if (label.RenderTransform is TranslateTransform t)
        {
            x += t.X;
            y += t.Y;
        }

        // Removing the old label is its own undoable step; committing the editor
        // pushes the replacement add, so two Ctrl+Z steps restore the original.
        Push(new EditorAction(
            undo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(label))
                    AnnotationCanvas.Children.Add(label);
            },
            redo: () => AnnotationCanvas.Children.Remove(label)));
        // Survey's existing-text commit clones the original and only replaces text/height
        // (textEditCommit.js:190-210) — a blank re-edit keeps the box, it never deletes it.
        _reeditSource = label;

        var style = Enum.TryParse(meta.Style, out TextStyle s) ? s : TextStyle.Plain;
        double fontSize = meta.FontSize is double fs && fs > 0 ? fs : AnnotationFactory.FontSizeFor(_thickness);
        var foreground = new SolidColorBrush(
            meta.Color is string hex && TryParseColor(hex, out var c) ? c : Colors.White);

        var tb = AnnotationFactory.CreateTextEditor(foreground, fontSize);
        tb.Tag = style;
        tb.Text = meta.Text ?? string.Empty;
        tb.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(meta.FontFamily) ? NewTextFontFamily : meta.FontFamily);
        tb.FontWeight = meta.Bold == true ? FontWeights.Bold : FontWeights.Normal;
        tb.FontStyle = meta.Italic == true ? FontStyles.Italic : FontStyles.Normal;
        tb.TextAlignment = AnnotationFactory.ParseAlign(meta.Align);
        tb.VerticalContentAlignment = AnnotationFactory.ParseVerticalAlign(meta.VerticalAlign);
        tb.TextDecorations = null;
        if (meta.Underline == true || meta.Strike == true)
        {
            var decorations = new TextDecorationCollection();
            if (meta.Underline == true) decorations.Add(TextDecorations.Underline);
            if (meta.Strike == true) decorations.Add(TextDecorations.Strikethrough);
            tb.TextDecorations = decorations;
        }
        tb.TextWrapping = TextWrapping.Wrap;
        tb.AcceptsReturn = true;
        if (meta.Rect is { Length: >= 4 } box && box[2] >= 8 && box[3] >= 8)
        {
            tb.Width = box[2];
            tb.Height = box[3];
            _activeTextBox = new Rect(x, y, box[2], box[3]);
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
        }
        else
        {
            tb.Width = AnnotationFactory.DefaultTextBoxWidth;
            tb.Height = AnnotationFactory.SingleLineBoxHeight(fontSize);
            _activeTextBox = new Rect(x, y, tb.Width, tb.Height);
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
        }
        ShowTextPreview(Canvas.GetLeft(tb), Canvas.GetTop(tb), tb.Width, tb.Height,
            AnnotationStyle.FillColorFrom(meta, Colors.Transparent),
            meta.Thickness ?? NewTextStrokeWidth, AnnotationStyle.LineStyleFrom(meta));
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        HookTextEditor(tb);
        UpdateContextPanels(); // brings up the rich-text row for the now-open editor
        Dispatcher.InvokeAsync(() =>
        {
            tb.Focus();
            tb.SelectAll();
        });
    }

    /// <summary>
    /// Survey re-measures on every keystroke and grows the box (never below the mount-time
    /// floor) so the chrome and the reflowed text track live
    /// (TextEditOverlay.jsx:239-270, :584-587).
    /// </summary>
    private void HookTextEditor(TextBox tb)
    {
        // A field, not a closure local: the rich-text strip re-grows the editor on a style
        // click too, and it has no access to this scope.
        _textEditorFloorH = tb.Height;
        tb.TextChanged += (_, _) => GrowTextEditor(tb, _textEditorFloorH);
        tb.LostKeyboardFocus += (_, ev) =>
        {
            // Survey's click-outside commit explicitly opts out of the rich-text toolbar, the
            // mini toolbar and the font-colour picker (TextEditOverlay.jsx:445-447), so
            // B/I/U/S, family, size, colour and align all apply LIVE to the still-open editor.
            if (KeepEditorOpenFor(ev.NewFocus as DependencyObject, tb)) return;
            CommitText();
        };
        tb.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape) { CancelText(); ev.Handled = true; }
            else if (ev.Key == Key.Enter && !tb.AcceptsReturn)
            {
                CommitText();
                ev.Handled = true;
            }
        };
    }

    /// <summary>
    /// True when <paramref name="node"/> lives inside the format strip (WinShot's counterpart
    /// of Survey's [data-rich-text-toolbar] / [data-mini-toolbar] / [data-font-color-picker]).
    /// Popup contents are their own visual tree, so the walk follows logical parents first.
    /// </summary>
    private bool IsFormatChrome(DependencyObject? node)
    {
        for (int guard = 0; node is not null && guard < 64; guard++)
        {
            // TextStylePanel is REPARENTED into EditorRichTextHost (the 34px sub-row) while an
            // editor is open, which takes it out of SecondaryToolPanel's tree - so both hosts
            // and the strip itself have to count, or the guard silently stops firing exactly
            // when it matters (Survey's [data-rich-text-toolbar] opt-out, TextEditOverlay.jsx:445).
            if (ReferenceEquals(node, SecondaryToolPanel) || ReferenceEquals(node, TextStylePanel)
                || ReferenceEquals(node, EditorRichTextHost) || ReferenceEquals(node, TextEditPanel))
                return true;
            node = LogicalTreeHelper.GetParent(node)
                ?? (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null);
        }
        return false;
    }

    /// <summary>
    /// True when focus moved into the format chrome, so the editor must stay open. A ComboBox
    /// needs the keyboard focus it just took to run its drop-down, so focus is handed back
    /// from the SelectionChanged handler instead of here; every other control (all
    /// non-focusable) never gets here at all.
    /// </summary>
    private bool KeepEditorOpenFor(DependencyObject? newFocus, TextBox tb)
    {
        if (!IsFormatChrome(newFocus)) return false;
        if (newFocus is not System.Windows.Controls.Primitives.Selector
            && newFocus is not ComboBoxItem)
            Dispatcher.BeginInvoke(() => tb.Focus(), DispatcherPriority.Input);
        return true;
    }

    private void GrowTextEditor(TextBox tb, double floorH)
    {
        double inner = Math.Max(1, tb.Width - 2 * AnnotationFactory.TextPadding);
        double grown = Math.Max(floorH, MeasureText(tb, inner).ContentHeight + 2 * AnnotationFactory.TextPadding);
        if (Math.Abs(grown - tb.Height) >= 0.5)
        {
            tb.Height = grown;
            // The commit reads _activeTextBox, so the grown height has to land there too.
            if (_activeTextBox is Rect r) _activeTextBox = new Rect(r.X, r.Y, r.Width, grown);
        }
        if (_textPreview is not null) _textPreview.Height = Math.Max(1, tb.Height);
    }

    /// <summary>
    /// WPF equivalent of Survey's two live measurements: canvas <c>measureText</c> per line
    /// (max width, TextEditOverlay.jsx:55-68) and <c>el.scrollHeight</c> for the wrapped
    /// content height (TextEditOverlay.jsx:224-232).
    /// </summary>
    private (double MaxLineWidth, double ContentHeight) MeasureText(TextBox tb, double innerWidth)
    {
        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string text = tb.Text ?? string.Empty;
        double maxLine = 0;
        foreach (string line in text.Split('\n'))
        {
            var one = new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, tb.FontSize, Brushes.Black, dpi);
            if (one.WidthIncludingTrailingWhitespace > maxLine) maxLine = one.WidthIncludingTrailingWhitespace;
        }
        var wrapped = new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, tb.FontSize, Brushes.Black, dpi)
        {
            MaxTextWidth = Math.Max(1, innerWidth),
            LineHeight = tb.FontSize * AnnotationFactory.TextLineHeightFactor,
        };
        return (maxLine, wrapped.Height);
    }

    private void CommitText()
    {
        var tb = _activeText;
        if (tb is null) return;
        if (tb.Tag is CalloutAnnotation)
        {
            CommitCalloutText();
            return;
        }
        _activeText = null; // guards against re-entry from LostKeyboardFocus on removal
        var box = _activeTextBox;
        _activeTextBox = null;
        var reeditSource = _reeditSource;
        _reeditSource = null;
        HideTextPreview();

        double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
        string text = tb.Text;
        AnnotationCanvas.Children.Remove(tb);
        UpdateContextPanels(); // Survey clears the rich-text row at the commit boundary
        // Survey discards only BLANK NEW text (textEditCommit.js:136); a re-edit blanked to
        // nothing still commits, so the box survives as an empty bordered rectangle.
        if (reeditSource is null && string.IsNullOrWhiteSpace(text)) return;

        var style = tb.Tag is TextStyle s ? s : TextStyle.Plain;
        Point topLeft = new(x, y);
        var (strokeColor, _) = AnnotationStyle.EnforceOneVisible(CurrentStrokeBrush(), CurrentFillBrush());

        // Survey's committed geometry. New text: tight-width fit for a single rendered line,
        // otherwise the wrap target; height is always content + 2*pad (textEditCommit.js:137-148).
        // Existing text: width unchanged, height grow-only (textEditCommit.js:200-202).
        const double pad = AnnotationFactory.TextPadding;
        double outerW = box?.Width is > 1 ? box!.Value.Width : AnnotationFactory.DefaultTextBoxWidth;
        var (maxLineWidth, contentHeight) = MeasureText(tb, Math.Max(1, outerW - 2 * pad));
        double perLine = tb.FontSize * AnnotationFactory.TextLineHeightFactor;
        int renderedLines = Math.Max(
            text.Split('\n').Length,
            perLine > 0 ? (int)Math.Round(contentHeight / perLine) : 1);

        double boxW, boxH;
        Color fillColor;
        double strokeWidth;
        LineBorderStyle lineStyle;
        if (reeditSource is not null)
        {
            var prior = reeditSource.Tag as AnnotationData;
            boxW = outerW;
            boxH = Math.Max(contentHeight + 2 * pad, box?.Height ?? 0);
            fillColor = prior is null ? Colors.Transparent : AnnotationStyle.FillColorFrom(prior, Colors.Transparent);
            strokeWidth = prior?.Thickness ?? NewTextStrokeWidth;
            lineStyle = prior is null ? LineBorderStyle.Solid : AnnotationStyle.LineStyleFrom(prior);
        }
        else
        {
            boxW = renderedLines <= 1 && maxLineWidth > 0
                ? Math.Ceiling(maxLineWidth + 2) + 2 * pad
                : outerW;
            boxH = contentHeight + 2 * pad;
            fillColor = Colors.Transparent; // Survey pins backgroundColor '' for new text
            strokeWidth = NewTextStrokeWidth;
            lineStyle = LineBorderStyle.Solid;
        }

        var label = AnnotationFactory.CreateStyledTextLabel(
            text, tb.Foreground, tb.FontSize, style, _textFontFamily,
            _textBold || style == TextStyle.Bold, _textItalic, _textUnderline, _textStrike,
            AnnotationFactory.ParseAlign(_textAlign), _textVerticalAlign,
            boxW, boxH, fillColor, strokeWidth, lineStyle);
        Canvas.SetLeft(label, topLeft.X);
        Canvas.SetTop(label, topLeft.Y);
        label.Tag = AnnotationData.ForText(topLeft, text, style, tb.FontSize,
            tb.Foreground is SolidColorBrush fg ? fg.Color : strokeColor,
            _textFontFamily, _textBold || style == TextStyle.Bold, _textItalic, _textUnderline, _textStrike, _textAlign,
            _textVerticalAlign, new Rect(topLeft.X, topLeft.Y, boxW, boxH),
            fillColor, strokeWidth, lineStyle);
        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(label),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(label))
                    AnnotationCanvas.Children.Add(label);
            }));
        // Survey's commit only clears the edit state (setEditingAnnotation(null),
        // PDFViewer.jsx:31936-31942) — no selection chrome appears on the new label.
    }

    private void CancelText()
    {
        var tb = _activeText;
        if (tb is null) return;
        _activeText = null;
        _activeTextBox = null;
        var reeditSource = _reeditSource;
        _reeditSource = null;
        HideTextPreview();
        AnnotationCanvas.Children.Remove(tb);
        // Escape during a re-edit is a true no-op: put the original label back and drop the
        // removal step BeginTextReEdit pushed (Survey's onEditCancel, TextEditOverlay.jsx:398-403).
        if (reeditSource is not null)
        {
            if (!AnnotationCanvas.Children.Contains(reeditSource))
                AnnotationCanvas.Children.Add(reeditSource);
            DiscardLastAction();
        }
        UpdateContextPanels();
    }

    // ------------------------------------------------------------- step tool

    // Erasing shows live feedback: every pointer move cuts the segment just drawn out of the
    // ink under it. That is PREVIEW ONLY - the authoritative carve runs once in
    // ApplyEraserStroke. Originals are remembered per element both to feed that recompute and
    // so the whole drag becomes one undo step when the pointer is released.
    private readonly Dictionary<Path, Geometry> _eraseBefore = new();
    private readonly List<UIElement> _eraseRemoved = new();

    private void EraseLive(Point from, Point to)
    {
        // Radius and mode come from the pointer-down snapshot, never the live toolbar
        // (FabricEraserCanvas.jsx:2146-2152).
        var cut = PaperInk.EraserStroke(new[] { from, to }, _gestureEraserSize);
        foreach (UIElement child in AnnotationCanvas.Children.Cast<UIElement>().ToList())
        {
            if (child is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
                continue;
            // Survey getEraserOperation: 'partial' only for true freehand ink; everything
            // else resolves to 'entire' and is deleted whole by the same partial-mode
            // gesture (eraserPolicy.js:52-75, PageAnnotationLayer.jsx:6668-6684).
            bool partial = _gestureEraserPartial
                && meta.Type is AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter
                && fe is Path { Data: not null };
            if (partial)
            {
                var path = (Path)fe;
                Vector off = ElementOffset(path);
                var world = TranslateGeometry(path.Data!, off);
                if (!GeometriesOverlap(world, cut)) continue;
                if (!_eraseBefore.ContainsKey(path))
                    _eraseBefore[path] = path.Data!.CloneCurrentValue();
                var after = PaperInk.Subtract(world, cut);
                // A failed cut is Survey's failed leaf: leave the preview alone. Deleting here
                // (which "empty" used to mean) was silent data loss.
                if (after is null) continue;
                // No CullSlivers here. Survey culls exactly once, inside the pointer-up
                // subtraction (paperAnnotationGeometry.js:2258-2266); its drag preview is a mask
                // carve that shows the same hairline residue until commit. Re-culling per sample
                // made the surviving piece count a function of pointer sample density.
                path.Data = TranslateGeometry(after, new Vector(-off.X, -off.Y));
            }
            else if (HitsEraser(fe, cut))
            {
                AnnotationCanvas.Children.Remove(fe);
                _eraseRemoved.Add(fe);
            }
        }
    }

    /// <summary>
    /// Pointer released. Survey's carve is a pure function of (original geometry, WHOLE gesture
    /// polyline, radius): the drag only paints a preview mask, and applyEraserAndCommit runs
    /// erasePageAnnotations ONCE over the entire accumulated point list against the ORIGINAL
    /// page annotations, building a single swept-disk record set for the gesture
    /// (FabricEraserCanvas.jsx:1820-1846, :2223-2240; paperAnnotationGeometry.js:2367-2380).
    /// So recompute here from the pristine snapshots instead of keeping whatever the per-sample
    /// preview happened to leave - that result depended on the pointer sample sequence, which is
    /// what made a slow drag erase differently from a fast one over the same path.
    /// </summary>
    private void ApplyEraserStroke(Point release)
    {
        if (_eraserPoints.Count > 0 && (release - _eraserPoints[^1]).Length >= PaperInk.LiveMinDistance)
            EraseLive(_eraserPoints[^1], release);
        if (_eraserPoints.Count > 0 && release != _eraserPoints[^1])
            _eraserPoints.Add(release);

        var inkEdits = new List<(Path path, Geometry before, Geometry? after)>();
        foreach (var (path, before) in _eraseBefore.Select(kv => (kv.Key, kv.Value)).ToList())
        {
            Vector off = ElementOffset(path);
            var carved = PaperInk.SubtractStroke(
                TranslateGeometry(before, off), _eraserPoints, _gestureEraserSize);
            // Survey culls the hairline crescents boolean subtraction leaves behind
            // (cullInkSliverPolygons, paperAnnotationGeometry.js:1586-1615) using the
            // annotation's own sourceWidth - once, here, then decides emptiness from what survives.
            carved = PaperInk.CullSlivers(carved, (path.Tag as AnnotationData)?.Thickness ?? 1);
            var after = TranslateGeometry(carved, new Vector(-off.X, -off.Y));
            if (PaperInk.IsEmpty(after))
            {
                path.Data = before; // undo baseline; the element leaves the canvas instead
                if (!_eraseRemoved.Contains(path)) _eraseRemoved.Add(path);
                AnnotationCanvas.Children.Remove(path);
            }
            else
            {
                // A mid-drag sliver cull could have emptied it wrongly; the gesture recompute
                // is authoritative, so put it back.
                _eraseRemoved.Remove(path);
                if (!AnnotationCanvas.Children.Contains(path))
                    AnnotationCanvas.Children.Add(path);
                path.Data = after;
                inkEdits.Add((path, before, after));
            }
        }
        _eraserPoints.Clear();

        var removed = _eraseRemoved.ToList();
        var removedBefore = removed.OfType<Path>().Where(p => _eraseBefore.ContainsKey(p))
            .ToDictionary(p => p, p => _eraseBefore[p]);
        _eraseBefore.Clear();
        _eraseRemoved.Clear();
        if (inkEdits.Count == 0 && removed.Count == 0) return;

        Push(new EditorAction(
            undo: () =>
            {
                foreach (var el in removed)
                {
                    if (el is Path rp && removedBefore.TryGetValue(rp, out var g)) rp.Data = g;
                    if (!AnnotationCanvas.Children.Contains(el))
                        AnnotationCanvas.Children.Add(el);
                }
                foreach (var (path, before, _) in inkEdits)
                    path.Data = before;
            },
            redo: () =>
            {
                foreach (var el in removed)
                    AnnotationCanvas.Children.Remove(el);
                foreach (var (path, _, after) in inkEdits)
                    path.Data = after;
            }), apply: false);
    }

    private static Geometry TranslateGeometry(Geometry geometry, Vector offset)
    {
        if (offset.Length < 0.01) return geometry;
        var copy = geometry.CloneCurrentValue();
        copy.Transform = new TranslateTransform(offset.X, offset.Y);
        // Baking a translation must not coarsen the outline, so flatten at PaperInk.Tolerance
        // and never at WPF's 0.25 absolute default, which turned a 1px stroke's round caps into
        // triangles every time an erase or a move round-tripped through here.
        return copy.GetFlattenedPathGeometry(PaperInk.Tolerance, ToleranceType.Absolute);
    }

    /// <summary>
    /// Survey decides overlap by real geometric intersection, with bounds used only as a
    /// fast-reject that can never drop a true hit (paperAnnotationGeometry.js:2396-2421;
    /// eraserBoundsPrefilter.js:5-20). A bounding-box area proxy is neither an upper nor a lower
    /// bound on the intersection, so any non-empty intersection counts. Both operands are
    /// already in canvas coordinates. PaperInk.Tolerance, not WPF's 0.25 absolute default, at
    /// which a grazing pass over a thin stroke could be reported as no overlap at all.
    /// </summary>
    private static bool GeometriesOverlap(Geometry world, Geometry cut)
    {
        var inter = Geometry.Combine(
            world, cut, GeometryCombineMode.Intersect, Transform.Identity,
            PaperInk.Tolerance, ToleranceType.Absolute);
        return inter is not null && !inter.IsEmpty();
    }

    private bool HitsEraser(FrameworkElement fe, Geometry cut)
    {
        if (fe is CalloutAnnotation)
        {
            var bounds = GetCanvasBounds(fe);
            return GeometriesOverlap(new RectangleGeometry(bounds), cut);
        }
        Rect b = new(
            Canvas.GetLeft(fe) + (double.IsNaN(Canvas.GetLeft(fe)) ? 0 : 0),
            Canvas.GetTop(fe) + (double.IsNaN(Canvas.GetTop(fe)) ? 0 : 0),
            Math.Max(1, fe.ActualWidth > 0 ? fe.ActualWidth : fe.Width),
            Math.Max(1, fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height));
        Vector off = ElementOffset(fe);
        b.X = (double.IsNaN(Canvas.GetLeft(fe)) ? 0 : Canvas.GetLeft(fe)) + off.X;
        b.Y = (double.IsNaN(Canvas.GetTop(fe)) ? 0 : Canvas.GetTop(fe)) + off.Y;
        if (fe is Path p && p.Data is not null)
            return GeometriesOverlap(TranslateGeometry(p.Data, off), cut);
        return GeometriesOverlap(new RectangleGeometry(b), cut);
    }

    // ------------------------------------------- counter press-drag-release placement

    /// <summary>
    /// Survey's counter drag (PDFViewer.jsx:31727-31790, 26079-26257): a counter is not
    /// dropped on click, it is placed by press → slide → release, and holding Shift mid-drag
    /// freezes the nub TIP and swings the badge body around it ("slide → Shift-twist → slide").
    /// Escape cancels outright. The same state machine drives a committed counter's
    /// Shift-orbit (useSVGInteraction.js:488-533, 1199-1263), because the geometry is identical:
    /// the only difference is whether release commits an add or a move.
    /// </summary>
    private sealed class CounterDrag
    {
        public required Grid Badge { get; init; }
        public required bool IsNew { get; init; }
        public required double Radius { get; init; }
        public required double TipDistance { get; init; }
        public double BodyX, BodyY, Angle;
        public bool ShiftActive;
        public double TipX, TipY;
        public double CursorOffsetX, CursorOffsetY;
        public double LastCursorX, LastCursorY;
        public Point StartBody;
        public Point StartCursor;
        public double StartAngle;
        public bool Moved;
    }

    private CounterDrag? _counterDrag;

    /// <summary>Survey's ~3px gate: below it a Shift-press on a committed counter is still a
    /// multi-select toggle, not an orbit (useSVGInteraction.js:490-494).</summary>
    private const double CounterOrbitThreshold = 3;

    private void BeginStepPlacement(Point pos)
    {
        double radius = CounterGeometry.RadiusFor(_thickness);
        var badge = AnnotationFactory.CreateStepBadge(_nextStep, _color, _thickness, _stepLetters);
        AnnotationCanvas.Children.Add(badge);
        _counterDrag = new CounterDrag
        {
            Badge = badge,
            IsNew = true,
            Radius = radius,
            TipDistance = CounterGeometry.TipDistance(radius),
            Angle = CounterGeometry.DefaultPointerAngle,
            StartAngle = CounterGeometry.DefaultPointerAngle,
            StartBody = pos,
            StartCursor = pos,
        };
        Viewport.CaptureMouse();
        ApplyCounterDragMove(pos);
    }

    /// <summary>Starts an orbit/slide drag on an ALREADY committed counter.</summary>
    private void BeginCounterOrbit(Grid badge, AnnotationData meta, Point cursor, bool shift)
    {
        double radius = CounterGeometry.RadiusFor(meta.Thickness ?? _thickness);
        double tipDist = CounterGeometry.TipDistance(radius);
        Vector off = ElementOffset(badge);
        double bodyX = (double.IsNaN(Canvas.GetLeft(badge)) ? 0 : Canvas.GetLeft(badge)) + off.X + tipDist;
        double bodyY = (double.IsNaN(Canvas.GetTop(badge)) ? 0 : Canvas.GetTop(badge)) + off.Y + tipDist;
        double angle = meta.PointerAngle ?? CounterGeometry.DefaultPointerAngle;
        _counterDrag = new CounterDrag
        {
            Badge = badge,
            IsNew = false,
            Radius = radius,
            TipDistance = tipDist,
            BodyX = bodyX,
            BodyY = bodyY,
            Angle = angle,
            StartBody = new Point(bodyX, bodyY),
            StartCursor = cursor,
            StartAngle = angle,
            LastCursorX = cursor.X,
            LastCursorY = cursor.Y,
            // Slide keeps the grab offset so the badge does not snap its centre to the cursor.
            CursorOffsetX = bodyX - cursor.X,
            CursorOffsetY = bodyY - cursor.Y,
        };
        // Arm orbit WITHOUT re-posing: Survey only anchors the tip at pointer-down and waits
        // for the first move, so a Shift-click that never moves stays a selection toggle.
        if (shift) AnchorCounterTip(_counterDrag);
        Viewport.CaptureMouse();
    }

    /// <summary>Survey's applyCounterDragMove (PDFViewer.jsx:26079-26154).</summary>
    private void ApplyCounterDragMove(Point cursor)
    {
        if (_counterDrag is not { } drag) return;
        drag.LastCursorX = cursor.X;
        drag.LastCursorY = cursor.Y;

        double bodyX, bodyY, angle;
        if (drag.ShiftActive)
        {
            double dx = cursor.X - drag.TipX, dy = cursor.Y - drag.TipY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < 0.001)
            {
                // Cursor sitting exactly on the frozen tip: keep the pose rather than
                // dividing by zero and snapping to a garbage angle.
                bodyX = drag.BodyX;
                bodyY = drag.BodyY;
                angle = drag.Angle;
            }
            else
            {
                double dirX = dx / dist, dirY = dy / dist;
                bodyX = drag.TipX + drag.TipDistance * dirX;
                bodyY = drag.TipY + drag.TipDistance * dirY;
                // pointerAngle is body → tip, i.e. the opposite of tip → cursor.
                angle = (Math.Atan2(-dirY, -dirX) * 180 / Math.PI + 360) % 360;
            }
        }
        else
        {
            bodyX = cursor.X + drag.CursorOffsetX;
            bodyY = cursor.Y + drag.CursorOffsetY;
            angle = drag.Angle;
        }

        // Survey clamps the BODY (not the tip) into the page in both modes: "stays on page"
        // wins over "perfect orbit at the edge".
        bodyX = Math.Clamp(bodyX, drag.Radius, Math.Max(drag.Radius, _source.Width - drag.Radius));
        bodyY = Math.Clamp(bodyY, drag.Radius, Math.Max(drag.Radius, _source.Height - drag.Radius));

        // Survey's gate is CURSOR travel, not the resulting pose: an off-centre Shift-click
        // can nudge the orbit pose without the user having dragged anything.
        if (Math.Abs(cursor.X - drag.StartCursor.X) > CounterOrbitThreshold ||
            Math.Abs(cursor.Y - drag.StartCursor.Y) > CounterOrbitThreshold)
            drag.Moved = true;

        drag.BodyX = bodyX;
        drag.BodyY = bodyY;
        drag.Angle = angle;
        drag.Badge.RenderTransform = null;
        Canvas.SetLeft(drag.Badge, bodyX - drag.TipDistance);
        Canvas.SetTop(drag.Badge, bodyY - drag.TipDistance);
        AnnotationFactory.SetStepPointerAngle(drag.Badge, angle);
        if (!drag.IsNew) UpdateSelectionVisual();
    }

    /// <summary>Shift down mid-drag: pin the tip where it is so the body can orbit it.</summary>
    private void FreezeCounterTip()
    {
        if (_counterDrag is not { } drag || drag.ShiftActive) return;
        AnchorCounterTip(drag);
        // Survey re-applies with the last known cursor so Shift-down poses the badge even if
        // the pointer never moves afterwards (PDFViewer.jsx:26240-26244).
        ApplyCounterDragMove(new Point(drag.LastCursorX, drag.LastCursorY));
    }

    private static void AnchorCounterTip(CounterDrag drag)
    {
        double rad = drag.Angle * Math.PI / 180;
        drag.TipX = drag.BodyX + drag.TipDistance * Math.Cos(rad);
        drag.TipY = drag.BodyY + drag.TipDistance * Math.Sin(rad);
        drag.ShiftActive = true;
    }

    /// <summary>Shift up: hand the body back to the cursor with no jump.</summary>
    private void ReleaseCounterTip()
    {
        if (_counterDrag is not { } drag || !drag.ShiftActive) return;
        drag.CursorOffsetX = drag.BodyX - drag.LastCursorX;
        drag.CursorOffsetY = drag.BodyY - drag.LastCursorY;
        drag.ShiftActive = false;
    }

    private void CancelCounterDrag()
    {
        if (_counterDrag is not { } drag) return;
        _counterDrag = null;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        if (drag.IsNew)
        {
            AnnotationCanvas.Children.Remove(drag.Badge);
            return;
        }
        Canvas.SetLeft(drag.Badge, drag.StartBody.X - drag.TipDistance);
        Canvas.SetTop(drag.Badge, drag.StartBody.Y - drag.TipDistance);
        AnnotationFactory.SetStepPointerAngle(drag.Badge, drag.StartAngle);
        UpdateSelectionVisual();
    }

    private void CommitCounterDrag()
    {
        if (_counterDrag is not { } drag) return;
        _counterDrag = null;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        var badge = drag.Badge;
        double left = drag.BodyX - drag.TipDistance, top = drag.BodyY - drag.TipDistance;

        if (drag.IsNew)
        {
            var data = AnnotationData.ForStep(
                new Point(left, top), _nextStep, _color, _thickness, ++_stepClock);
            data.PointerAngle = drag.Angle;
            if (_stepLetters) data.Style = "Letter";
            badge.Tag = data;
            Push(new EditorAction(
                undo: () => AnnotationCanvas.Children.Remove(badge),
                redo: () =>
                {
                    if (!AnnotationCanvas.Children.Contains(badge))
                        AnnotationCanvas.Children.Add(badge);
                }));
            return;
        }

        if (badge.Tag is not AnnotationData meta) return;
        if (!drag.Moved)
        {
            // A pure Shift-click never became an orbit, so it stays a multi-select toggle.
            Canvas.SetLeft(badge, drag.StartBody.X - drag.TipDistance);
            Canvas.SetTop(badge, drag.StartBody.Y - drag.TipDistance);
            AnnotationFactory.SetStepPointerAngle(badge, drag.StartAngle);
            ToggleSelection(badge);
            return;
        }
        Point beforeBody = drag.StartBody;
        double beforeAngle = drag.StartAngle;
        Point afterBody = new(drag.BodyX, drag.BodyY);
        double afterAngle = drag.Angle;
        double tipDist = drag.TipDistance;
        void Apply(Point body, double angle)
        {
            badge.RenderTransform = null;
            Canvas.SetLeft(badge, body.X - tipDist);
            Canvas.SetTop(badge, body.Y - tipDist);
            AnnotationFactory.SetStepPointerAngle(badge, angle);
            meta.Points = new[] { new[] { body.X - tipDist, body.Y - tipDist } };
            meta.PointerAngle = angle;
            meta.Tx = 0;
            meta.Ty = 0;
            badge.Tag = meta;
            UpdateSelectionVisual();
        }
        Apply(afterBody, afterAngle);
        Push(new EditorAction(
            undo: () => Apply(beforeBody, beforeAngle),
            redo: () => Apply(afterBody, afterAngle)), apply: false);
    }

    /// <summary>Monotonic creation stamp handed to each new counter (Survey's data.createdAt).</summary>
    private long _stepClock;

    /// <summary>
    /// Survey renumberCounters (counterNumbering.js:25-65): counter numbers are DERIVED, never
    /// stored monotonically. Sort every pin by creation time and assign 1..n, so deleting #2 of
    /// 5 leaves 1,2,3,4 rather than 1,3,4,5. Runs after every add, delete, undo and redo.
    /// </summary>
    private void RenumberCounters()
    {
        var pins = AnnotationCanvas.Children.OfType<Grid>()
            .Where(g => g.Tag is AnnotationData { Type: AnnotationData.TypeStep })
            .Select(g => (Badge: g, Meta: (AnnotationData)g.Tag))
            .OrderBy(p => p.Meta.CreatedAt ?? 0)
            .ThenBy(p => p.Meta.Number ?? 0)
            .ToArray();
        for (int i = 0; i < pins.Length; i++)
        {
            var (badge, meta) = pins[i];
            int number = i + 1;
            if (meta.Number == number) continue;
            meta.Number = number;
            AnnotationFactory.SetStepCaption(badge, number, meta.Style == "Letter");
        }
        _nextStep = pins.Length + 1;
    }

    // ------------------------------------------------------------------ blur

    private async void ApplyBlur(SD.Rectangle region)
    {
        if (_sourceOperationActive) return;
        region.Intersect(new SD.Rectangle(0, 0, _source.Width, _source.Height));
        if (region.Width < 2 || region.Height < 2) return;

        var backup = _source.Clone(region, _source.PixelFormat);
        _owned.Add(backup);
        var r = region;
        // Capture the strength-mapped radius per action so undo → redo replays identically
        // even if the user changes the strength control afterward.
        int radius = AnnotationFactory.BlurRadiusFor(_blurStrength);
        if (!await ApplySourceRegionEffectAsync(
                () => BitmapEffects.Blur(_source, r, radius),
                "Blur failed"))
        {
            if (_owned.Remove(backup)) backup.Dispose();
            return;
        }
        if (_closed) return;

        Push(new EditorAction(
            undo: async () =>
            {
                await ApplySourceRegionEffectAsync(
                    () => BitmapEffects.RestoreRegion(_source, backup, r),
                    "Blur undo failed");
            },
            redo: async () =>
            {
                await ApplySourceRegionEffectAsync(
                    () => BitmapEffects.Blur(_source, r, radius),
                    "Blur redo failed");
            },
            onDiscard: () =>
            {
                if (_owned.Remove(backup)) backup.Dispose();
            }), apply: false);
    }

    /// <summary>
    /// Same interaction and undo pattern as blur, but the mosaic gets per-cell random
    /// jitter so the censored text cannot be reconstructed. The seed is captured per
    /// action, which keeps undo → redo byte-identical.
    /// </summary>
    private async void ApplyPixelate(SD.Rectangle region)
    {
        if (_sourceOperationActive) return;
        region.Intersect(new SD.Rectangle(0, 0, _source.Width, _source.Height));
        if (region.Width < 2 || region.Height < 2) return;

        var backup = _source.Clone(region, _source.PixelFormat);
        _owned.Add(backup);
        var r = region;
        int seed = ToolRandom.Next();
        // Capture the strength-mapped block size per action so undo → redo stays byte-identical.
        int cellSize = AnnotationFactory.PixelateCellFor(_pixelateStrength);
        if (!await ApplySourceRegionEffectAsync(
                () => BitmapEffects.PixelateRandomized(_source, r, seed, cellSize),
                "Pixelate failed"))
        {
            if (_owned.Remove(backup)) backup.Dispose();
            return;
        }
        if (_closed) return;

        Push(new EditorAction(
            undo: async () =>
            {
                await ApplySourceRegionEffectAsync(
                    () => BitmapEffects.RestoreRegion(_source, backup, r),
                    "Pixelate undo failed");
            },
            redo: async () =>
            {
                await ApplySourceRegionEffectAsync(
                    () => BitmapEffects.PixelateRandomized(_source, r, seed, cellSize),
                    "Pixelate redo failed");
            },
            onDiscard: () =>
            {
                if (_owned.Remove(backup)) backup.Dispose();
            }), apply: false);
    }

    private async Task<bool> ApplySourceRegionEffectAsync(Action effect, string logContext)
    {
        using IDisposable sourceOperation = _sourceLifetime.Acquire();
        _sourceOperationActive = true;
        Cursor = Cursors.Wait;
        try
        {
            await Task.Run(effect);
            await RefreshImageAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(logContext, ex);
            return false;
        }
        finally
        {
            _sourceOperationActive = false;
            if (!_closed)
                UpdateCursor();
        }
    }

    // ------------------------------------------------------------------ crop

    private async void OnApplyCrop(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        if (_pendingCrop is not SD.Rectangle region)
        {
            ClearCropPreview(restoreTool: true);
            return;
        }
        ClearCropPreview(restoreTool: true);

        SD.Bitmap before = _source;
        SD.Bitmap after;
        try
        {
            after = CaptureService.Crop(before, region);
        }
        catch (ArgumentException)
        {
            return;
        }
        _owned.Add(after);

        // Apply the crop now, then record the undo entry WITHOUT re-applying (apply:false),
        // exactly like rotate/flip. Pushing with apply:true would run Redo synchronously via
        // GetAwaiter().GetResult() on the UI thread, which awaits Dispatcher.InvokeAsync inside
        // RefreshImageAsync -> the UI thread blocks waiting on itself -> deadlock.
        _source = after;
        await OnSourceReplacedAsync();
        if (_closed) return;
        ShiftAnnotations(-region.X, -region.Y);

        Push(new EditorAction(
            undo: async () =>
            {
                _source = before;
                await OnSourceReplacedAsync();
                ShiftAnnotations(region.X, region.Y);
            },
            redo: async () =>
            {
                _source = after;
                await OnSourceReplacedAsync();
                ShiftAnnotations(-region.X, -region.Y);
            }), apply: false);
    }

    private void OnCancelCrop(object sender, RoutedEventArgs e) => ClearCropPreview(restoreTool: true);

    private async Task OnSourceReplacedAsync()
    {
        using IDisposable sourceOperation = _sourceLifetime.Acquire();
        _sourceOperationActive = true;
        Cursor = Cursors.Wait;
        try
        {
            await RefreshImageAsync();
        }
        finally
        {
            _sourceOperationActive = false;
            if (!_closed)
                UpdateCursor();
        }
        if (_closed)
            return;
        SetSurfaceSize(_source.Width, _source.Height);
        Select(null);
        FitToView();
    }

    /// <summary>Translates every live annotation so it stays glued to the same image content after a crop.</summary>
    private void ShiftAnnotations(double dx, double dy)
    {
        foreach (UIElement el in AnnotationCanvas.Children)
            MoveElement(el, dx, dy);
    }

}
