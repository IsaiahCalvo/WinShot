using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>
/// Per-frame signature used by the scroll matcher: one exact FNV hash, one mean-luma
/// value and one vertical-gradient-energy value per row (each also split into three
/// column strips for consensus voting). Computed in a single LockBits pass so the
/// capture loop hashes every frame exactly once and reuses the result when the frame
/// becomes "previous".
/// </summary>
public sealed class FrameSignature
{
    public const int Strips = 3;

    public int Width { get; }
    public int Height { get; }
    /// <summary>Exact FNV-1a hash per row over the side-trimmed span (scrollbars excluded).</summary>
    public ulong[] RowHash { get; }
    /// <summary>Mean luma per row (side-trimmed). AA/ClearType noise averages out here.</summary>
    public float[] Mean { get; }
    /// <summary>Per-pixel-normalized vertical gradient energy per row: mean |L(x,y)-L(x,y-1)|.
    /// Text/edges dominate; AA perturbations are low-amplitude. ~0 on whitespace, which makes
    /// this double as the "does this row carry information" weight.</summary>
    public float[] Energy { get; }
    /// <summary>Same as <see cref="Mean"/>/<see cref="Energy"/> but per column strip (thirds
    /// of the trimmed span), for the multi-strip consensus vote.</summary>
    public float[][] StripMean { get; }
    public float[][] StripEnergy { get; }

    private FrameSignature(int width, int height, ulong[] rowHash, float[] mean, float[] energy,
        float[][] stripMean, float[][] stripEnergy)
    {
        Width = width;
        Height = height;
        RowHash = rowHash;
        Mean = mean;
        Energy = energy;
        StripMean = stripMean;
        StripEnergy = stripEnergy;
    }

    /// <summary>Side margin excluded from all row math — same formula ShareX uses; wide enough
    /// to cover a scrollbar even on narrow regions.</summary>
    public static int SideMargin(int width) => Math.Min(Math.Max(50, width / 20), width / 3);

    public static FrameSignature Build(SD.Bitmap bmp)
    {
        int width = bmp.Width, height = bmp.Height;
        int margin = SideMargin(width);
        int startX = margin, endX = width - margin;
        if (endX <= startX) { startX = 0; endX = width; }
        int span = endX - startX;
        int stripLen = Math.Max(1, span / Strips);

        var rowHash = new ulong[height];
        var mean = new float[height];
        var energy = new float[height];
        var stripMean = new float[Strips][];
        var stripEnergy = new float[Strips][];
        for (int s = 0; s < Strips; s++)
        {
            stripMean[s] = new float[height];
            stripEnergy[s] = new float[height];
        }

        var data = bmp.LockBits(new SD.Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[width * 4];
            var luma = new int[span];
            var prevLuma = new int[span];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);

                ulong hash = ImageStitcher.FnvOffsetBasis;
                for (int i = startX * 4; i < endX * 4; i++)
                    hash = (hash ^ row[i]) * ImageStitcher.FnvPrime;
                rowHash[y] = hash;

                long[] sum = new long[Strips];
                long[] grad = new long[Strips];
                for (int x = 0; x < span; x++)
                {
                    int i = (startX + x) * 4;
                    // BGRA; integer luma ≈ 0.30R + 0.59G + 0.11B
                    int l = (row[i + 2] * 77 + row[i + 1] * 151 + row[i] * 28) >> 8;
                    luma[x] = l;
                    int s = Math.Min(Strips - 1, x / stripLen);
                    sum[s] += l;
                    if (y > 0)
                        grad[s] += Math.Abs(l - prevLuma[x]);
                }

                long totalSum = 0, totalGrad = 0;
                for (int s = 0; s < Strips; s++)
                {
                    int len = s == Strips - 1 ? span - stripLen * (Strips - 1) : stripLen;
                    stripMean[s][y] = sum[s] / (float)len;
                    stripEnergy[s][y] = y == 0 ? 0f : grad[s] / (float)len;
                    totalSum += sum[s];
                    totalGrad += grad[s];
                }
                mean[y] = totalSum / (float)span;
                energy[y] = y == 0 ? 0f : totalGrad / (float)span;

                (luma, prevLuma) = (prevLuma, luma);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return new FrameSignature(width, height, rowHash, mean, energy, stripMean, stripEnergy);
    }
}

