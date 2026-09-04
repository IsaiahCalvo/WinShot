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

    /// <summary>
    /// The cursor readout pill, offset down-and-right of the pointer and flipped back inside
    /// when that would run off the surface. This is the readout the crosshair carries when the
    /// magnifier is off — before this the coordinates lived only inside the loupe, so turning
    /// the magnifier off (or tapping Alt) left the selector with no position readout at all.
    /// </summary>
    public static void DrawCursorReadout(SD.Graphics g, SD.Size clientSize, string text, SD.Point cursor)
    {
        SD.Size pill = MeasureLabel(text);
        int left = cursor.X + CursorReadoutOffsetX;
        int top = cursor.Y + CursorReadoutOffsetY;
        if (left + pill.Width > clientSize.Width)
            left = cursor.X - CursorReadoutOffsetX - pill.Width;
        if (top + pill.Height > clientSize.Height)
            top = cursor.Y - CursorReadoutOffsetY - pill.Height;
        DrawLabel(g, clientSize, text, left, top);
    }

    private const int CursorReadoutOffsetX = 16;
    private const int CursorReadoutOffsetY = 20;

    /// <summary>Draws the first-run hint pill centred near the top of a monitor.</summary>
    public static void DrawHint(SD.Graphics g, SD.Size clientSize, string text, double opacity)
    {
        SD.Size pill = MeasureLabel(text);
        DrawLabel(g, clientSize, text,
            (clientSize.Width - pill.Width) / 2,
            Math.Max(0, clientSize.Height / 8),
            opacity);
    }

    /// <summary>The rendered size of the label pill for <paramref name="text"/>, for callers
    /// that place the pill by an edge other than its top-left.</summary>
    public static SD.Size MeasureLabel(string text)
    {
        using var font = ThemePalette.UiFont(ThemePalette.TextBodyPt, SD.FontStyle.Bold);
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        return new SD.Size(size.Width + 16, size.Height + 8);
    }

    /// <summary>Draws the size/coordinate label pill, clamped inside the client area.</summary>
    public static void DrawLabel(SD.Graphics g, SD.Size clientSize, string text, int x, int y) =>
        DrawLabel(g, clientSize, text, x, y, 1d);

    /// <summary>
    /// The label pill at a given opacity, for chrome that fades in and out. Both the pill and
    /// its text carry the alpha, so a fading pill never leaves ghost text behind.
    /// </summary>
    public static void DrawLabel(SD.Graphics g, SD.Size clientSize, string text, int x, int y, double opacity)
    {
        int alpha = (int)Math.Round(255 * Math.Clamp(opacity, 0d, 1d));
        if (alpha <= 0)
            return;
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
        using (var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(alpha * 235 / 255, 0x1C, 0x1C, 0x1E)))
            g.FillPath(bgBrush, path);
        g.SmoothingMode = prev;

        WF.TextRenderer.DrawText(g, text, font, bg, SD.Color.FromArgb(alpha, ThemePalette.TextPrimary),
            WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.NoPadding);
    }
}
