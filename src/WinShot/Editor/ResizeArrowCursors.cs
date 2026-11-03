using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinShot.Editor;

/// <summary>
/// Survey handleCursors.js resizeArrowCursor (Drawboard, 2026-10-26): a resize grabber shows a
/// black double arrow with a white edge turned to the EXACT direction it pulls, the grabber's
/// own direction plus the mark's turn, instead of the nearest of the four fixed system resize
/// cursors. A double arrow looks the same turned half a circle, so the angle folds into
/// [0, 180) and rounds to a whole degree; one cursor per degree and screen scale is cached.
/// </summary>
internal static class ResizeArrowCursors
{
    private const double CursorDip = 18; // Survey CURSOR_SIZE
    private static readonly Geometry Arrow =
        Geometry.Parse("M3 15H27M3 15L8.5 10M3 15L8.5 20M27 15L21.5 10M27 15L21.5 20");
    private static readonly Dictionary<(int, int), Cursor?> Cache = new();

    /// <summary>The direction each box grabber pulls, in screen degrees with y down.
    /// WinShot order: 0 TL, 1 TR, 2 BR, 3 BL, 4 T, 5 R, 6 B, 7 L.</summary>
    private static readonly double[] PullAngle = { 225, 315, 45, 135, 270, 0, 90, 180 };

    public static double PullAngleOf(int handle) =>
        handle >= 0 && handle < PullAngle.Length ? PullAngle[handle] : 0;

    public static int Fold(double angleDegrees) =>
        double.IsFinite(angleDegrees) ? (((int)Math.Round(angleDegrees) % 180) + 180) % 180 : 0;

    /// <summary>The turned arrow, or null when Windows will not take the picture.</summary>
    public static Cursor? For(double angleDegrees, double dpiScale)
    {
        int folded = Fold(angleDegrees);
        int scaleKey = (int)Math.Round(dpiScale * 100);
        if (Cache.TryGetValue((folded, scaleKey), out Cursor? hit)) return hit;
        Cursor? made;
        try { made = Build(folded, dpiScale); }
        catch { made = null; }
        Cache[(folded, scaleKey)] = made;
        return made;
    }

    private static Cursor Build(int angle, double dpiScale)
    {
        int px = Math.Clamp((int)Math.Round(CursorDip * Math.Max(1, dpiScale)), 16, 128);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(px / 30.0, px / 30.0));
            dc.PushTransform(new RotateTransform(angle, 15, 15));
            Pen Stroke(Brush b, double w) => new(b, w)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, Stroke(Brushes.White, 6), Arrow);
            dc.DrawGeometry(null, Stroke(Brushes.Black, 2.5), Arrow);
        }
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var straight = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int stride = px * 4;
        var pixels = new byte[stride * px];
        straight.CopyPixels(pixels, stride, 0);
        return new Cursor(new MemoryStream(CurFile(pixels, px, px / 2)));
    }

    /// <summary>A one-image .cur file: 32-bit BGRA DIB, bottom-up, with an empty AND mask.</summary>
    internal static byte[] CurFile(byte[] bgraTopDown, int size, int hotspot)
    {
        int maskStride = ((size + 31) / 32) * 4;
        int dibBytes = 40 + size * size * 4 + maskStride * size;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // ICONDIR
        w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1);
        // ICONDIRENTRY
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)hotspot); w.Write((ushort)hotspot);
        w.Write((uint)dibBytes); w.Write((uint)22);
        // BITMAPINFOHEADER (height counts the XOR and AND halves)
        w.Write(40); w.Write(size); w.Write(size * 2); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(0); w.Write(size * size * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int row = size - 1; row >= 0; row--) w.Write(bgraTopDown, row * size * 4, size * 4);
        w.Write(new byte[maskStride * size]);
        w.Flush();
        return ms.ToArray();
    }
}