/// <summary>
/// Growable 1-D profile of the whole stitched canvas (mean + energy per row, per strip),
/// appended in lockstep with the stitched bitmap. Lets the matcher re-locate a live frame
/// anywhere in the capture ("scroll back up to continue") without keeping bitmap copies —
/// a 32000-row canvas costs ~1 MB of floats.
/// </summary>
public sealed class CanvasProfile
{
    private float[] _mean = new float[4096];
    private float[] _energy = new float[4096];
    private readonly float[][] _stripMean;
    private readonly float[][] _stripEnergy;

    public int Height { get; private set; }
    public float[] Mean => _mean;
    public float[] Energy => _energy;
    public float[][] StripMean => _stripMean;
    public float[][] StripEnergy => _stripEnergy;

    public CanvasProfile()
    {
        _stripMean = new float[FrameSignature.Strips][];
        _stripEnergy = new float[FrameSignature.Strips][];
        for (int s = 0; s < FrameSignature.Strips; s++)
        {
            _stripMean[s] = new float[4096];
            _stripEnergy[s] = new float[4096];
        }
    }

    /// <summary>Drops the bottom rows, mirroring a stitch retraction (newly detected footer).</summary>
    public void Retract(int rows) => Height = Math.Max(0, Height - rows);

    /// <summary>Appends rows [start, start+count) of <paramref name="sig"/>'s profiles.</summary>
    public void Append(FrameSignature sig, int start, int count)
    {
        EnsureCapacity(Height + count);
        Array.Copy(sig.Mean, start, _mean, Height, count);
        Array.Copy(sig.Energy, start, _energy, Height, count);
        for (int s = 0; s < FrameSignature.Strips; s++)
        {
            Array.Copy(sig.StripMean[s], start, _stripMean[s], Height, count);
            Array.Copy(sig.StripEnergy[s], start, _stripEnergy[s], Height, count);
        }
        Height += count;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _mean.Length) return;
        int cap = Math.Max(needed, _mean.Length * 2);
        Array.Resize(ref _mean, cap);
        Array.Resize(ref _energy, cap);
        for (int s = 0; s < FrameSignature.Strips; s++)
        {
            Array.Resize(ref _stripMean[s], cap);
            Array.Resize(ref _stripEnergy[s], cap);
        }
    }
}

/// <summary>
/// Result of locating a live frame inside the stitched canvas: <see cref="Position"/> is the
/// canvas row where the frame's row 0 aligns; <see cref="NewRows"/> is how many frame rows
/// extend past the canvas bottom (0 = the frame is entirely over already-captured content).
/// </summary>
public readonly record struct CanvasLock(int Position, int NewRows);

/// <summary>
/// Scroll alignment with two tiers plus verification gates. Tier 1 is the exact FNV
/// longest-distinctive-run matcher (byte-perfect for native apps that blit on scroll).
/// Tier 2 handles browsers/WPF apps that re-rasterize text at each scroll position
/// (sub-pixel AA — rows are never byte-identical): zero-mean normalized cross-correlation
/// of the per-row gradient-energy profiles, accepted only if it passes ALL gates —
/// information floor (enough non-blank rows in the overlap), unique peak (rejects
/// repetitive content), mean-luma confirmation, and 2-of-3 column-strip consensus
/// (rejects offsets carried by one region, e.g. a sticky sidebar). Tolerance only ever
/// CONFIRMS an offset found by correlation; it never searches — that inversion is what
/// sank the previous "tolerant matching" attempt.
/// </summary>
public static class ScrollMatcher
{
    /// <summary>Minimum NCC score for an acceptable offset.</summary>
    private const float MinScore = 0.60f;
    /// <summary>Best peak must beat the runner-up outside the exclusion zone by this margin.</summary>
    private const float PeakMargin = 0.05f;
    /// <summary>Rows around the best offset treated as the SAME peak (its shoulders), not a
    /// rival. Gradient profiles of text decorrelate within a few rows, so ±4 covers the peak
    /// width; repetitive content (table rows etc.) repeats at larger periods and still lands
    /// outside the zone, where it correctly kills the match.</summary>
    private const int PeakExclusionZone = 4;
    /// <summary>A row is informative when its normalized gradient energy exceeds this
    /// (plain AA noise floor is well below 1 luma unit/px).</summary>
    private const float EnergyFloor = 1.5f;
    /// <summary>Minimum informative rows required in the compared overlap.</summary>
    private const int MinInformativeRows = 8;
    /// <summary>Mean-luma profiles at the chosen offset must agree within this per row.</summary>
    private const float MeanConfirmTolerance = 3.0f;
    /// <summary>Strips voting for the offset (NCC score ≥ 0.5) required, among informative strips.</summary>
    private const int MinStripVotes = 2;
    private const float StripVoteScore = 0.50f;

