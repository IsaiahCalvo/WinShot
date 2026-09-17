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
    private const string NewTextFontFamily = AnnotationFactory.DefaultTextFontFamily;

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
        _reeditAngle = 0;
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
        double x, double y, double w, double h, Color fill, double strokeThickness, LineBorderStyle lineStyle,
        Color? stroke = null)
    {
        HideTextPreview();
        var rect = new Rectangle
        {
            Width = Math.Max(1, w),
            Height = Math.Max(1, h),
            Fill = fill.A == 0 ? Brushes.Transparent : new SolidColorBrush(fill),
            // Survey's new text commits stroke '#000000'; a re-edit previews the box's OWN
            // stored border colour so nothing flips face on entering the editor.
            Stroke = strokeThickness > 0
                ? new SolidColorBrush(stroke ?? Colors.Black)
                : Brushes.Transparent,
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
        // Survey's handleRequestCalloutEditMode never touches the selection
        // (PDFViewer.jsx:11207-11290): the callout stays selected all the way through the
        // edit, so the top-row swatches keep patching it and it is still selected the instant
        // the editor closes. The handles are hidden by UpdateSelectionVisual's edit gate, the
        // way Survey's overlay returns null for the object being edited
        // (SVGAnnotationLayer.jsx:5100-5101).
        var layout = visual.Layout;
        // A rotated callout carries a TransformGroup, not a bare TranslateTransform, so an
        // `is TranslateTransform` read silently dropped the whole drag offset and mounted the
        // editor away from the visible box.
        Vector off = AnnotationTransform.OffsetOf(visual);
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
                UpdateSelectionVisual(); // the editor is gone, so the handles come back
                UpdateContextPanels();
            }
        };
        SeedTextFormatFromMeta(meta);
        UpdateSelectionVisual(); // hides the handles while the editor owns the box
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
        // Survey's setEditingAnnotation leaves selectedIds alone (PDFViewer.jsx:11310-11328),
        // so the box is still selected when the editor closes. UpdateSelectionVisual's edit
        // gate hides the handles meanwhile (SVGAnnotationLayer.jsx:5100-5101).
        double x = Canvas.GetLeft(label), y = Canvas.GetTop(label);
        // Survey's existing-text commit deepClones the stored annotation, so angle and every
        // other geometry field survive the edit (textEditCommit.js:186-212). WinShot rebuilds
        // the label, so the rotation has to be carried across by hand — and a rotated label
        // carries a TransformGroup, which the old `is TranslateTransform` read missed, losing
        // the drag offset too.
        Vector off = AnnotationTransform.OffsetOf(label);
        x += off.X;
        y += off.Y;
        _reeditAngle = AnnotationTransform.AngleOf(label);

        // Survey's existing-text commit clones the original and only replaces text/height
        // (textEditCommit.js:190-210) — a blank re-edit keeps the box, it never deletes it.
        _reeditSource = label;
        // The committed annotation KEEPS RENDERING during the edit: Survey draws it with
        // `renderText(..., /*hideText*/ true)` (SVGAnnotationLayer.jsx:3896-3906), so the
        // border, fill, dash and angle never change appearance — only the glyphs step aside
        // for the chromeless editor. Plain/Bold/Huge are a Grid of [chrome Rectangle, inner
        // TextBlock] (AnnotationFactory.cs:507-526), so hiding the inner child is exactly that
        // render. Outline (Path) and Pill (Border) carry no chrome of their own, so they hide
        // whole and the preview rectangle stands in.
        _reeditHiddenGlyphs = label is Grid grid && grid.Children.Count > 1 ? grid.Children[1] : label;
        _reeditHiddenGlyphs.Visibility = Visibility.Collapsed;
        _reeditBox = ReferenceEquals(_reeditHiddenGlyphs, label) ? null : label;

        var style = Enum.TryParse(meta.Style, out TextStyle s) ? s : TextStyle.Plain;
        double fontSize = meta.FontSize is double fs && fs > 0 ? fs : AnnotationFactory.FontSizeFor(_thickness);
        // The editor shows the GLYPHS, so it takes the glyph colour (Survey's `fill`), not the
        // border stroke the same field used to double as.
        var foreground = new SolidColorBrush(AnnotationStyle.TextGlyphColor(
            meta, meta.Color is string hex && TryParseColor(hex, out var c) ? c : Colors.White));

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
        // Only the chrome-less styles need a stand-in box; for everything else the committed
        // annotation is still painting its own border, so a second rectangle would double it.
        if (_reeditBox is null)
            ShowTextPreview(Canvas.GetLeft(tb), Canvas.GetTop(tb), tb.Width, tb.Height,
                AnnotationStyle.FillColorFrom(meta, Colors.Transparent),
                meta.Thickness ?? NewTextStrokeWidth, AnnotationStyle.LineStyleFrom(meta),
                meta.Color is string bhex && TryParseColor(bhex, out var bcol) ? bcol : Colors.Black);
        // Survey's contentEditable overlay is rotated WITH the box
        // (`transform: rotate(${g.angle}deg)`, transformOrigin 'center center',
        // TextEditOverlay.jsx:553-566), so the caret sits inside the tilted box instead of in
        // an axis-aligned ghost at its corner. RenderTransformOrigin keeps the pivot centred
        // as the editor grows.
        if (_reeditAngle != 0)
        {
            tb.RenderTransformOrigin = new Point(0.5, 0.5);
            tb.RenderTransform = new RotateTransform(_reeditAngle);
        }
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        HookTextEditor(tb);
        UpdateSelectionVisual(); // hides the handles while the editor owns the box
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
            // Survey's document-level commit opts out of EXACTLY three markers —
            // [data-rich-text-toolbar], [data-mini-toolbar], [data-font-color-picker]
            // (TextEditOverlay.jsx:446-454) — and [data-rich-text-toolbar] is on the portalled
            // strip ALONE (AppShell.jsx:1770). Every other chrome click, the top-row stroke
            // swatch, the width input, the line-style and arrowhead dropdowns and the Aa button
            // included, falls through and commits the edit. Naming SecondaryToolPanel here
            // covered the whole top row, which also let KeepEditorOpenFor yank focus straight
            // back out of SizeInput and made the stroke-width field impossible to type into.
            // FontColorPopup needs no entry of its own: the logical walk reaches TextStylePanel
            // from inside it (EditorWindow.xaml:567 sits within :550).
            if (ReferenceEquals(node, TextStylePanel) || ReferenceEquals(node, EditorRichTextHost))
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
        // Re-editing keeps the committed box on the canvas, so IT is the chrome that has to
        // track the live bounds (Survey re-broadcasts liveTextEditBounds into the real
        // annotation's render, TextEditOverlay.jsx:239-270). The chrome Rectangle stretches
        // with the Grid; the rotation pivot is re-centred so the live box sits exactly where
        // CommitText will rebuild it.
        if (_reeditSource is Grid keep && Math.Abs(keep.Height - tb.Height) >= 0.5)
        {
            keep.Height = Math.Max(1, tb.Height);
            if (_reeditAngle != 0)
                AnnotationTransform.Apply(keep, AnnotationTransform.OffsetOf(keep), _reeditAngle,
                    new Point(keep.Width / 2, keep.Height / 2));
        }
    }

    /// <summary>
    /// WPF equivalent of Survey's two live measurements: canvas <c>measureText</c> per line
    /// (max width, TextEditOverlay.jsx:55-68) and <c>el.scrollHeight</c> for the wrapped
    /// content height (TextEditOverlay.jsx:224-232).
    /// </summary>
    private (double MaxLineWidth, double ContentHeight) MeasureText(TextBox tb, double innerWidth) =>
        MeasureText(tb.Text ?? string.Empty,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, FontStretches.Normal),
            tb.FontSize, innerWidth);

    /// <summary>
    /// Same measurement without an editor, for a restyle of a merely SELECTED text
    /// (see <c>GrowStoredTextRect</c>).
    /// </summary>
    private (double MaxLineWidth, double ContentHeight) MeasureText(
        string text, Typeface typeface, double fontSize, double innerWidth)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double maxLine = 0;
        foreach (string line in text.Split('\n'))
        {
            var one = new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black, dpi);
            if (one.WidthIncludingTrailingWhitespace > maxLine) maxLine = one.WidthIncludingTrailingWhitespace;
        }
        var wrapped = new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, dpi)
        {
            MaxTextWidth = Math.Max(1, innerWidth),
            LineHeight = fontSize * AnnotationFactory.TextLineHeightFactor,
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
        var hiddenGlyphs = _reeditHiddenGlyphs;
        _reeditHiddenGlyphs = null;
        _reeditBox = null;
        double reeditAngle = _reeditAngle;
        _reeditAngle = 0;
        HideTextPreview();

        double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
        string text = tb.Text;
        AnnotationCanvas.Children.Remove(tb);
        UpdateContextPanels(); // Survey clears the rich-text row at the commit boundary
        // Survey discards only BLANK NEW text (textEditCommit.js:136); a re-edit blanked to
        // nothing still commits, so the box survives as an empty bordered rectangle.
        if (reeditSource is null && string.IsNullOrWhiteSpace(text))
        {
            if (hiddenGlyphs is not null) hiddenGlyphs.Visibility = Visibility.Visible;
            return;
        }

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

        // Survey keeps a Textbox's border colour (`stroke`) and its glyph colour (`fill`)
        // apart: new text commits stroke '#000000' and only the font-colour swatch touches
        // the letters (textEditCommit.js:151-156, TextEditOverlay.jsx:302).
        var glyphColor = tb.Foreground is SolidColorBrush fg ? fg.Color : strokeColor;
        var borderColor = reeditSource?.Tag is AnnotationData priorMeta
            && priorMeta.Color is string bh && TryParseColor(bh, out var bc)
            ? bc
            : Colors.Black;
        // Survey pins the ROTATED top-left corner when the height changes, because the box
        // rotates about its own centre (textEditCommit.js:204-212).
        double dh = boxH - (box?.Height ?? boxH);
        if (reeditAngle != 0 && dh != 0)
        {
            double rad = reeditAngle * Math.PI / 180;
            topLeft.X -= dh / 2 * Math.Sin(rad);
            topLeft.Y -= dh / 2 * (1 - Math.Cos(rad));
        }
        var label = AnnotationFactory.CreateStyledTextLabel(
            text, tb.Foreground, tb.FontSize, style, _textFontFamily,
            _textBold, _textItalic, _textUnderline, _textStrike,
            AnnotationFactory.ParseAlign(_textAlign), _textVerticalAlign,
            boxW, boxH, fillColor, strokeWidth, lineStyle, new SolidColorBrush(borderColor));
        Canvas.SetLeft(label, topLeft.X);
        Canvas.SetTop(label, topLeft.Y);
        var textMeta = AnnotationData.ForText(topLeft, text, style, tb.FontSize, borderColor,
            _textFontFamily, _textBold, _textItalic, _textUnderline, _textStrike, _textAlign,
            _textVerticalAlign, new Rect(topLeft.X, topLeft.Y, boxW, boxH),
            fillColor, strokeWidth, lineStyle);
        textMeta.FontColor = ToHex(glyphColor);
        textMeta.Angle = reeditAngle;
        label.Tag = textMeta;
        // The committed box stayed on the canvas for the whole edit, so the commit is ONE
        // step: swap it for the rebuilt label. (It used to be two — a removal pushed at edit
        // start plus this add — which meant one Ctrl+Z left the text gone and the caret's box
        // missing.) Survey checkpoints the existing-text commit once (textEditCommit.js:186-212).
        if (hiddenGlyphs is not null) hiddenGlyphs.Visibility = Visibility.Visible;
        Push(new EditorAction(
            undo: () =>
            {
                AnnotationCanvas.Children.Remove(label);
                if (reeditSource is not null && !AnnotationCanvas.Children.Contains(reeditSource))
                    AnnotationCanvas.Children.Add(reeditSource);
            },
            redo: () =>
            {
                if (reeditSource is not null) AnnotationCanvas.Children.Remove(reeditSource);
                if (!AnnotationCanvas.Children.Contains(label))
                    AnnotationCanvas.Children.Add(label);
            }));
        // The rebuilt label is a different element, so the angle has to be re-installed on it.
        // Measure/arrange it first (the canvas repeats both later) so SetAngle resolves the
        // same LocalCentre pivot the rotate handle uses — otherwise the next rotate drag would
        // pop the box to a different centre.
        if (reeditAngle != 0)
        {
            label.Measure(new Size(boxW, boxH));
            label.Arrange(new Rect(topLeft.X, topLeft.Y, boxW, boxH));
            AnnotationTransform.SetAngle(label, reeditAngle);
        }
        // Survey's commit only clears the edit state (setEditingAnnotation(null),
        // PDFViewer.jsx:31936-31942) — selectedIds are left INTACT, so a box that was selected
        // when the editor opened is still selected the instant it closes and the very next
        // Bold press restyles it. WinShot rebuilds the element, so the selection has to be
        // re-pointed at the replacement; a NEW text was never selected and stays unselected.
        if (reeditSource is not null && ReplaceInSelection(reeditSource, label))
        {
            UpdateSelectionVisual();
            SyncChromeFromSelection();
            UpdateContextPanels();
        }
    }

    /// <summary>
    /// Re-points the selection at a rebuilt element. Returns false when the old one was not
    /// selected, so nothing gets selected that was not selected before.
    /// </summary>
    private bool ReplaceInSelection(UIElement before, UIElement after)
    {
        bool hit = false;
        for (int i = 0; i < _selection.Count; i++)
            if (ReferenceEquals(_selection[i], before)) { _selection[i] = after; hit = true; }
        if (ReferenceEquals(_selected, before)) { _selected = after; hit = true; }
        return hit;
    }

    private void CancelText()
    {
        var tb = _activeText;
        if (tb is null) return;
        _activeText = null;
        _activeTextBox = null;
        var reeditSource = _reeditSource;
        _reeditSource = null;
        var hiddenGlyphs = _reeditHiddenGlyphs;
        _reeditHiddenGlyphs = null;
        _reeditBox = null;
        double cancelAngle = _reeditAngle;
        _reeditAngle = 0;
        HideTextPreview();
        AnnotationCanvas.Children.Remove(tb);
        // Escape during a re-edit is a true no-op: the original label never left the canvas,
        // so all that has to come back is its glyphs — and its mount-time height, in case the
        // live grow stretched it (Survey's onEditCancel, TextEditOverlay.jsx:398-403).
        if (hiddenGlyphs is not null) hiddenGlyphs.Visibility = Visibility.Visible;
        if (reeditSource is Grid cancelled
            && (reeditSource.Tag as AnnotationData)?.Rect is { Length: >= 4 } r0 && r0[3] >= 8
            && Math.Abs(cancelled.Height - r0[3]) >= 0.5)
        {
            cancelled.Height = r0[3];
            if (cancelAngle != 0)
                AnnotationTransform.Apply(cancelled, AnnotationTransform.OffsetOf(cancelled), cancelAngle,
                    new Point(cancelled.Width / 2, cancelled.Height / 2));
        }
        UpdateSelectionVisual(); // the editor is gone, so the handles come back
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
        bool changed = false;
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
                // Survey layers these the OPPOSITE way round from what WinShot did. Candidacy
                // for the authoritative pointer-up carve is decided by a CONSERVATIVE padded
                // AABB that "must NEVER reject an object the real hit test would accept"
                // (eraserBoundsPrefilter.js:5-21; paperAnnotationGeometry.js:2391-2402), and
                // the exact boolean is used only for the live preview. Gating the SNAPSHOT on
                // the exact test meant one rejected - or one failed - segment dropped the
                // stroke from the commit carve entirely, silently and permanently.
                if (!BoundsOverlap(world.Bounds, (meta.Thickness ?? 1) / 2 + EraserBoundsPad,
                                   cut.Bounds, _gestureEraserSize / 2 + EraserBoundsPad))
                    continue;
                if (!_eraseBefore.ContainsKey(path))
                    _eraseBefore[path] = path.Data!.CloneCurrentValue();
                // Preview only, and fail OPEN: Survey's partial branch has no touch pre-test at
                // all and calls subtractPolygonRecord unconditionally (:2519-2538). A false
                // positive costs one null-safe Subtract; a false negative would show stale ink.
                if (GeometriesOverlap(world, cut) == false) continue;
                var after = PaperInk.Subtract(world, cut);
                // A failed cut is Survey's failed leaf: leave the preview alone. Deleting here
                // (which "empty" used to mean) was silent data loss.
                if (after is null) continue;
                // No CullSlivers here. Survey culls exactly once, inside the pointer-up
                // subtraction (paperAnnotationGeometry.js:2258-2266); its drag preview is a mask
                // carve that shows the same hairline residue until commit. Re-culling per sample
                // made the surviving piece count a function of pointer sample density.
                path.Data = TranslateGeometry(after, new Vector(-off.X, -off.Y));
                changed = true;
            }
            else if (HitsEraser(fe, cut))
            {
                AnnotationCanvas.Children.Remove(fe);
                _eraseRemoved.Add(fe);
                changed = true;
            }
        }
        // Survey's mix-blend-mode:multiply is re-evaluated by the compositor every frame, so a
        // highlighter shows whatever is under it RIGHT NOW (svgAnnotationRenderers.jsx:272 sets
        // no isolation). WPF has to bake that blend into an ImageBrush, and until now it was
        // rebuilt only on an undoable mutation - so erasing ink beneath a highlighter showed
        // nothing until release, then popped. Free when the canvas holds no highlighter.
        if (changed) RefreshHighlighters();
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
    /// <remarks>
    /// Returns NULL for "the clipper failed, the answer is unknown", the same contract
    /// PaperInk.Subtract already uses. Survey's polygonRecordTouches wraps its intersection in
    /// try/catch and reports failedLeaves rather than guessing (paperAnnotationGeometry.js:
    /// 2336-2358); this API demonstrably throws in this codebase (PaperInk.cs:119-124), and an
    /// unguarded throw here escaped straight into the pointer-move handler. Each caller then
    /// picks the direction Survey picks for its branch.
    /// </remarks>
    private static bool? GeometriesOverlap(Geometry world, Geometry cut)
    {
        try
        {
            var inter = Geometry.Combine(
                world, cut, GeometryCombineMode.Intersect, Transform.Identity,
                PaperInk.Tolerance, ToleranceType.Absolute);
            return inter is null ? null : !inter.IsEmpty();
        }
        catch (Exception)
        {
            System.Diagnostics.Debug.WriteLine("GeometriesOverlap: clipper failed (Survey's failed leaf).");
            return null;
        }
    }

    /// <summary>eraserBoundsPrefilter BOUNDS_PAD (eraserBoundsPrefilter.js:24).</summary>
    private const double EraserBoundsPad = 4;

    /// <summary>
    /// Survey's padded-AABB fast-reject (eraserBoundsPrefilter.js:37-48): each side inflated by
    /// its own slop so the gate can never reject something the real hit test would accept.
    /// </summary>
    private static bool BoundsOverlap(Rect a, double padA, Rect b, double padB)
    {
        if (a.IsEmpty || b.IsEmpty) return false;
        a.Inflate(padA, padA);
        b.Inflate(padB, padB);
        return a.IntersectsWith(b);
    }

    private bool HitsEraser(FrameworkElement fe, Geometry cut)
    {
        // Deleting a whole annotation is unrecoverable inside the gesture, so this branch fails
        // CLOSED on a clipper failure, matching Survey's 'full' branch (:2493-2516), which
        // passes the annotation through untouched when every leaf fails.
        if (fe is CalloutAnnotation)
        {
            var bounds = GetCanvasBounds(fe);
            return GeometriesOverlap(new RectangleGeometry(bounds), cut) == true;
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
            return GeometriesOverlap(TranslateGeometry(p.Data, off), cut) == true;
        if (fe is Shape s)
        {
            // Survey: an UNFILLED shape is only erasable where its stroke actually is -
            // isPointOnRect / isPointOnCircle gate the filled-area branch on hasFill and
            // otherwise test the border band only (geometryHitTest.js:902-925, :1002-1015),
            // and eraseAnnotations routes the same case down the border-centerline capsule
            // branch (paperAnnotationGeometry.js:2415-2422). The solid bounding box used here
            // deleted a hollow rect swiped through its empty middle, and an ellipse tapped on
            // a bbox CORNER where no ink exists. Fill is Brushes.Transparent (never null) for
            // an unfilled shape, so test the alpha. Widening a closed geometry yields the ring.
            Geometry g = s is Ellipse ? new EllipseGeometry(b) : new RectangleGeometry(b);
            if ((s.Fill as SolidColorBrush)?.Color.A is null or 0)
                g = g.GetWidenedPathGeometry(
                    new Pen(Brushes.Black, Math.Max(1, s.StrokeThickness)),
                    PaperInk.Tolerance, ToleranceType.Absolute);
            return GeometriesOverlap(g, cut) == true;
        }
        // Text, counters and emoji: Survey's isPointOnTextbox is a box test.
        return GeometriesOverlap(new RectangleGeometry(b), cut) == true;
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
            // Every pin joins a SERIES. Survey starts the first one implicitly rather than
            // making the user press "+ New Count" before the first pin can be dropped.
            var data = AnnotationData.ForStep(
                new Point(left, top), _nextStep, _color, _thickness, ++_stepClock,
                EnsureCounterSeries(), _counterSeriesStart);
            data.PointerAngle = drag.Angle;
            if (_stepLetters) data.Style = "Letter";
            badge.Tag = data;
            Push(new EditorAction(
                undo: () => { AnnotationCanvas.Children.Remove(badge); RenumberCounters(); },
                redo: () =>
                {
                    if (!AnnotationCanvas.Children.Contains(badge))
                        AnnotationCanvas.Children.Add(badge);
                    RenumberCounters();
                }));
            RenumberCounters();
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
    /// <summary>
    /// Every counter's displayed number, derived from its position within ITS SERIES. The
    /// rules live in <see cref="CounterSeries.Renumber"/> (Survey's counterNumbering.js); this
    /// is only the part that has to touch WPF — repainting the badges whose number moved, and
    /// advancing the active series' "next number" so the Start field still reads true.
    ///
    /// Before series existed this numbered the whole canvas 1..n. That is now what the legacy
    /// bucket does on its own, so a project written by an older build renumbers identically.
    /// </summary>
    private void RenumberCounters()
    {
        var pins = AnnotationCanvas.Children.OfType<Grid>()
            .Where(g => g.Tag is AnnotationData { Type: AnnotationData.TypeStep })
            .Select(g => (Badge: g, Meta: (AnnotationData)g.Tag))
            .ToArray();

        var moved = CounterSeries.Renumber(pins.Select(p => p.Meta)).ToHashSet();
        foreach (var (badge, meta) in pins)
            if (moved.Contains(meta))
                AnnotationFactory.SetStepCaption(badge, meta.Number ?? 1, meta.Style == "Letter");

        // The Start field shows where the NEXT pin of the active series lands. An empty series
        // (just created, or all its pins deleted) keeps the start the user typed.
        //
        // A null active series means the LEGACY bucket, not "no series": that is the state a
        // project written before series existed loads into, and its pins still have to advance
        // the next number the way they always did.
        string activeKey = _counterSeriesId ?? CounterSeries.LegacyKey;
        var active = pins.Select(p => p.Meta)
            .Where(m => (string.IsNullOrEmpty(m.SeriesId) ? CounterSeries.LegacyKey : m.SeriesId) == activeKey)
            .ToArray();
        _nextStep = active.Length == 0
            ? _counterSeriesStart
            : active.Max(m => m.Number ?? 0) + 1;
        RefreshCounterSeriesChrome();
    }

    // ------------------------------------------------------------------ blur



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
        // Every crop, rotate, flip and resize lands here, and every one of them changes what
        // is UNDER a pixel-backed mark. One repaint pass at the one place the source is
        // swapped, rather than six call sites each remembering.
        RefreshAllRedactions();
        Select(null);
        FitToView();
    }

    /// <summary>Translates every live annotation so it stays glued to the same image content after a crop.</summary>
    private void ShiftAnnotations(double dx, double dy)
    {
        foreach (UIElement el in AnnotationCanvas.Children)
            MoveElement(el, dx, dy);
    }


    // -------------------------------------------------------------- redaction

    /// <summary>
    /// A redaction is a live object, not baked pixels: a rectangle filled with the
    /// effected crop of whatever it currently covers. It can be moved, resized, restyled
    /// and deleted for the whole session, and only <see cref="Flatten"/> bakes it — which
    /// is what makes "I blurred the wrong line" a drag instead of an undo-and-redraw.
    /// </summary>
    private System.Windows.Shapes.Rectangle BeginRedaction(Rect bounds)
    {
        _activeRedactSeed = ToolRandom.Next();
        var mark = new System.Windows.Shapes.Rectangle();
        Canvas.SetLeft(mark, bounds.X);
        Canvas.SetTop(mark, bounds.Y);
        AnnotationFactory.PaintRedaction(mark, _source, bounds, _redactStyle, _redactStrength, _activeRedactSeed);
        _redactPreviewClock.Restart();
        return mark;
    }

    /// <summary>
    /// The floor between two live repaints. Repainting means re-running the effect over the
    /// covered pixels, which is linear in the AREA: measured on a 4K source, a 400x120
    /// Blur-secure mark costs 2.5 ms a frame and a 2400x1400 one costs 173-267 ms.
    /// ponytail: a fixed interval, not an adaptive one; measure before making it clever.
    /// </summary>
    private const int RedactPreviewIntervalMs = 30;

    /// <summary>
    /// Whether a live repaint is due. The gate is the interval OR the last repaint's own
    /// cost, whichever is longer, because a fixed interval buys nothing on exactly the
    /// marks that need it: a paint that takes 267 ms has already overrun a 30 ms floor by
    /// the time the next mouse-move arrives, so every frame repaints and the editor drops
    /// to ~4 fps. Charging the gesture the paint's own cost caps live repainting at about
    /// half the drag's frames however big the mark is.
    /// </summary>
    internal static bool RedactRepaintDue(long sinceLastPaintMs, long lastPaintCostMs) =>
        sinceLastPaintMs >= Math.Max(RedactPreviewIntervalMs, Math.Max(0, lastPaintCostMs));

    /// <summary>What the last pixel-backed repaint cost, in milliseconds. See above.</summary>
    private long _redactPaintCostMs;

    /// <summary>
    /// A clock that has never run means nothing has been painted yet, so the first repaint
    /// of a mark loaded from a project is due immediately — without this it would be queued
    /// against a stopwatch reading zero forever and never land.
    /// </summary>
    private bool RedactPreviewDue() =>
        !_redactPreviewClock.IsRunning ||
        RedactRepaintDue(_redactPreviewClock.ElapsedMilliseconds, _redactPaintCostMs);

    /// <summary>Repaints a pixel-backed mark and remembers what it cost.</summary>
    private void PaintPixelBackedMark(Action paint)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        paint();
        _redactPaintCostMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _redactPreviewClock.Restart();
    }

    /// <summary>Live preview while the drag is still happening.</summary>
    private void UpdateRedactionPreview(System.Windows.Shapes.Rectangle mark, Rect bounds, bool force)
    {
        Canvas.SetLeft(mark, bounds.X);
        Canvas.SetTop(mark, bounds.Y);
        mark.Width = Math.Max(1, bounds.Width);
        mark.Height = Math.Max(1, bounds.Height);
        if (!force && !RedactPreviewDue()) return;
        PaintPixelBackedMark(() => AnnotationFactory.PaintRedaction(
            mark, _source, bounds, _redactStyle, _redactStrength, _activeRedactSeed));
    }

    /// <summary>
    /// Starts a magnifier callout. Like a redaction, the mark IS the preview: it fills with
    /// the blown-up pixels as the bubble grows, rather than being a dashed box that only
    /// becomes a callout on mouse-up.
    /// </summary>
    private System.Windows.Shapes.Rectangle BeginMagnifier(Rect bounds)
    {
        var bubble = new System.Windows.Shapes.Rectangle
        {
            RadiusX = 6,
            RadiusY = 6,
            Stroke = new SolidColorBrush(WithOpacity(_color, _opacity)),
            StrokeThickness = _thickness,
        };
        Canvas.SetLeft(bubble, bounds.X);
        Canvas.SetTop(bubble, bounds.Y);
        AnnotationFactory.PaintMagnifier(
            bubble, _source, bounds, MagnifierLayout.DefaultFocus(bounds), _magnification);
        _redactPreviewClock.Restart();
        return bubble;
    }

    /// <summary>
    /// Live preview of a growing callout. It looks at its own centre while it is being
    /// drawn, so what you see is the pixels the bubble is over, blown up; aiming it
    /// somewhere else is the ring's job, afterwards. Rate-limited on the same clock the
    /// redaction preview uses, and for the same reason.
    /// </summary>
    private void UpdateMagnifierPreview(System.Windows.Shapes.Rectangle bubble, Rect bounds, bool force)
    {
        Canvas.SetLeft(bubble, bounds.X);
        Canvas.SetTop(bubble, bounds.Y);
        bubble.Width = Math.Max(1, bounds.Width);
        bubble.Height = Math.Max(1, bounds.Height);
        if (!force && !RedactPreviewDue()) return;
        PaintPixelBackedMark(() => AnnotationFactory.PaintMagnifier(
            bubble, _source, bounds, MagnifierLayout.DefaultFocus(bounds), _magnification));
    }

    /// <summary>
    /// True while a pointer gesture is driving repaints frame by frame: a drag-move, a
    /// handle resize, or one of the two live sliders. Those are the callers that must not
    /// pay full price on every mouse-move; a nudge, a crop, an undo or a redo is a single
    /// repaint and simply pays it.
    /// </summary>
    private bool RedactRepaintIsLive =>
        _movingSelection || _activeHandle >= 0 ||
        _redactStrengthElement is not null || _magnifyElement is not null;

    /// <summary>Marks a live gesture skipped, repainted when the rate limit allows.</summary>
    private readonly HashSet<UIElement> _redactPending = new();
    private DispatcherTimer? _redactPendingTimer;

    /// <summary>
    /// Repaints a committed redaction from the pixels it covers NOW — after a move, a
    /// resize, a crop shift, a style change or an undo. The seed rides in the metadata so
    /// a mosaic redraws with the same blocks instead of reshuffling on every recompute.
    ///
    /// The initial drag rate-limits itself; move, resize and the strength slider used to
    /// not, so the expensive gesture was the unguarded one — a 2400x1400 Blur-secure mark
    /// ran 173-267 ms of GDI on the UI thread per mouse-move frame. They come through here,
    /// so the gate lives here rather than at each call site, and a skipped frame is queued
    /// instead of dropped: whatever the gesture skipped is repainted on the next tick, so
    /// no mark can be left showing pixels it no longer covers.
    /// </summary>
    private void RefreshRedaction(UIElement element)
    {
        if (element is not System.Windows.Shapes.Rectangle mark ||
            mark.Tag is not AnnotationData meta)
            return;

        if (RedactRepaintIsLive && !RedactPreviewDue())
        {
            _redactPending.Add(element);
            StartRedactPendingTimer();
            return;
        }
        _redactPending.Remove(element);
        RepaintPixelBackedMark(mark, meta);
    }

    /// <summary>
    /// Two marks are painted from the pixels underneath rather than from their own
    /// properties, and both have to be repainted by the same six callers — every move,
    /// resize, crop, rotate, undo and redo. One entry point, so a new pixel-backed mark
    /// never has to find those six again.
    /// </summary>
    private void RepaintPixelBackedMark(System.Windows.Shapes.Rectangle mark, AnnotationData meta)
    {
        switch (meta.Type)
        {
            case AnnotationData.TypeRedact:
                PaintPixelBackedMark(() => AnnotationFactory.PaintRedaction(
                    mark, _source, RedactionBounds(mark, meta),
                    Redaction.Parse(meta.Style), meta.Strength ?? Redaction.DefaultStrength, meta.Seed ?? 0));
                break;
            case AnnotationData.TypeMagnifier:
                PaintPixelBackedMark(() => AnnotationFactory.PaintMagnifier(
                    mark, _source, RedactionBounds(mark, meta),
                    MagnifierFocus(meta), meta.Zoom ?? MagnifierLayout.DefaultScale));
                break;
        }
    }

    /// <summary>
    /// Drains the skipped repaints once the rate limit allows. A timer rather than a hook
    /// on each gesture's end: a mark whose repaint was skipped has to catch up whether the
    /// gesture finished, was aborted, or lost mouse capture to an Alt+Tab.
    /// </summary>
    private void StartRedactPendingTimer()
    {
        if (_redactPendingTimer is not null) return;
        _redactPendingTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(RedactPreviewIntervalMs),
        };
        _redactPendingTimer.Tick += (_, _) =>
        {
            // A DispatcherTimer keeps ticking after the window has gone, and what it would
            // tick INTO is a repaint that reads _source — which the close path disposes. An
            // exception from a timer tick is unhandled by construction, so it takes the
            // process, not the editor.
            if (_closed) { StopRedactPendingTimer(); _redactPending.Clear(); return; }
            if (_redactPending.Count > 0 && !RedactPreviewDue()) return;
            StopRedactPendingTimer();
            FlushRedactionRefresh();
        };
        _redactPendingTimer.Start();
    }

    private void StopRedactPendingTimer()
    {
        _redactPendingTimer?.Stop();
        _redactPendingTimer = null;
    }

    /// <summary>
    /// Repaints everything a live gesture skipped. Called from the timer and before the
    /// canvas is read for pixels, so a flatten can never bake a stale redaction.
    /// </summary>
    private void FlushRedactionRefresh()
    {
        if (_closed) { _redactPending.Clear(); return; }
        if (_redactPending.Count == 0) return;
        UIElement[] pending = _redactPending.ToArray();
        _redactPending.Clear();
        // Straight to the paint, not back through the gate: this pass IS the catch-up, so
        // re-asking "is a repaint due?" would queue the same marks again forever.
        foreach (UIElement element in pending)
        {
            // A mark queued mid-gesture can be deleted or undone before the flush reaches it.
            // Painting a detached Rectangle is wasted GDI on exactly the large redactions the
            // rate limit exists for, and the set would hold it alive until the next flush.
            if (!AnnotationCanvas.Children.Contains(element)) continue;
            if (element is System.Windows.Shapes.Rectangle mark && mark.Tag is AnnotationData meta)
                RepaintPixelBackedMark(mark, meta);
        }
    }

    /// <summary>The point of the picture a callout is looking at, defaulting to its own centre.</summary>
    private static Point MagnifierFocus(AnnotationData meta) =>
        meta.Points is { Length: >= 1 } pts && pts[0] is { Length: >= 2 } p
            ? new Point(p[0], p[1])
            : MagnifierLayout.DefaultFocus(
                meta.Rect is { Length: >= 4 } r ? new Rect(r[0], r[1], r[2], r[3]) : default);

    /// <summary>
    /// Where a redaction sits in source pixels, move offset included. A rotated redaction
    /// samples its UNROTATED box — the mark still covers what it covers, and sampling a
    /// rotated quad would need a resample pass for a case nobody has asked for.
    /// </summary>
    private static Rect RedactionBounds(FrameworkElement fe, AnnotationData meta)
    {
        double x = Canvas.GetLeft(fe), y = Canvas.GetTop(fe);
        if (double.IsNaN(x)) x = meta.Rect is { Length: >= 4 } r0 ? r0[0] : 0;
        if (double.IsNaN(y)) y = meta.Rect is { Length: >= 4 } r1 ? r1[1] : 0;
        Vector offset = AnnotationTransform.OffsetOf(fe);
        double w = double.IsNaN(fe.Width) ? fe.ActualWidth : fe.Width;
        double h = double.IsNaN(fe.Height) ? fe.ActualHeight : fe.Height;
        return new Rect(x + offset.X, y + offset.Y, Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>Repaints every pixel-backed mark — after a crop, rotate or resize of the source.</summary>
    private void RefreshAllRedactions()
    {
        foreach (UIElement child in AnnotationCanvas.Children)
            RefreshRedaction(child);
    }

    /// <summary>
    /// Applies a style and/or strength to the selected redaction as one undoable step.
    /// Returns false when nothing was selected, so the caller can treat the change as
    /// setting the default for the next mark instead.
    /// </summary>
    private bool RestyleSelectedRedaction(RedactStyle? style, int? strength, bool push)
    {
        if (_selected is not System.Windows.Shapes.Rectangle mark ||
            mark.Tag is not AnnotationData meta || meta.Type != AnnotationData.TypeRedact)
            return false;

        var before = meta.Clone();
        var after = meta.Clone();
        if (style is RedactStyle s) after.Style = s.ToString();
        if (strength is int v) after.Strength = Redaction.ClampStrength(v);
        if (before.Style == after.Style && before.Strength == after.Strength)
            return true;

        mark.Tag = after;
        RefreshRedaction(mark);
        if (push)
        {
            Push(new EditorAction(
                undo: () => { mark.Tag = before; RefreshRedaction(mark); },
                redo: () => { mark.Tag = after; RefreshRedaction(mark); }), apply: false);
        }
        return true;
    }




    // ------------------------------------------------- copy / paste / duplicate

    /// <summary>
    /// One mark on the annotation clipboard: its serialisable description plus, for an
    /// image annotation, the bitmap it draws — the description alone only carries an index
    /// into a project's embedded images, which means nothing across windows.
    /// </summary>
    private sealed record ClipboardMark(AnnotationData Data, BitmapSource? Image);

    /// <summary>
    /// The current geometry of a live annotation as serialisable data. Reads the box off
    /// the ELEMENT rather than the metadata, so a mark that has been resized copies at the
    /// size it is on screen rather than the size it was drawn at.
    /// </summary>
    private static AnnotationData SnapshotAnnotation(FrameworkElement fe, AnnotationData meta)
    {
        var data = meta.Clone();
        Vector offset = AnnotationTransform.OffsetOf(fe);
        data.Tx = offset.X;
        data.Ty = offset.Y;
        data.Angle = AnnotationTransform.AngleOf(fe);

        double left = Canvas.GetLeft(fe), top = Canvas.GetTop(fe);
        if (data.Rect is { Length: >= 4 } && !double.IsNaN(left) && !double.IsNaN(top))
        {
            double w = double.IsNaN(fe.Width) ? fe.ActualWidth : fe.Width;
            double h = double.IsNaN(fe.Height) ? fe.ActualHeight : fe.Height;
            data.Rect = new[] { left, top, w, h };
        }
        if (data.Type == AnnotationData.TypeText && CurrentFontSize(fe) is double fontSize)
            data.FontSize = fontSize;
        return data;
    }

    private List<ClipboardMark> SnapshotSelection()
    {
        var marks = new List<ClipboardMark>();
        IEnumerable<UIElement> picked = _selection.Count > 1
            ? _selection
            : _selected is null ? Array.Empty<UIElement>() : new[] { _selected };
        foreach (UIElement el in picked)
        {
            if (el is not FrameworkElement fe || fe.Tag is not AnnotationData meta) continue;
            BitmapSource? image = el is Image img ? img.Source as BitmapSource : null;
            marks.Add(new ClipboardMark(SnapshotAnnotation(fe, meta), image));
        }
        return marks;
    }

    /// <summary>Copies the selection to the annotation clipboard; false when nothing is selected.</summary>
    private bool CopySelectedAnnotations()
    {
        List<ClipboardMark> marks = SnapshotSelection();
        if (marks.Count == 0) return false;
        AnnotationClipboard.Clear();
        AnnotationClipboard.AddRange(marks);
        // Ctrl+C over a mark used to be completely silent: nothing on screen, nothing on the
        // Windows clipboard, while the button beside it said the image had been copied. The
        // user pressed it twice with no clue why the first press did nothing. Same checkmark
        // the picture copy flashes — this IS a copy, and the button's help text says which.
        ButtonFlash.Show(BtnCopy, "", "");
        return true;
    }

    /// <summary>How far a pasted or duplicated mark lands from the one it came from.</summary>
    private const double PasteOffset = 32;

    private void PasteAnnotations() => PlaceCopies(AnnotationClipboard);

    private void DuplicateSelectedAnnotations() => PlaceCopies(SnapshotSelection());

    /// <summary>
    /// Rebuilds a set of copied marks through the same factory a project reload uses, offset
    /// so the copy is visibly a copy, then selects them — one undo step for the whole set.
    /// </summary>
    private void PlaceCopies(IReadOnlyList<ClipboardMark> marks)
    {
        if (marks.Count == 0) return;
        CommitText();
        CommitPendingCurve();

        var added = new List<UIElement>();
        foreach (ClipboardMark mark in marks)
        {
            var data = mark.Data.Clone();
            data.Tx += PasteOffset;
            data.Ty += PasteOffset;
            if (data.ImageIndex is not null) data.ImageIndex = 0;
            IReadOnlyList<BitmapSource> images = mark.Image is null
                ? Array.Empty<BitmapSource>()
                : new[] { mark.Image };
            UIElement element;
            try
            {
                element = ProjectSerializer.CreateElement(data, images, _source);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not paste a '{data.Type}' annotation", ex);
                continue;
            }
            if (element is FrameworkElement fe)
            {
                fe.Tag = data;
                if (data.Tx != 0 || data.Ty != 0 || data.Angle != 0)
                    AnnotationTransform.Apply(fe, new Vector(data.Tx, data.Ty), data.Angle);
            }
            AnnotationCanvas.Children.Add(element);
            added.Add(element);
        }
        if (added.Count == 0)
        {
            // Every mark failed to rebuild. The reasons are in the log; without this the
            // paste simply looks like it did nothing.
            App.Toast("Paste failed", "WinShot could not paste those marks.", severity: ToastSeverity.Error);
            return;
        }

        Push(new EditorAction(
            undo: () =>
            {
                foreach (UIElement el in added) AnnotationCanvas.Children.Remove(el);
                Select(null);
                Select(null);
            },
            redo: () =>
            {
                foreach (UIElement el in added)
                    if (!AnnotationCanvas.Children.Contains(el))
                        AnnotationCanvas.Children.Add(el);
            }), apply: false);

        // A rotation pivot is the element's own centre, which is only known once the copy
        // has been measured — the same deferral a project reload needs.
        if (added.OfType<FrameworkElement>().Any(fe => fe.Tag is AnnotationData { Angle: not 0 }))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                foreach (FrameworkElement fe in added.OfType<FrameworkElement>())
                    if (fe.Tag is AnnotationData { Angle: not 0 } d)
                        AnnotationTransform.SetAngle(fe, d.Angle);
            }));
        }

        Select(null);
        if (added.Count == 1)
        {
            Select(added[0]);
        }
        else
        {
            Select(null);
            SelectMany(added);
        }
    }

    /// <summary>
    /// Alt-drag: leaves a copy behind and drags the copy, so the original stays put. The
    /// clone lands exactly on top of the original (no paste offset) — the drag itself is
    /// what separates them.
    /// </summary>
    private UIElement? CloneForAltDrag(UIElement source)
    {
        if (source is not FrameworkElement fe || fe.Tag is not AnnotationData meta) return null;
        var data = SnapshotAnnotation(fe, meta);
        if (data.ImageIndex is not null) data.ImageIndex = 0;
        IReadOnlyList<BitmapSource> images = source is Image img && img.Source is BitmapSource bs
            ? new[] { bs }
            : Array.Empty<BitmapSource>();
        UIElement clone;
        try
        {
            clone = ProjectSerializer.CreateElement(data, images, _source);
        }
        catch (Exception ex)
        {
            // Silent before this: the user Alt-dragged, nothing appeared, nothing was said,
            // so they Alt-dragged again. Every other user-initiated failure in the editor
            // raises something.
            Log.Error($"Could not clone a '{data.Type}' annotation", ex);
            App.Toast("Duplicate failed", "WinShot could not copy that mark.",
                severity: ToastSeverity.Error);
            return null;
        }
        if (clone is FrameworkElement cloneFe)
        {
            cloneFe.Tag = data;
            if (data.Tx != 0 || data.Ty != 0 || data.Angle != 0)
                AnnotationTransform.Apply(cloneFe, new Vector(data.Tx, data.Ty), data.Angle);
        }
        AnnotationCanvas.Children.Add(clone);
        PushAddElement(clone);
        return clone;
    }

}
