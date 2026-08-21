using System.Runtime.InteropServices;
using SD = System.Drawing;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace WinShot.Core;

/// <summary>
/// Gives a drag operation the floating thumbnail Windows shows under the cursor, via the shell's
/// IDragSourceHelper. Drop targets that cooperate (Explorer, Outlook, Chrome, Teams) also draw the
/// "Copy to &lt;app&gt;" hint. Targets that don't simply show the plain cursor, so this is best-effort.
/// </summary>
internal static class DragImage
{
    /// <summary>Longest edge of the floating thumbnail, in logical pixels.</summary>
    private const int MaxEdge = 180;

    /// <summary>Opaque thumbnail of <paramref name="image"/>, sized for the drag cursor.</summary>
    public static SD.Bitmap CreateThumbnail(SD.Bitmap image, double dpiScale)
    {
        int max = Math.Max(1, (int)Math.Round(MaxEdge * (dpiScale > 0 ? dpiScale : 1.0)));
        double scale = Math.Min(1.0, (double)max / Math.Max(image.Width, image.Height));
        int w = Math.Max(1, (int)Math.Round(image.Width * scale));
        int h = Math.Max(1, (int)Math.Round(image.Height * scale));

        var thumb = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(thumb);
        g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
        // The shell drag image has no alpha channel, so flatten onto white first — otherwise
        // transparent captures drag as a black block.
        g.Clear(SD.Color.White);
        g.DrawImage(image, new SD.Rectangle(0, 0, w, h));
        return thumb;
    }

    /// <summary>
    /// Attaches <paramref name="thumbnail"/> to <paramref name="dataObject"/> as the drag image.
    /// Must be called before DoDragDrop. Failures are logged and ignored — the drag still works.
    /// </summary>
    public static void Attach(object dataObject, SD.Bitmap thumbnail)
    {
        if (dataObject is not ComTypes.IDataObject comData) return;

        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            hbitmap = thumbnail.GetHbitmap();
            var info = new ShDragImage
            {
                Size = new SD.Size(thumbnail.Width, thumbnail.Height),
                Offset = new SD.Point(thumbnail.Width / 2, thumbnail.Height / 2),
                Bitmap = hbitmap,
                ColorKey = unchecked((int)0xFFFFFFFF), // CLR_NONE
            };

            var helper = (IDragSourceHelper)new DragDropHelper();
            helper.InitializeFromBitmap(ref info, comData);
            hbitmap = IntPtr.Zero; // the helper owns (and deletes) the bitmap on success
        }
        catch (Exception ex)
        {
            Log.Error("Drag image setup failed", ex);
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
                DeleteObject(hbitmap);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShDragImage
    {
        public SD.Size Size;
        public SD.Point Offset;
        public IntPtr Bitmap;
        public int ColorKey;
    }

    [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    private class DragDropHelper { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
    private interface IDragSourceHelper
    {
        void InitializeFromBitmap(ref ShDragImage image, [MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject dataObject);
        void InitializeFromWindow(IntPtr hwnd, ref SD.Point offset, [MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject dataObject);
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
