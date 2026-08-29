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
        var layout = CalloutLayout.FromDrag(tip, boxOrigin);
        var (stroke, fill) = AnnotationStyle.EnforceOneVisible(CurrentStrokeBrush(), CurrentFillBrush());
        double fontSize = AnnotationStyle.ClampTextSize(_textFontSize);
        var visual = AnnotationFactory.CreateCallout(
            layout, "", stroke, fill, _thickness, _arrowhead,
            _lineStyle == LineBorderStyle.Cloud ? LineBorderStyle.Solid : _lineStyle, fontSize);
        visual.Tag = AnnotationData.ForCallout(
            layout, "", stroke, fill, _thickness, _arrowhead,
            _lineStyle == LineBorderStyle.Cloud ? LineBorderStyle.Solid : _lineStyle, fontSize);
        AnnotationCanvas.Children.Add(visual);

        var tb = AnnotationFactory.CreateTextEditor(new SolidColorBrush(stroke), fontSize);
        tb.Width = layout.Box.Width;
        tb.Height = layout.Box.Height;
        tb.Tag = visual;
        Canvas.SetLeft(tb, layout.Box.X);
        Canvas.SetTop(tb, layout.Box.Y);
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        tb.LostKeyboardFocus += (_, _) => CommitCalloutText();
        tb.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter) { CommitCalloutText(); ev.Handled = true; }
            else if (ev.Key == Key.Escape)
            {
                AnnotationCanvas.Children.Remove(tb);
                _activeText = null;
                AnnotationCanvas.Children.Remove(visual);
                ev.Handled = true;
            }
        };
        Dispatcher.InvokeAsync(() => tb.Focus());
    }

    private void CommitCalloutText()
    {
        var tb = _activeText;
        if (tb is null || tb.Tag is not CalloutAnnotation visual) return;
        _activeText = null;
        string text = tb.Text?.Trim() ?? "";
        AnnotationCanvas.Children.Remove(tb);
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
        tb.FontWeight = _textBold || style == TextStyle.Bold ? FontWeights.Bold : FontWeights.SemiBold;
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
            Canvas.SetLeft(tb, box.X - AnnotationFactory.TextPadding);
            Canvas.SetTop(tb, box.Y - AnnotationFactory.TextPadding);
        }
        AnnotationCanvas.Children.Add(tb);
        _activeText = tb;
        HookTextEditor(tb);
        Dispatcher.InvokeAsync(() => tb.Focus());
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
        Point topLeft = sized ? new Point(x, y) : new Point(x + AnnotationFactory.TextPadding, y + AnnotationFactory.TextPadding);
        var label = AnnotationFactory.CreateStyledTextLabel(
            text, tb.Foreground, tb.FontSize, style, _textFontFamily,
            _textBold || style == TextStyle.Bold, _textItalic, _textUnderline, _textStrike,
            AnnotationFactory.ParseAlign(_textAlign), _textVerticalAlign,
            sized ? box!.Value.Width : null, sized ? box!.Value.Height : null);
        Canvas.SetLeft(label, topLeft.X);
        Canvas.SetTop(label, topLeft.Y);
        label.Tag = AnnotationData.ForText(topLeft, text, style, tb.FontSize,
            tb.Foreground is SolidColorBrush fg ? fg.Color : Colors.White,
            _textFontFamily, _textBold || style == TextStyle.Bold, _textItalic, _textUnderline, _textStrike, _textAlign,
            _textVerticalAlign, sized ? box : null);
        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(label),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(label))
                    AnnotationCanvas.Children.Add(label);
            }));
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

    private void ApplyEraserStroke()
    {
        if (_eraserPoints.Count == 0) return;
        var cut = PaperInk.EraserStroke(_eraserPoints, _eraserSize);
        _eraserPoints.Clear();
        var removed = new List<UIElement>();
        var inkEdits = new List<(Path path, Geometry before, Geometry after, AnnotationData meta)>();

        foreach (UIElement child in AnnotationCanvas.Children.Cast<UIElement>().ToList())
        {
            if (child is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
                continue;
            bool ink = meta.Type is AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter;
            if (_eraserPartial)
            {
                if (!ink || fe is not Path path || path.Data is null) continue;
                if (!GeometriesOverlap(path.Data, cut, fe)) continue;
                var before = path.Data.CloneCurrentValue();
                var after = PaperInk.Subtract(TranslateGeometry(before, ElementOffset(path)), cut);
                after = TranslateGeometry(after, new Vector(-ElementOffset(path).X, -ElementOffset(path).Y));
                if (PaperInk.IsEmpty(after))
                    removed.Add(path);
                else
                    inkEdits.Add((path, before, after, meta));
            }
            else if (HitsEraser(fe, cut))
            {
                removed.Add(fe);
            }
        }

        foreach (var (path, _, after, _) in inkEdits)
            path.Data = after;
        foreach (var el in removed)
            AnnotationCanvas.Children.Remove(el);
        if (removed.Count == 0 && inkEdits.Count == 0) return;

        Push(new EditorAction(
            undo: () =>
            {
                foreach (var el in removed)
                    if (!AnnotationCanvas.Children.Contains(el))
                        AnnotationCanvas.Children.Add(el);
                foreach (var (path, before, _, _) in inkEdits)
                    path.Data = before;
            },
            redo: () =>
            {
                foreach (var el in removed)
                    AnnotationCanvas.Children.Remove(el);
                foreach (var (path, _, after, _) in inkEdits)
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
        var world = TranslateGeometry(ink, ElementOffset(owner));
        var inter = Geometry.Combine(world, cut, GeometryCombineMode.Intersect, Transform.Identity);
        return inter is not null && !inter.IsEmpty() && inter.Bounds.Width * inter.Bounds.Height > 0.25;
    }

    private static bool HitsEraser(FrameworkElement fe, Geometry cut)
    {
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
        var box = new RectangleGeometry(b);
        var inter = Geometry.Combine(box, cut, GeometryCombineMode.Intersect, Transform.Identity);
        return inter is not null && !inter.IsEmpty() && inter.Bounds.Width > 0 && inter.Bounds.Height > 0;
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
            ClearCropPreview();
            return;
        }
        ClearCropPreview();

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

    private void OnCancelCrop(object sender, RoutedEventArgs e) => ClearCropPreview();

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
