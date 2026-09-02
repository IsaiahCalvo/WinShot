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
        var visual = AnnotationFactory.CreateCallout(
            layout, "", stroke, fill, thickness, _arrowhead, lineStyle, fontSize, fontFamily);
        visual.Tag = AnnotationData.ForCallout(
            layout, "", stroke, fill, thickness, _arrowhead, lineStyle, fontSize, fontFamily);
        AnnotationCanvas.Children.Add(visual);

        var tb = AnnotationFactory.CreateTextEditor(new SolidColorBrush(stroke), fontSize);
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
        tb.LostKeyboardFocus += (_, _) => CommitCalloutText();
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
        Dispatcher.InvokeAsync(() => tb.Focus());
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
    }

    /// <summary>
    /// Survey's Fabric textbox auto-grows vertically as the user types and the live bounds
    /// retract line1 to the growing edge (TextEditOverlay.jsx:239-270,
    /// svgAnnotationRenderers.jsx:1341-1352). Grow-only: never below the drag-created height.
    /// </summary>
    private void HookCalloutGrow(TextBox tb, CalloutAnnotation visual)
    {
        double floor = visual.Layout.Box.Height;
        tb.TextChanged += (_, _) =>
        {
            var layout = visual.Layout;
            double grown = Math.Max(floor, MeasureCalloutText(visual, tb.Text ?? "", layout.Box.Width));
            if (Math.Abs(grown - layout.Box.Height) < 0.5) return;
            ApplyCalloutLayout(visual, layout.WithBox(
                new Rect(layout.Box.X, layout.Box.Y, layout.Box.Width, grown)));
            tb.Height = visual.Layout.RenderBox.Height;
            UpdateSelectionVisual();
        };
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
            Select(visual);
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
        PushAddElement(visual);
        Select(visual);
        UpdateSelectionVisual();
    }

    private void PlaceText(Point pos) => PlaceTextInBox(new Rect(pos.X, pos.Y, 0, 0));

    /// <summary>
    /// Survey text: a click (empty/tiny box) opens a free editor; a drag opens a
    /// wrapped editor inset by TEXT_PADDING = 6 inside the dragged rect.
    /// </summary>
    private void PlaceTextInBox(Rect box)
    {
        var style = _textStyle;
        double fontSize = AnnotationStyle.ClampTextSize(_textFontSize);
        if (style == TextStyle.Huge) fontSize *= 2.2;
        var tb = AnnotationFactory.CreateTextEditor(new SolidColorBrush(_color), fontSize);
        tb.FontFamily = new FontFamily(_textFontFamily);
        tb.FontWeight = _textBold || style == TextStyle.Bold ? FontWeights.Bold : FontWeights.Normal;
        tb.FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal;
        tb.TextAlignment = AnnotationFactory.ParseAlign(_textAlign);
        tb.VerticalContentAlignment = AnnotationFactory.ParseVerticalAlign(_textVerticalAlign);
        tb.Tag = style;
        bool sized = box.Width >= 8 && box.Height >= 8;
        _activeTextBox = sized ? box : null;
        if (sized)
        {
            tb.Width = box.Width;
            tb.Height = box.Height;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.AcceptsReturn = true;
            Canvas.SetLeft(tb, box.X);
            Canvas.SetTop(tb, box.Y);
        }
        else
        {
            tb.Width = AnnotationFactory.DefaultTextBoxWidth;
            tb.Height = AnnotationFactory.SingleLineBoxHeight(fontSize, _textFontFamily);
            tb.TextWrapping = TextWrapping.Wrap;
            tb.AcceptsReturn = true;
            _activeTextBox = new Rect(box.X, box.Y, tb.Width, tb.Height);
            Canvas.SetLeft(tb, box.X);
            Canvas.SetTop(tb, box.Y);
        }
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        HookTextEditor(tb);
        Dispatcher.InvokeAsync(() => tb.Focus());
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
            meta.Color is string hex && TryParseColor(hex, out var c) ? c : Colors.White),
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
        tb.LostKeyboardFocus += (_, _) => CommitCalloutText();
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

        var style = Enum.TryParse(meta.Style, out TextStyle s) ? s : TextStyle.Plain;
        double fontSize = meta.FontSize is double fs && fs > 0 ? fs : AnnotationFactory.FontSizeFor(_thickness);
        var foreground = new SolidColorBrush(
            meta.Color is string hex && TryParseColor(hex, out var c) ? c : Colors.White);

        var tb = AnnotationFactory.CreateTextEditor(foreground, fontSize);
        tb.Tag = style;
        tb.Text = meta.Text ?? string.Empty;
        if (meta.Rect is { Length: >= 4 } box && box[2] >= 8 && box[3] >= 8)
        {
            tb.Width = box[2];
            tb.Height = box[3];
            tb.TextWrapping = TextWrapping.Wrap;
            tb.AcceptsReturn = true;
            _activeTextBox = new Rect(x, y, box[2], box[3]);
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
        }
        else
        {
            _activeTextBox = null;
            Canvas.SetLeft(tb, x - AnnotationFactory.TextPadding);
            Canvas.SetTop(tb, y - AnnotationFactory.TextPadding);
        }
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        HookTextEditor(tb);
        Dispatcher.InvokeAsync(() =>
        {
            tb.Focus();
            tb.SelectAll();
        });
    }

    private void HookTextEditor(TextBox tb)
    {
        tb.LostKeyboardFocus += (_, _) => CommitText();
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

        double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
        string text = tb.Text;
        AnnotationCanvas.Children.Remove(tb);
        if (string.IsNullOrWhiteSpace(text)) return;

        var style = tb.Tag is TextStyle s ? s : TextStyle.Plain;
        bool sized = box is Rect sizedBox && sizedBox.Width >= 8 && sizedBox.Height >= 8;
        Point topLeft = sized ? new Point(x, y) : new Point(x, y);
        var (strokeColor, fillColor) = AnnotationStyle.EnforceOneVisible(CurrentStrokeBrush(), CurrentFillBrush());
        double boxW = sized ? box!.Value.Width : AnnotationFactory.DefaultTextBoxWidth;
        double boxH = sized ? box!.Value.Height : AnnotationFactory.SingleLineBoxHeight(tb.FontSize, tb.FontFamily.Source);
        var label = AnnotationFactory.CreateStyledTextLabel(
            text, tb.Foreground, tb.FontSize, style, _textFontFamily,
            _textBold || style == TextStyle.Bold, _textItalic, _textUnderline, _textStrike,
            AnnotationFactory.ParseAlign(_textAlign), _textVerticalAlign,
            boxW, boxH, fillColor, _shapeSize, _lineStyle);
        Canvas.SetLeft(label, topLeft.X);
        Canvas.SetTop(label, topLeft.Y);
        label.Tag = AnnotationData.ForText(topLeft, text, style, tb.FontSize,
            tb.Foreground is SolidColorBrush fg ? fg.Color : strokeColor,
            _textFontFamily, _textBold || style == TextStyle.Bold, _textItalic, _textUnderline, _textStrike, _textAlign,
            _textVerticalAlign, new Rect(topLeft.X, topLeft.Y, boxW, boxH),
            fillColor, _shapeSize, _lineStyle);
        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(label),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(label))
                    AnnotationCanvas.Children.Add(label);
            }));
        Select(label);
    }

    private void CancelText()
    {
        var tb = _activeText;
        if (tb is null) return;
        _activeText = null;
        _activeTextBox = null;
        AnnotationCanvas.Children.Remove(tb);
    }

    // ------------------------------------------------------------- step tool

    // Erasing is live: every pointer move cuts the segment just drawn out of the ink
    // under it (Survey behaviour). Originals are remembered per element so the whole
    // drag becomes one undo step when the pointer is released.
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
                if (!GeometriesOverlap(path.Data!, cut, fe)) continue;
                if (!_eraseBefore.ContainsKey(path))
                    _eraseBefore[path] = path.Data!.CloneCurrentValue();
                var after = PaperInk.Subtract(TranslateGeometry(path.Data!, ElementOffset(path)), cut);
                // Survey culls the hairline crescents boolean subtraction leaves behind
                // (cullInkSliverPolygons, paperAnnotationGeometry.js:1586-1615) using the
                // annotation's own sourceWidth, then decides emptiness from what survives.
                after = PaperInk.CullSlivers(after, meta.Thickness ?? 1);
                after = TranslateGeometry(after, new Vector(-ElementOffset(path).X, -ElementOffset(path).Y));
                if (PaperInk.IsEmpty(after))
                {
                    AnnotationCanvas.Children.Remove(path);
                    _eraseRemoved.Add(path);
                }
                else
                {
                    path.Data = after;
                }
            }
            else if (HitsEraser(fe, cut))
            {
                AnnotationCanvas.Children.Remove(fe);
                _eraseRemoved.Add(fe);
            }
        }
    }

    /// <summary>
    /// Pointer released: cut the final segment up to the release position (Survey's
    /// finishPointer, FabricEraserCanvas.jsx:2223-2232) and fold the live edits into one
    /// undo step. The press itself already carved, so a click needs no extra dot here.
    /// </summary>
    private void ApplyEraserStroke(Point release)
    {
        if (_eraserPoints.Count > 0 && (release - _eraserPoints[^1]).Length >= PaperInk.LiveMinDistance)
            EraseLive(_eraserPoints[^1], release);
        _eraserPoints.Clear();
        if (_eraseBefore.Count == 0 && _eraseRemoved.Count == 0) return;

        var removed = _eraseRemoved.ToList();
        var inkEdits = _eraseBefore
            .Where(kv => !removed.Contains(kv.Key))
            .Select(kv => (path: kv.Key, before: kv.Value, after: kv.Key.Data))
            .ToList();
        var removedBefore = removed.OfType<Path>().Where(p => _eraseBefore.ContainsKey(p))
            .ToDictionary(p => p, p => _eraseBefore[p]);
        _eraseBefore.Clear();
        _eraseRemoved.Clear();

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
        return copy.GetFlattenedPathGeometry();
    }

    private static bool GeometriesOverlap(Geometry ink, Geometry cut, FrameworkElement owner)
    {
        // Survey decides overlap by real geometric intersection, with bounds used only as
        // a fast-reject that can never drop a true hit (paperAnnotationGeometry.js:2396-2421;
        // eraserBoundsPrefilter.js:5-20). A bounding-box area proxy is neither an upper nor
        // a lower bound on the intersection, so any non-empty intersection counts.
        var world = TranslateGeometry(ink, ElementOffset(owner));
        var inter = Geometry.Combine(world, cut, GeometryCombineMode.Intersect, Transform.Identity);
        return inter is not null && !inter.IsEmpty();
    }

    private bool HitsEraser(FrameworkElement fe, Geometry cut)
    {
        if (fe is CalloutAnnotation)
        {
            var bounds = GetCanvasBounds(fe);
            var box = new RectangleGeometry(bounds);
            var inter = Geometry.Combine(box, cut, GeometryCombineMode.Intersect, Transform.Identity);
            return inter is not null && !inter.IsEmpty() && inter.Bounds.Width > 0 && inter.Bounds.Height > 0;
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
            return GeometriesOverlap(p.Data, cut, fe);
        var hit = new RectangleGeometry(b);
        var overlap = Geometry.Combine(hit, cut, GeometryCombineMode.Intersect, Transform.Identity);
        return overlap is not null && !overlap.IsEmpty() && overlap.Bounds.Width > 0 && overlap.Bounds.Height > 0;
    }

    private void PlaceStep(Point pos)
    {
        int number = _nextStep;
        bool letters = _stepLetters;
        var badge = AnnotationFactory.CreateStepBadge(number, _color, _thickness, letters);
        double left = pos.X - badge.Width / 2, top = pos.Y - badge.Height / 2;
        Canvas.SetLeft(badge, left);
        Canvas.SetTop(badge, top);
        var data = AnnotationData.ForStep(new Point(left, top), number, _color, _thickness);
        // Record the caption mode so project reload rebuilds the same number/letter badge.
        if (letters) data.Style = "Letter";
        badge.Tag = data;
        Push(new EditorAction(
            undo: () =>
            {
                AnnotationCanvas.Children.Remove(badge);
                _nextStep = number;
            },
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(badge))
                    AnnotationCanvas.Children.Add(badge);
                _nextStep = number + 1;
            }));
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
