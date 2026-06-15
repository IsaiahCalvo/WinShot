using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinShot.Core;
using ZXing;
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
    /// <summary>
    /// Runs barcode detection and OCR over <paramref name="bmp"/>.
    /// <paramref name="joinLines"/>=true joins OCR lines with spaces into a
    /// paragraph; false keeps one line per OCR line.
    /// Throws <see cref="InvalidOperationException"/> when no OCR language pack
    /// is available and the image contains no barcodes either.
    /// </summary>
    public static async Task<OcrCaptureResult> ExtractAsync(SD.Bitmap bmp, bool joinLines)
    {
        List<string> qrCodes = await Task.Run(() => DecodeBarcodes(bmp));

        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine is null)
        {
            // No OCR available, but a barcode hit is still a useful result.
            if (qrCodes.Count > 0)
                return new OcrCaptureResult(string.Empty, qrCodes);
            throw new InvalidOperationException(
                "No OCR language pack is installed. Add a language under Windows Settings > " +
                "Time & Language > Language & Region, then try again.");
        }

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
                scaled = new SD.Bitmap(bmp,
                    Math.Max(1, (int)(bmp.Width * factor)),
                    Math.Max(1, (int)(bmp.Height * factor)));
                source = scaled;
            }

            using SoftwareBitmap softwareBitmap = await ToSoftwareBitmapAsync(source);
            OcrResult result = await engine.RecognizeAsync(softwareBitmap);
            IEnumerable<string> lines = result.Lines.Select(l => l.Text);
            string text = joinLines
                ? string.Join(" ", lines)
                : string.Join(Environment.NewLine, lines);
            return new OcrCaptureResult(text, qrCodes);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>Legacy text-only entry point; preserved for existing callers.</summary>
    public static async Task<string> ExtractTextAsync(SD.Bitmap bmp) =>
        (await ExtractAsync(bmp, joinLines: false)).Text;

    /// <summary>Decodes QR / DataMatrix / Aztec symbols. Never throws — returns
    /// an empty list when nothing is found or decoding fails.</summary>
    private static List<string> DecodeBarcodes(SD.Bitmap bmp)
    {
        try
        {
            var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
            SDI.BitmapData data = bmp.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppArgb);
            try
            {
                int width = data.Width;
                int height = data.Height;
                // Copy row by row into a tightly packed BGRA buffer (stride-safe).
                var pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * width * 4, width * 4);

                var luminance = new RGBLuminanceSource(pixels, width, height,
                    RGBLuminanceSource.BitmapFormat.BGRA32);
                var reader = new BarcodeReaderGeneric
                {
                    Options =
                    {
                        TryHarder = true,
                        PossibleFormats = new List<BarcodeFormat>
                        {
                            BarcodeFormat.QR_CODE,
                            BarcodeFormat.DATA_MATRIX,
                            BarcodeFormat.AZTEC,
                        },
                    },
                };

                Result[]? results = reader.DecodeMultiple(luminance);
                return results is null
                    ? new List<string>()
                    : results.Select(r => r.Text)
                             .Where(t => !string.IsNullOrEmpty(t))
                             .Distinct()
                             .ToList();
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Barcode decode failed", ex);
            return new List<string>();
        }
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(SD.Bitmap bmp)
    {
        using var stream = new InMemoryRandomAccessStream();
        // The adapter is intentionally not disposed: disposing it would close the
        // underlying WinRT stream before the decoder gets to read it.
        Stream adapter = stream.AsStreamForWrite();
        bmp.Save(adapter, SDI.ImageFormat.Png);
        adapter.Flush();
        stream.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
