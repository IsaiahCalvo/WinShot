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

    /// <summary>Minimum fraction of compared rows that must match to accept an offset.</summary>
    private const double MinMatchedFraction = 0.3;

    /// <summary>
    /// Returns how many pixels the content moved UP between <paramref name="previous"/> and
    /// <paramref name="current"/> (0 = no movement detected). Rows are compared via 64-bit
    /// hashes; the offset maximizing matched rows wins, but only if at least 30% of the
    /// compared rows match and no fewer than 24 rows match — otherwise 0 is returned.
    /// The left and right 5% of each row are excluded from the hash, where scrollbars
    /// (which move differently from content) live.
    /// </summary>
    public static int FindScrollOffset(SD.Bitmap previous, SD.Bitmap current)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
            return 0;

        int height = previous.Height;
        if (height < 2 || previous.Width < 1)
            return 0;

        ulong[] prevHashes = ComputeRowHashes(previous);
        ulong[] currHashes = ComputeRowHashes(current);

        int bestOffset = 0;
        int bestMatches = 0;
        for (int offset = 1; offset < height; offset++)
        {
            int compared = height - offset;
            if (compared < MinMatchedRows)
                break; // even a perfect match cannot reach the row floor

            int matches = 0;
            for (int y = 0; y < compared; y++)
            {
                if (prevHashes[y + offset] == currHashes[y])
                    matches++;
            }

            if (matches >= MinMatchedRows && matches >= compared * MinMatchedFraction && matches > bestMatches)
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

    /// <summary>One FNV-1a hash per row over the middle 90% of its pixels.</summary>
    private static ulong[] ComputeRowHashes(SD.Bitmap bmp)
    {
        int width = bmp.Width, height = bmp.Height;
        int margin = width / 20; // 5% per side
        int startX = margin, endX = width - margin;
        if (endX <= startX) // degenerate width: hash the whole row
        {
            startX = 0;
            endX = width;
        }

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
}
