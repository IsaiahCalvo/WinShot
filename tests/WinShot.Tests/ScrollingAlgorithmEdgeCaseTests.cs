using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>Pure bitmap coverage for difficult content inside a scrolling viewport.</summary>
public class ScrollingAlgorithmEdgeCaseTests
{
    private const int Width = 420;
    private const int Height = 300;

    [Fact]
    public void AnimatedVideoInsideDocument_DoesNotMoveTheChosenSeam()
    {
        using var previous = MakeFrame(0, 0, animatedVideo: true);
        using var current = MakeFrame(43, 1, animatedVideo: true);

        Assert.Equal(43, ScrollMatcher.FindOffset(FrameSignature.Build(previous), FrameSignature.Build(current)));
    }

    [Fact]
    public void FractionalDpiStyleRerasterization_StillFindsTheOffset()
    {
        using var previous = MakeFrame(0, 10, rasterNoise: 3);
        using var current = MakeFrame(37, 11, rasterNoise: 3);

        Assert.Equal(37, ScrollMatcher.FindOffset(FrameSignature.Build(previous), FrameSignature.Build(current)));
    }

    [Fact]
    public void NestedScrollContainer_WithFixedSidePanels_UsesMovingCenter()
    {
        using var previous = MakeFrame(0, 0, fixedSidePanels: true);
        using var current = MakeFrame(31, 1, fixedSidePanels: true);

        Assert.Equal(31, ScrollMatcher.FindOffset(FrameSignature.Build(previous), FrameSignature.Build(current)));
    }

    [Fact]
    public void LazyLoadedNewRows_DoNotDisturbTheExistingOverlap()
    {
        const int offset = 52;
        using var previous = MakeFrame(0, 0);
        using var current = MakeFrame(offset, 1, changeNewRowsAfter: Height - offset);

        Assert.Equal(offset, ScrollMatcher.FindOffset(FrameSignature.Build(previous), FrameSignature.Build(current)));
    }

    [Fact]
    public void ReflowAcrossTheOverlap_IsRejectedInsteadOfInventingASeam()
    {
        using var previous = MakeFrame(0, 0);
        using var current = MakeFrame(29, 1, reflow: true);

        Assert.Equal(0, ScrollMatcher.FindOffset(FrameSignature.Build(previous), FrameSignature.Build(current)));
    }

    [Fact]
    public void EndOfPageNoMotion_IsIdenticalAndAddsNothing()
    {
        using var previous = MakeFrame(250, 4);
        using var current = MakeFrame(250, 4);
        var a = FrameSignature.Build(previous);
        var b = FrameSignature.Build(current);

        Assert.True(ScrollMatcher.Identical(a, b));
        Assert.Equal(0, ScrollMatcher.FindOffset(a, b));
    }

    private static SD.Bitmap MakeFrame(int top, int frameIndex, bool animatedVideo = false,
        int rasterNoise = 0, bool fixedSidePanels = false, int changeNewRowsAfter = int.MaxValue,
        bool reflow = false)
    {
        var bitmap = new SD.Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new SD.Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Width * 4];
            for (int y = 0; y < Height; y++)
            {
                int documentRow = top + y;
                if (reflow)
                    documentRow += (y / 7) % 3;
                for (int x = 0; x < Width; x++)
                {
                    SD.Color color;
                    if (fixedSidePanels && (x < 62 || x >= Width - 62))
                        color = Pixel(x, y, 8_000_000);
                    else if (animatedVideo && documentRow is >= 155 and < 225 && x is >= 135 and < 285)
                        color = Pixel(x + frameIndex * 29, documentRow + frameIndex * 17, 6_000_000);
                    else
                        color = Pixel(x, documentRow, y >= changeNewRowsAfter ? 7_000_000 + frameIndex : 0);

                    if (rasterNoise > 0)
                    {
                        int n = Math.Abs(Mix(x, y, frameIndex)) % (rasterNoise * 2 + 1) - rasterNoise;
                        color = SD.Color.FromArgb(255, Clamp(color.R + n), Clamp(color.G + n), Clamp(color.B + n));
                    }
                    int i = x * 4;
                    row[i] = color.B; row[i + 1] = color.G; row[i + 2] = color.R; row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static SD.Color Pixel(int x, int y, int seed)
    {
        // Text-like horizontal structure plus aperiodic detail makes real and false peaks distinct.
        int h = Mix(x / 5, y, seed);
        int line = y % 17 < 3 ? 42 : 0;
        return SD.Color.FromArgb(255,
            Clamp(55 + (h & 0x7F) + line),
            Clamp(65 + ((h >> 8) & 0x7F) + line),
            Clamp(75 + ((h >> 16) & 0x7F) + line));
    }

    private static int Mix(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (int)(h ^ (h >> 16));
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
