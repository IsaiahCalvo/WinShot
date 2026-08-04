using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>Stationary regions that are not necessarily full-width header/footer bands.</summary>
internal readonly record struct StationaryViewportRegions(int LeftSide, int RightSide, int BottomOverlay);

/// <summary>
/// Separates viewport-fixed pixels from vertically moving document pixels. A region is only
/// confirmed when it matches at the same viewport coordinate and clearly does not match at
/// the measured scroll displacement. That distinction rejects ordinary page content.
/// </summary>
internal static class StationaryViewportDetector
{
    private const int Step = 4;
    private const int SideBlockWidth = 8;
    private const int TileWidth = 8;
    private const int TileHeight = 8;
    private const int Tolerance = 12;
    private const double StableRatio = 0.86;
    private const double ShiftAdvantage = 0.04;

    public static StationaryViewportRegions Detect(SD.Bitmap previous, SD.Bitmap current, int scrollOffset = 0)
    {
        if (previous.Width != current.Width || previous.Height != current.Height ||
            previous.Width < 64 || previous.Height < 64)
            return default;

        int width = current.Width, height = current.Height;
        int sideCount = (width + SideBlockWidth - 1) / SideBlockWidth;
        int tileColumns = (width + TileWidth - 1) / TileWidth;
        int tileRows = (height + TileHeight - 1) / TileHeight;
        var side = new Evidence[sideCount];
        var tiles = new Evidence[tileColumns * tileRows];
        var priorLuma = new int[(width + Step - 1) / Step];
        Array.Fill(priorLuma, -1);

        var prevData = previous.LockBits(new SD.Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var currData = current.LockBits(new SD.Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var p = new byte[width * 4];
            var c = new byte[width * 4];
            var shifted = new byte[width * 4];
            for (int y = 0; y < height; y += Step)
            {
                Marshal.Copy(prevData.Scan0 + y * prevData.Stride, p, 0, p.Length);
                Marshal.Copy(currData.Scan0 + y * currData.Stride, c, 0, c.Length);
                bool forwardShift = scrollOffset > 0 && y + scrollOffset < height;
                bool backwardShift = !forwardShift && scrollOffset > 0 && y - scrollOffset >= 0;
                bool hasShift = forwardShift || backwardShift;
                if (forwardShift)
                    Marshal.Copy(prevData.Scan0 + (y + scrollOffset) * prevData.Stride,
                        shifted, 0, shifted.Length);
                else if (backwardShift)
                    Marshal.Copy(currData.Scan0 + (y - scrollOffset) * currData.Stride,
                        shifted, 0, shifted.Length);

                int previousHorizontalLuma = -1;
                for (int x = 0; x < width; x += Step)
                {
                    int i = x * 4;
                    bool same = Similar(p, c, i);
                    bool shiftSame = forwardShift
                        ? Similar(shifted, c, i)
                        : backwardShift && Similar(p, shifted, i);
                    int luma = Luma(c, i);
                    int sampleX = x / Step;
                    bool structured = (previousHorizontalLuma >= 0 && Math.Abs(luma - previousHorizontalLuma) >= 14) ||
                        (priorLuma[sampleX] >= 0 && Math.Abs(luma - priorLuma[sampleX]) >= 14);
                    previousHorizontalLuma = luma;
                    priorLuma[sampleX] = luma;

                    int sideIndex = x / SideBlockWidth;
                    side[sideIndex].Add(same, shiftSame, hasShift, structured);
                    int tileIndex = (y / TileHeight) * tileColumns + x / TileWidth;
                    tiles[tileIndex].Add(same, shiftSame, hasShift, structured);
                }
            }
        }
        finally
        {
            current.UnlockBits(currData);
            previous.UnlockBits(prevData);
        }

        bool confirmed = scrollOffset > 0;
        int left = FindSide(side, width, fromLeft: true, confirmed);
        int right = FindSide(side, width, fromLeft: false, confirmed);
        int bottom = FindBottomOverlay(tiles, tileColumns, tileRows, width, height,
            left, right, confirmed);
        return new StationaryViewportRegions(left, right, bottom);
    }

    private static int FindSide(Evidence[] evidence, int width, bool fromLeft, bool confirmed)
    {
        int outerBlocks = Math.Max(1, (int)Math.Ceiling(evidence.Length * 0.42));
        int first = fromLeft ? 0 : evidence.Length - outerBlocks;
        int last = fromLeft ? outerBlocks : evidence.Length;
        var matches = new List<int>();
        for (int i = first; i < last; i++)
        {
            if (IsFixed(evidence[i], confirmed, strongStructure: false))
                matches.Add(i);
        }

        // A real fixed sidebar has evidence spread across several columns. A lone fixed
        // button near an edge must not crop a large part of the document.
        if (matches.Count < 4 || (matches[^1] - matches[0] + 1) * SideBlockWidth < 24)
            return 0;

        int inset = fromLeft
            ? Math.Min(width, (matches[^1] + 4) * SideBlockWidth)
            : Math.Min(width, width - Math.Max(0, (matches[0] - 3) * SideBlockWidth));
        return Math.Min(inset, (int)(width * 0.45));
    }

    private static int FindBottomOverlay(Evidence[] evidence, int columns, int rows,
        int width, int height, int leftSide, int rightSide, bool confirmed)
    {
        int startRow = (int)(rows * 0.60);
        int bottomReach = Math.Max(2, (72 + TileHeight - 1) / TileHeight);
        var fixedTile = new bool[evidence.Length];
        for (int ty = startRow; ty < rows; ty++)
        {
            for (int tx = 0; tx < columns; tx++)
            {
                int x = tx * TileWidth;
                if (x < leftSide || x >= width - rightSide)
                    continue;
                fixedTile[ty * columns + tx] = IsFixed(evidence[ty * columns + tx], confirmed,
                    strongStructure: true);
            }
        }

        int top = height;
        var seen = new bool[fixedTile.Length];
        for (int ty = startRow; ty < rows; ty++)
        {
            for (int tx = 0; tx < columns; tx++)
            {
                int root = ty * columns + tx;
                if (!fixedTile[root] || seen[root])
                    continue;

                int count = 0, componentTop = ty, componentBottom = ty;
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue((tx, ty));
                seen[root] = true;
                while (queue.Count > 0)
                {
                    var (x, y) = queue.Dequeue();
                    count++;
                    componentTop = Math.Min(componentTop, y);
                    componentBottom = Math.Max(componentBottom, y);
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= columns || ny < startRow || ny >= rows)
                            continue;
                        int ni = ny * columns + nx;
                        if (!seen[ni] && fixedTile[ni])
                        {
                            seen[ni] = true;
                            queue.Enqueue((nx, ny));
                        }
                    }
                }

                if (count >= 2 && rows - 1 - componentBottom <= bottomReach)
                    top = Math.Min(top, componentTop * TileHeight);
            }
        }

