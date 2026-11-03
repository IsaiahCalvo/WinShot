using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinShot.Core;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace WinShot.Ocr;

/// <summary>
/// Combined result of one recognition pass: OCR text plus any decoded QR/DataMatrix/Aztec
/// payloads found in the image. <paramref name="Lines"/> is the recognizer's raw output before
/// <see cref="OcrTextFormatter"/> joined or broke it, kept so the result toast can offer the
/// other answer after the user has seen the text -- joining is a preference you can only
/// judge once you are looking at it.
/// </summary>
public sealed record OcrCaptureResult(string Text, List<string> QrCodes, List<string> Lines)
{
    public OcrCaptureResult(string text, List<string> qrCodes)
        : this(text, qrCodes, new List<string>()) { }
}

/// <summary>
/// Extracts text from a bitmap using the built-in Windows OCR engine
/// (Windows.Media.Ocr) and decodes 2D barcodes via ZXing.Net.
/// OCR requires an installed OCR-capable language pack.
/// </summary>
public static class OcrService
{
    private static readonly object EngineGate = new();
    private static readonly List<BarcodeFormat> BarcodeFormats =
    [
        BarcodeFormat.QR_CODE,
        BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.AZTEC,
    ];

    private static OcrEngine? _engine;
    // Volatile because the unlocked fast path in GetEngine reads them: the engine is built
    // under EngineGate on whichever thread ran the capture, and the next capture can be on
    // another. Without this a stale read costs one more capture on the previous language.
    private static volatile bool _engineInitialized;
    private static volatile string _engineLanguage = AutoLanguage;

    /// <summary>The "let Windows decide" value of <see cref="PreferredLanguage"/>.</summary>
    public const string AutoLanguage = "auto";

    // Volatile, not guarded by EngineGate: GetEngine holds that lock across
    // OcrEngine.TryCreateFromLanguage, and this setter runs on the UI thread from Settings'
    // Changed event -- pressing Done while a capture built a cold engine stalled the UI for
    // the length of the build. Publishing the string is all the setter ever needed to do;
    // GetEngine re-reads it under the lock and rebuilds on the next capture.
    private static volatile string _preferredLanguage = AutoLanguage;

    /// <summary>
    /// BCP-47 tag the recognizer should prefer, or <see cref="AutoLanguage"/> to follow the
    /// user profile. App pushes Settings.OcrLanguage here at startup and whenever Settings is
    /// saved; changing it rebuilds the cached engine on the next capture rather than
    /// immediately, so a settings save never pays the engine-creation cost on the UI thread.
    /// </summary>
    public static string PreferredLanguage
    {
        get => _preferredLanguage;
        set
        {
            string tag = string.IsNullOrWhiteSpace(value) ? AutoLanguage : value.Trim();
            if (string.Equals(_preferredLanguage, tag, StringComparison.OrdinalIgnoreCase))
                return;
            _preferredLanguage = tag;
        }
    }

