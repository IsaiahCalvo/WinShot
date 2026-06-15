using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2 = System.Drawing.Drawing2D;
using SD = System.Drawing;

namespace WinShot.Editor;

public static class BitmapEffects
{
    /// <summary>Pixelates a region of the bitmap in place using roughly cellSize-pixel blocks.</summary>
    public static void Pixelate(SD.Bitmap bmp, SD.Rectangle region, int cellSize = 12)
    {
        region.Intersect(new SD.Rectangle(0, 0, bmp.Width, bmp.Height));
        if (region.Width < 2 || region.Height < 2) return;

        int sw = Math.Max(1, (int)Math.Round(region.Width / (double)cellSize));
        int sh = Math.Max(1, (int)Math.Round(region.Height / (double)cellSize));

        using var small = new SD.Bitmap(sw, sh, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var gs = SD.Graphics.FromImage(small))
        {
            gs.InterpolationMode = D2.InterpolationMode.HighQualityBilinear;
            gs.PixelOffsetMode = D2.PixelOffsetMode.Half;
            gs.DrawImage(bmp, new SD.Rectangle(0, 0, sw, sh), region, SD.GraphicsUnit.Pixel);
        }

        using var g = SD.Graphics.FromImage(bmp);
        g.InterpolationMode = D2.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = D2.PixelOffsetMode.Half;
        g.CompositingMode = D2.CompositingMode.SourceCopy;
        g.DrawImage(small, region, new SD.Rectangle(0, 0, sw, sh), SD.GraphicsUnit.Pixel);
    }

