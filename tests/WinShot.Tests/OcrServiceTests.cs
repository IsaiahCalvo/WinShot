using WinShot.Ocr;
using Xunit;
using ZXing;
using ZXing.QrCode;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace WinShot.Tests;

public class OcrServiceTests
{
    [Fact]
    public async Task ExtractAsync_ReturnsQrCodePayload()
    {
        const string payload = "winshot-qr-smoke";

        using var bitmap = CreateQrBitmap(payload, SDI.PixelFormat.Format32bppArgb);
        var result = await OcrService.ExtractAsync(bitmap, joinLines: false);

        Assert.Contains(payload, result.QrCodes);
    }

    [Fact]
    public async Task ExtractAsync_ReturnsQrCodePayloadFromScreenshotPixelFormat()
    {
        const string payload = "winshot-qr-screenshot-format";

        using var bitmap = CreateQrBitmap(payload, SDI.PixelFormat.Format32bppRgb);
        var result = await OcrService.ExtractAsync(bitmap, joinLines: false);

        Assert.Contains(payload, result.QrCodes);
    }

    [Fact]
    public async Task ExtractAsync_ReadsSmallText()
    {
        // Small UI text: ~7pt Segoe UI in a narrow crop, well below the
        // ~20px glyph height Windows OCR needs at native scale.
        using var bmp = new SD.Bitmap(220, 26, SDI.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.Clear(SD.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new SD.Font("Segoe UI", 7f);
            g.DrawString("The quick brown fox 12345", font, SD.Brushes.Black, 2, 5);
        }

        var result = await OcrService.ExtractAsync(bmp, joinLines: true);

        Assert.Contains("quick brown fox", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static SD.Bitmap CreateQrBitmap(string text, SDI.PixelFormat pixelFormat)
    {
        const int size = 180;
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size);
        var bitmap = new SD.Bitmap(size, size, pixelFormat);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bitmap.SetPixel(x, y, matrix[x, y] ? SD.Color.Black : SD.Color.White);
            }
        }

        return bitmap;
    }
    [Fact]
    public void NoTextInAPictureSizedCropIsAnsweredWithoutEscalating()
    {
        // The 218x224 crop from the log: three escalating passes, 6.4 seconds, to say
        // "no text". The first pass upscales it to ~896px, which is plenty to see any
        // glyph that is there.
        double scale = OcrService.InitialOcrScale(218, 224);

        Assert.Equal(4.0, scale, 2);
        Assert.False(OcrService.ShouldRetryLarger(218, 224, scale, out _));
    }

    [Fact]
    public void TinyCropsStillGetOneLargerPass()
    {
        // 4x of a 100px crop is 400px, small enough that glyphs may sit under the
        // engine's floor — this is the case the retry exists for.
        double scale = OcrService.InitialOcrScale(100, 40);

        Assert.Equal(4.0, scale, 2);
        Assert.True(OcrService.ShouldRetryLarger(100, 40, scale, out double next));
        Assert.True(next > scale, "the retry has to actually be bigger");
        Assert.False(OcrService.ShouldRetryLarger(100, 40, next, out _));
    }

    [Fact]
    public void LargeCapturesNeverRetry()
    {
        // Shrunk to fit the engine, so a second pass could only be smaller.
        double scale = OcrService.InitialOcrScale(3840, 2160);

        Assert.True(scale <= 1.0);
        Assert.False(OcrService.ShouldRetryLarger(3840, 2160, scale, out _));
    }

    [Fact]
    public async Task ExtractAsync_AnswersQuicklyWhenThereIsNoText()
    {
        using var blank = new SD.Bitmap(218, 224, SDI.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(blank))
        {
            g.Clear(SD.Color.FromArgb(32, 34, 40));
            g.FillEllipse(SD.Brushes.SteelBlue, 40, 40, 140, 140);
        }

        // Warm first: the measurement is about the recognition path, not engine startup.
        await OcrService.ExtractAsync(blank, joinLines: false);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await OcrService.ExtractAsync(blank, joinLines: false);
        sw.Stop();

        Assert.True(string.IsNullOrWhiteSpace(result.Text));
        Assert.Empty(result.QrCodes);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"no-text answer took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The engine reads a SCALED copy, so a caller that forgot to divide back down would
    /// redact the top-left corner of the text and leave the rest showing. Rounding is
    /// outward on purpose: a box that lands a pixel inside a glyph leaves a sliver of the
    /// letter visible under the mark.
    /// </summary>
    [Fact]
    public void ScaleWordBox_DividesBackDownAndRoundsOutward()
    {
        var image = new SD.Rectangle(0, 0, 400, 300);

        SD.Rectangle box = OcrService.ScaleWordBox(100, 50, 60, 20, scale: 2, image);
        Assert.Equal(new SD.Rectangle(50, 25, 30, 10), box);

        // 4.5 floors to 4, 4.5+7.5 = 12 ceils to 12 — the box grows, never shrinks.
        SD.Rectangle rounded = OcrService.ScaleWordBox(9, 9, 15, 15, scale: 2, image);
        Assert.Equal(4, rounded.X);
        Assert.Equal(4, rounded.Y);
        Assert.True(rounded.Right >= 12);
        Assert.True(rounded.Bottom >= 12);
    }

    [Fact]
    public void ScaleWordBox_ClipsToTheImageAndRejectsNonsenseScales()
    {
        var image = new SD.Rectangle(0, 0, 100, 100);

        SD.Rectangle clipped = OcrService.ScaleWordBox(90, 90, 60, 60, scale: 1, image);
        Assert.Equal(new SD.Rectangle(90, 90, 10, 10), clipped);

        Assert.True(OcrService.ScaleWordBox(0, 0, 10, 10, scale: 0, image).IsEmpty);
        Assert.True(OcrService.ScaleWordBox(0, 0, 10, 10, double.NaN, image).IsEmpty);
    }
}
