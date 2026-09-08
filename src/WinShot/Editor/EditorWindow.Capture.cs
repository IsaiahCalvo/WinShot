using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WinShot.Core;
using WinShot.Ocr;
using SD = System.Drawing;

namespace WinShot.Editor;

public partial class EditorWindow : Window
{
    /// <summary>
    /// How the editor asks for another region capture. App fills this in at startup; the
    /// editor cannot reach the capture flow itself, and a static hook is a smaller seam than
    /// threading a capture service through every constructor that makes an editor.
    /// Null in tests and in a standalone editor, where Add capture simply does nothing.
    /// </summary>
    internal static Func<Task<SD.Bitmap?>>? RegionCaptureProvider { get; set; }

    // ------------------------------------------------------------- add capture

    /// <summary>
    /// Takes another region shot and adds it to this canvas. The editor hides for the
    /// duration — it is almost certainly covering whatever the user wants to grab.
    /// </summary>
    private async void OnAddCapture(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        if (RegionCaptureProvider is not { } capture) return;

        CommitText();
        CommitPendingCurve();
        WindowState state = WindowState;
        Hide();

        SD.Bitmap? shot = null;
        bool failed = false;
        try
        {
            shot = await capture();
        }
        catch (Exception ex)
        {
            // The editor un-hides with nothing added; without this the only record of why
            // is the log, while the sibling failure below already says so out loud.
            Log.Error("Add capture failed", ex);
            failed = true;
        }
        finally
        {
            if (!_closed)
            {
                Show();
                WindowState = state;
                Activate();
            }
        }

        if (shot is null || _closed)
        {
            shot?.Dispose();
            if (failed && !_closed)
                ThemedMessageDialog.Show(this, "Could not take the capture",
                    "WinShot could not take that shot. Try again in a moment.");
            return;
        }

        try
        {
            if (_settings.Current.AutoExpandCanvas)
                await AppendCaptureBelowAsync(shot);
            else
                InsertImageAnnotation(CaptureService.ToBitmapSource(shot), ViewportCenterInContent());
        }
        catch (Exception ex)
        {
            Log.Error("Adding the capture to the canvas failed", ex);
            // The await above can outlive the window; ThemedMessageDialog assigns Owner
            // unconditionally and WPF throws on a closed one, out of an async void handler.
            if (!_closed)
                ThemedMessageDialog.Show(this, "Could not add the capture",
                    "WinShot took the shot but could not add it to this canvas.");
        }
        finally
        {
            shot.Dispose();
        }
    }

    /// <summary>
    /// Grows the canvas downward and drops the new shot in flush under the old one. Nothing
    /// already on the canvas moves — every annotation keeps its coordinates — so unlike the
    /// rotate/resize path this does not have to flatten anything first.
    /// </summary>
    private async Task AppendCaptureBelowAsync(SD.Bitmap capture)
    {
        SD.Bitmap before = _source;
        int width = Math.Max(before.Width, capture.Width);
        int height = before.Height + capture.Height;

        var after = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(after))
        {
            g.Clear(SD.Color.Transparent);
            g.DrawImageUnscaled(before, 0, 0);
            g.DrawImageUnscaled(capture, 0, before.Height);
        }
        _owned.Add(after);

        _source = after;
        await OnSourceReplacedAsync();
        if (_closed) return;

