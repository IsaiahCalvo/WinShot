using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// Pixel-based element detection for apps whose accessibility tree is disabled (the Claude
/// and Codex desktop apps ship with renderer accessibility off — no UIA wake works).
///
/// Method: background segmentation. Detect the crop's dominant background color, mask
/// everything that differs, dilate the mask so glyphs and lines fuse into blocks, then
/// flood the connected block under the cursor and return its bounding box. Unlike edge
/// scanning (the first attempt), the answer is a property of the ELEMENT, not of where
/// inside it the cursor happens to sit — so the highlight does not drift or jitter as the
/// mouse moves within a card. ponytail: classic CV segmentation done with integral-image
/// dilation and BFS on a 2x-downsampled crop; an ONNX UI-detector model is the upgrade
/// path if flat-design heuristics ever stop being enough.
/// </summary>
internal static class VisualElementDetector
{
    /// <summary>Diagnostics for offline tuning (mask coverage, flood size). Test-only.</summary>
    internal static string LastDiagnostic = "";

    private const int SearchReach = 600;  // crop half-size around the cursor
    private const int Scale = 2;          // detection runs on a 2x downsample
    private const int BackgroundTolerance = 22; // max channel distance to count as background
    // Tuned on real chat UIs: line spacing is ~20-28px (fuse it), message gaps ~28-40px
    // (keep them apart) — radius 6 cells bridges 24px at the 2x downsample.
    private const int DilateRadius = 6;
    private const int SeedSearch = 20;    // cells; hovering a paragraph gap still finds the message

    /// <summary>Point and bounds in the bitmap's own coordinate space.</summary>
    public static SD.Rectangle? Find(SD.Bitmap frozen, SD.Point p, SD.Rectangle bounds)
    {
        bounds.Intersect(new SD.Rectangle(0, 0, frozen.Width, frozen.Height));
        if (!bounds.Contains(p))
            return null;
        var crop = SD.Rectangle.Intersect(bounds, new SD.Rectangle(
            p.X - SearchReach, p.Y - SearchReach, SearchReach * 2, SearchReach * 2));
        if (crop.Width < 48 || crop.Height < 32)
            return null;

        int w = crop.Width / Scale, h = crop.Height / Scale;
        var (r, g, b) = Downsample(frozen, crop, w, h);

        (byte bgR, byte bgG, byte bgB) = DominantColor(r, g, b, w, h);
        var mask = new bool[w * h];
        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = Math.Abs(r[i] - bgR) > BackgroundTolerance ||
                      Math.Abs(g[i] - bgG) > BackgroundTolerance ||
                      Math.Abs(b[i] - bgB) > BackgroundTolerance;
        }

        bool[] dilated = Dilate(mask, w, h, DilateRadius);

        int cx = Math.Clamp((p.X - crop.X) / Scale, 0, w - 1);
        int cy = Math.Clamp((p.Y - crop.Y) / Scale, 0, h - 1);
        (int sx, int sy)? seed = FindSeed(dilated, w, h, cx, cy);
        int maskPct = (int)(100L * mask.Count(m => m) / mask.Length);
        if (seed is null)
        {
            LastDiagnostic = $"mask={maskPct}% seed=none";
            return null;
        }

        SD.Rectangle? cellBox = FloodBoundingBox(dilated, w, h, seed.Value.sx, seed.Value.sy);
        LastDiagnostic = $"mask={maskPct}% flood={(cellBox is SD.Rectangle cb ? cb.ToString() : "aborted")}";
        if (cellBox is not SD.Rectangle box)
            return null;

