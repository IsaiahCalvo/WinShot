using System.Drawing.Drawing2D;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Core;

/// <summary>
/// One recipe for the app's rounded popup cards: the window Region and the painted
/// hairline border derive from the same corner radius, so the border follows the
/// curve instead of being clipped square at the corners (the old
/// DrawRectangle-over-CreateRoundRectRgn combo lost its border wherever the
/// region curved away).
/// </summary>
public static class PopupChrome
{
    /// <summary>Translucent light hairline shared by every dark popup card.</summary>
    public static readonly SD.Color Hairline = ThemePalette.BorderStrong;

    public static void ApplyRegion(WF.Form form, int cornerRadius)
    {
        if (form.Width <= 0 || form.Height <= 0)
            return;
        using var path = GdiPaths.RoundedRect(new SD.Rectangle(0, 0, form.Width, form.Height), cornerRadius);
        form.Region?.Dispose();
        form.Region = new SD.Region(path);
    }

    public static void DrawBorder(SD.Graphics g, SD.Size clientSize, int cornerRadius)
        => DrawBorder(g, clientSize, cornerRadius, Hairline);

    /// <summary>Anti-aliased 1px hairline that follows the rounded corner. Inset by half
    /// a pixel so the region edge doesn't shave the stroke.</summary>
    public static void DrawBorder(SD.Graphics g, SD.Size clientSize, int cornerRadius, SD.Color color)
    {
        if (clientSize.Width <= 1 || clientSize.Height <= 1)
            return;

        SmoothingMode prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new SD.Pen(color, 1f);
        using var path = RoundedRectF(
            new SD.RectangleF(0.5f, 0.5f, clientSize.Width - 1f, clientSize.Height - 1f),
            cornerRadius);
        g.DrawPath(pen, path);
        g.SmoothingMode = prev;
    }

    private static GraphicsPath RoundedRectF(SD.RectangleF bounds, float radius)
    {
        float d = Math.Max(1f, Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
