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

/// <summary>Combined result of one recognition pass: OCR text plus any decoded
/// QR/DataMatrix/Aztec payloads found in the image.</summary>
public sealed record OcrCaptureResult(string Text, List<string> QrCodes);

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
    private static bool _engineInitialized;

    /// <summary>
    /// Runs barcode detection and OCR over <paramref name="bmp"/>.
    /// <paramref name="joinLines"/>=true joins OCR lines with spaces into a
    /// paragraph; false keeps one line per OCR line.
    /// Throws <see cref="InvalidOperationException"/> when no OCR language pack
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
        // oversized captures (e.g. a 4K fullscreen) are scaled down to fit.
        SD.Bitmap? scaled = null;
        try
        {
            SD.Bitmap source = bmp;
            int maxDim = (int)OcrEngine.MaxImageDimension;
            if (bmp.Width > maxDim || bmp.Height > maxDim)
            {
                double factor = Math.Min((double)maxDim / bmp.Width, (double)maxDim / bmp.Height);
                int targetW = Math.Max(1, (int)(bmp.Width * factor));
                int targetH = Math.Max(1, (int)(bmp.Height * factor));
                // High-quality bicubic downscale so dense/small text survives the
                // resize; the default Bitmap(bmp, w, h) ctor uses low-quality
                // interpolation that smears glyph edges and hurts OCR accuracy on 4K.
                scaled = new SD.Bitmap(targetW, targetH, SDI.PixelFormat.Format32bppArgb);
                using (var g = SD.Graphics.FromImage(scaled))
                {
                    g.CompositingQuality = SD.Drawing2D.CompositingQuality.HighQuality;
                    g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SD.Drawing2D.SmoothingMode.HighQuality;
                    g.DrawImage(bmp, new SD.Rectangle(0, 0, targetW, targetH));
                }
                source = scaled;
            }

            var step = Stopwatch.StartNew();
            BgraSnapshot snapshot = await Task.Run(() => CaptureBgraSnapshot(source));
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
                throw new InvalidOperationException(
                    "No OCR language pack is installed. Add a language under Windows Settings > " +
                    "Time & Language > Language & Region, then try again.");
            }

            step.Restart();
            using SoftwareBitmap softwareBitmap = ToSoftwareBitmap(snapshot);
            softwareBitmapMs = step.ElapsedMilliseconds;
            step.Restart();
            OcrResult result = await engine.RecognizeAsync(softwareBitmap);
            recognizeMs = step.ElapsedMilliseconds;
            string text = OcrTextFormatter.Format(result.Lines.Select(l => l.Text), joinLines);

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
            return new OcrCaptureResult(text, qrCodes);
        }
        finally
        {
            scaled?.Dispose();
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

    private static OcrEngine? GetEngine()
    {
        if (_engineInitialized)
            return _engine;

        lock (EngineGate)
        {
            if (_engineInitialized)
                return _engine;

            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            _engineInitialized = true;
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
}
