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
