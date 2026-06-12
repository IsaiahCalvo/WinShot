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