    /// <summary>
    /// How many pixels the content moved up between <paramref name="previous"/> and
    /// <paramref name="current"/>; 0 when no verifiable movement was found. The bottom
    /// <paramref name="bottomBand"/> rows (sticky footer) are excluded from all comparisons.
    /// Runs the exact tier first, then the correlation tier.
    /// </summary>
    public static int FindOffset(FrameSignature previous, FrameSignature current, int bottomBand = 0)
    {
        if (previous.Height != current.Height || previous.Width != current.Width)
            return 0;

        int exact = ImageStitcher.FindScrollOffsetFromHashes(previous.RowHash, current.RowHash, 0, bottomBand);
        if (exact > 0)
            return exact;

        return FindOffsetByCorrelation(previous, current, bottomBand);
    }

    private static int FindOffsetByCorrelation(FrameSignature prev, FrameSignature curr, int bottomBand)
    {
        int height = curr.Height;
        int end = height - Math.Clamp(bottomBand, 0, height / 2);
        // Candidate d: current row y aligns with previous row y+d.
        int maxOffset = end - MinInformativeRows * 2;
        if (maxOffset < 1)
            return 0;

        float bestScore = float.MinValue, secondScore = float.MinValue;
        int bestOffset = 0;
        for (int d = 1; d < maxOffset; d++)
        {
            int overlap = end - d;
            if (CountInformative(curr.Energy, 0, overlap) < MinInformativeRows ||
                CountInformative(prev.Energy, d, overlap) < MinInformativeRows)
                continue;

            float score = Ncc(curr.Energy, 0, prev.Energy, d, overlap);
            if (score > bestScore)
            {
                if (Math.Abs(d - bestOffset) > PeakExclusionZone)
                    secondScore = bestScore;
                bestScore = score;
                bestOffset = d;
            }
            else if (Math.Abs(d - bestOffset) > PeakExclusionZone && score > secondScore)
            {
                secondScore = score;
            }
        }

        if (bestOffset == 0 || bestScore < MinScore)
            return 0;
        if (secondScore > float.MinValue && bestScore - secondScore < PeakMargin)
            return 0; // repetitive content: several offsets look alike — refuse to guess

        int overlapLen = end - bestOffset;
        if (!MeanConfirms(curr.Mean, 0, prev.Mean, bestOffset, overlapLen))
            return 0;
        if (!StripsAgree(curr, prev, bestOffset, overlapLen))
            return 0;
        return bestOffset;
    }

    /// <summary>
    /// Locates <paramref name="frame"/> inside the stitched canvas (bottom
    /// <paramref name="searchWindow"/> rows; pass int.MaxValue to search everything).
    /// Used to re-lock after a too-fast scroll ("scroll back up") and to recognize
    /// already-captured content so scrolling back never duplicates rows. Returns null
    /// unless a verified unique alignment exists. The frame's bottom
    /// <paramref name="bottomBand"/> rows are ignored (sticky footer).
    /// </summary>
    public static CanvasLock? LocateInCanvas(CanvasProfile canvas, FrameSignature frame,
        int searchWindow = int.MaxValue, int bottomBand = 0)
    {
        int frameRows = frame.Height - Math.Clamp(bottomBand, 0, frame.Height / 2);
        if (canvas.Height < MinInformativeRows * 2 || frameRows < MinInformativeRows * 2)
            return null;

        int windowStart = Math.Max(0, canvas.Height - Math.Min(searchWindow, canvas.Height));
        // p = canvas row where frame row 0 sits. The frame may overhang the canvas bottom by
        // up to frameRows - MinInformativeRows*2 rows (we still need a meaningful overlap).
        int pMax = canvas.Height - MinInformativeRows * 2;

        float bestScore = float.MinValue, secondScore = float.MinValue;
        int bestPos = -1;
        for (int p = windowStart; p <= pMax; p++)
        {
            int overlap = Math.Min(frameRows, canvas.Height - p);
            if (CountInformative(frame.Energy, 0, overlap) < MinInformativeRows ||
                CountInformative(canvas.Energy, p, overlap) < MinInformativeRows)
                continue;

            float score = Ncc(frame.Energy, 0, canvas.Energy, p, overlap);
            if (score > bestScore)
            {
                if (bestPos >= 0 && Math.Abs(p - bestPos) > PeakExclusionZone)
                    secondScore = bestScore;
                bestScore = score;
                bestPos = p;
            }
            else if (bestPos >= 0 && Math.Abs(p - bestPos) > PeakExclusionZone && score > secondScore)
            {
                secondScore = score;
            }
        }

        if (bestPos < 0 || bestScore < MinScore)
            return null;
        if (secondScore > float.MinValue && bestScore - secondScore < PeakMargin)
            return null;

        int overlapLen = Math.Min(frameRows, canvas.Height - bestPos);
        if (!MeanConfirms(frame.Mean, 0, canvas.Mean, bestPos, overlapLen))
            return null;

        int newRows = Math.Max(0, bestPos + frameRows - canvas.Height);
        return new CanvasLock(bestPos, newRows);
    }