    /// <summary>
    /// Like <see cref="Pixelate"/>, but every mosaic cell additionally gets random
    /// brightness/channel jitter so the original text cannot be reconstructed by
    /// averaging attacks. The jitter sequence is derived from <paramref name="seed"/>,
    /// which makes one apply deterministic so undo/redo replays the exact same pixels;
    /// callers draw the seed from a per-application random source.
    /// </summary>
    public static void PixelateRandomized(SD.Bitmap bmp, SD.Rectangle region, int seed, int cellSize = 12)
    {
        region.Intersect(new SD.Rectangle(0, 0, bmp.Width, bmp.Height));
        if (region.Width < 2 || region.Height < 2) return;

        int sw = Math.Max(1, (int)Math.Round(region.Width / (double)cellSize));
        int sh = Math.Max(1, (int)Math.Round(region.Height / (double)cellSize));

        using var small = new SD.Bitmap(sw, sh, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var gs = SD.Graphics.FromImage(small))
        {
            gs.InterpolationMode = D2.InterpolationMode.HighQualityBilinear;
            gs.PixelOffsetMode = D2.PixelOffsetMode.Half;
            gs.DrawImage(bmp, new SD.Rectangle(0, 0, sw, sh), region, SD.GraphicsUnit.Pixel);
        }

        var rng = new Random(seed);
        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                var c = small.GetPixel(x, y);
                int shift = rng.Next(-26, 27); // shared luminance jitter per cell
                int r = Math.Clamp(c.R + shift + rng.Next(-12, 13), 0, 255);
                int g = Math.Clamp(c.G + shift + rng.Next(-12, 13), 0, 255);
                int b = Math.Clamp(c.B + shift + rng.Next(-12, 13), 0, 255);
                small.SetPixel(x, y, SD.Color.FromArgb(c.A, r, g, b));
            }
        }

        using var gd = SD.Graphics.FromImage(bmp);
        gd.InterpolationMode = D2.InterpolationMode.NearestNeighbor;
        gd.PixelOffsetMode = D2.PixelOffsetMode.Half;
        gd.CompositingMode = D2.CompositingMode.SourceCopy;
        gd.DrawImage(small, region, new SD.Rectangle(0, 0, sw, sh), SD.GraphicsUnit.Pixel);
    }

    /// <summary>
    /// Blurs a region of the bitmap in place. Three passes of a clamped running-sum
    /// box blur approximate a Gaussian, giving a smooth blur (distinct from the
    /// hard-edged mosaic produced by <see cref="Pixelate"/>).
    /// </summary>
    public static void Blur(SD.Bitmap bmp, SD.Rectangle region, int radius = 6)
    {
        region.Intersect(new SD.Rectangle(0, 0, bmp.Width, bmp.Height));
        if (region.Width < 2 || region.Height < 2 || radius < 1) return;

        int w = region.Width, h = region.Height;
        var data = bmp.LockBits(region, SD.Imaging.ImageLockMode.ReadWrite, SD.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int bytes = stride * h;
            byte[] buffer = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, bytes);

            // Split into BGRA channel planes, blur each, recombine.
            var src = new byte[4][];
            var tmp = new byte[4][];
            for (int c = 0; c < 4; c++) { src[c] = new byte[w * h]; tmp[c] = new byte[w * h]; }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = y * stride + x * 4;
                    int i = y * w + x;
                    src[0][i] = buffer[p];
                    src[1][i] = buffer[p + 1];
                    src[2][i] = buffer[p + 2];
                    src[3][i] = buffer[p + 3];
                }

            for (int pass = 0; pass < 3; pass++)
                for (int c = 0; c < 4; c++)
                {
                    BoxBlurH(src[c], tmp[c], w, h, radius);
                    BoxBlurV(tmp[c], src[c], w, h, radius);
                }

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = y * stride + x * 4;
                    int i = y * w + x;
                    buffer[p] = src[0][i];
                    buffer[p + 1] = src[1][i];
                    buffer[p + 2] = src[2][i];
                    buffer[p + 3] = src[3][i];
                }
            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, bytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static void BoxBlurH(byte[] src, byte[] dst, int w, int h, int r)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int right = Math.Min(r, w - 1);
            int sum = 0;
            for (int k = 0; k <= right; k++) sum += src[row + k];
            int count = right + 1;
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = (byte)(sum / count);
                int addIdx = x + 1 + r;
                int remIdx = x - r;
                if (addIdx < w) { sum += src[row + addIdx]; count++; }
                if (remIdx >= 0) { sum -= src[row + remIdx]; count--; }
            }
        }
    }

    private static void BoxBlurV(byte[] src, byte[] dst, int w, int h, int r)
    {
        for (int x = 0; x < w; x++)
        {
            int bottom = Math.Min(r, h - 1);
            int sum = 0;
            for (int k = 0; k <= bottom; k++) sum += src[k * w + x];
            int count = bottom + 1;
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = (byte)(sum / count);
                int addIdx = y + 1 + r;
                int remIdx = y - r;
                if (addIdx < h) { sum += src[addIdx * w + x]; count++; }
                if (remIdx >= 0) { sum -= src[remIdx * w + x]; count--; }
            }
        }
    }

    /// <summary>Returns a new bitmap resampled to the given pixel size (high-quality bicubic).</summary>
    public static SD.Bitmap Resize(SD.Bitmap source, int width, int height)
    {
        var dst = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(dst);
        g.InterpolationMode = D2.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = D2.PixelOffsetMode.Half;
        g.SmoothingMode = D2.SmoothingMode.HighQuality;
        g.CompositingMode = D2.CompositingMode.SourceCopy;
        g.DrawImage(source,
            new SD.Rectangle(0, 0, width, height),
            new SD.Rectangle(0, 0, source.Width, source.Height),
            SD.GraphicsUnit.Pixel);
        return dst;
    }

    /// <summary>Copies a previously cloned region back into the bitmap (blur undo).</summary>
    public static void RestoreRegion(SD.Bitmap bmp, SD.Bitmap backup, SD.Rectangle region)
    {
        using var g = SD.Graphics.FromImage(bmp);
        g.CompositingMode = D2.CompositingMode.SourceCopy;
        g.DrawImage(backup, region, new SD.Rectangle(0, 0, backup.Width, backup.Height), SD.GraphicsUnit.Pixel);
    }

    /// <summary>
    /// Renders a visual at an explicit pixel size and converts the result to a
    /// GDI bitmap. A VisualBrush is used so the visual's layout offset and any
    /// ancestor transforms (e.g. the editor's zoom/pan view transform) do not
    /// leak into the output, and rendering at the bitmap's pixel size keeps the
    /// export resolution identical to the source regardless of monitor DPI.
    /// </summary>
    public static SD.Bitmap RenderVisual(Visual visual, int pixelWidth, int pixelHeight)
    {
        var brush = new VisualBrush(visual)
        {
            // Absolute viewbox pins the mapping 1:1 even if the visual's
            // content bounds ever differ from its layout size.
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, pixelWidth, pixelHeight),
        };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(brush, null, new Rect(0, 0, pixelWidth, pixelHeight));

        var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        // Bitmap(stream) keeps the stream pinned; copy once more to detach.
        using var decoded = new SD.Bitmap(ms);
        return new SD.Bitmap(decoded);
    }
}
