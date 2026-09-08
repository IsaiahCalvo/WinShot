using System.Drawing.Drawing2D;
using WinShot.Core;
using WinShot.Recording;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Shared visual chrome for the capture selectors (<see cref="FastRegionSelectorDialog"/>
/// and <see cref="FastAllInOneSelectorDialog"/>): the crosshair guide lines, the
/// size/coordinate label pill, and the common overlay-surface setup. Kept in one place so
/// both selectors draw identical chrome and a styling change happens once.
/// </summary>
internal static class SelectorChrome
{
    private const int CrosshairGapPx = 10;
    private const double LiveDesktopOpacity = 0.45;

    /// <summary>LiveDesktopOpacity as a layered-window alpha, for SetLayeredAlpha resets
    /// on pooled surfaces whose alpha was flipped to 255 by a previous frozen swap.</summary>
    internal const byte LiveAlpha = (byte)(LiveDesktopOpacity * 255);

    /// <summary>Shared overlay-surface setup for a selector coordinator form or one of its panes.</summary>
    public static void ConfigureSurface(WF.Form form)
    {
        form.AutoScaleMode = WF.AutoScaleMode.None;
        form.BackColor = SD.Color.Black;
        form.Cursor = WF.Cursors.Cross;
        form.FormBorderStyle = WF.FormBorderStyle.None;
        form.KeyPreview = true;
        form.ShowInTaskbar = false;
        form.StartPosition = WF.FormStartPosition.Manual;
        form.TopMost = true;
        // The selector opens BEFORE the freeze snapshot is taken (so the hotkey feels
        // instant); exclusion keeps these surfaces out of that deferred snapshot and out
        // of any confirm-time live grab. WGC/Desktop Duplication honor display affinity;
        // the BitBlt tier omits CAPTUREBLT, so the translucent (layered) surface is
        // skipped there by construction.
        form.HandleCreated += (_, _) => WinShot.Scrolling.CaptureExclusion.Apply(form.Handle);
    }

    /// <summary>Freeze-on surfaces are opaque snapshots. Freeze-off surfaces are native
    /// translucent overlays, leaving the changing desktop visible without background work.</summary>
    public static void ConfigurePresentation(WF.Form form, bool freezeScreen)
    {
        form.Opacity = freezeScreen ? 1.0 : LiveDesktopOpacity;
    }

    /// <summary>Draws the crosshair guide lines (shadow + white) with a gap around the cursor.</summary>
    public static void DrawCrosshair(SD.Graphics g, SD.Size clientSize, SD.Point cursor)
    {
        var guides = FastSelectorGuideLayout.Calculate(clientSize, cursor, CrosshairGapPx);
        if (!guides.IsVisible)
            return;

        using var shadow = new SD.Pen(SD.Color.FromArgb(120, 0, 0, 0), 3);
        using var pen = new SD.Pen(SD.Color.FromArgb(210, 255, 255, 255), 1);
        DrawGuideLines(g, shadow, guides);
        DrawGuideLines(g, pen, guides);
    }

    private static void DrawGuideLines(SD.Graphics g, SD.Pen pen, FastSelectorGuideLines guides)
    {
        g.DrawLine(pen, guides.LeftStart, guides.LeftEnd);
        g.DrawLine(pen, guides.RightStart, guides.RightEnd);
        g.DrawLine(pen, guides.TopStart, guides.TopEnd);
        g.DrawLine(pen, guides.BottomStart, guides.BottomEnd);
    }

    /// <summary>The rendered size of the label pill for <paramref name="text"/>, for callers
    /// that place the pill by an edge other than its top-left.</summary>
    public static SD.Size MeasureLabel(string text)
    {
        using var font = ThemePalette.UiFont(ThemePalette.TextBodyPt, SD.FontStyle.Bold);
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        return new SD.Size(size.Width + 16, size.Height + 8);
    }

    /// <summary>
    /// Instructional pill: the inverse of the size label — a light fill with dark text —
    /// centred horizontally near the top of the surface, so it reads as guidance rather than
    /// as another readout. <paramref name="opacity"/> lets the caller fade it out on dismissal.
    /// </summary>
    /// <param name="scale">The pill's monitor scale. Both the face and the padding go through
    /// it, so the box keeps the same proportions at 150% instead of squeezing the text.</param>
    public static void DrawHint(
        SD.Graphics g, SD.Size clientSize, string text, double opacity = 1, double scale = 1)
    {
        if (string.IsNullOrEmpty(text) || opacity <= 0)
            return;

        // Pixel-unit face: the padding below is in device pixels, so a point-size font would
        // scale a second time on a HiDPI monitor and burst the box it was measured into.
        using SD.Font font = ThemePalette.UiFontPx(
            RecordingMonitorDpi.Px((int)Math.Round(ThemePalette.TextBodyLgPt * 96 / 72), scale));
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        int w = size.Width + RecordingMonitorDpi.Px(28, scale);
        int h = size.Height + RecordingMonitorDpi.Px(14, scale);
        int left = Math.Max(0, (clientSize.Width - w) / 2);
        int top = Math.Max(0, (int)Math.Round(clientSize.Height * 0.12));
        var bg = new SD.Rectangle(left, top, w, h);

        // Buffered, not alpha-scaled: TextRenderer drops the alpha channel, so scaling the ink
        // colour would dissolve the capsule out from under a still-solid black sentence that
        // then vanished in one frame. GdiFade composites the whole pill instead.
        GdiFade.Draw(g, bg, opacity, (target, box) =>
        {
            SmoothingMode prev = target.SmoothingMode;
            target.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = GdiPaths.RoundedRect(box, box.Height / 2))
            using (var bgBrush = new SD.SolidBrush(
                SD.Color.FromArgb(238, ThemePalette.TextPrimary)))
                target.FillPath(bgBrush, path);
            target.SmoothingMode = prev;

            WF.TextRenderer.DrawText(target, text, font, box, ThemePalette.WindowBg,
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.NoPadding);
        });
    }

    /// <summary>Draws the size/coordinate label pill, clamped inside the client area.</summary>
    public static void DrawLabel(SD.Graphics g, SD.Size clientSize, string text, int x, int y)
    {
        using var font = ThemePalette.UiFont(ThemePalette.TextBodyPt, SD.FontStyle.Bold);
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        int w = size.Width + 16;
        int h = size.Height + 8;
        int left = Math.Clamp(x, 0, Math.Max(0, clientSize.Width - w));
        int top = Math.Clamp(y, 0, Math.Max(0, clientSize.Height - h));
        var bg = new SD.Rectangle(left, top, w, h);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(bg, 6))
        using (var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(235, ThemePalette.WindowBg)))
            g.FillPath(bgBrush, path);
        g.SmoothingMode = prev;

        WF.TextRenderer.DrawText(g, text, font, bg, ThemePalette.TextPrimary,
            WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.NoPadding);
    }
}