    /// <summary>True when every row hash matches — the "user paused / nothing changed" test.</summary>
    public static bool Identical(FrameSignature a, FrameSignature b)
    {
        if (a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
        {
            if (a.RowHash[y] != b.RowHash[y])
                return false;
        }
        return true;
    }

    /// <summary>
    /// Cheap paused-frame pre-check: hashes 16 evenly spaced rows of <paramref name="bmp"/>
    /// (side-trimmed like the full signature) so a manual-mode poll can skip the full
    /// signature build while nothing moves. Any real scroll changes sampled rows, so a
    /// sparse match is safe to treat as "no movement" — content changing ONLY between the
    /// sampled rows (a blinking caret, a spinner) is exactly what we want to ignore anyway.
    /// </summary>
    public static ulong SparseProbe(SD.Bitmap bmp)
    {
        int width = bmp.Width, height = bmp.Height;
        int margin = FrameSignature.SideMargin(width);
        int startX = margin, endX = width - margin;
        if (endX <= startX) { startX = 0; endX = width; }

        var data = bmp.LockBits(new SD.Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            ulong hash = ImageStitcher.FnvOffsetBasis;
            var row = new byte[width * 4];
            int step = Math.Max(1, height / 16);
            for (int y = step / 2; y < height; y += step)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int i = startX * 4; i < endX * 4; i += 4) // one channel stride is plenty here
                    hash = (hash ^ row[i]) * ImageStitcher.FnvPrime;
                hash = (hash ^ (ulong)y) * ImageStitcher.FnvPrime;
            }
            return hash;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>True when the frame carries almost no visual information (a blank viewport —
    /// e.g. scrolling through a whitespace gap). Alignment is impossible on such frames.</summary>
    public static bool IsLowInformation(FrameSignature sig) =>
        CountInformative(sig.Energy, 0, sig.Height) < MinInformativeRows;

    private static int CountInformative(float[] energy, int start, int count)
    {
        int n = 0;
        int end = start + count;
        for (int y = start; y < end; y++)
        {
            if (energy[y] > EnergyFloor)
                n++;
        }
        return n;
    }

    /// <summary>Zero-mean normalized cross-correlation of a[ao..ao+len) vs b[bo..bo+len).</summary>
    private static float Ncc(float[] a, int ao, float[] b, int bo, int len)
    {
        float ma = 0, mb = 0;
        for (int i = 0; i < len; i++) { ma += a[ao + i]; mb += b[bo + i]; }
        ma /= len; mb /= len;

        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < len; i++)
        {
            float da = a[ao + i] - ma, db = b[bo + i] - mb;
            dot += da * db;
            na += da * da;
            nb += db * db;
        }
        if (na < 1e-6f || nb < 1e-6f)
            return -1f; // flat signal — no information to correlate
        return dot / MathF.Sqrt(na * nb);
    }

    private static bool MeanConfirms(float[] a, int ao, float[] b, int bo, int len)
    {
        // Tolerant check that only CONFIRMS the already-chosen offset: average absolute
        // mean-luma disagreement across the overlap must be small. AA re-rasterization
        // barely moves a row's mean; a wrong offset misplaces whole rows and blows past this.
        float sum = 0;
        for (int i = 0; i < len; i++)
            sum += Math.Abs(a[ao + i] - b[bo + i]);
        return sum / len <= MeanConfirmTolerance;
    }

    private static bool StripsAgree(FrameSignature curr, FrameSignature prev, int offset, int len)
    {
        int votes = 0, informative = 0;
        for (int s = 0; s < FrameSignature.Strips; s++)
        {
            if (CountInformative(curr.StripEnergy[s], 0, len) < MinInformativeRows)
                continue; // blank strip abstains
            informative++;
            if (Ncc(curr.StripEnergy[s], 0, prev.StripEnergy[s], offset, len) >= StripVoteScore)
                votes++;
        }
        // With ≤1 informative strip the full-span score already told the story.
        return informative <= 1 || votes >= Math.Min(MinStripVotes, informative);
    }
}
