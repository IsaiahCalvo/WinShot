using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Regression fixtures for the second live 2026-08-04 failure: a chat app whose pinned
/// input bar sits ABOVE a mostly-blank bottom margin (so the contiguous-footer model can
/// never cover it) and whose smooth scrolling yields real partial offsets (+10 then +140).
/// Every append used to stamp the bar into the body; the seam-heal overwrite must leave it
/// exactly once, at the true bottom.
/// </summary>
public class ScrollingSeamHealTests
{
    private const int Width = 640;
    private const int Height = 400;
    private const int BarTop = Height - 100;    // pinned bar viewport rows [300, 336)
    private const int BarBottom = Height - 64;
    private const int BarLeft = 60;
    private const int BarRight = Width - 60;

    [Fact]
    public async Task PinnedInputBar_AboveBlankMargin_SurvivesExactlyOnce()
    {
        // Smooth-scroll pattern from the live log: partial steps completed by the next frame.
        int[] positions = { 0, 10, 150, 160, 300, 310, 450, 460, 600 };
        using var stitched = await RunCapture(positions);

        Assert.InRange(stitched.Height, positions[^1] + Height - 120, positions[^1] + Height);

        // The amber bar must appear exactly once, near the true bottom. Any stamp higher up
        // is a healed-seam regression.
        (int first, int last) = BarRowSpan(stitched);
        Assert.True(first >= 0, "pinned bar missing from the stitch");
        Assert.InRange(first, stitched.Height - 160, stitched.Height - 1);
        Assert.True(last - first < 60, $"pinned bar stamped more than once (rows {first}..{last})");
    }

    [Fact]
    public async Task SteadyScroll_KeepsDocumentContiguous()
    {
        int[] positions = { 0, 140, 280, 420, 560 };
        using var stitched = await RunCapture(positions);

        // Document rows must be contiguous: spot-check that the stitched body equals the
        // virtual document at each y (outside the final viewport's chrome zone).
        for (int y = 40; y < stitched.Height - 160; y += 53)
            Assert.Equal(DocumentColor(97, y).ToArgb(), stitched.GetPixel(97, y).ToArgb());
        (int first, int last) = BarRowSpan(stitched);
        Assert.True(first >= 0 && last - first < 60,
            $"pinned bar not exactly-once (rows {first}..{last})");
    }

    private static (int First, int Last) BarRowSpan(SD.Bitmap stitched)
    {
        int first = -1, last = -1;
        for (int y = 0; y < stitched.Height; y++)
        {
            SD.Color c = stitched.GetPixel((BarLeft + BarRight) / 2, y);
            if (c.R > 200 && c.G is > 120 and < 200 && c.B < 90)
            {
                if (first < 0) first = y;
                last = y;
            }
        }
        return (first, last);
    }

    private static async Task<SD.Bitmap> RunCapture(IReadOnlyList<int> positions)
    {
        using var cancellation = new CancellationTokenSource();
        int index = 0;
        SD.Bitmap Grab(SD.Rectangle _)
        {
            if (index >= positions.Count)
            {
                cancellation.Cancel();
                return MakeFrame(positions[^1], positions.Count - 1);
            }
            int i = index++;
            return MakeFrame(positions[i], i);
        }

        return (await ScrollingCaptureService.RunAsync(
            new SD.Rectangle(0, 0, Width, Height), ScrollDirection.Vertical,
            () => false, _ => { }, _ => { }, cancellation.Token,
            preview: null, frameSource: Grab))!;
    }

    /// <summary>
    /// Byte-exact frames (native/Electron blit — exercises the tier-1 exact matcher):
    /// scrolling document, pinned amber input bar with a vertical gradient (rows distinct so
    /// it cannot fake an alignment), a blank margin below it, and one changing status pixel
    /// row near the bottom so the contiguous-footer band stays small — like the real app.
    /// </summary>
    private static SD.Bitmap MakeFrame(int top, int frameIndex)
    {
        var bitmap = new SD.Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new SD.Rectangle(0, 0, Width, Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Width * 4];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    SD.Color color;
                    if (y >= BarTop && y < BarBottom && x >= BarLeft && x < BarRight)
                    {
                        int shade = 150 + (y - BarTop) * 2; // vertical gradient: rows distinct
                        color = SD.Color.FromArgb(255, 236, Math.Min(shade, 199), 60);
                    }
                    else if (y >= BarTop)
                    {
                        // Bottom margin: blank, except a status row whose pixels change per
                        // frame (keeps DetectConstantBottomBand from covering the bar).
                        color = y == Height - 20 && x is >= 20 and < 60
                            ? SD.Color.FromArgb(255, 90 + (frameIndex * 13 % 100), 90, 90)
                            : SD.Color.FromArgb(255, 246, 247, 249);
                    }
                    else
                    {
                        color = DocumentColor(x, top + y);
                    }

                    int i = x * 4;
                    row[i] = color.B;
                    row[i + 1] = color.G;
                    row[i + 2] = color.R;
                    row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static SD.Color DocumentColor(int x, int documentY)
    {
        int segment = documentY / 97;
        int lineHeight = 13 + (Mix(5, segment) & 7);
        int line = segment * 8 + (documentY % 97) / lineHeight;
        int lineHash = Mix(11, line);
        int rowInLine = (documentY % 97) % lineHeight;
        bool inTextBand = rowInLine >= 3 && rowInLine <= lineHeight - 5;
        int wordPeriod = 18 + (lineHash & 31);
        int wordLen = 7 + ((lineHash >> 5) & 7);
        bool ink = inTextBand && ((x + (lineHash >> 8)) % wordPeriod) < wordLen &&
            x < 80 + ((lineHash >> 12) & 255);
        if (ink)
            return SD.Color.FromArgb(255, 40 + (lineHash & 31), 44, 52);
        return SD.Color.FromArgb(255, 238, 240, 243);
    }

    private static int Mix(int x, int y)
    {
        unchecked
        {
            uint value = (uint)(x * 374761393 + y * 668265263 + 12345);
            value = (value ^ (value >> 13)) * 1274126177u;
            return (int)(value ^ (value >> 16));
        }
    }
}