        Push(new EditorAction(
            undo: async () => { _source = before; await OnSourceReplacedAsync(); },
            redo: async () => { _source = after; await OnSourceReplacedAsync(); }), apply: false);
    }

    // ------------------------------------------------------------------- OCR

    /// <summary>
    /// Reads the text in the crop region — or the whole picture when nothing is cropped —
    /// and copies it. OCR was reachable only from the tray and the quick-actions card, so
    /// once an image was open in the editor the only way to read it was to close the editor.
    /// </summary>
    private async void OnExtractText(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive || _ocrRunning) return;
        // A cold read of a large region takes seconds with nothing on screen; two clicks
        // used to mean two passes and two toasts. T6 owns the shared working indicator —
        // this is the guard that stops the second pass in the meantime.
        _ocrRunning = true;
        OcrRegionBtn.IsEnabled = false;
        // Flatten allocates a RenderTargetBitmap the full size of the canvas and can throw on
        // a grown one; outside the try it would leave the flag set and the button dead for the
        // life of the window, so it is guarded like every sibling's flatten.
        SD.Bitmap? flat = null;
        try
        {
            flat = Flatten();
            if (_pendingCrop is SD.Rectangle region && region.Width > 1 && region.Height > 1)
            {
                SD.Bitmap cropped = CaptureService.Crop(flat, region);
                flat.Dispose();
                flat = cropped;
            }

            OcrCaptureResult result = await OcrService.ExtractAsync(flat, _settings.Current.OcrJoinLines);
            // The same formatter the tray and the card use, so the editor's result reads
            // identically to every other way of getting text out of a shot.
            if (OcrClipboardFormatter.Build(result) is not { } payload)
            {
                App.Toast("Extract text", "No text found in that area.");
                return;
            }

            await CaptureService.SetTextToClipboardAsync(payload.ClipboardText);
            App.Toast(payload.BalloonTitle, payload.Preview, severity: ToastSeverity.Success);
        }
        catch (OcrUnavailableException ex)
        {
            // The one message in this flow written for the user — it names the fix (add a
            // language pack). The tray path already lets it through; the editor button was
            // swallowing it into a generic failure with no way to discover the cause.
            Log.Info($"OCR unavailable: {ex.Message}");
            App.Toast("OCR unavailable", ex.Message, severity: ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error("Editor OCR failed", ex);
            App.Toast("Extract text", "WinShot could not read the text in that area.",
                severity: ToastSeverity.Error);
        }
        finally
        {
            flat?.Dispose();
            _ocrRunning = false;
            if (!_closed) OcrRegionBtn.IsEnabled = true;
        }
    }

    /// <summary>An OCR pass is in flight; the button is disabled and a second click is dropped.</summary>
    private bool _ocrRunning;

    /// <summary>
    /// "Text only": covers each WORD the OCR engine finds inside the dragged region and
    /// leaves photos and graphics visible. Every box becomes an ordinary redaction object,
    /// so each one can still be moved, restyled or deleted afterwards — and the whole set is
    /// one undo step, because "hide the text here" was one gesture.
    /// </summary>
    private const int MaxTextOnlyBoxes = 250;

    private async Task RedactTextInRegionAsync(Rect area)
    {
        SD.Rectangle region = ToPixelRect(area);
        if (region.Width < 4 || region.Height < 4) return;

        IReadOnlyList<SD.Rectangle> boxes;
        try
        {
            using SD.Bitmap crop = CaptureService.Crop(_source, region);
            boxes = await OcrService.ExtractWordBoxesAsync(crop);
        }
        catch (Exception ex)
        {
            Log.Error("Text-only redaction failed to read the region", ex);
            App.Toast("Redact text", "WinShot could not read that area.", severity: ToastSeverity.Error);
            return;
        }
        if (_closed) return;

        if (boxes.Count == 0)
        {
            // An empty result and a missing language pack look identical from here, and
            // "no text found" sends the user hunting for text that was never readable.
            App.Toast("Redact text", OcrService.HasLanguagePack()
                ? "No text found in that area."
                : "No OCR language pack is installed. Add one in Settings > Time & language.");
            return;
        }

        if (boxes.Count > MaxTextOnlyBoxes)
        {
            // Every box is a live redaction: its own GDI render now, and another one each
            // time the picture is cropped, rotated or resized. A full page of text is ~800
            // of them on the UI thread, which is where a nice feature becomes a freeze.
            App.Toast("Redact text",
                $"That area has more than {MaxTextOnlyBoxes} words. Redact it in smaller pieces, or drop Text only to cover the whole region.");
            return;
        }

        var added = new List<UIElement>();
        foreach (SD.Rectangle box in boxes)
        {
            // The engine's boxes hug the glyphs; a couple of pixels of margin stops a
            // descender or an antialiased edge peeking out from under the mark.
            var padded = SD.Rectangle.Inflate(box, 2, 2);
            var bounds = new Rect(
                region.X + padded.X, region.Y + padded.Y,
                Math.Max(1, padded.Width), Math.Max(1, padded.Height));

            int seed = ToolRandom.Next();
            var mark = new System.Windows.Shapes.Rectangle();
            Canvas.SetLeft(mark, bounds.X);
            Canvas.SetTop(mark, bounds.Y);
            AnnotationFactory.PaintRedaction(mark, _source, bounds, _redactStyle, _redactStrength, seed);
            mark.Tag = AnnotationData.ForRedaction(bounds, _redactStyle, _redactStrength, seed);
            AnnotationCanvas.Children.Add(mark);
            added.Add(mark);
        }

        Push(new EditorAction(
            undo: () =>
            {
                foreach (UIElement el in added) AnnotationCanvas.Children.Remove(el);
            },
            redo: () =>
            {
                foreach (UIElement el in added)
                    if (!AnnotationCanvas.Children.Contains(el))
                        AnnotationCanvas.Children.Add(el);
            }), apply: false);
    }
}
