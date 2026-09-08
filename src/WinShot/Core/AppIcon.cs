using System.IO;
using System.Windows.Media.Imaging;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// The one WinShot mark. <c>Assets/winshot.ico</c> is the single source for the exe icon,
/// the tray icon and the About pane, so there is never a second mark or a second blue.
/// Re-cut it with <c>tools\make-icon.ps1</c>.
/// </summary>
public static class AppIcon
{
    private const string ResourceName = "WinShot.Assets.winshot.ico";

    private static BitmapSource? _large;

    /// <summary>Opens the embedded .ico. Null only if the asset was stripped from the build.</summary>
    public static Stream? OpenStream() =>
        typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);

    /// <summary>The largest frame, for WPF surfaces (About pane). Frozen and cached.</summary>
    public static BitmapSource? Large()
    {
        if (_large is not null)
            return _large;
        try
        {
            using Stream? stream = OpenStream();
            if (stream is null)
                return null;
            var decoder = new IconBitmapDecoder(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            frame.Freeze();
            return _large = frame;
        }
        catch (Exception ex)
        {
            Log.Error("App icon could not be decoded for the About pane", ex);
            return null;
        }
    }

    /// <summary>The frame closest to <paramref name="size"/>, as a GDI+ bitmap the tray
    /// composer can draw badges onto. Caller owns the bitmap.</summary>
    /// <remarks>Decoded through WPF rather than <c>System.Drawing.Icon</c>: every frame in
    /// the asset is PNG-compressed, and Icon's own frame picker silently hands back a
    /// smaller size for those.</remarks>
    public static SD.Bitmap? Raster(int size)
    {
        try
        {
            using Stream? stream = OpenStream();
            if (stream is null)
                return null;
            var decoder = new IconBitmapDecoder(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames
                .OrderBy(f => Math.Abs(f.PixelWidth - size))
                .ThenByDescending(f => f.PixelWidth)
                .First();

            var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
            var bitmap = new SD.Bitmap(
                converted.PixelWidth, converted.PixelHeight, SD.Imaging.PixelFormat.Format32bppPArgb);
            SD.Imaging.BitmapData data = bitmap.LockBits(
                new SD.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                SD.Imaging.ImageLockMode.WriteOnly,
                SD.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                converted.CopyPixels(
                    new System.Windows.Int32Rect(0, 0, bitmap.Width, bitmap.Height),
                    data.Scan0,
                    data.Stride * data.Height,
                    data.Stride);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error($"App icon could not be rasterized at {size}px", ex);
            return null;
        }
    }
}