        // Undo the dilation padding, map back to screen space, and sanity-bound.
        var rect = new SD.Rectangle(
            crop.X + (box.X + DilateRadius) * Scale - 2,
            crop.Y + (box.Y + DilateRadius) * Scale - 2,
            Math.Max(1, box.Width - DilateRadius * 2) * Scale + 4,
            Math.Max(1, box.Height - DilateRadius * 2) * Scale + 4);
        rect.Intersect(bounds);
        if (rect.Width < 24 || rect.Height < 14 ||
            (long)rect.Width * rect.Height > (long)crop.Width * crop.Height * 92 / 100)
            return null;
        return rect;
    }

    private static (byte[] R, byte[] G, byte[] B) Downsample(SD.Bitmap bmp, SD.Rectangle crop, int w, int h)
    {
        var r = new byte[w * h];
        var g = new byte[w * h];
        var b = new byte[w * h];
        var data = bmp.LockBits(crop, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[crop.Width * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * Scale * data.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int i = x * Scale * 4;
                    b[y * w + x] = row[i];
                    g[y * w + x] = row[i + 1];
                    r[y * w + x] = row[i + 2];
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return (r, g, b);
    }

    private static (byte R, byte G, byte B) DominantColor(byte[] r, byte[] g, byte[] b, int w, int h)
    {
        // 4-bit-per-channel histogram over a sparse sample — the background bucket wins by
        // sheer area in any real UI crop.
        var histogram = new int[4096];
        for (int i = 0; i < w * h; i += 3)
            histogram[((r[i] >> 4) << 8) | ((g[i] >> 4) << 4) | (b[i] >> 4)]++;
        int winner = 0, best = -1;
        for (int k = 0; k < 4096; k++)
        {
            if (histogram[k] > best) { best = histogram[k]; winner = k; }
        }
        return ((byte)(((winner >> 8) & 15) * 16 + 8), (byte)(((winner >> 4) & 15) * 16 + 8),
            (byte)((winner & 15) * 16 + 8));
    }

    private static bool[] Dilate(bool[] mask, int w, int h, int radius)
    {
        // Integral image → box sum ≥ 1 == dilation with a square structuring element.
        var integral = new int[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            integral[(y + 1) * (w + 1) + x + 1] = (mask[y * w + x] ? 1 : 0)
                + integral[y * (w + 1) + x + 1] + integral[(y + 1) * (w + 1) + x]
                - integral[y * (w + 1) + x];
        }
        var outMask = new bool[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
            int sum = integral[(y1 + 1) * (w + 1) + x1 + 1] - integral[y0 * (w + 1) + x1 + 1]
                - integral[(y1 + 1) * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
            outMask[y * w + x] = sum > 0;
        }
        return outMask;
    }

    private static (int, int)? FindSeed(bool[] mask, int w, int h, int cx, int cy)
    {
        for (int radius = 0; radius <= SeedSearch; radius++)
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            int x = cx + dx, y = cy + dy;
            if (x >= 0 && x < w && y >= 0 && y < h && mask[y * w + x])
                return (x, y);
        }
        return null;
    }

    private static SD.Rectangle? FloodBoundingBox(bool[] mask, int w, int h, int sx, int sy)
    {
        var seen = new bool[w * h];
        var queue = new Queue<int>();
        queue.Enqueue(sy * w + sx);
        seen[sy * w + sx] = true;
        int minX = sx, maxX = sx, minY = sy, maxY = sy, cells = 0;
        int budget = w * h; // a runaway flood covering the whole crop means bg detection failed
        while (queue.Count > 0 && cells < budget)
        {
            int i = queue.Dequeue();
            cells++;
            int x = i % w, y = i / w;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            if (x > 0 && mask[i - 1] && !seen[i - 1]) { seen[i - 1] = true; queue.Enqueue(i - 1); }
            if (x < w - 1 && mask[i + 1] && !seen[i + 1]) { seen[i + 1] = true; queue.Enqueue(i + 1); }
            if (y > 0 && mask[i - w] && !seen[i - w]) { seen[i - w] = true; queue.Enqueue(i - w); }
            if (y < h - 1 && mask[i + w] && !seen[i + w]) { seen[i + w] = true; queue.Enqueue(i + w); }
        }
        if (cells >= w * h * 3 / 4)
            return null; // flood ate the crop — background misdetected
        return SD.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
}
