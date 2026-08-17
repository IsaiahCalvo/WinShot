using System.Drawing.Drawing2D;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// The one HUD card recipe (recording bar, toasts, tray-adjacent popups, selector
/// mode bar, size pills): charcoal fill, hairline border rgba(255,255,255,.14)
/// with a brighter rgba(255,255,255,.2) edge-light across the top, rounded corners.
/// ponytail: opaque #262628 stands in for the acrylic rgba(38,38,40,.88)+blur —
/// the handoff's sanctioned fallback; real DWM acrylic can't reach these
/// GDI-painted layered popups. Upgrade path: DWMWA_SYSTEMBACKDROP_TYPE on
/// non-layered HUDs if the blur ever matters.
/// </summary>
public static class HudChrome
{
    public static readonly SD.Color Fill = ThemePalette.ToolbarBg;                       // #262628
    public static readonly SD.Color BorderColor = ThemePalette.Border;                   // white .14
    public static readonly SD.Color EdgeLight = ThemePalette.BorderStrong;               // white .2

    /// <summary>
    /// Paints the HUD card into <paramref name="bounds"/> (border drawn inside).
    /// Caller has already set smoothing/anti-alias on <paramref name="g"/>.
    /// </summary>
    public static void Paint(SD.Graphics g, SD.Rectangle bounds, int radius)
    {
        if (bounds.Width <= 2 || bounds.Height <= 2) return;

        var inner = new SD.Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        using var path = GdiPaths.RoundedRect(inner, radius);
        using (var fill = new SD.SolidBrush(Fill))
            g.FillPath(fill, path);
        using (var border = new SD.Pen(BorderColor))
            g.DrawPath(border, path);

        // Top edge-light: redraw just the upper slice of the same path brighter.
        var state = g.Save();
        g.SetClip(new SD.Rectangle(bounds.X, bounds.Y, bounds.Width, Math.Max(3, radius + 2)));
        using (var light = new SD.Pen(EdgeLight))
            g.DrawPath(light, path);
        g.Restore(state);
    }
}
