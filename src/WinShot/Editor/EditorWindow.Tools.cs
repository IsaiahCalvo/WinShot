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

    private void PlaceText(Point pos)
    {
        var style = _textStyle;
        double fontSize = AnnotationFactory.FontSizeFor(_thickness);
        if (style == TextStyle.Huge) fontSize *= 2.2;
        var tb = AnnotationFactory.CreateTextEditor(new SolidColorBrush(_color), fontSize);
        tb.Tag = style; // CommitText reads the style back when building the label
        // Offset by the editor chrome (1px border + 2px padding) so committed
        // text lands exactly where the user clicked.
        Canvas.SetLeft(tb, pos.X - 3);
        Canvas.SetTop(tb, pos.Y - 3);
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
        Vector labelOffset = AnnotationTransform.OffsetOf(label);
        x += labelOffset.X;
        y += labelOffset.Y;

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
        tb.Tag = style; // CommitText reads the style back when building the replacement label
        tb.Text = meta.Text ?? string.Empty;
        Canvas.SetLeft(tb, x - 3);
        Canvas.SetTop(tb, y - 3);
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
            if (ev.Key == Key.Enter) { CommitText(); ev.Handled = true; }
            else if (ev.Key == Key.Escape) { CancelText(); ev.Handled = true; }
        };
    }

    private void CommitText()
    {
        var tb = _activeText;
        if (tb is null) return;
        _activeText = null; // guards against re-entry from LostKeyboardFocus on removal

        double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
        string text = tb.Text;
        AnnotationCanvas.Children.Remove(tb);
        if (string.IsNullOrWhiteSpace(text)) return;

        var style = tb.Tag is TextStyle s ? s : TextStyle.Plain;
        var label = AnnotationFactory.CreateStyledTextLabel(text, tb.Foreground, tb.FontSize, style);
        Canvas.SetLeft(label, x + 3);
        Canvas.SetTop(label, y + 3);
        label.Tag = AnnotationData.ForText(new Point(x + 3, y + 3), text, style, tb.FontSize,
            tb.Foreground is SolidColorBrush fg ? fg.Color : Colors.White);
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
        AnnotationCanvas.Children.Remove(tb);
    }

    // ------------------------------------------------------------- step tool

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
    /// Live preview while the drag is still happening. Repainting means re-running the
    /// effect over the covered pixels, which is linear in the area — so on a very large
    /// mark this rate-limits itself rather than dropping the frame rate of the drag.
    /// ponytail: a fixed interval, not an adaptive one; measure before making it clever.
    /// </summary>
    private const int RedactPreviewIntervalMs = 30;

    private void UpdateRedactionPreview(System.Windows.Shapes.Rectangle mark, Rect bounds, bool force)
    {
        Canvas.SetLeft(mark, bounds.X);
        Canvas.SetTop(mark, bounds.Y);
        mark.Width = Math.Max(1, bounds.Width);
        mark.Height = Math.Max(1, bounds.Height);
        if (!force && _redactPreviewClock.ElapsedMilliseconds < RedactPreviewIntervalMs)
            return;
        _redactPreviewClock.Restart();
        AnnotationFactory.PaintRedaction(mark, _source, bounds, _redactStyle, _redactStrength, _activeRedactSeed);
    }

    /// <summary>
    /// Repaints a committed redaction from the pixels it covers NOW — after a move, a
    /// resize, a crop shift, a style change or an undo. The seed rides in the metadata so
    /// a mosaic redraws with the same blocks instead of reshuffling on every recompute.
    /// </summary>
    private void RefreshRedaction(UIElement element)
    {
        if (element is not System.Windows.Shapes.Rectangle mark ||
            mark.Tag is not AnnotationData meta || meta.Type != AnnotationData.TypeRedact)
            return;
        AnnotationFactory.PaintRedaction(mark, _source, RedactionBounds(mark, meta),
            Redaction.Parse(meta.Style), meta.Strength ?? Redaction.DefaultStrength, meta.Seed ?? 0);
    }

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

    /// <summary>Repaints every redaction on the canvas — after a crop, rotate or resize of the source.</summary>
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
        // The source under every redaction just changed (crop / rotate / resize), so each
        // one has to re-read the pixels it now covers or it keeps showing the old ones.
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

}
