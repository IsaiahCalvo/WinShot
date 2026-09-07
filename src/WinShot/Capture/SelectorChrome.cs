using WinShot.Core;
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

    /// <summary>Draws the size/coordinate label pill, clamped inside the client area.
    /// <paramref name="alpha"/> scales the whole pill, so a caller can fade one out
    /// (TextRenderer cannot draw translucent text, so the text goes through
    /// Graphics.DrawString at every alpha -- switching renderers mid-fade moved the glyphs).</summary>
    public static void DrawLabel(SD.Graphics g, SD.Size clientSize, string text, int x, int y,
        double alpha = 1d)
    {
        double amount = Math.Clamp(alpha, 0d, 1d);
        if (amount <= 0d) return;

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
        using (var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(Scale(235, amount), ThemePalette.WindowBg)))
            g.FillPath(bgBrush, path);
        g.SmoothingMode = prev;

        // One renderer at every alpha. It used to draw through TextRenderer (GDI, hinted) at
        // rest and through DrawString (GDI+, different metrics) below 1, so the pill's text
        // visibly re-laid out on the first frame of the 140 ms fade -- a snap inside the
        // transition that exists so nothing snaps.
        using var textBrush = new SD.SolidBrush(SD.Color.FromArgb(Scale(255, amount), ThemePalette.TextPrimary));
        using var format = new SD.StringFormat
        {
            Alignment = SD.StringAlignment.Center,
            LineAlignment = SD.StringAlignment.Center,
            // The pill is measured with TextRenderer and filled with DrawString; GDI+ is the
            // narrower of the two for every string these pills carry, but without NoWrap a
            // future longer one would silently wrap inside a pill sized for one line.
            FormatFlags = SD.StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, textBrush, bg, format);
    }

    private static int Scale(int value, double amount)
        => (int)Math.Clamp(Math.Round(value * amount), 0d, 255d);
}
