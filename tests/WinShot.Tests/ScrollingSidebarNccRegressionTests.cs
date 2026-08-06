using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Regression fixtures for the live 2026-08-04 failure: a browser-like window where the
/// document pane re-rasterizes on every scroll position (rows never byte-identical, so the
/// exact tier fails and everything rides the NCC tier) while a right sidebar and a floating
/// bottom arrow stay pixel-static. The old matcher accepted +1..17px micro-offsets voted in
/// by the sidebar, re-stamping it down the stitch, and the footer band ratcheted to a third
/// of the frame. These tests pin the fixed behavior end to end.
/// </summary>
public class ScrollingSidebarNccRegressionTests
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Sidebar = 144;

    [Fact]
    public void StaticSidebarCannotCarryAnNccOffset()
    {
        // Antialiased document + byte-static sidebar: the offset must come from the moving
        // pane (135) — never from a sidebar-driven micro-lag.
        using var previous = MakeJitteredFrame(0, seed: 1);
        using var current = MakeJitteredFrame(135, seed: 2);
        FrameSignature a = FrameSignature.Build(previous);
        FrameSignature b = FrameSignature.Build(current);

        int offset = ScrollMatcher.FindOffset(a, b, 0, 40);

        Assert.True(offset == 0 || offset > 100,
            $"micro-offset {offset} accepted — static sidebar carried the match");
    }

    [Fact]
    public async Task JitteredCapture_NoMicroAppends_SidebarOnce_ArrowAtBottom()
    {
        int[] positions = { 0, 135, 270, 405, 540 };
        using var stitched = await RunCapture(positions);

        // No micro-append inflation and no runaway footer trim: the stitch must land within
        // one seam's slack of the true document height.
        Assert.InRange(stitched.Height, positions[^1] + Height - 48, positions[^1] + Height);

        // The byte-static sidebar never changes all run — whole-run evidence CROPS it out
        // of the output entirely (CleanShot behavior): no sidebar pixels anywhere.
        Assert.Equal(Width - Sidebar, stitched.Width);

        // The floating arrow survives exactly once, near the true bottom.
        int firstArrow = FindFirstRedRow(stitched);
        Assert.InRange(firstArrow, stitched.Height - 64, stitched.Height - 1);
    }

    [Fact]
    public async Task JitteredCapture_WithRelockStart_StillLocksSidebar()
    {
        // Mirrors the live session: the first accepted step arrives via canvas re-lock
        // (a small +31 jump) instead of a clean first pair, then normal ~135px steps.
        int[] positions = { 0, 31, 166, 301, 436 };
        using var stitched = await RunCapture(positions);

        Assert.InRange(stitched.Height, positions[^1] + Height - 48, positions[^1] + Height);
        Assert.Equal(Width - Sidebar, stitched.Width); // static sidebar cropped
        Assert.InRange(FindFirstRedRow(stitched), stitched.Height - 64, stitched.Height - 1);
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
                return MakeJitteredFrame(positions[^1], seed: positions.Count - 1);
            }
            int i = index++;
            return MakeJitteredFrame(positions[i], seed: i);
        }

        return (await ScrollingCaptureService.RunAsync(
            new SD.Rectangle(0, 0, Width, Height), ScrollDirection.Vertical,
            () => false, _ => { }, _ => { }, cancellation.Token,
            preview: null, frameSource: Grab))!;
    }

    /// <summary>
    /// Like the stationary-detector fixture, but the document pane gets a per-frame ±2 luma
    /// jitter (deterministic in the seed) simulating sub-pixel AA re-rasterization: rows are
    /// never byte-identical across frames, so tier-1 exact matching always fails. The
    /// sidebar and the floating arrow stay byte-static.
    /// </summary>
    private static SD.Bitmap MakeJitteredFrame(int top, int seed)
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
                    if (x >= Width - Sidebar)
                    {
                        bool marker = ((y / 19) % 3 == 0 && x % 37 is >= 4 and <= 10) ||
                            (y % 61 is >= 8 and <= 12 && x < Width - 12);
                        color = marker ? SD.Color.FromArgb(255, 32, 214, 105) : SD.Color.FromArgb(255, 35, 38, 43);
                    }
                    else
                    {
                        int documentY = top + y;
                        color = DocumentColor(x, documentY);
                        int jitter = (Mix(x ^ (seed * 7919), documentY) & 3) - 1; // -1..2
                        color = SD.Color.FromArgb(255,
                            Math.Clamp(color.R + jitter, 0, 255),
                            Math.Clamp(color.G + jitter, 0, 255),
                            Math.Clamp(color.B + jitter, 0, 255));
                    }

                    int arrowX = (Width - Sidebar) / 2;
                    if (y >= Height - 32 && y < Height - 14 &&
                        Math.Abs(x - arrowX) <= (y - (Height - 32)) / 2 + 2)
                        color = SD.Color.FromArgb(255, 242, 76, 76);

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

    /// <summary>
    /// Text-like aperiodic document: lines of varying "ink" density and word length keyed to
    /// a per-line hash. Periodic patterns (stripes every N px) would trip the matcher's
    /// unique-peak gate by design — real pages don't repeat, and neither does this.
    /// </summary>
    private static SD.Color DocumentColor(int x, int documentY)
    {
        // Two-level line layout so neither line height nor line rhythm is periodic:
        // 97px segments, each with its own line height (13..20px). A constant line height
        // would put rival correlation peaks one line apart and trip the unique-peak gate.
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

    private static int CountMarkerPixels(SD.Bitmap bitmap, int startY, int endY)
    {
        int count = 0;
        for (int y = startY; y < endY; y++)
        for (int x = bitmap.Width - Sidebar; x < bitmap.Width; x++)
        {
            SD.Color color = bitmap.GetPixel(x, y);
            if (color.G > 180 && color.R < 80 && color.B < 120)
                count++;
        }
        return count;
    }

    private static int FindFirstRedRow(SD.Bitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width - Sidebar; x++)
        {
            SD.Color color = bitmap.GetPixel(x, y);
            if (color.R > 220 && color.G < 100 && color.B < 100)
                return y;
        }
        return -1;
    }
}
