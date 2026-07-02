using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// End-to-end simulations of the scrolling-capture loop with an injected frame source
/// (no real screen access): a synthetic "document" is windowed at scripted scroll
/// positions and the final stitched bitmap is checked row-for-row against the document.
/// Covers the behaviors that can't be verified over RDP: recovery after a too-fast
/// flick (scroll back up re-locks), no duplication when scrolling up and back down,
/// and sticky-footer exclusion with a single re-attach at the end.
/// </summary>
public class ScrollingCaptureServiceTests
{
    private const int Width = 320;
    private const int FrameHeight = 400;

    private static int Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (int)((h ^ (h >> 16)) & 0x7FFFFFFF);
        }
    }

    /// <summary>Pseudo-random, aperiodic per-document-row color (realistic edge statistics).</summary>
    private static SD.Color RowColor(int docRow)
    {
        int h = Hash(docRow, 0, 99);
        return SD.Color.FromArgb(255, h & 0xFF, (h >> 8) & 0xFF, (h >> 16) & 0xFF);
    }

    /// <summary>Viewport frame showing document rows [top, top+FrameHeight), with an optional
    /// constant sticky footer pinned over the bottom <paramref name="footer"/> rows.</summary>
    private static SD.Bitmap MakeFrame(int top, int footer = 0)
    {
        var bmp = new SD.Bitmap(Width, FrameHeight, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, Width, FrameHeight),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Width * 4];
            for (int y = 0; y < FrameHeight; y++)
            {
                var c = y >= FrameHeight - footer
                    ? RowColor(5_000_000 + (y - (FrameHeight - footer))) // body-disjoint constant band
                    : RowColor(top + y);
                for (int x = 0; x < Width; x++)
                {
                    int i = x * 4;
                    row[i] = c.B; row[i + 1] = c.G; row[i + 2] = c.R; row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>
    /// Runs the capture loop in manual mode over the scripted scroll positions; when the
    /// script is exhausted the capture is finalized (Done). Returns the stitched bitmap.
    /// </summary>
    private static SD.Bitmap? RunScripted(IReadOnlyList<int> scrollPositions, int footer = 0)
    {
        using var cts = new CancellationTokenSource();
        int index = 0;
        SD.Bitmap FrameSource(SD.Rectangle _)
        {
            if (index >= scrollPositions.Count)
            {
                cts.Cancel(); // Done — finalize with the result
                return MakeFrame(scrollPositions[^1], footer);
            }
            return MakeFrame(scrollPositions[index++], footer);
        }

        return ScrollingCaptureService.RunAsync(
            new SD.Rectangle(0, 0, Width, FrameHeight),
            ScrollDirection.Vertical,
            autoScroll: () => false,
            status: _ => { },
            hint: _ => { },
            cts.Token,
            preview: null,
            frameSource: FrameSource).GetAwaiter().GetResult();
    }

    private static void AssertRowIsDocRow(SD.Bitmap stitched, int stitchedY, int docRow)
    {
        var expected = RowColor(docRow).ToArgb();
        Assert.Equal(expected, stitched.GetPixel(Width / 2, stitchedY).ToArgb());
    }

    [Fact]
    public void SlowScroll_ReconstructsContiguousDocument()
    {
        var positions = new List<int>();
        for (int top = 0; top <= 600; top += 30)
            positions.Add(top);

        using var stitched = RunScripted(positions);

        Assert.NotNull(stitched);
        Assert.Equal(600 + FrameHeight, stitched!.Height);
        for (int y = 0; y < stitched.Height; y += 97)
            AssertRowIsDocRow(stitched, y, y);
    }

    [Fact]
    public void FastFlickThenScrollBack_RecoversWithoutGapOrDuplicate()
    {
        // Slow start, then a flick far past the overlap (rows 60 → 700, zero overlap),
        // then scroll back up over captured content and descend slowly through the gap.
        var positions = new List<int> { 0, 30, 60 };
        positions.Add(700);           // flick — unlockable, must NOT stitch
        positions.Add(300);           // back up: overlaps canvas (0..460) → re-locks
        for (int top = 330; top <= 900; top += 30)
            positions.Add(top);       // steady descent re-captures the gap and beyond

        using var stitched = RunScripted(positions);

        Assert.NotNull(stitched);
        // Contiguous document rows 0..(900+FrameHeight): nothing skipped, nothing doubled.
        Assert.Equal(900 + FrameHeight, stitched!.Height);
        for (int y = 0; y < stitched.Height; y += 89)
            AssertRowIsDocRow(stitched, y, y);
    }

    [Fact]
    public void ScrollUpThenDown_NeverDuplicatesRows()
    {
        var positions = new List<int> { 0, 40, 80, 120, /* re-read: */ 40, 10, /* resume: */ 90, 150, 200 };

        using var stitched = RunScripted(positions);

        Assert.NotNull(stitched);
        Assert.Equal(200 + FrameHeight, stitched!.Height);
        for (int y = 0; y < stitched.Height; y += 53)
            AssertRowIsDocRow(stitched, y, y);
    }

    /// <summary>
    /// Simulates a user scrolling at a given velocity against the loop's WORST-CASE cadence:
    /// virtual time advances a fixed 45ms per grab (30ms poll + ~15ms processing budget), so
    /// each frame's scroll delta is exactly velocity × 45ms — deterministic and immune to CPU
    /// load from parallel tests, unlike wall-clock pacing. This is the "can a user scroll at
    /// a comfortable speed" regression test.
    /// </summary>
    private static SD.Bitmap? RunAtVelocity(Func<double, int> positionAtSeconds, int docEnd)
    {
        const double cadenceSeconds = 0.045;
        using var cts = new CancellationTokenSource();
        int grabs = 0, framesAtEnd = 0;
        SD.Bitmap FrameSource(SD.Rectangle _)
        {
            double t = grabs++ * cadenceSeconds;
            int pos = positionAtSeconds(t);
            if (pos >= docEnd && ++framesAtEnd >= 8)
                cts.Cancel(); // linger at the bottom a few frames, then Done
            return MakeFrame(Math.Min(pos, docEnd));
        }

        return ScrollingCaptureService.RunAsync(
            new SD.Rectangle(0, 0, Width, FrameHeight),
            ScrollDirection.Vertical,
            autoScroll: () => false,
            status: _ => { },
            hint: _ => { },
            cts.Token,
            preview: null,
            frameSource: FrameSource).GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(1200)]  // comfortable, steady reading-scroll
    [InlineData(3000)]  // brisk, wheel spinning
    [InlineData(5000)]  // very fast — ~225px per frame at worst-case cadence
    public void SteadyScroll_AtRealVelocity_StaysContiguous(int pxPerSecond)
    {
        const int docEnd = 3000;
        using var stitched = RunAtVelocity(t => (int)Math.Round(t * pxPerSecond), docEnd);

        Assert.NotNull(stitched);
        Assert.Equal(docEnd + FrameHeight, stitched!.Height);
        for (int y = 0; y < stitched.Height; y += 173)
            AssertRowIsDocRow(stitched, y, y);
    }

    [Fact]
    public void FlickScrolling_BurstsWithPauses_StaysContiguous()
    {
        // Flick pattern: 250ms bursts at 4000px/s separated by 350ms pauses — an aggressive
        // but realistic wheel-fling rhythm (~1000px per flick).
        const int docEnd = 4000;
        int PositionAt(double t)
        {
            const double burst = 0.25, pause = 0.35, cycle = burst + pause;
            int full = (int)(t / cycle);
            double rem = t - full * cycle;
            double moved = full * burst * 4000 + Math.Min(rem, burst) * 4000;
            return (int)Math.Round(moved);
        }
        using var stitched = RunAtVelocity(PositionAt, docEnd);

        Assert.NotNull(stitched);
        Assert.Equal(docEnd + FrameHeight, stitched!.Height);
        for (int y = 0; y < stitched.Height; y += 211)
            AssertRowIsDocRow(stitched, y, y);
    }

    [Fact]
    public void StickyFooter_ExcludedFromSeams_AttachedExactlyOnce()
    {
        const int footer = 40;
        var positions = new List<int>();
        for (int top = 0; top <= 300; top += 30)
            positions.Add(top);

        using var stitched = RunScripted(positions, footer);

        Assert.NotNull(stitched);
        // Body: rows 0..(300 + FrameHeight - footer) contiguous, then the footer once.
        int bodyRows = 300 + FrameHeight - footer;
        Assert.Equal(bodyRows + footer, stitched!.Height);
        for (int y = 0; y < bodyRows; y += 41)
            AssertRowIsDocRow(stitched, y, y);
        // Footer band sits at the very bottom…
        var footerTop = RowColor(5_000_000).ToArgb();
        Assert.Equal(footerTop, stitched.GetPixel(Width / 2, bodyRows).ToArgb());
        // …and nowhere inside the body.
        for (int y = 0; y < bodyRows; y++)
            Assert.NotEqual(footerTop, stitched.GetPixel(Width / 2, y).ToArgb());
    }
}
