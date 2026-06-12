using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WinShot.Core;

public static class CaptureService
{
    /// <summary>Bounds of all monitors combined, in physical screen pixels.</summary>
    public static Rectangle VirtualScreen => System.Windows.Forms.SystemInformation.VirtualScreen;

    public static Bitmap CaptureVirtualDesktop()
    {
        var vs = VirtualScreen;
        var bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(vs.X, vs.Y, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>Captures a rectangle given in physical screen coordinates.</summary>
    public static Bitmap CaptureScreenRegion(Rectangle screenRect)
    {
        var bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(screenRect.X, screenRect.Y, 0, 0, screenRect.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static Bitmap Crop(Bitmap source, Rectangle region)
    {
        region.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        if (region.Width < 1 || region.Height < 1)
            throw new ArgumentException("Crop region is empty.", nameof(region));

        var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(source, new Rectangle(0, 0, region.Width, region.Height), region, GraphicsUnit.Pixel);
        return bmp;
    }

    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        IntPtr hBitmap = bmp.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    /// <summary>Puts the image on the clipboard as both a bitmap and a PNG stream (for apps that prefer PNG).</summary>
    public static void CopyToClipboard(Bitmap bmp)
    {
        var data = new DataObject();
        data.SetImage(ToBitmapSource(bmp));
        using var pngStream = new MemoryStream();
        bmp.Save(pngStream, ImageFormat.Png);
        pngStream.Position = 0;
        data.SetData("PNG", pngStream, autoConvert: false);

        // The clipboard can be transiently locked by another process; retry briefly.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (attempt < 3)
            {
                Thread.Sleep(100);
            }
        }
    }

    public static void Save(Bitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
            bmp.Save(path, ImageFormat.Jpeg);
        else
            bmp.Save(path, ImageFormat.Png);
    }

    public static string DefaultFileName(string extension) =>
        $"WinShot {DateTime.Now:yyyy-MM-dd 'at' HH.mm.ss}.{extension}";

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
