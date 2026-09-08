using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>Which overlay the tray mark carries. The mark itself never changes.</summary>
public enum TrayIconVariant
{
    /// <summary>The plain WinShot mark.</summary>
    Normal,

    /// <summary>An accent check disc — the copy/save confirmation beat.</summary>
    Confirm,

    /// <summary>A slash — desktop icons are hidden by the tray toggle.</summary>
    Slashed,
}

internal static class TrayIconFactory
{
    /// <summary>Pop in, hold, fall away: three durations from the motion vocabulary rather
    /// than one invented total.</summary>
    public const int ConfirmationMs = Motion.EnterDurationMs + Motion.SwapMs + Motion.ExitDurationMs;

    /// <summary>
    /// The badge's size at <paramref name="progress"/> through the confirmation beat, where
    /// progress is LINEAR time over <see cref="ConfirmationMs"/>. A beat is a timeline, not a
    /// curve: driving these phase boundaries with an eased value instead put EaseOutBack's
    /// overshoot above 1.0 for two thirds of the tween, which the phase maths read as "past
    /// the end" and collapsed the badge a third of the way in. EaseOutBack shapes the pop-in
    /// alone — its overshoot is what makes a confirmation land rather than merely appear.
    /// </summary>
    public static double ConfirmationBadgeScale(double progress)
    {
        double ms = Math.Clamp(progress, 0, 1) * ConfirmationMs;
        if (ms < Motion.EnterDurationMs)
            return Motion.EaseOutBack(ms / Motion.EnterDurationMs);

        double held = ms - Motion.EnterDurationMs;
        if (held < Motion.SwapMs)
            return 1;

        return Math.Max(0, 1 - (held - Motion.SwapMs) / Motion.ExitDurationMs);
    }

    // Icons are cached for the process: a NotifyIcon swap happens on every capture
    // confirmation, and each Create() otherwise costs an ICO decode plus a GDI handle.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(TrayIconVariant, int), SD.Icon>
        Cache = new();

    /// <summary>The tray mark, loaded from the shipped <c>winshot.ico</c> (never drawn from
    /// scratch — the exe, the tray and About must show the same mark).</summary>
    /// <remarks>The returned icon is cached for the process and shared by every caller:
    /// do NOT dispose it. Disposing one hands the next caller a dead handle.</remarks>
    public static SD.Icon Create(TrayIconVariant variant = TrayIconVariant.Normal, double badgeScale = 1)
    {
        // Quantize so a running animation reuses a handful of cached icons instead of
        // allocating a GDI icon per frame.
        int step = variant == TrayIconVariant.Confirm
            ? Math.Clamp((int)Math.Round(badgeScale * 6), 0, 6)
            : 6;
        return Cache.GetOrAdd((variant, step), key => Render(key.Item1, key.Item2 / 6.0));
    }

    private static SD.Icon Render(TrayIconVariant variant, double badgeScale)
    {
        // 32 px covers the shell's small-icon request at 100–200% scaling; Windows
        // downsamples for the smaller steps.
        const int size = 32;
        using SD.Bitmap? source = AppIcon.Raster(size);
        using var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(SD.Color.Transparent);
            if (source is not null)
                g.DrawImage(source, 0, 0, size, size);
            else
            {
                // The asset is missing (stripped build): a plain accent squircle still reads
                // as WinShot rather than the generic .NET box.
                using var fallback = new SD.SolidBrush(ThemePalette.Accent);
                g.FillEllipse(fallback, 1, 1, size - 2, size - 2);
            }

            switch (variant)
            {
                case TrayIconVariant.Confirm when badgeScale > 0.01:
                    DrawConfirmBadge(g, size, badgeScale);
                    break;
                case TrayIconVariant.Slashed:
                    DrawSlash(g, size);
                    break;
            }
        }
        return FromBitmap(bmp);
    }

    private static void DrawConfirmBadge(SD.Graphics g, int size, double scale)
    {
        float d = (float)(size * 0.58 * scale);
        float x = size - d - size * 0.03f;
        float y = size - d - size * 0.03f;
        using var ring = new SD.SolidBrush(SD.Color.White);
        g.FillEllipse(ring, x, y, d, d);
        // Success is the accent here, exactly as the toast paints its success glyph —
        // the app has one accent and no second "confirmation" colour.
        using var disc = new SD.SolidBrush(ThemePalette.Accent);
        float inset = d * 0.12f;
        g.FillEllipse(disc, x + inset, y + inset, d - inset * 2, d - inset * 2);

        using var tick = new SD.Pen(SD.Color.White, Math.Max(1.2f, d * 0.16f))
        {
            StartCap = SD.Drawing2D.LineCap.Round,
            EndCap = SD.Drawing2D.LineCap.Round,
            LineJoin = SD.Drawing2D.LineJoin.Round,
        };
        g.DrawLines(tick,
        [
            new SD.PointF(x + d * 0.28f, y + d * 0.52f),
            new SD.PointF(x + d * 0.44f, y + d * 0.68f),
            new SD.PointF(x + d * 0.73f, y + d * 0.34f),
        ]);
    }

    private static void DrawSlash(SD.Graphics g, int size)
    {
        using var shadow = new SD.Pen(SD.Color.FromArgb(190, 0, 0, 0), Math.Max(3f, size * 0.16f))
        {
            StartCap = SD.Drawing2D.LineCap.Round,
            EndCap = SD.Drawing2D.LineCap.Round,
        };
        using var line = new SD.Pen(SD.Color.White, Math.Max(1.6f, size * 0.09f))
        {
            StartCap = SD.Drawing2D.LineCap.Round,
            EndCap = SD.Drawing2D.LineCap.Round,
        };
        float a = size * 0.16f;
        float b = size * 0.84f;
        g.DrawLine(shadow, a, b, b, a);
        g.DrawLine(line, a, b, b, a);
    }

    /// <summary>Copies the GDI bitmap into a managed icon and frees the HICON — repeatedly
    /// calling Icon.FromHandle without this leaks one icon handle per call, and the
    /// confirmation animation calls it per distinct frame.</summary>
    private static SD.Icon FromBitmap(SD.Bitmap bmp)
    {
        IntPtr handle = bmp.GetHicon();
        try
        {
            using var borrowed = SD.Icon.FromHandle(handle);
            return (SD.Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
