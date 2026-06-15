using System.Drawing.Drawing2D;
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

    /// <summary>Returns a half-size copy using high-quality bicubic resampling (HiDPI downscale).</summary>
    public static SD.Bitmap HalfSize(SD.Bitmap source)
    {
        int width = Math.Max(1, source.Width / 2);
        int height = Math.Max(1, source.Height / 2);

        var result = new SD.Bitmap(width, height, PixelFormat.Format32bppArgb);
        result.SetResolution(source.HorizontalResolution, source.VerticalResolution);

        using var g = SD.Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(
            source,
            new SD.Rectangle(0, 0, width, height),
            new SD.Rectangle(0, 0, source.Width, source.Height),
            SD.GraphicsUnit.Pixel);

        return result;
    }

    private static void SaveJpeg(SD.Bitmap bmp, string path)
    {
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
