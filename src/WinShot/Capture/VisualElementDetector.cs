using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// Pixel-based element detection for apps whose accessibility tree is disabled (the Claude
/// and Codex desktop apps ship with renderer accessibility off — no UIA wake works). Finds
/// the nearest enclosing visual box around a point by scanning the FROZEN screenshot for
/// sustained contrast edges: cards, bubbles, and panels in flat UIs are fills or borders
/// that differ from their background along a whole edge. Works on every app, needs nothing
/// from the target, and runs in a few milliseconds on a local crop.
/// </summary>
internal static class VisualElementDetector
{
    private const int SearchReach = 720;   // how far an edge may be from the cursor
    private const int BandHalf = 44;       // rows/cols sampled along the perpendicular band
    private const int EdgeDelta = 14;      // luma step that counts as an edge
    private const double EdgeRatio = 0.58; // fraction of the band that must step (rounded corners eat the rest)

    /// <summary>Point and bounds in the bitmap's own coordinate space.</summary>
    public static SD.Rectangle? Find(SD.Bitmap frozen, SD.Point p, SD.Rectangle bounds)
    {
        bounds.Intersect(new SD.Rectangle(0, 0, frozen.Width, frozen.Height));
        if (!bounds.Contains(p))
            return null;
        var crop = SD.Rectangle.Intersect(bounds, new SD.Rectangle(
            p.X - SearchReach, p.Y - SearchReach, SearchReach * 2, SearchReach * 2));
        if (crop.Width < 32 || crop.Height < 24)
            return null;

        byte[,] luma = Luma(frozen, crop);
        int cx = p.X - crop.X, cy = p.Y - crop.Y;

        int? left = ScanVertical(luma, cx, cy, crop, step: -1);
        int? right = ScanVertical(luma, cx, cy, crop, step: +1);
        int? top = ScanHorizontal(luma, cx, cy, crop, step: -1);
        int? bottom = ScanHorizontal(luma, cx, cy, crop, step: +1);

        int missing = (left is null ? 1 : 0) + (right is null ? 1 : 0) +
                      (top is null ? 1 : 0) + (bottom is null ? 1 : 0);
        if (missing > 1)
            return null;
        // One missing side: the element runs to the window/crop edge there.
        var rect = SD.Rectangle.FromLTRB(
            crop.X + (left ?? 0),
            crop.Y + (top ?? 0),
            crop.X + (right ?? crop.Width - 1) + 1,
            crop.Y + (bottom ?? crop.Height - 1) + 1);
        if (rect.Width < 24 || rect.Height < 16)
            return null;
        return rect;
    }

    private static byte[,] Luma(SD.Bitmap bmp, SD.Rectangle crop)
    {
        var luma = new byte[crop.Width, crop.Height];
        var data = bmp.LockBits(crop, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[crop.Width * 4];
            for (int y = 0; y < crop.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < crop.Width; x++)
                {
                    int i = x * 4;
                    luma[x, y] = (byte)((row[i + 2] * 77 + row[i + 1] * 151 + row[i] * 28) >> 8);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return luma;
    }

    /// <summary>Walks columns away from the cursor; a column where most band rows show a
    /// luma step between its neighbours is a vertical element edge.</summary>
    private static int? ScanVertical(byte[,] luma, int cx, int cy, SD.Rectangle crop, int step)
    {
        int y0 = Math.Max(1, cy - BandHalf), y1 = Math.Min(crop.Height - 2, cy + BandHalf);
        int band = y1 - y0 + 1;
        if (band < 12)
            return null;
        int needed = (int)(band * EdgeRatio);
        for (int x = cx + step * 3; x > 1 && x < crop.Width - 2; x += step)
        {
            int hits = 0;
            for (int y = y0; y <= y1; y++)
            {
                if (Math.Abs(luma[x + 1, y] - luma[x - 1, y]) >= EdgeDelta)
                    hits++;
            }
            if (hits >= needed)
                return x;
        }
        return null;
    }

    private static int? ScanHorizontal(byte[,] luma, int cx, int cy, SD.Rectangle crop, int step)
    {
        int x0 = Math.Max(1, cx - BandHalf), x1 = Math.Min(crop.Width - 2, cx + BandHalf);
        int band = x1 - x0 + 1;
        if (band < 12)
            return null;
        int needed = (int)(band * EdgeRatio);
        for (int y = cy + step * 3; y > 1 && y < crop.Height - 2; y += step)
        {
            int hits = 0;
            for (int x = x0; x <= x1; x++)
            {
                if (Math.Abs(luma[x, y + 1] - luma[x, y - 1]) >= EdgeDelta)
                    hits++;
            }
            if (hits >= needed)
                return y;
        }
        return null;
    }
}