        if (top == height)
            return 0;
        return Math.Min(height / 3, height - Math.Max(0, top - Step));
    }

    private static bool IsFixed(Evidence value, bool confirmed, bool strongStructure)
    {
        if (value.Samples == 0)
            return false;
        double same = value.Same / (double)value.Samples;
        double shifted = value.ShiftedSamples == 0 ? -1 : value.Shifted / (double)value.ShiftedSamples;
        int requiredStructure = strongStructure
            ? 1
            : Math.Max(1, value.Samples / 80);
        double requiredStableRatio = strongStructure ? 0.50 : StableRatio;
        double requiredShiftAdvantage = strongStructure ? 0.25 : ShiftAdvantage;
        if (value.Structured < requiredStructure || same < requiredStableRatio)
            return false;
        return !confirmed || shifted >= 0 && same - shifted >= requiredShiftAdvantage;
    }

    private static bool Similar(byte[] a, byte[] b, int i) =>
        Math.Abs(a[i] - b[i]) <= Tolerance &&
        Math.Abs(a[i + 1] - b[i + 1]) <= Tolerance &&
        Math.Abs(a[i + 2] - b[i + 2]) <= Tolerance;

    private static int Luma(byte[] bytes, int i) =>
        (bytes[i + 2] * 77 + bytes[i + 1] * 151 + bytes[i] * 28) >> 8;

    private struct Evidence
    {
        public int Same;
        public int Shifted;
        public int ShiftedSamples;
        public int Samples;
        public int Structured;

        public void Add(bool same, bool shifted, bool hasShift, bool structured)
        {
            Samples++;
            if (same) Same++;
            if (hasShift)
            {
                ShiftedSamples++;
                if (shifted) Shifted++;
            }
            if (structured) Structured++;
        }
    }
}

/// <summary>Removes repeated copies of a fixed sidebar below its first viewport instance.</summary>
internal static class StationaryViewportCompositor
{
    public static void RemoveRepeatedSidebars(SD.Bitmap stitched, SD.Bitmap? leftSidebar,
        SD.Bitmap? rightSidebar, int firstViewportHeight)
    {
        if (leftSidebar is null && rightSidebar is null)
            return;
        using var graphics = SD.Graphics.FromImage(stitched);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        int fillStart = Math.Min(firstViewportHeight, stitched.Height);
        if (leftSidebar is not null)
        {
            using var brush = new SD.SolidBrush(DominantColor(leftSidebar));
            graphics.FillRectangle(brush, 0, fillStart, leftSidebar.Width,
                stitched.Height - fillStart);
            DrawSidebarOnce(graphics, leftSidebar, 0, stitched.Height);
        }
        if (rightSidebar is not null)
        {
            int x = Math.Max(0, stitched.Width - rightSidebar.Width);
            using var brush = new SD.SolidBrush(DominantColor(rightSidebar));
            graphics.FillRectangle(brush, x, fillStart, rightSidebar.Width,
                stitched.Height - fillStart);
            DrawSidebarOnce(graphics, rightSidebar, x, stitched.Height);
        }
    }

    private static void DrawSidebarOnce(SD.Graphics graphics, SD.Bitmap sidebar, int x, int canvasHeight)
    {
        int height = Math.Min(sidebar.Height, canvasHeight);
        graphics.DrawImage(sidebar,
            new SD.Rectangle(x, 0, sidebar.Width, height),
            new SD.Rectangle(0, 0, sidebar.Width, height),
            SD.GraphicsUnit.Pixel);
    }

    private static SD.Color DominantColor(SD.Bitmap bitmap)
    {
        var histogram = new Dictionary<int, int>();
        for (int y = 0; y < bitmap.Height; y += 4)
        for (int x = 0; x < bitmap.Width; x += 4)
        {
            SD.Color color = bitmap.GetPixel(x, y);
            int key = ((color.R >> 3) << 10) | ((color.G >> 3) << 5) | (color.B >> 3);
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
        }
        if (histogram.Count == 0)
            return SD.Color.Transparent;
        int winner = histogram.MaxBy(pair => pair.Value).Key;
        return SD.Color.FromArgb(255,
            ((winner >> 10) & 31) * 8 + 4,
            ((winner >> 5) & 31) * 8 + 4,
            (winner & 31) * 8 + 4);
    }
}
