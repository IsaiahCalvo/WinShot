using System.Runtime.InteropServices;
using System.Text;
using WinShot.Core;
using WinShot.Editor.Background;
using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// Composites a captured window onto a backdrop with padding, rounded corners and a soft
/// shadow — the mockup look window capture is usually wanted for. Before this the styled
/// result was reachable only through one unbound tray item that opened the whole background
/// composer; a plain window capture was a bare rectangle with the desktop cropped off at its
/// edges.
/// </summary>
public static class WindowBackdrop
{
    /// <summary>Padding presets, in source pixels, matching the reference's Min/Default/Max.</summary>
    public static int PaddingFor(string? preset) => (preset ?? "default").Trim().ToLowerInvariant() switch
    {
        "min" => 24,
        "max" => 96,
        _ => 48,
    };

    /// <summary>Whether a settings value asks for a composited window capture.</summary>
    public static bool IsEnabled(string? mode) =>
        !string.Equals((mode ?? "wallpaper").Trim(), "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Draws <paramref name="source"/> centred on a canvas <paramref name="padding"/> px
    /// larger on every side. <paramref name="backdrop"/> is scaled to cover the canvas
    /// (aspect preserved, overflow cropped) and a neutral gradient stands in when it is null,
    /// so a machine with no readable wallpaper still gets a mockup rather than a black frame.
    /// The caller owns <paramref name="source"/>; the returned bitmap is new.
    /// </summary>
    public static SD.Bitmap Compose(SD.Bitmap source, SD.Image? backdrop, int padding)
    {
        ArgumentNullException.ThrowIfNull(source);
        BackgroundLayoutResult layout = BackgroundLayout.Calculate(source.Size, padding, aspectRatio: null);
        var canvas = new SD.Bitmap(layout.CanvasSize.Width, layout.CanvasSize.Height,
            SD.Imaging.PixelFormat.Format32bppArgb);

        using (var g = SD.Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
            DrawBackdrop(g, backdrop, layout.CanvasSize);

            var target = new SD.Rectangle(
                (layout.CanvasSize.Width - source.Width) / 2,
                (layout.CanvasSize.Height - source.Height) / 2,
                source.Width,
                source.Height);
            int radius = Math.Clamp(Math.Min(source.Width, source.Height) / 40, 6, 16);
            DrawShadow(g, target, radius, padding);

            var state = g.Save();
            using (var clip = GdiPaths.RoundedRect(target, radius))
                g.SetClip(clip);
            g.DrawImage(source, target);
            g.Restore(state);
        }

        return canvas;
    }

    private static void DrawBackdrop(SD.Graphics g, SD.Image? backdrop, SD.Size canvas)
    {
        var full = new SD.Rectangle(0, 0, canvas.Width, canvas.Height);
        if (backdrop is null)
        {
            using var fallback = new SD.Drawing2D.LinearGradientBrush(
                full, SD.Color.FromArgb(0x24, 0x28, 0x34), SD.Color.FromArgb(0x11, 0x13, 0x1A), 45f);
            g.FillRectangle(fallback, full);
            return;
        }

        // Cover, not stretch: a 16:9 wallpaper squashed into a tall canvas is the giveaway
        // that this is a composite rather than a photograph of a desktop.
        double scale = Math.Max(canvas.Width / (double)backdrop.Width, canvas.Height / (double)backdrop.Height);
        int width = Math.Max(1, (int)Math.Ceiling(backdrop.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(backdrop.Height * scale));
        g.DrawImage(backdrop, new SD.Rectangle(
            (canvas.Width - width) / 2, (canvas.Height - height) / 2, width, height));
    }

    /// <summary>A large soft shadow, faked with concentric rounded strokes the way the loupe
    /// and the card already do it — cheap, and it survives on a light or a dark backdrop.</summary>
    private static void DrawShadow(SD.Graphics g, SD.Rectangle target, int radius, int padding)
    {
        int spread = Math.Clamp(padding / 2, 6, 40);
        for (int i = spread; i >= 1; i--)
        {
            var rect = target;
            rect.Inflate(i, i);
            rect.Offset(0, Math.Max(1, i / 3));
            int alpha = Math.Max(2, 26 - (int)(24.0 * i / spread));
            using var pen = new SD.Pen(SD.Color.FromArgb(alpha, 0, 0, 0), 2f);
            using var path = GdiPaths.RoundedRect(rect, radius + i);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>
    /// The user's desktop wallpaper, or null when there is none, it is unreadable, or it is a
    /// format GDI+ will not decode. Never throws — a failed backdrop must not fail a capture.
    /// </summary>
    public static SD.Image? TryLoadWallpaper()
    {
        try
        {
            var buffer = new StringBuilder(520);
            if (!SystemParametersInfo(SpiGetDeskWallpaper, (uint)buffer.Capacity, buffer, 0))
                return null;
            string path = buffer.ToString();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                return null;

            // Read through a stream and clone: Image.FromFile keeps the file locked for the
            // life of the image, and this one outlives the capture that made it.
            using var stream = System.IO.File.OpenRead(path);
            using var loaded = SD.Image.FromStream(stream);
            return new SD.Bitmap(loaded);
        }
        catch (Exception ex)
        {
            Log.Error("Desktop wallpaper could not be read; using the fallback backdrop", ex);
            return null;
        }
    }

    private const uint SpiGetDeskWallpaper = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);
}
