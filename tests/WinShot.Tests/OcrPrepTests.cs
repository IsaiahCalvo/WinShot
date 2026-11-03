using WinShot.Ocr;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// What OCR is handed (Isaiah 2026-11-03: text detection "needs to be 100%"): every pixel
/// opaque, a margin of background round the selection, and a high-contrast second look.
/// </summary>
public class OcrPrepTests
{
    [Fact]
    public void ARgbCaptureWithZeroAlphaBytesBecomesOpaque()
    {
        // A BitBlt into 32bppRgb leaves the fourth byte 0; read as premultiplied that was
        // a fully transparent image with nothing to read.
        var px = new byte[] { 10, 20, 30, 0, 200, 210, 220, 0 };
        OcrService.MakeOpaque(px, OcrService.AlphaKind.Ignored);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 200, 210, 220, 255 }, px);
    }

    [Fact]
    public void SeeThroughPixelsAreLaidOverWhite()
    {
        var straight = new byte[] { 0, 0, 0, 0, 0, 0, 0, 128 };
        OcrService.MakeOpaque(straight, OcrService.AlphaKind.Straight);
        Assert.Equal(255, straight[0]);
        Assert.InRange(straight[4], 126, 128);
        Assert.Equal(255, straight[7]);

        var pre = new byte[] { 0, 0, 0, 128 };
        OcrService.MakeOpaque(pre, OcrService.AlphaKind.Premultiplied);
        Assert.Equal(127, pre[0]);
        Assert.Equal(255, pre[3]);
    }

    [Fact]
    public void PaddingUsesTheBackgroundColourAndKeepsThePicture()
    {
        // 2x2 dark ground with one white pixel.
        var px = new byte[] { 20, 20, 20, 255, 20, 20, 20, 255, 20, 20, 20, 255, 250, 250, 250, 255 };
        byte[] padded = OcrService.Pad(px, 2, 2, 3, out int w, out int h);
        Assert.Equal(8, w);
        Assert.Equal(8, h);
        Assert.InRange(padded[0], 76, 78); // edge average (3 x 20 + 250) / 4
        int inner = ((3 + 1) * w + 3 + 1) * 4;
        Assert.Equal(250, padded[inner]);
    }

    [Fact]
    public void EnhanceTurnsLightTextOnDarkIntoDarkOnLight()
    {
        // 3x3 dark ground, one light pixel in the middle.
        var px = new byte[9 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 30; px[i + 3] = 255; }
        px[16] = px[17] = px[18] = 230;
        byte[] e = OcrService.Enhance(px, 3, 3);
        Assert.Equal(255, e[0]);   // ground is now white
        Assert.Equal(0, e[16]);    // text is now black
    }

    [Fact]
    public void ThePadNeverPushesPastTheEngineCap()
    {
        Assert.InRange(OcrService.OcrPad(400, 100, 4), 16, 48);
        Assert.Equal(0, OcrService.OcrPad(10000, 100, 1));
    }
}
