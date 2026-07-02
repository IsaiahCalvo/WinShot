using System.Drawing.Drawing2D;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>Shared GDI+ path builders.</summary>
public static class GdiPaths
{
    /// <summary>
    /// Builds a closed rounded-rectangle path with the given corner radius. Returns
    /// an empty path for non-positive bounds. Caller owns the returned path (dispose it).
    /// </summary>
    public static GraphicsPath RoundedRect(SD.Rectangle bounds, int radius)
    {
        // Clamp the corner diameter to the rect's short side so an oversized radius
        // degrades to a capsule instead of a self-intersecting path.
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return path;

        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
