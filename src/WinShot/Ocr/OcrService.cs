using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace WinShot.Ocr;

/// <summary>
/// Extracts text from a bitmap using the built-in Windows OCR engine
/// (Windows.Media.Ocr). Requires an installed OCR-capable language pack.
/// </summary>
public static class OcrService
{
    public static async Task<string> ExtractTextAsync(SD.Bitmap bmp)
    {
        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine is null)
            throw new InvalidOperationException(
                "No OCR language pack is installed. Add a language under Windows Settings > " +
                "Time & Language > Language & Region, then try again.");

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
            return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
        }
        finally
        {
            scaled?.Dispose();
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
