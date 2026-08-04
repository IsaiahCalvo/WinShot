using System.Buffers.Binary;
using System.IO;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class NativeImageClipboardPayloadTests
{
    [Fact]
    public void CreatePayload_MaterializesBottomUpDibAndCompletePng()
    {
        byte[] dib;
        byte[] png;
        using (var source = CreatePattern())
        using (CaptureService.NativeImageClipboardPayload payload =
               CaptureService.CreateNativeImageClipboardPayloadForTest(source))
        {
            Assert.True(payload.HasPng);
            Assert.Equal(40 + 2 * 2 * 4, payload.DibLength);
            Assert.True(payload.PngLength > 24);
            dib = payload.CopyDibBytesForTest();
            png = payload.CopyPngBytesForTest();
        }

        Assert.Equal(40, ReadInt32LittleEndian(dib, 0));
        Assert.Equal(2, ReadInt32LittleEndian(dib, 4));
        Assert.Equal(2, ReadInt32LittleEndian(dib, 8)); // positive means bottom-up CF_DIB
        Assert.Equal(1, ReadInt16LittleEndian(dib, 12));
        Assert.Equal(32, ReadInt16LittleEndian(dib, 14));
        Assert.Equal(0, ReadInt32LittleEndian(dib, 16)); // BI_RGB
        Assert.Equal(16, ReadInt32LittleEndian(dib, 20));

        // DIB pixels are BGRA, bottom row first.
        Assert.Equal(new byte[] { 90, 80, 70, 255 }, dib[40..44]);
        Assert.Equal(new byte[] { 120, 110, 100, 255 }, dib[44..48]);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, dib[48..52]);
        Assert.Equal(new byte[] { 60, 50, 40, 255 }, dib[52..56]);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));

        using var stream = new MemoryStream(png, writable: false);
        using var decoded = new SD.Bitmap(stream);
        Assert.Equal(SD.Color.FromArgb(255, 10, 20, 30).ToArgb(), decoded.GetPixel(0, 0).ToArgb());
        Assert.Equal(SD.Color.FromArgb(255, 100, 110, 120).ToArgb(), decoded.GetPixel(1, 1).ToArgb());
    }

    [Fact]
    public void CreatePayload_WithoutPng_ContainsOnlyImmediateDib()
    {
        using var source = CreatePattern();
        using CaptureService.NativeImageClipboardPayload payload =
            CaptureService.CreateNativeImageClipboardPayloadForTest(source, includePng: false);

        Assert.False(payload.HasPng);
        Assert.Equal(0, payload.PngLength);
        Assert.Empty(payload.CopyPngBytesForTest());
        Assert.Equal(56, payload.CopyDibBytesForTest().Length);
    }

    [Fact]
    public void CreatePayload_ConvertsNon32BitSourceWithoutDelayedSourceAccess()
    {
        byte[] dib;
        byte[] png;
        var source = new SD.Bitmap(3, 2, SD.Imaging.PixelFormat.Format24bppRgb);
        using (var graphics = SD.Graphics.FromImage(source))
            graphics.Clear(SD.Color.Orchid);

        using (CaptureService.NativeImageClipboardPayload payload =
               CaptureService.CreateNativeImageClipboardPayloadForTest(source))
        {
            source.Dispose();
            dib = payload.CopyDibBytesForTest();
            png = payload.CopyPngBytesForTest();
        }

        Assert.Equal(40 + 3 * 2 * 4, dib.Length);
        Assert.Equal(3, ReadInt32LittleEndian(dib, 4));
        Assert.Equal(2, ReadInt32LittleEndian(dib, 8));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    private static SD.Bitmap CreatePattern()
    {
        var bitmap = new SD.Bitmap(2, 2, SD.Imaging.PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, SD.Color.FromArgb(255, 10, 20, 30));
        bitmap.SetPixel(1, 0, SD.Color.FromArgb(255, 40, 50, 60));
        bitmap.SetPixel(0, 1, SD.Color.FromArgb(255, 70, 80, 90));
        bitmap.SetPixel(1, 1, SD.Color.FromArgb(255, 100, 110, 120));
        return bitmap;
    }

    private static int ReadInt32LittleEndian(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static short ReadInt16LittleEndian(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2));
}
