using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>Fixed bands at the leading and trailing edges of the scroll axis.</summary>
internal readonly record struct FixedBands(int Leading, int Trailing);

/// <summary>
/// Detects viewport chrome that stays in place while the content beneath it scrolls.
/// The detector samples pixels directly and measures each row/column independently, so
/// a small changing area (caret, spinner, timestamp) does not split an otherwise fixed band.
/// </summary>
internal static class FixedRegionDetector
{
    private const int SampleStep = 4;
    private const int ChannelTolerance = 12;
    private const double StableRatio = 0.88;
    private const double ShiftAdvantage = 0.08;
    private const int MinimumBand = 4;

    public static FixedBands DetectVertical(SD.Bitmap previous, SD.Bitmap current, int scrollOffset = 0) =>
        Detect(previous, current, horizontal: false, scrollOffset);

    public static FixedBands DetectHorizontal(SD.Bitmap previous, SD.Bitmap current, int scrollOffset = 0) =>
        Detect(previous, current, horizontal: true, scrollOffset);

    private static FixedBands Detect(SD.Bitmap previous, SD.Bitmap current, bool horizontal, int scrollOffset)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
            return default;

        int axisLength = horizontal ? current.Width : current.Height;
        int crossLength = horizontal ? current.Height : current.Width;
        if (axisLength < MinimumBand * 3 || crossLength < 2)
            return default;

        var same = new int[axisLength];
        var shifted = new int[axisLength];
        var shiftedSamples = new int[axisLength];
        var samples = new int[axisLength];
        var edgeSamples = new int[axisLength];
        var lumaSum = new long[axisLength];

        var prevData = previous.LockBits(new SD.Rectangle(0, 0, previous.Width, previous.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var currData = current.LockBits(new SD.Rectangle(0, 0, current.Width, current.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (horizontal)
                SampleColumns(prevData, currData, current.Width, current.Height, scrollOffset,
                    same, shifted, shiftedSamples, samples, edgeSamples, lumaSum);
            else
                SampleRows(prevData, currData, current.Width, current.Height, scrollOffset,
                    same, shifted, shiftedSamples, samples, edgeSamples, lumaSum);
        }
        finally
        {
            current.UnlockBits(currData);
            previous.UnlockBits(prevData);
        }

        var fixedLine = new bool[axisLength];
        var structured = new bool[axisLength];
        var mean = new double[axisLength];
        for (int i = 0; i < axisLength; i++)
        {
            if (samples[i] == 0)
                continue;
            double sameRatio = same[i] / (double)samples[i];
            double shiftedRatio = shiftedSamples[i] == 0 ? -1 : shifted[i] / (double)shiftedSamples[i];
            fixedLine[i] = sameRatio >= StableRatio &&
                (scrollOffset <= 0 || shiftedRatio < 0 || sameRatio - shiftedRatio >= ShiftAdvantage);
            mean[i] = lumaSum[i] / (double)samples[i];
            structured[i] = edgeSamples[i] >= Math.Max(1, samples[i] / 100);
        }

        // A solid toolbar can still carry structure across the scroll axis (border/shadow).
        for (int i = 1; i < axisLength; i++)
        {
            if (Math.Abs(mean[i] - mean[i - 1]) >= 4)
            {
                structured[i] = true;
                structured[i - 1] = true;
            }
        }

        int maximum = axisLength / 3;
        int leading = FindEdgeBand(fixedLine, structured, maximum, fromLeading: true);
        int trailing = FindEdgeBand(fixedLine, structured, maximum, fromLeading: false);
        if (leading + trailing > axisLength - 24)
            return default; // too little scrollable content remains; classification is ambiguous
        return new FixedBands(leading, trailing);
    }

    private static void SampleRows(BitmapData prev, BitmapData curr, int width, int height, int offset,
        int[] same, int[] shifted, int[] shiftedSamples, int[] samples, int[] edges, long[] lumaSum)
    {
        var p = new byte[width * 4];
        var c = new byte[width * 4];
        var ps = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(prev.Scan0 + y * prev.Stride, p, 0, p.Length);
            Marshal.Copy(curr.Scan0 + y * curr.Stride, c, 0, c.Length);
            bool hasShift = offset > 0 && y + offset < height;
            if (hasShift)
                Marshal.Copy(prev.Scan0 + (y + offset) * prev.Stride, ps, 0, ps.Length);

            int priorLuma = -1;
            for (int x = 0; x < width; x += SampleStep)
            {
                int i = x * 4;
                samples[y]++;
                if (Similar(p, c, i)) same[y]++;
                if (hasShift)
                {
                    shiftedSamples[y]++;
                    if (Similar(ps, c, i)) shifted[y]++;
                }
                int luma = Luma(c, i);
                lumaSum[y] += luma;
                if (priorLuma >= 0 && Math.Abs(luma - priorLuma) >= 16) edges[y]++;
                priorLuma = luma;
            }
        }
    }

    private static void SampleColumns(BitmapData prev, BitmapData curr, int width, int height, int offset,
        int[] same, int[] shifted, int[] shiftedSamples, int[] samples, int[] edges, long[] lumaSum)
    {
        var p = new byte[width * 4];
        var c = new byte[width * 4];
        var priorLuma = new int[width];
        Array.Fill(priorLuma, -1);
        for (int y = 0; y < height; y += SampleStep)
        {
            Marshal.Copy(prev.Scan0 + y * prev.Stride, p, 0, p.Length);
            Marshal.Copy(curr.Scan0 + y * curr.Stride, c, 0, c.Length);
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                samples[x]++;
                if (Similar(p, c, i)) same[x]++;
                if (offset > 0 && x + offset < width)
                {
                    int si = (x + offset) * 4;
                    shiftedSamples[x]++;
                    if (Similar(p, c, si, i)) shifted[x]++;
                }
                int luma = Luma(c, i);
                lumaSum[x] += luma;
                if (priorLuma[x] >= 0 && Math.Abs(luma - priorLuma[x]) >= 16) edges[x]++;
                priorLuma[x] = luma;
            }
        }
    }

    private static int FindEdgeBand(bool[] fixedLine, bool[] structured, int maximum, bool fromLeading)
    {
        int lastAccepted = 0;
        int unstableRun = 0;
        int structuredLines = 0;
        int allowedGap = Math.Clamp(maximum / 20, 2, 8);
        for (int distance = 0; distance < maximum; distance++)
        {
            int i = fromLeading ? distance : fixedLine.Length - 1 - distance;
            if (fixedLine[i])
            {
                unstableRun = 0;
                lastAccepted = distance + 1;
                if (structured[i]) structuredLines++;
            }
            else if (++unstableRun > allowedGap)
            {
                break;
            }
        }
        return lastAccepted >= MinimumBand && structuredLines >= 2 ? lastAccepted : 0;
    }

    private static bool Similar(byte[] a, byte[] b, int i) => Similar(a, b, i, i);

    private static bool Similar(byte[] a, byte[] b, int ai, int bi) =>
        Math.Abs(a[ai] - b[bi]) <= ChannelTolerance &&
        Math.Abs(a[ai + 1] - b[bi + 1]) <= ChannelTolerance &&
        Math.Abs(a[ai + 2] - b[bi + 2]) <= ChannelTolerance;

    private static int Luma(byte[] bytes, int i) =>
        (bytes[i + 2] * 77 + bytes[i + 1] * 151 + bytes[i] * 28) >> 8;
}
