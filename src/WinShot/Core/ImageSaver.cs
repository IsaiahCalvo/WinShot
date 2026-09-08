using System.Drawing.Imaging;
using System.IO;
using SkiaSharp;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// Routes image saves by extension: .png/.jpg via GDI+, .webp via SkiaSharp.
/// Anything unrecognized is written as PNG.
/// </summary>
public static class ImageSaver
{
    private const int JpegQuality = 90;
    private const int WebpQuality = 90;

    public static void Save(SD.Bitmap bmp, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
                SaveJpeg(bmp, path);
                break;
            case ".webp":
                SaveWebp(bmp, path);
                break;
            default:
                bmp.Save(path, ImageFormat.Png);
                break;
        }
    }

    private static void SaveJpeg(SD.Bitmap source, string path)
    {
        // JPEG has no alpha channel and the GDI+ encoder does not composite: it writes the
        // RGB under a transparent pixel, which for anything GDI+ cleared is black. A window
        // capture with its rounded corners trimmed would come out with four black wedges.
        using SD.Bitmap? flattened = AlphaFlatten.OverWhite(source);
        SD.Bitmap bmp = flattened ?? source;

        ImageCodecInfo? codec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (codec is null)
        {
            bmp.Save(path, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)JpegQuality);
        bmp.Save(path, codec, parameters);
    }

    private static void SaveWebp(SD.Bitmap bmp, string path)
    {
        var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
        // LockBits converts to BGRA on read regardless of the bitmap's native format.
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var image = SKImage.FromPixels(info, data.Scan0, data.Stride);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
            using var stream = File.Create(path);
            encoded.SaveTo(stream);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
