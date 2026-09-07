using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            if (ev.Key == Key.Escape) { CancelText(); ev.Handled = true; return; }
            if (ev.Key != Key.Enter) return;

            // Shift+Enter breaks the line; plain Enter and Ctrl+Enter both commit, so the
            // habit of pressing Enter to finish survives the box becoming multi-line.
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                return;
            CommitText();
            ev.Handled = true;
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
        // A counter lands rather than appearing: 0.6 -> 1 with a light overshoot, about its
        // own centre so the badge does not slide while it grows. The centre is the SCALE's
        // own CenterX/CenterY, not RenderTransformOrigin: AnnotationTransform.Apply builds
        // its RotateTransform around an explicit element-local pivot on the assumption that
        // the origin is (0,0), so a badge left at (0.5,0.5) would orbit rather than spin the
        // day counters become rotatable.
        badge.RenderTransform = new ScaleTransform(1, 1)
        {
            CenterX = badge.Width / 2,
            CenterY = badge.Height / 2,
        };
        badge.BeginStoryboard((Storyboard)FindResource("CounterPopIn"));
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
        IEnumerable<UIElement> picked = _multiSelected.Count > 1
            ? _multiSelected
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
        if (added.Count == 0) return;

        Push(new EditorAction(
            undo: () =>
            {
                foreach (UIElement el in added) AnnotationCanvas.Children.Remove(el);
                Select(null);
                ClearMultiSelection();
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

        ClearMultiSelection();
        if (added.Count == 1)
        {
            Select(added[0]);
        }
        else
        {
            Select(null);
            _multiSelected.AddRange(added);
            UpdateMultiSelectionVisual();
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
            Log.Error($"Could not clone a '{data.Type}' annotation", ex);
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
        if (!force && _redactPreviewClock.ElapsedMilliseconds < RedactPreviewIntervalMs) return;
        _redactPreviewClock.Restart();
        AnnotationFactory.PaintMagnifier(
            bubble, _source, bounds, MagnifierLayout.DefaultFocus(bounds), _magnification);
    }

    /// <summary>
    /// Repaints a committed redaction from the pixels it covers NOW — after a move, a
    /// resize, a crop shift, a style change or an undo. The seed rides in the metadata so
    /// a mosaic redraws with the same blocks instead of reshuffling on every recompute.
    /// </summary>
    private void RefreshRedaction(UIElement element)
    {
        if (element is not System.Windows.Shapes.Rectangle mark ||
            mark.Tag is not AnnotationData meta)
            return;

        // Two marks are painted from the pixels underneath rather than from their own
        // properties, and both have to be repainted by the same six callers — every move,
        // resize, crop, rotate, undo and redo. One entry point, so a new pixel-backed mark
        // never has to find those six again.
        switch (meta.Type)
        {
            case AnnotationData.TypeRedact:
                AnnotationFactory.PaintRedaction(mark, _source, RedactionBounds(mark, meta),
                    Redaction.Parse(meta.Style), meta.Strength ?? Redaction.DefaultStrength, meta.Seed ?? 0);
                break;
            case AnnotationData.TypeMagnifier:
                AnnotationFactory.PaintMagnifier(mark, _source, RedactionBounds(mark, meta),
                    MagnifierFocus(meta), meta.Zoom ?? MagnifierLayout.DefaultScale);
                break;
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
        SyncImageSizeLabel();
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
