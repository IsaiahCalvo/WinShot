using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>
/// Pure pixel math for scrolling capture: detects how far the content of a
/// fixed screen region scrolled between two frames, and grows the stitched
/// image with newly revealed rows. No UI dependencies; unit-testable.
/// </summary>
public static class ImageStitcher
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Minimum absolute number of matching rows required to accept an offset.</summary>
    private const int MinMatchedRows = 24;

    /// <summary>Minimum absolute number of matching columns required to accept a horizontal offset.</summary>
    private const int MinMatchedCols = 24;

    /// <summary>Minimum fraction of compared rows/columns that must match to accept an offset.</summary>
    private const double MinMatchedFraction = 0.3;

    /// <summary>
    /// Minimum number of DISTINCTIVE (non-repeated) matched rows/columns required to accept
    /// an offset. Guards against locking onto a wrong alignment dominated by identical blank
    /// rows that all share one hash (e.g. a tall whitespace band).
    /// </summary>
    private const int MinDistinctiveMatchedRows = 8;

    /// <summary>
    /// Returns how many pixels the content moved UP between <paramref name="previous"/> and
    /// <paramref name="current"/> (0 = no movement detected). Rows are compared via 64-bit
    /// hashes; the offset maximizing matched rows wins, but only if at least 30% of the
    /// compared rows match and no fewer than 24 rows match — otherwise 0 is returned.
    /// The left and right 5% of each row are excluded from the hash, where scrollbars
    /// (which move differently from content) live.
    /// </summary>
    public static int FindScrollOffset(SD.Bitmap previous, SD.Bitmap current)
        => FindScrollOffset(previous, current, 0, 0);

    /// <summary>
    /// Band-aware overload of <see cref="FindScrollOffset(SD.Bitmap, SD.Bitmap)"/>. The top
    /// <paramref name="topBand"/> rows and bottom <paramref name="bottomBand"/> rows are
    /// EXCLUDED from the comparison window so a sticky header/footer (which never moves
    /// between frames) cannot inflate the match count for a wrong offset. Pass 0/0 for the
    /// classic behavior.
    /// </summary>
    public static int FindScrollOffset(SD.Bitmap previous, SD.Bitmap current, int topBand, int bottomBand)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
            return 0;

        int height = previous.Height;
        if (height < 2 || previous.Width < 1)
            return 0;

        // The scrollable content lives between the sticky bands. Clamp defensively.
        int top = Math.Clamp(topBand, 0, height);
        int bottom = Math.Clamp(bottomBand, 0, height - top);
        int contentStart = top;
        int contentEnd = height - bottom; // exclusive
        if (contentEnd - contentStart < 2)
            return 0;

        byte[] prevSig = ComputeRowSignatures(previous);
        byte[] currSig = ComputeRowSignatures(current);

        // Distinctiveness is judged by EXACT row hashes WITHIN the current frame (no cross-frame
        // noise there): a row whose hash is unique anchors a match; rows that repeat (a blank band)
        // do not, so whitespace can't carry a wrong offset. Matching ACROSS frames is tolerant (SAD).
        ulong[] currHashes = ComputeRowHashes(current);
        bool[] currDistinctive = MarkDistinctiveRows(currHashes, contentStart, contentEnd);

        // No scroll? Every row matches its counterpart at the same position.
        bool sameEverywhere = true;
        for (int y = contentStart; y < contentEnd; y++)
            if (!RowsSimilar(prevSig, y, currSig, y)) { sameEverywhere = false; break; }
        if (sameEverywhere)
            return 0;

        // Pick the offset with the longest UNBROKEN run of SIMILAR rows that is anchored to real
        // content (ShareX-style longest-run matching, but tolerant). At the true scroll delta the
        // overlapping content lines up into one long contiguous run; wrong offsets only ever produce
        // short, scattered coincidences. The run must contain a distinctive row.
        int bestOffset = 0;
        int bestRun = 0;
        for (int offset = 1; offset < contentEnd - contentStart; offset++)
        {
            int compared = 0, run = 0, runDistinctive = 0, longestDistinctiveRun = 0;
            for (int y = contentStart; y < contentEnd; y++)
            {
                int py = y + offset;
                if (py >= contentEnd)
                    break;
                compared++;
                if (RowsSimilar(prevSig, py, currSig, y))
                {
                    run++;
                    if (currDistinctive[y])
                        runDistinctive++;
                    if (runDistinctive > 0 && run > longestDistinctiveRun)
                        longestDistinctiveRun = run; // only runs touching real content count
                }
                else
                {
                    run = 0;
                    runDistinctive = 0;
                }
            }

            if (compared < MinMatchedRows)
                break; // even a perfect match cannot reach the row floor

            if (longestDistinctiveRun > bestRun)
            {
                bestRun = longestDistinctiveRun;
                bestOffset = offset;
            }
        }

        // Accept only a substantial content-anchored run (rejects short coincidences and
        // all-whitespace frames, where no run ever contains a distinctive row).
        if (bestRun < MinMatchedRows)
            return 0;

        return bestOffset;
    }

    /// <summary>
    /// True when two same-size frames are byte-identical across the hashed (middle-90%) region
    /// of every row — used to decide a captured frame has STABILIZED (no animation / lazy-load
    /// still settling) before measuring a scroll offset. Differently-sized frames are never
    /// considered identical.
    /// </summary>
    public static bool FramesIdentical(SD.Bitmap a, SD.Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return false;

        // Tolerant: a paused frame re-rendered with sub-pixel noise still counts as "unchanged",
        // so the capture doesn't treat slow smooth scrolling as a flicker.
        byte[] sa = ComputeRowSignatures(a);
        byte[] sb = ComputeRowSignatures(b);
        for (int y = 0; y < a.Height; y++)
            if (!RowsSimilar(sa, y, sb, y))
                return false;
        return true;
    }

    /// <summary>
    /// Height (in rows) of the constant TOP band shared by <paramref name="previous"/> and
    /// <paramref name="current"/>: rows that are byte-identical from y=0 down to the first
    /// divergence. This is a sticky header (or any pinned top chrome) that does not scroll.
    /// Returns 0 when the very first row already differs.
    /// </summary>
    public static int DetectConstantTopBand(SD.Bitmap previous, SD.Bitmap current)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
            return 0;

        ulong[] prev = ComputeRowHashes(previous);
        ulong[] curr = ComputeRowHashes(current);
        int height = prev.Length;
        int band = 0;
        while (band < height && prev[band] == curr[band])
            band++;
        // A frame that is identical top-to-bottom is "no scroll", not "all-sticky".
        return band >= height ? 0 : band;
    }

    /// <summary>
    /// Height (in rows) of the constant BOTTOM band shared by <paramref name="previous"/> and
    /// <paramref name="current"/>: rows that are byte-identical from the bottom up to the
    /// first divergence. This is a sticky footer (or any pinned bottom chrome). Returns 0
    /// when the bottom-most row already differs.
    /// </summary>
    public static int DetectConstantBottomBand(SD.Bitmap previous, SD.Bitmap current)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
            return 0;

        ulong[] prev = ComputeRowHashes(previous);
        ulong[] curr = ComputeRowHashes(current);
        int height = prev.Length;
        int band = 0;
        while (band < height && prev[height - 1 - band] == curr[height - 1 - band])
            band++;
        return band >= height ? 0 : band;
    }

    /// <summary>
    /// Marks each row in [start,end) as distinctive (true) when its hash occurs exactly once
    /// in that window, or non-distinctive (false) when it repeats. Rows outside the window
    /// are false. Used to weight the offset search toward real content over blank runs.
    /// </summary>
    private static bool[] MarkDistinctiveRows(ulong[] hashes, int start, int end)
    {
        var counts = new Dictionary<ulong, int>(end - start);
        for (int y = start; y < end; y++)
            counts[hashes[y]] = counts.TryGetValue(hashes[y], out int c) ? c + 1 : 1;

        var distinctive = new bool[hashes.Length];
        for (int y = start; y < end; y++)
            distinctive[y] = counts[hashes[y]] == 1;
        return distinctive;
    }

    /// <summary>
    /// Horizontal counterpart of <see cref="FindScrollOffset"/>: returns how many pixels
    /// the content moved LEFT between <paramref name="previous"/> and <paramref name="current"/>
    /// (0 = no movement detected). Columns are compared via 64-bit hashes; the offset
    /// maximizing matched columns wins, but only if at least 30% of the compared columns
    /// match and no fewer than 24 columns match — otherwise 0 is returned. The top and
    /// bottom 5% of each column are excluded from the hash, where horizontal scrollbars
    /// (which move differently from content) live.
    /// </summary>
    public static int FindScrollOffsetHorizontal(SD.Bitmap previous, SD.Bitmap current)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
            return 0;

        int width = previous.Width;
        if (width < 2 || previous.Height < 1)
            return 0;

        ulong[] prevHashes = ComputeColumnHashes(previous);
        ulong[] currHashes = ComputeColumnHashes(current);
        if (HashesMatchAtSamePosition(prevHashes, currHashes))
            return 0;

        int bestOffset = 0;
        int bestMatches = 0;
        for (int offset = 1; offset < width; offset++)
        {
            int compared = width - offset;
            if (compared < MinMatchedCols)
                break; // even a perfect match cannot reach the column floor

            int matches = 0;
            for (int x = 0; x < compared; x++)
            {
                if (prevHashes[x + offset] == currHashes[x])
                    matches++;
            }

            if (matches >= MinMatchedCols && matches >= compared * MinMatchedFraction && matches > bestMatches)
            {
                bestMatches = matches;
                bestOffset = offset;
            }
        }

        return bestOffset;
    }

    /// <summary>
    /// Returns a NEW bitmap consisting of <paramref name="stitched"/> with the bottom
    /// <paramref name="newRows"/> rows of <paramref name="current"/> appended below it.
    /// Neither input is disposed — that is the caller's responsibility.
    /// </summary>
    public static SD.Bitmap AppendBelow(SD.Bitmap stitched, SD.Bitmap current, int newRows)
    {
        if (stitched.Width != current.Width)
            throw new ArgumentException("Bitmaps must have the same width.", nameof(current));

        newRows = Math.Clamp(newRows, 0, current.Height);
        var result = new SD.Bitmap(stitched.Width, stitched.Height + newRows, PixelFormat.Format32bppArgb);
        CopyRows(stitched, 0, stitched.Height, result, 0);
        CopyRows(current, current.Height - newRows, newRows, result, stitched.Height);
        return result;
    }

    /// <summary>
    /// Footer-aware variant of <see cref="AppendBelow(SD.Bitmap, SD.Bitmap, int)"/>. When the
    /// current frame carries a sticky footer of height <paramref name="footerBand"/>, the
    /// bottom <paramref name="footerBand"/> rows are NOT content — they are the same pinned
    /// footer that already sits once at the bottom of <paramref name="stitched"/>. This appends
    /// only the <paramref name="newRows"/> genuinely-new CONTENT rows that sit directly ABOVE
    /// the footer band, so the footer is never re-stitched into the body. With
    /// <paramref name="footerBand"/> == 0 this is identical to <see cref="AppendBelow(SD.Bitmap, SD.Bitmap, int)"/>.
    /// </summary>
    public static SD.Bitmap AppendBelowExcludingFooter(SD.Bitmap stitched, SD.Bitmap current, int newRows, int footerBand)
    {
        if (stitched.Width != current.Width)
            throw new ArgumentException("Bitmaps must have the same width.", nameof(current));

        footerBand = Math.Clamp(footerBand, 0, current.Height);
        int contentBottom = current.Height - footerBand; // first row of the footer (exclusive end of content)
        newRows = Math.Clamp(newRows, 0, contentBottom);

        var result = new SD.Bitmap(stitched.Width, stitched.Height + newRows, PixelFormat.Format32bppArgb);
        CopyRows(stitched, 0, stitched.Height, result, 0);
        // The newly revealed content rows are the last `newRows` rows of the content area,
        // i.e. the rows directly above the sticky footer: [contentBottom - newRows, contentBottom).
        CopyRows(current, contentBottom - newRows, newRows, result, stitched.Height);
        return result;
    }

    /// <summary>
    /// Returns a NEW bitmap containing the bottom <paramref name="rowCount"/> rows of
    /// <paramref name="source"/> (a sticky-footer strip lifted off the running stitch so it
    /// can be re-applied exactly once at the very end). <paramref name="source"/> is not disposed.
    /// </summary>
    public static SD.Bitmap CropBottomRows(SD.Bitmap source, int rowCount)
    {
        rowCount = Math.Clamp(rowCount, 1, source.Height);
        var result = new SD.Bitmap(source.Width, rowCount, PixelFormat.Format32bppArgb);
        CopyRows(source, source.Height - rowCount, rowCount, result, 0);
        return result;
    }

    /// <summary>
    /// Returns a NEW bitmap that is <paramref name="source"/> with its bottom
    /// <paramref name="rowCount"/> rows removed (used to lift an interior sticky footer off the
    /// running stitch). <paramref name="source"/> is not disposed.
    /// </summary>
    public static SD.Bitmap RemoveBottomRows(SD.Bitmap source, int rowCount)
    {
        rowCount = Math.Clamp(rowCount, 0, source.Height - 1);
        int kept = source.Height - rowCount;
        var result = new SD.Bitmap(source.Width, kept, PixelFormat.Format32bppArgb);
        CopyRows(source, 0, kept, result, 0);
        return result;
    }

    /// <summary>
    /// Returns a NEW bitmap consisting of <paramref name="stitched"/> with the rightmost
    /// <paramref name="newCols"/> columns of <paramref name="current"/> appended to its right.
    /// Neither input is disposed — that is the caller's responsibility.
    /// </summary>
    public static SD.Bitmap AppendRight(SD.Bitmap stitched, SD.Bitmap current, int newCols)
    {
        if (stitched.Height != current.Height)
            throw new ArgumentException("Bitmaps must have the same height.", nameof(current));

        newCols = Math.Clamp(newCols, 0, current.Width);
        var result = new SD.Bitmap(stitched.Width + newCols, stitched.Height, PixelFormat.Format32bppArgb);
        CopyColumns(stitched, 0, stitched.Width, result, 0);
        CopyColumns(current, current.Width - newCols, newCols, result, stitched.Width);
        return result;
    }

    private static bool HashesMatchAtSamePosition(ulong[] previous, ulong[] current)
    {
        if (previous.Length != current.Length)
            return false;

        return HashesMatchAtSamePosition(previous, current, 0, previous.Length);
    }

    /// <summary>True when previous and current are identical across [start,end).</summary>
    private static bool HashesMatchAtSamePosition(ulong[] previous, ulong[] current, int start, int end)
    {
        if (previous.Length != current.Length)
            return false;

        for (int i = start; i < end; i++)
        {
            if (previous[i] != current[i])
                return false;
        }
        return true;
    }

    /// <summary>One exact FNV-1a hash per row over the middle (scrollbar-trimmed) span. Used by the
    /// niche band-detection + horizontal helpers; the main vertical matcher uses tolerant signatures
    /// (<see cref="ComputeRowSignatures"/>) instead.</summary>
    private static ulong[] ComputeRowHashes(SD.Bitmap bmp)
    {
        int width = bmp.Width, height = bmp.Height;
        int margin = Math.Min(Math.Max(50, width / 20), width / 3);
        int startX = margin, endX = width - margin;
        if (endX <= startX) { startX = 0; endX = width; }

        var data = bmp.LockBits(new SD.Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var hashes = new ulong[height];
            var row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                ulong hash = FnvOffsetBasis;
                for (int i = startX * 4; i < endX * 4; i++)
                    hash = (hash ^ row[i]) * FnvPrime;
                hashes[y] = hash;
            }
            return hashes;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>Averaged samples a row's middle span is reduced to for tolerant matching.</summary>
    private const int SigBuckets = 32;
    private const int SigLen = SigBuckets * 3; // B,G,R per bucket

    /// <summary>Max average per-channel difference (0–255) for two rows to count as the SAME content.
    /// Soft threshold (SAD), not exact equality — this is what lets the same line of text match even
    /// when re-rendered with slightly different pixels at a different sub-pixel scroll offset.</summary>
    private const int RowMatchMeanTol = 16;

    /// <summary>
    /// Per-row TOLERANT signature: the row's middle (scrollbar-trimmed) span is averaged into
    /// <see cref="SigBuckets"/> buckets (B,G,R). Averaging smooths the sub-pixel/ClearType render
    /// noise that makes the same content differ byte-for-byte at different scroll offsets — the root
    /// cause of "captures a bit then stops" with exact matching. Rows are then compared by SAD with a
    /// tolerance (<see cref="RowsSimilar"/>), not exact equality.
    /// </summary>
    private static byte[] ComputeRowSignatures(SD.Bitmap bmp)
    {
        int width = bmp.Width, height = bmp.Height;
        int margin = Math.Min(Math.Max(50, width / 20), width / 3);
        int startX = margin, endX = width - margin;
        if (endX <= startX) { startX = 0; endX = width; }
        int span = endX - startX;

        var data = bmp.LockBits(new SD.Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var sig = new byte[height * SigLen];
            var row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                int baseIdx = y * SigLen;
                for (int b = 0; b < SigBuckets; b++)
                {
                    int x0 = startX + (int)((long)b * span / SigBuckets);
                    int x1 = startX + (int)((long)(b + 1) * span / SigBuckets);
                    if (x0 >= endX) x0 = endX - 1;
                    if (x1 <= x0) x1 = Math.Min(x0 + 1, endX);
                    long sumB = 0, sumG = 0, sumR = 0;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = x * 4;
                        sumB += row[i]; sumG += row[i + 1]; sumR += row[i + 2];
                    }
                    int n = x1 - x0;
                    int j = baseIdx + b * 3;
                    sig[j] = (byte)(sumB / n); sig[j + 1] = (byte)(sumG / n); sig[j + 2] = (byte)(sumR / n);
                }
            }
            return sig;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>True when row <paramref name="ra"/> of <paramref name="a"/> and row <paramref name="rb"/>
    /// of <paramref name="b"/> are the same content within tolerance (mean per-channel |diff| ≤
    /// <see cref="RowMatchMeanTol"/>).</summary>
    private static bool RowsSimilar(byte[] a, int ra, byte[] b, int rb)
    {
        int ia = ra * SigLen, ib = rb * SigLen, sad = 0;
        for (int k = 0; k < SigLen; k++)
            sad += Math.Abs(a[ia + k] - b[ib + k]);
        return sad <= RowMatchMeanTol * SigLen;
    }

    /// <summary>One FNV-1a hash per column over the middle 90% of its pixels.</summary>
    private static ulong[] ComputeColumnHashes(SD.Bitmap bmp)
    {
        int width = bmp.Width, height = bmp.Height;
        int margin = height / 20; // 5% top + bottom, where horizontal scrollbars live
        int startY = margin, endY = height - margin;
        if (endY <= startY) // degenerate height: hash the whole column
        {
            startY = 0;
            endY = height;
        }

        var data = bmp.LockBits(new SD.Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Stream row-by-row (cache friendly, one row buffer) and fold each pixel
            // into its column's running FNV-1a hash. Bytes are consumed in increasing-y
            // order per column, equivalent to hashing each column top-to-bottom.
            var hashes = new ulong[width];
            Array.Fill(hashes, FnvOffsetBasis);
            var row = new byte[width * 4];
            for (int y = startY; y < endY; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < width; x++)
                {
                    int i = x * 4;
                    ulong hash = hashes[x];
                    hash = (hash ^ row[i]) * FnvPrime;
                    hash = (hash ^ row[i + 1]) * FnvPrime;
                    hash = (hash ^ row[i + 2]) * FnvPrime;
                    hash = (hash ^ row[i + 3]) * FnvPrime;
                    hashes[x] = hash;
                }
            }
            return hashes;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static void CopyRows(SD.Bitmap source, int sourceY, int rowCount, SD.Bitmap dest, int destY)
    {
        if (rowCount <= 0) return;

        int width = source.Width;
        var src = source.LockBits(new SD.Rectangle(0, sourceY, width, rowCount),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var dst = dest.LockBits(new SD.Rectangle(0, destY, width, rowCount),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[width * 4];
                for (int y = 0; y < rowCount; y++)
                {
                    Marshal.Copy(src.Scan0 + y * src.Stride, row, 0, row.Length);
                    Marshal.Copy(row, 0, dst.Scan0 + y * dst.Stride, row.Length);
                }
            }
            finally
            {
                dest.UnlockBits(dst);
            }
        }
        finally
        {
            source.UnlockBits(src);
        }
    }

    private static void CopyColumns(SD.Bitmap source, int sourceX, int colCount, SD.Bitmap dest, int destX)
    {
        if (colCount <= 0) return;

        int height = source.Height;
        var src = source.LockBits(new SD.Rectangle(sourceX, 0, colCount, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var dst = dest.LockBits(new SD.Rectangle(destX, 0, colCount, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[colCount * 4];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(src.Scan0 + y * src.Stride, row, 0, row.Length);
                    Marshal.Copy(row, 0, dst.Scan0 + y * dst.Stride, row.Length);
                }
            }
            finally
            {
                dest.UnlockBits(dst);
            }
        }
        finally
        {
            source.UnlockBits(src);
        }
    }
}
