using System.Runtime.InteropServices;
using System.Text;
using SD = System.Drawing;
using SDD = System.Drawing.Drawing2D;

namespace WinShot.Core;

/// <summary>
/// Puts a capture on a backdrop as it is taken — the padded, presentable version of a
/// screenshot, without a trip through the background composer. <see cref="None"/> (the
/// default) leaves every capture exactly as it was grabbed.
/// </summary>
/// <remarks>
/// Deliberately headless: it is GDI+ only, so it runs on the capture path with no window,
/// no WPF and no user interaction. The interactive composer is still the place to choose a
/// preset per capture; this is the "always frame my shots like this" answer.
/// </remarks>
public static class ScreenshotBackdrop
{
    public const string None = "none";
    public const string Wallpaper = "wallpaper";
    public const string Gradient = "gradient";
    public const string Solid = "color";

    /// <summary>Padding around the shot, as a share of its SHORT edge. Enough to read as a
    /// deliberate frame at any capture size; the floor keeps small crops from getting a
    /// hairline instead of a margin, and the ceiling keeps a 1200x12000 stitched scrolling
    /// capture from picking up 660 px of gradient on all four sides.</summary>
    private const double PaddingShare = 0.055;
    private const int MinPadding = 24;
    private const int MaxPadding = 96;

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Wallpaper => Wallpaper,
        Gradient => Gradient,
        Solid => Solid,
        _ => None,
    };

    public static bool IsEnabled(string? mode) => Normalize(mode) != None;

    /// <summary>
    /// Whether the standing "frame every shot like this" preference applies to THIS capture.
    /// It does not when the capture is on its way to the interactive background composer:
    /// that surface frames the shot itself, so composing first handed it an already-padded,
    /// already-shadowed bitmap to pad and shadow again — nested margins, a shadow inside a
    /// shadow, and a saved file that is visibly wrong with no error anywhere.
    /// </summary>
    public static bool AppliesTo(string? mode, string? postCaptureActionOverride) =>
        IsEnabled(mode) && postCaptureActionOverride != PostCaptureAction.Background;

    /// <summary>The padded canvas size for a shot — the geometry, separated so it can be
    /// asserted without painting anything.</summary>
    public static SD.Size CanvasFor(SD.Size shot)
    {
        int pad = Padding(shot);
        return new SD.Size(shot.Width + pad * 2, shot.Height + pad * 2);
    }

    public static int Padding(SD.Size shot) => Math.Clamp(
        (int)Math.Round(Math.Min(shot.Width, shot.Height) * PaddingShare), MinPadding, MaxPadding);

    /// <summary>
    /// A new bitmap with <paramref name="shot"/> centred on the chosen backdrop, or null when
    /// the mode is <see cref="None"/> or the composition fails — in both cases the caller
    /// keeps the capture it already has. Never disposes or mutates <paramref name="shot"/>.
    /// </summary>
    /// <param name="shadow">
    /// Whether to lift the shot off the backdrop with a drop shadow. This is the only shadow a
    /// WinShot window capture ever has — the capture rect itself is DWM's extended frame
    /// bounds, which already excludes the system one — so turning it off is what "trim the
    /// window shadow" means here: a flat, hard-edged frame.
    /// </param>
    public static SD.Bitmap? Compose(SD.Bitmap shot, string? mode, string? solidColor, bool shadow = true)
    {
        string kind = Normalize(mode);
        if (kind == None || shot.Width <= 0 || shot.Height <= 0)
            return null;

        SD.Bitmap? canvas = null;
        try
        {
            int pad = Padding(shot.Size);
            SD.Size size = CanvasFor(shot.Size);
            canvas = new SD.Bitmap(size.Width, size.Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using (SD.Graphics g = SD.Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SDD.SmoothingMode.AntiAlias;
                g.InterpolationMode = SDD.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = SDD.PixelOffsetMode.HighQuality;

                PaintBackdrop(g, size, kind, solidColor);
                if (shadow)
                    PaintShadow(g, new SD.Rectangle(pad, pad, shot.Width, shot.Height));
                g.DrawImage(shot, new SD.Rectangle(pad, pad, shot.Width, shot.Height));
            }
            return canvas;
        }
        catch (Exception ex)
        {
            // A backdrop is decoration. Losing it must never lose the capture.
            canvas?.Dispose();
            Log.Error($"Screenshot backdrop '{kind}' failed; keeping the plain capture", ex);
            return null;
        }
    }

    private static void PaintBackdrop(SD.Graphics g, SD.Size size, string kind, string? solidColor)
    {
        var bounds = new SD.Rectangle(0, 0, size.Width, size.Height);

        if (kind == Wallpaper && TryLoadWallpaper() is SD.Bitmap paper)
        {
            using (paper)
            {
                DrawCoverFill(g, paper, bounds);
                // A wallpaper is busy by definition; the veil is what keeps the shot on top
                // of it readable, and is the same scrim weight the selector dims with.
                using var veil = new SD.SolidBrush(SD.Color.FromArgb(90, 0, 0, 0));
                g.FillRectangle(veil, bounds);
            }
            return;
        }

        if (kind == Solid)
        {
            using var solid = new SD.SolidBrush(ParseColor(solidColor));
            g.FillRectangle(solid, bounds);
            return;
        }

        // Gradient, and the fallback when the wallpaper cannot be read: the app's own accent
        // ramp, so a framed screenshot still looks like WinShot rather than a stock template.
        using var gradient = new SDD.LinearGradientBrush(
            bounds, ThemePalette.Accent, ThemePalette.AccentGradientEnd, 45f);
        g.FillRectangle(gradient, bounds);
    }

    /// <summary>A few stacked translucent rounded rects: a soft drop shadow without pulling
    /// in an image-effects dependency on the capture path.</summary>
    private static void PaintShadow(SD.Graphics g, SD.Rectangle shot)
    {
        const int layers = 8;
        for (int i = layers; i >= 1; i--)
        {
            int spread = i * 2;
            var rect = new SD.Rectangle(
                shot.X - spread, shot.Y - spread + i, shot.Width + spread * 2, shot.Height + spread * 2);
            using var brush = new SD.SolidBrush(SD.Color.FromArgb(10, 0, 0, 0));
            g.FillRectangle(brush, rect);
        }
    }

    /// <summary>Scales an image to cover the bounds, cropping the overflow — the wallpaper
    /// must not letterbox or stretch out of aspect behind the shot.</summary>
    private static void DrawCoverFill(SD.Graphics g, SD.Bitmap image, SD.Rectangle bounds)
    {
        double scale = Math.Max(
            bounds.Width / (double)image.Width, bounds.Height / (double)image.Height);
        int w = (int)Math.Ceiling(image.Width * scale);
        int h = (int)Math.Ceiling(image.Height * scale);
        g.DrawImage(image, new SD.Rectangle(
            bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h));
    }

    /// <summary>#RRGGBB or #AARRGGBB, with or without the hash. Anything else is the app's
    /// window colour rather than an error the user has to notice mid-capture.</summary>
    public static SD.Color ParseColor(string? value)
    {
        string text = (value ?? string.Empty).Trim().TrimStart('#');
        if ((text.Length == 6 || text.Length == 8) &&
            int.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed))
        {
            return text.Length == 6
                ? SD.Color.FromArgb(255, (parsed >> 16) & 0xFF, (parsed >> 8) & 0xFF, parsed & 0xFF)
                : SD.Color.FromArgb(parsed);
        }
        return ThemePalette.WindowBg;
    }

    private static SD.Bitmap? TryLoadWallpaper()
    {
        try
        {
            var path = new StringBuilder(520);
            if (!SystemParametersInfo(SpiGetDeskWallpaper, (uint)path.Capacity, path, 0))
                return null;

            string file = path.ToString();
            if (file.Length == 0 || !System.IO.File.Exists(file))
                return null;

            // Copied off the decoded image so the file handle does not stay open behind a
            // lazily-decoded Bitmap for the life of the process.
            using var loaded = new SD.Bitmap(file);
            return new SD.Bitmap(loaded);
        }
        catch (Exception ex)
        {
            Log.Info($"Desktop wallpaper unavailable for the capture backdrop: {ex.Message}");
            return null;
        }
    }

    private const uint SpiGetDeskWallpaper = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder result, uint winIni);
}
