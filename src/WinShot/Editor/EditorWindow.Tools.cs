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
        // Record the caption mode so a future serializer can round-trip letter badges;
        // the current project schema still rebuilds steps as numbers on reload.
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

    // ------------------------------------------------------------ emoji tool

    /// <summary>Drops the picked emoji as a 32px text annotation centered on the click.</summary>
    private void PlaceEmoji(Point pos)
    {
        var label = AnnotationFactory.CreateEmojiLabel(_pendingEmoji);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = pos.X - label.DesiredSize.Width / 2, top = pos.Y - label.DesiredSize.Height / 2;
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        label.Tag = AnnotationData.ForEmoji(new Point(left, top), _pendingEmoji);
        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(label),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(label))
                    AnnotationCanvas.Children.Add(label);
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
        _sourceOperationActive = true;
        Cursor = Cursors.Wait;
        try
        {
            await RefreshImageAsync();
        }
        finally
        {
            _sourceOperationActive = false;
            UpdateCursor();
        }
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
