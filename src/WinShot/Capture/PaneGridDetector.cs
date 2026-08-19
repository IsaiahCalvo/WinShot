using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// Finds a window's LAYOUT — the sidebar, the header, the footer, the content pane — by
/// looking for the long straight seams between them, rather than by asking what any of it is.
///
/// Why this exists: measured on a live desktop, the accessibility tree answers nothing at all
/// inside an Electron app's left sidebar, and pulling a whole window's UIA subtree to look for
/// scrollable regions costs 4.4 s for Claude and 8.5 s for ChatGPT — unusable under an overlay
/// that has to open instantly. But a resizable pane has a splitter, and a splitter is a line
/// that runs the height of the window. Layout is visible even when structure is not.
///
/// Method: adjacent-column (and adjacent-row) difference energy on the frozen snapshot. A
/// boundary is a column whose neighbours disagree down most of the window's height. That is a
/// pane seam; short edges — text, icons, buttons — never span far enough to qualify.
///
/// ponytail: projection profiling, the oldest trick in document layout analysis. If flat
/// designs with no seam at all ever become the norm, the upgrade path is an ONNX UI-layout
/// model (Microsoft's OmniParser is the reference), which costs a GPU and ~1 s per screen.
/// </summary>
internal static class PaneGridDetector
{
    private const int Scale = 2;             // analysis runs on a 2x downsample
    private const int ChannelDelta = 24;     // per-channel difference that counts as an edge
    private const int MinSpanPercent = 60;   // share of the window a seam must run to qualify
    private const int MinPaneCells = 40;     // cells; seams closer than this are one seam
    private const int MinPanePx = 80;        // a pane narrower than this is not a pane

    /// <summary>Vertical and horizontal seam positions in screen coordinates, window edges
    /// included, each sorted ascending.</summary>
    internal readonly record struct Grid(int[] ColumnEdges, int[] RowEdges)
    {
        public bool IsEmpty => ColumnEdges.Length < 2 || RowEdges.Length < 2 ||
            (ColumnEdges.Length == 2 && RowEdges.Length == 2);
    }

    /// <summary>
    /// The pane containing <paramref name="point"/>, or null when the window has no internal
    /// seams worth speaking of. <paramref name="bitmapRect"/> is the window in the bitmap's
    /// own coordinates; the result comes back in the same space.
    /// </summary>
    public static SD.Rectangle? PaneAt(Grid grid, SD.Point point, SD.Rectangle bitmapRect)
    {
        if (grid.IsEmpty || !bitmapRect.Contains(point))
            return null;

        (int left, int right) = Span(grid.ColumnEdges, point.X);
        (int top, int bottom) = Span(grid.RowEdges, point.Y);
        var pane = SD.Rectangle.FromLTRB(left, top, right, bottom);

        // A pane the size of the whole window says nothing the window rect did not.
        if (pane.Width < MinPanePx || pane.Height < MinPanePx)
            return null;
        if (pane.Width >= bitmapRect.Width - 4 && pane.Height >= bitmapRect.Height - 4)
            return null;
        return pane;
    }

    private static (int Low, int High) Span(int[] edges, int value)
    {
        int low = edges[0], high = edges[^1];
        foreach (int edge in edges)
        {
            if (edge <= value && edge > low) low = edge;
            if (edge > value && edge < high) high = edge;
        }
        return (low, high);
    }

    /// <summary>Scans a window's pixels once. Costs a few ms; cache the result per window.</summary>
    public static Grid Build(SD.Bitmap frozen, SD.Rectangle bitmapRect)
    {
        var area = SD.Rectangle.Intersect(bitmapRect, new SD.Rectangle(0, 0, frozen.Width, frozen.Height));
        int w = area.Width / Scale, h = area.Height / Scale;
        if (w < 8 || h < 8)
            return new Grid(new[] { bitmapRect.Left, bitmapRect.Right }, new[] { bitmapRect.Top, bitmapRect.Bottom });

        byte[] pixels = Downsample(frozen, area, w, h);

        var columns = new int[w];
        var rows = new int[h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 1; x < w; x++)
            {
                if (Differs(pixels, (y * w + x) * 3, (y * w + x - 1) * 3))
                    columns[x]++;
            }
        }
        for (int y = 1; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (Differs(pixels, (y * w + x) * 3, ((y - 1) * w + x) * 3))
                    rows[y]++;
            }
        }

        int[] columnEdges = Seams(columns, h * MinSpanPercent / 100, area.Left, area.Right);
        int[] rowEdges = Seams(rows, w * MinSpanPercent / 100, area.Top, area.Bottom);
        return new Grid(columnEdges, rowEdges);
    }

    private static bool Differs(byte[] p, int a, int b) =>
        Math.Abs(p[a] - p[b]) > ChannelDelta ||
        Math.Abs(p[a + 1] - p[b + 1]) > ChannelDelta ||
        Math.Abs(p[a + 2] - p[b + 2]) > ChannelDelta;

    /// <summary>Peaks that clear the span threshold, thinned so a 2 px seam counts once, then
    /// mapped back to screen coordinates with the window edges bracketing them.</summary>
    private static int[] Seams(int[] energy, int threshold, int origin, int end)
    {
        var seams = new List<int> { origin };
        int index = 1;
        while (index < energy.Length)
        {
            if (energy[index] < threshold)
            {
                index++;
                continue;
            }

            // Take the strongest cell of this run, then skip past the whole run.
            int best = index, run = index;
            while (run < energy.Length && energy[run] >= threshold)
            {
                if (energy[run] > energy[best]) best = run;
                run++;
            }

            int at = origin + (best * Scale);
            if (at - seams[^1] >= MinPaneCells)
                seams.Add(at);
            index = run;
        }

        if (end - seams[^1] < MinPaneCells && seams.Count > 1)
            seams[^1] = end;
        else
            seams.Add(end);
        return seams.ToArray();
    }

    private static byte[] Downsample(SD.Bitmap bmp, SD.Rectangle area, int w, int h)
    {
        var outPixels = new byte[w * h * 3];
        var data = bmp.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[area.Width * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + (y * Scale * data.Stride), row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int src = x * Scale * 4;
                    int dst = (y * w + x) * 3;
                    outPixels[dst] = row[src];
                    outPixels[dst + 1] = row[src + 1];
                    outPixels[dst + 2] = row[src + 2];
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return outPixels;
    }
}