    /// <summary>
    /// The recognizer languages this machine actually has packs for, as BCP-47 tags with the
    /// display names Windows gives them. Empty when no pack is installed at all -- the combo
    /// then offers only "Automatically detect", which is the honest answer.
    /// </summary>
    public static IReadOnlyList<(string Tag, string Display)> AvailableLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => (l.LanguageTag, string.IsNullOrWhiteSpace(l.DisplayName) ? l.LanguageTag : l.DisplayName))
                .OrderBy(l => l.Item2, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Could not list OCR recognizer languages (non-fatal)", ex);
            return Array.Empty<(string, string)>();
        }
    }

    private const double MaxInitialUpscale = 4.0;
    private const int OcrTargetLongSide = 1600;

    /// <summary>Below this, an empty read might still be a glyph-size problem worth a
    /// second pass; at or above it, it is genuinely a picture with no text.</summary>
    private const int RetryLongSideFloor = 800;

    /// <summary>
    /// Builds the OCR engine ahead of the first capture. Creating it is a one-time cost
    /// that otherwise lands on whoever presses the hotkey first.
    /// </summary>
    public static void Warm() => Task.Run(() =>
    {
        try
        {
            GetEngine();
        }
        catch (Exception ex)
        {
            Log.Error("OCR engine warm-up failed", ex);
        }
    });

    /// <summary>
    /// Runs barcode detection and OCR over <paramref name="bmp"/>.
    /// <paramref name="joinLines"/>=true joins OCR lines with spaces into a
    /// paragraph; false keeps one line per OCR line.
    /// Throws <see cref="OcrUnavailableException"/> when no OCR language pack
    /// is available and the image contains no barcodes either.
    /// </summary>
    public static async Task<OcrCaptureResult> ExtractAsync(SD.Bitmap bmp, bool joinLines)
    {
        var total = Stopwatch.StartNew();
        long snapshotMs = 0;
        long softwareBitmapMs = 0;
        long recognizeMs = 0;
        long barcodeMs = 0;
        long deepBarcodeMs = 0;
        OcrEngine? engine = GetEngine();

        // The engine rejects images larger than MaxImageDimension (~2600 px), so
        // oversized captures (e.g. a 4K fullscreen) are scaled down to fit. The
        // barcode snapshot stays at native size otherwise — ZXing prefers it.
        {
            double fitScale = Math.Min(1.0, OcrEngine.MaxImageDimension / (double)Math.Max(bmp.Width, bmp.Height));

            var step = Stopwatch.StartNew();
            BgraSnapshot snapshot = await Task.Run(() => SnapshotAtScale(bmp, fitScale));
            snapshotMs = step.ElapsedMilliseconds;
            Task<(List<string> Results, long ElapsedMs)> fastBarcodeTask = Task.Run(() =>
            {
                var qr = Stopwatch.StartNew();
                return (DecodeBarcodesFast(snapshot), qr.ElapsedMilliseconds);
            });

            if (ShouldPreferBarcodeResult(snapshot))
            {
                var standaloneCode = await fastBarcodeTask;
                barcodeMs = standaloneCode.ElapsedMs;
                if (standaloneCode.Results.Count > 0)
                {
                    LogOcrBreakdown(snapshot, snapshotMs, softwareBitmapMs, recognizeMs, barcodeMs, deepBarcodeMs, total.ElapsedMilliseconds);
                    return new OcrCaptureResult(string.Empty, standaloneCode.Results);
                }
            }

            if (engine is null)
            {
                // No OCR available, but a barcode hit is still a useful result.
                var qrOnly = await DecodeBarcodesWithFallbackAsync(snapshot, fastBarcodeTask);
                barcodeMs = qrOnly.ElapsedMs;
                deepBarcodeMs = qrOnly.DeepElapsedMs;
                LogOcrBreakdown(snapshot, snapshotMs, softwareBitmapMs, recognizeMs, barcodeMs, deepBarcodeMs, total.ElapsedMilliseconds);
                if (qrOnly.Results.Count > 0)
                    return new OcrCaptureResult(string.Empty, qrOnly.Results);
                // Goes straight onto the toast, whose body box holds three 244-DIP rows. The
                // long form ran past that and lost its tail — and the tail is the whole point
                // of this message, so it says where to go and stops.
                throw new OcrUnavailableException(
                    "No OCR language pack is installed. Add one in Settings > Time & language.");
            }

            // Windows OCR misses glyphs under ~20 px tall, and screen text in a
            // typical selection sits below that. Small captures are magnified
            // before recognition; an empty read retries at double the scale
            // until the engine's dimension cap is hit.
            string text = string.Empty;
            var lines = new List<string>();
            double scale = InitialOcrScale(bmp.Width, bmp.Height);
            double firstScale = scale;
            BgraSnapshot? firstSnapshot = null;
            while (true)
            {
                double snapScale = scale;
                BgraSnapshot ocrSnapshot = Math.Abs(snapScale - fitScale) < 0.01
                    ? snapshot
                    : await Task.Run(() => SnapshotAtScale(bmp, snapScale));
                snapshotMs += Math.Abs(snapScale - fitScale) < 0.01 ? 0 : step.ElapsedMilliseconds;
                firstSnapshot ??= ocrSnapshot;

                step.Restart();
                int pad = OcrPad(bmp.Width, bmp.Height, snapScale);
                BgraSnapshot input = await Task.Run(() => Padded(ocrSnapshot, pad, enhance: false));
                softwareBitmapMs += step.ElapsedMilliseconds;
                step.Restart();
                lines = await RecognizeLinesAsync(engine, input);
                recognizeMs += step.ElapsedMilliseconds;
                text = OcrTextFormatter.Format(lines, joinLines);

                if (!string.IsNullOrWhiteSpace(text))
                    break;
                if (!ShouldRetryLarger(bmp.Width, bmp.Height, scale, out double next))
                    break;
                scale = next;
                step.Restart();
            }

            // Nothing yet: one more look at the first pass's image in high-contrast grey,
            // light-on-dark turned to dark-on-light, before answering "no text".
            if (string.IsNullOrWhiteSpace(text) && firstSnapshot is { } plain)
            {
                step.Restart();
                int pad = OcrPad(bmp.Width, bmp.Height, firstScale);
                BgraSnapshot enhanced = await Task.Run(() => Padded(plain, pad, enhance: true));
                lines = await RecognizeLinesAsync(engine, enhanced);
                recognizeMs += step.ElapsedMilliseconds;
                text = OcrTextFormatter.Format(lines, joinLines);
            }

            var fastBarcode = await fastBarcodeTask;
            barcodeMs = fastBarcode.ElapsedMs;
            List<string> qrCodes = fastBarcode.Results;
            if (qrCodes.Count == 0 && string.IsNullOrWhiteSpace(text))
            {
                var deepBarcode = await DecodeBarcodesDeepAsync(snapshot);
                deepBarcodeMs = deepBarcode.ElapsedMs;
                barcodeMs += deepBarcode.ElapsedMs;
                qrCodes = deepBarcode.Results;
            }

            LogOcrBreakdown(snapshot, snapshotMs, softwareBitmapMs, recognizeMs, barcodeMs, deepBarcodeMs, total.ElapsedMilliseconds);
            return new OcrCaptureResult(text, qrCodes, lines);
        }
    }

    /// <summary>
    /// Whether an empty read is worth one more pass at a bigger scale, and at what scale.
    ///
    /// This loop used to double until it hit the engine's dimension cap. On a 218x224 crop
    /// with no text in it that was three passes ending on a 2528x2598 upscale — 6.4 seconds
    /// to answer "nothing here", which is the one answer that has to be quick. A retry only
    /// pays when the first pass was clamped by <see cref="InitialOcrScale"/>'s 4x ceiling
    /// and the result is still small enough that glyphs may sit under the engine's ~20px
    /// floor; past that the first pass has already seen everything a second one would.
    /// </summary>
    internal static bool ShouldRetryLarger(int width, int height, double scale, out double next)
    {
        next = 0;
        int maxSide = Math.Max(width, height);
        if (scale < MaxInitialUpscale - 0.01)
            return false; // the first pass already reached its target size
        if (maxSide * scale >= RetryLongSideFloor)
            return false; // already big enough that small glyphs were not the problem

        next = Math.Min(scale * 2, OcrEngine.MaxImageDimension / (double)maxSide);
        return next >= scale * 1.25;
    }

    /// <summary>Scale for the first OCR pass: small captures are magnified
    /// (up to 4x, toward a ~1600 px long side) so typical screen text clears
    /// the engine's minimum glyph size; oversized captures shrink to fit
    /// <see cref="OcrEngine.MaxImageDimension"/>.</summary>
    internal static double InitialOcrScale(int width, int height)
    {
        int maxSide = Math.Max(width, height);
        double upscale = Math.Clamp(OcrTargetLongSide / (double)maxSide, 1.0, MaxInitialUpscale);
        return Math.Min(upscale, OcrEngine.MaxImageDimension / (double)maxSide);
    }

    /// <summary>
    /// Where the words are, in <paramref name="bmp"/>'s OWN pixel coordinates. The engine
    /// reads a scaled copy, so every box is divided back down by the scale that was used —
    /// a caller that skipped that would redact the top-left quarter of the text.
    ///
    /// Returns an empty list when there is no language pack rather than throwing: the
    /// callers here are "hide the text if you can find it", not "read this out to me".
    /// </summary>
    public static async Task<IReadOnlyList<SD.Rectangle>> ExtractWordBoxesAsync(SD.Bitmap bmp)
    {
        OcrEngine? engine = GetEngine();
        if (engine is null) return Array.Empty<SD.Rectangle>();

        double scale = InitialOcrScale(bmp.Width, bmp.Height);
        BgraSnapshot snapshot = await Task.Run(() => SnapshotAtScale(bmp, scale));
        int pad = OcrPad(bmp.Width, bmp.Height, scale);
        BgraSnapshot input = Padded(snapshot, pad, enhance: false);
        using SoftwareBitmap softwareBitmap = ToSoftwareBitmap(input);
        OcrResult result = await engine.RecognizeAsync(softwareBitmap);

        var boxes = new List<SD.Rectangle>();
        var image = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
        foreach (OcrLine line in result.Lines)
        {
            foreach (OcrWord word in line.Words)
            {
                SD.Rectangle box = ScaleWordBox(
                    word.BoundingRect.X - pad, word.BoundingRect.Y - pad,
                    word.BoundingRect.Width, word.BoundingRect.Height,
                    scale, image);
                if (box.Width > 0 && box.Height > 0)
                    boxes.Add(box);
            }
        }
        return boxes;
    }

    /// <summary>
    /// One word box, divided back down by the scale the engine read at and clipped to the
    /// image. Rounding is deliberately outward — floor the origin, ceil the size — so the
    /// box never lands a pixel INSIDE a glyph, which for a redaction would leave a sliver
    /// of the letter showing.
    /// </summary>
    internal static SD.Rectangle ScaleWordBox(
        double x, double y, double width, double height, double scale, SD.Rectangle image)
    {
        if (scale <= 0 || !double.IsFinite(scale)) return SD.Rectangle.Empty;

        int left = (int)Math.Floor(x / scale);
        int top = (int)Math.Floor(y / scale);
        var box = new SD.Rectangle(
            left,
            top,
            (int)Math.Ceiling((x + width) / scale) - left,
            (int)Math.Ceiling((y + height) / scale) - top);
        box.Intersect(image);
        return box;
    }

    /// <summary>Snapshots <paramref name="bmp"/> rendered at <paramref name="scale"/>
    /// with high-quality bicubic resampling (cheap ctor interpolation smears
    /// glyph edges and hurts OCR accuracy).</summary>
    private static BgraSnapshot SnapshotAtScale(SD.Bitmap bmp, double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.01)
            return CaptureBgraSnapshot(bmp);

        lock (bmp)
        {
            int targetW = Math.Max(1, (int)Math.Round(bmp.Width * scale));
            int targetH = Math.Max(1, (int)Math.Round(bmp.Height * scale));
            using var scaled = new SD.Bitmap(targetW, targetH, SDI.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(scaled))
            {
                g.CompositingQuality = SD.Drawing2D.CompositingQuality.HighQuality;
                g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(bmp, new SD.Rectangle(0, 0, targetW, targetH));
            }
            return CaptureBgraSnapshot(scaled);
        }
    }

    private static void LogOcrBreakdown(
        BgraSnapshot snapshot,
        long snapshotMs,
        long softwareBitmapMs,
        long recognizeMs,
        long barcodeMs,
        long deepBarcodeMs,
        long totalMs)
    {
        if (totalMs <= 50)
            return;

        Log.Info(
            "Perf ocr breakdown: " +
            $"snapshot={snapshotMs} software={softwareBitmapMs} " +
            $"recognize={recognizeMs} barcode={barcodeMs} " +
            $"deepBarcode={deepBarcodeMs} " +
            $"total={totalMs} ms size={snapshot.Width}x{snapshot.Height}");
    }

    /// <summary>Decodes QR / DataMatrix / Aztec symbols. Never throws — returns
    /// an empty list when nothing is found or decoding fails.</summary>
    private static bool ShouldPreferBarcodeResult(BgraSnapshot snapshot)
    {
        int max = Math.Max(snapshot.Width, snapshot.Height);
        int min = Math.Min(snapshot.Width, snapshot.Height);
        double ratio = min / (double)max;
        return max <= 640 && ratio >= 0.75;
    }

    private static async Task<(List<string> Results, long ElapsedMs, long DeepElapsedMs)> DecodeBarcodesWithFallbackAsync(
        BgraSnapshot snapshot,
        Task<(List<string> Results, long ElapsedMs)> fastBarcodeTask)
    {
        var fast = await fastBarcodeTask;
        if (fast.Results.Count > 0)
            return (fast.Results, fast.ElapsedMs, 0);

        var deep = await DecodeBarcodesDeepAsync(snapshot);
        return (deep.Results, fast.ElapsedMs + deep.ElapsedMs, deep.ElapsedMs);
    }

    private static Task<(List<string> Results, long ElapsedMs)> DecodeBarcodesDeepAsync(BgraSnapshot snapshot) =>
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            return (DecodeBarcodesDeep(snapshot), sw.ElapsedMilliseconds);
        });

    /// <summary>Runs the slower deep code scan. Never throws.</summary>
    private static List<string> DecodeBarcodesDeep(BgraSnapshot snapshot)
    {
        try
        {
            const long DeepScanPixelLimit = 1_500_000;
            return (long)snapshot.Width * snapshot.Height <= DeepScanPixelLimit
                ? DecodeBarcodes(snapshot, tryHarder: true)
                : new List<string>();
        }
        catch (Exception ex)
        {
            Log.Error("Barcode decode failed", ex);
            return new List<string>();
        }
    }

    /// <summary>Decodes a likely single code quickly. Never throws.</summary>
    private static List<string> DecodeBarcodesFast(BgraSnapshot snapshot)
    {
        try
        {
            return DecodeSingleBarcode(snapshot, tryHarder: false);
        }
        catch (Exception ex)
        {
            Log.Error("Barcode decode failed", ex);
            return new List<string>();
        }
    }

    private static List<string> DecodeBarcodes(BgraSnapshot snapshot, bool tryHarder)
    {
        var luminance = new RGBLuminanceSource(
            snapshot.Pixels,
            snapshot.Width,
            snapshot.Height,
            RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new BarcodeReaderGeneric();
        reader.Options.TryHarder = tryHarder;
        reader.Options.PossibleFormats = BarcodeFormats;

        Result[]? results = reader.DecodeMultiple(luminance);
        return results is null
            ? new List<string>()
            : results.Select(r => r.Text)
                     .Where(t => !string.IsNullOrEmpty(t))
                     .Distinct()
                     .ToList();
    }

    private static List<string> DecodeSingleBarcode(BgraSnapshot snapshot, bool tryHarder)
    {
        if (!tryHarder)
        {
            List<string> qr = DecodeQrCode(snapshot);
            if (qr.Count > 0)
                return qr;
        }

        var luminance = new RGBLuminanceSource(
            snapshot.Pixels,
            snapshot.Width,
            snapshot.Height,
            RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new BarcodeReaderGeneric();
        reader.Options.TryHarder = tryHarder;
        reader.Options.PossibleFormats = BarcodeFormats;

        Result? result = reader.Decode(luminance);
        return result is null || string.IsNullOrEmpty(result.Text)
            ? new List<string>()
            : new List<string> { result.Text };
    }

    private static List<string> DecodeQrCode(BgraSnapshot snapshot)
    {
        try
        {
            var luminance = new RGBLuminanceSource(
                snapshot.Pixels,
                snapshot.Width,
                snapshot.Height,
                RGBLuminanceSource.BitmapFormat.BGRA32);
            var binary = new BinaryBitmap(new HybridBinarizer(luminance));
            Result result = new QRCodeReader().decode(binary);
            return string.IsNullOrEmpty(result.Text)
                ? new List<string>()
                : new List<string> { result.Text };
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Whether an OCR engine can be created at all. Callers that tolerate a missing language
    /// pack (word boxes come back empty) need this to tell "nothing to read here" apart from
    /// "this machine cannot read anything".
    /// </summary>
    public static bool HasLanguagePack() => GetEngine() is not null;

    private static OcrEngine? GetEngine()
    {
        if (_engineInitialized && string.Equals(_engineLanguage, _preferredLanguage, StringComparison.OrdinalIgnoreCase))
            return _engine;

        lock (EngineGate)
        {
            string wanted = _preferredLanguage;
            if (_engineInitialized && string.Equals(_engineLanguage, wanted, StringComparison.OrdinalIgnoreCase))
                return _engine;

            // An explicit choice is a preference, not a demand: a user who picks a language
            // and then uninstalls its pack must still get OCR rather than "no engine".
            OcrEngine? engine = null;
            if (!string.Equals(wanted, AutoLanguage, StringComparison.OrdinalIgnoreCase))
            {
                try { engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(wanted)); }
                catch (Exception ex) { Log.Error($"OCR language '{wanted}' is not available; falling back", ex); }
            }

            _engine = engine
                ?? OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            _engineLanguage = wanted;
            _engineInitialized = true; // last: the fast path gates on it, so _engine and
                                       // _engineLanguage must both be published first

            return _engine;
        }
    }

    private static BgraSnapshot CaptureBgraSnapshot(SD.Bitmap bmp)
    {
        lock (bmp)
        {
            if (bmp.PixelFormat is not (
                SDI.PixelFormat.Format32bppArgb or
                SDI.PixelFormat.Format32bppPArgb or
                SDI.PixelFormat.Format32bppRgb))
            {
                using var converted = new SD.Bitmap(bmp.Width, bmp.Height, SDI.PixelFormat.Format32bppArgb);
                using (var g = SD.Graphics.FromImage(converted))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    g.DrawImageUnscaled(bmp, 0, 0);
                }
                return CaptureBgraSnapshot(converted);
            }

            var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
            SDI.BitmapData data = bmp.LockBits(rect, SDI.ImageLockMode.ReadOnly, bmp.PixelFormat);
            try
            {
                int rowBytes = bmp.Width * 4;
                int stride = Math.Abs(data.Stride);
                var pixels = new byte[rowBytes * bmp.Height];
                for (int y = 0; y < bmp.Height; y++)
                {
                    int sourceY = data.Stride >= 0 ? y : bmp.Height - 1 - y;
                    Marshal.Copy(
                        IntPtr.Add(data.Scan0, sourceY * stride),
                        pixels,
                        y * rowBytes,
                        rowBytes);
                }

                MakeOpaque(pixels, bmp.PixelFormat switch
                {
                    SDI.PixelFormat.Format32bppRgb => AlphaKind.Ignored,
                    SDI.PixelFormat.Format32bppPArgb => AlphaKind.Premultiplied,
                    _ => AlphaKind.Straight,
                });
                return new BgraSnapshot(pixels, bmp.Width, bmp.Height);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
    }

    private static SoftwareBitmap ToSoftwareBitmap(BgraSnapshot snapshot)
    {
        var softwareBitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            snapshot.Width,
            snapshot.Height,
            BitmapAlphaMode.Premultiplied);
        softwareBitmap.CopyFromBuffer(snapshot.Pixels.AsBuffer());
        return softwareBitmap;
    }

    private sealed record BgraSnapshot(byte[] Pixels, int Width, int Height);

    internal enum AlphaKind { Ignored, Straight, Premultiplied }

    /// <summary>
    /// Every pixel opaque, see-through ones laid over white. The engine is handed the bytes as
    /// PREMULTIPLIED Bgra8, so a 32bppRgb capture (whose fourth byte is undefined, and 0 after
    /// a BitBlt) read as fully transparent and came back with no text. That only happened on
    /// the passes that skip the resample (a big selection), which is why OCR worked on some
    /// selections and not others (Isaiah 2026-11-03: "it needs to be 100%").
    /// </summary>
    internal static void MakeOpaque(byte[] bgra, AlphaKind kind)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (kind == AlphaKind.Ignored || a == 255) { bgra[i + 3] = 255; continue; }
            int white = 255 - a;
            for (int c = 0; c < 3; c++)
            {
                int v = kind == AlphaKind.Premultiplied
                    ? bgra[i + c] + white
                    : (bgra[i + c] * a + 255 * white + 127) / 255;
                bgra[i + c] = (byte)Math.Min(255, v);
            }
            bgra[i + 3] = 255;
        }
    }

    /// <summary>
    /// The average colour around the image's edge, which is its background in nearly every
    /// selection of screen text.
    /// </summary>
    internal static (byte B, byte G, byte R) EdgeColour(byte[] bgra, int width, int height)
    {
        long b = 0, g = 0, r = 0, n = 0;
        void Add(int x, int y)
        {
            int i = (y * width + x) * 4;
            b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; n++;
        }
        for (int x = 0; x < width; x++) { Add(x, 0); Add(x, height - 1); }
        for (int y = 1; y < height - 1; y++) { Add(0, y); Add(width - 1, y); }
        if (n == 0) return (255, 255, 255);
        return ((byte)(b / n), (byte)(g / n), (byte)(r / n));
    }

    /// <summary>
    /// The image with a margin of its own background colour on every side. Windows OCR
    /// regularly drops a word that touches the edge of a tight selection; a margin gives
    /// every glyph room on all sides.
    /// </summary>
    internal static byte[] Pad(byte[] bgra, int width, int height, int pad, out int newWidth, out int newHeight)
    {
        var (eb, eg, er) = EdgeColour(bgra, width, height);
        newWidth = width + pad * 2;
        newHeight = height + pad * 2;
        var output = new byte[newWidth * newHeight * 4];
        for (int i = 0; i < output.Length; i += 4)
        {
            output[i] = eb; output[i + 1] = eg; output[i + 2] = er; output[i + 3] = 255;
        }
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(bgra, y * width * 4, output, ((y + pad) * newWidth + pad) * 4, width * 4);
        return output;
    }

    /// <summary>
    /// A second look for an image the plain pass read nothing from: grey, stretched to full
    /// contrast, and turned to dark text on a light ground when the background is dark
    /// (light text on a dark theme is the case the engine misses most).
    /// </summary>
    internal static byte[] Enhance(byte[] bgra, int width, int height)
    {
        var (eb, eg, er) = EdgeColour(bgra, width, height);
        bool darkGround = (er * 299 + eg * 587 + eb * 114) / 1000 < 128;
        var grey = new byte[width * height];
        int lo = 255, hi = 0;
        for (int p = 0, i = 0; p < grey.Length; p++, i += 4)
        {
            int v = (bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000;
            if (darkGround) v = 255 - v;
            grey[p] = (byte)v;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        int span = Math.Max(1, hi - lo);
        var output = new byte[bgra.Length];
        for (int p = 0, i = 0; p < grey.Length; p++, i += 4)
        {
            byte v = (byte)Math.Clamp((grey[p] - lo) * 255 / span, 0, 255);
            output[i] = v; output[i + 1] = v; output[i + 2] = v; output[i + 3] = 255;
        }
        return output;
    }

    /// <summary>The margin, in OCR pixels, put around a pass: about one tall glyph.</summary>
    internal static int OcrPad(int width, int height, double scale)
    {
        int pad = (int)Math.Round(Math.Clamp(0.04 * Math.Max(width, height) * scale, 16, 48));
        // Never push the padded image past the engine's dimension cap.
        int room = (int)((OcrEngine.MaxImageDimension - Math.Max(width, height) * scale) / 2);
        return Math.Max(0, Math.Min(pad, room));
    }

    private static BgraSnapshot Padded(BgraSnapshot s, int pad, bool enhance)
    {
        byte[] pixels = enhance ? Enhance(s.Pixels, s.Width, s.Height) : s.Pixels;
        if (pad <= 0) return enhance ? new BgraSnapshot(pixels, s.Width, s.Height) : s;
        byte[] padded = Pad(pixels, s.Width, s.Height, pad, out int w, out int h);
        return new BgraSnapshot(padded, w, h);
    }

    private static async Task<List<string>> RecognizeLinesAsync(OcrEngine engine, BgraSnapshot snapshot)
    {
        using SoftwareBitmap softwareBitmap = ToSoftwareBitmap(snapshot);
        OcrResult result = await engine.RecognizeAsync(softwareBitmap);
        return result.Lines.Select(l => l.Text).ToList();
    }
}

/// <summary>
/// The one OCR failure whose message is written for the user rather than the log, so the
/// one the caller is allowed to put on screen verbatim. It is a type and not a message on
/// <see cref="InvalidOperationException"/> because catching the base type caught every
/// framework InvalidOperationException the SoftwareBitmap and GDI+ work inside
/// <see cref="OcrService.ExtractAsync"/> can raise, and quoted those to the user too.
/// </summary>
public sealed class OcrUnavailableException(string message) : Exception(message);
