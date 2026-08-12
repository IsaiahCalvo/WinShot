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
}
