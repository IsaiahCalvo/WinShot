using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>Off-screen, pixel-exact coverage for fixed/stagnant viewport regions.</summary>
public class ScrollingFixedRegionTests
{
    private const int Width = 360;
    private const int Height = 260;

    [Fact]
    public void Detector_FindsAnimatedHeaderAndFooter_WithoutTreatingScrollbarAsChrome()
    {
        using var previous = MakeVerticalFrame(0, 28, 52, frameIndex: 0, translucentFooter: true, scrollbar: true);
        using var current = MakeVerticalFrame(37, 28, 52, frameIndex: 1, translucentFooter: true, scrollbar: true);

        var bands = FixedRegionDetector.DetectVertical(previous, current, 37);

        Assert.InRange(bands.Leading, 26, 30);
        Assert.InRange(bands.Trailing, 50, 54);
        Assert.Equal(37, ImageStitcher.FindScrollOffset(previous, current, bands.Leading, bands.Trailing));
    }

    [Fact]
    public void FixedFooter_WithBlinkingCaret_IsAttachedExactlyOnce()
    {
        int[] positions = { 0, 19, 47, 83, 122, 168, 219 };
        using var stitched = RunVertical(positions, header: 0, footer: 56,
            translucentFooter: false, scrollbar: false);

        AssertVerticalResult(stitched, positions[^1], header: 0, footer: 56,
            finalFrameIndex: positions.Length - 1, translucentFooter: false);
    }

    [Fact]
    public void FixedHeaderAndTranslucentFooter_AppearOnceAroundContinuousBody()
    {
        int[] positions = { 0, 23, 51, 88, 132, 181, 235 };
        using var stitched = RunVertical(positions, header: 30, footer: 58,
            translucentFooter: true, scrollbar: true);

        AssertVerticalResult(stitched, positions[^1], header: 30, footer: 58,
            finalFrameIndex: positions.Length - 1, translucentFooter: true);
    }

    [Fact]
    public void HorizontalFixedLeftAndRight_AppearOnceAroundContinuousContent()
    {
        const int leftBand = 26, rightBand = 48;
        int[] positions = { 0, 21, 49, 82, 121, 165 };
        using (var first = MakeHorizontalFrame(positions[0], leftBand, rightBand, 0))
        using (var second = MakeHorizontalFrame(positions[1], leftBand, rightBand, 1))
        {
            FixedBands provisional = FixedRegionDetector.DetectHorizontal(first, second);
            int provisionalOffset = ImageStitcher.FindScrollOffsetHorizontal(first, second,
                provisional.Leading, provisional.Trailing);
            FixedBands refined = FixedRegionDetector.DetectHorizontal(first, second, provisionalOffset);
            Assert.True(provisionalOffset > 0 && refined.Leading > 0 && refined.Trailing > 0,
                $"provisional={provisional} offset={provisionalOffset} refined={refined}");
        }
        using var stitched = RunHorizontal(positions, leftBand, rightBand);

        Assert.NotNull(stitched);
        Assert.Equal(positions[^1] + Width, stitched!.Width);
        Assert.Equal(ChromePixel(0, Height / 2, 0).ToArgb(), stitched.GetPixel(0, Height / 2).ToArgb());
        int bodyStart = leftBand;
        int bodyEnd = leftBand + positions[^1] + Width - leftBand - rightBand;
        for (int x = bodyStart; x < bodyEnd; x += 29)
            Assert.Equal(DocumentPixel(x - leftBand, Height / 2).ToArgb(), stitched.GetPixel(x, Height / 2).ToArgb());
        Assert.Equal(ChromePixel(rightBand - 1, Height / 2, 9_000_000).ToArgb(),
            stitched.GetPixel(stitched.Width - 1, Height / 2).ToArgb());
    }

    internal static SD.Bitmap RunVertical(IReadOnlyList<int> positions, int header, int footer,
        bool translucentFooter, bool scrollbar)
    {
        using var cts = new CancellationTokenSource();
        int index = 0;
        SD.Bitmap Grab(SD.Rectangle _)
        {
            if (index >= positions.Count)
            {
                cts.Cancel();
                return MakeVerticalFrame(positions[^1], header, footer, index - 1, translucentFooter, scrollbar);
            }
            return MakeVerticalFrame(positions[index], header, footer, index++, translucentFooter, scrollbar);
        }
        return ScrollingCaptureService.RunAsync(new SD.Rectangle(0, 0, Width, Height),
            ScrollDirection.Vertical, () => false, _ => { }, _ => { }, cts.Token,
            preview: null, frameSource: Grab).GetAwaiter().GetResult()!;
    }

    private static SD.Bitmap RunHorizontal(IReadOnlyList<int> positions, int leftBand, int rightBand)
    {
        using var cts = new CancellationTokenSource();
        int index = 0;
        SD.Bitmap Grab(SD.Rectangle _)
        {
            if (index >= positions.Count)
            {
                cts.Cancel();
                return MakeHorizontalFrame(positions[^1], leftBand, rightBand, index - 1);
            }
            return MakeHorizontalFrame(positions[index], leftBand, rightBand, index++);
        }
        return ScrollingCaptureService.RunAsync(new SD.Rectangle(0, 0, Width, Height),
            ScrollDirection.Horizontal, () => false, _ => { }, _ => { }, cts.Token,
            preview: null, frameSource: Grab).GetAwaiter().GetResult()!;
    }

    internal static void AssertVerticalResult(SD.Bitmap stitched, int lastTop, int header, int footer,
        int finalFrameIndex, bool translucentFooter)
    {
        Assert.NotNull(stitched);
        Assert.Equal(lastTop + Height, stitched.Height);
        if (header > 0)
            Assert.Equal(ChromePixel(Width / 2, header / 2, 1_000_000).ToArgb(),
                stitched.GetPixel(Width / 2, header / 2).ToArgb());

        int bodyRows = lastTop + Height - header - footer;
        for (int y = 0; y < bodyRows; y += 31)
            Assert.Equal(DocumentPixel(Width / 2, y).ToArgb(), stitched.GetPixel(Width / 2, header + y).ToArgb());

        int footerStart = header + bodyRows;
        for (int fy = 0; fy < footer; fy += 11)
        {
            SD.Color expected = FooterPixel(Width / 2, fy, footer, finalFrameIndex,
                underlyingDocumentRow: lastTop + Height - header - footer + fy, translucentFooter);
            Assert.Equal(expected.ToArgb(), stitched.GetPixel(Width / 2, footerStart + fy).ToArgb());
        }
        SD.Color footerMarker = FooterPixel(Width / 2, 0, footer, finalFrameIndex,
            lastTop + Height - header - footer, translucentFooter);
        for (int y = header; y < footerStart; y++)
            Assert.NotEqual(footerMarker.ToArgb(), stitched.GetPixel(Width / 2, y).ToArgb());
    }

    internal static SD.Bitmap MakeVerticalFrame(int top, int header, int footer, int frameIndex,
        bool translucentFooter, bool scrollbar)
    {
        var bitmap = new SD.Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new SD.Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Width * 4];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    SD.Color color;
                    if (y < header)
                        color = ChromePixel(x, y, 1_000_000);
                    else if (y >= Height - footer)
                    {
                        int fy = y - (Height - footer);
                        color = FooterPixel(x, fy, footer, frameIndex,
                            top + Height - header - footer + fy, translucentFooter);
                    }
                    else
                        color = DocumentPixel(x, top + y - header);

                    if (scrollbar && x >= Width - 7 && y >= 20 + frameIndex * 5 && y < 72 + frameIndex * 5)
                        color = SD.Color.FromArgb(255, 90, 95, 103);
                    int i = x * 4;
                    row[i] = color.B; row[i + 1] = color.G; row[i + 2] = color.R; row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static SD.Bitmap MakeHorizontalFrame(int left, int leftBand, int rightBand, int frameIndex)
    {
        var bitmap = new SD.Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            SD.Color color = x < leftBand
                ? ChromePixel(x, y, 0)
                : x >= Width - rightBand
                    ? ChromePixel(x - (Width - rightBand), y, 9_000_000)
                    : DocumentPixel(left + x - leftBand, y);
            bitmap.SetPixel(x, y, color);
        }
        return bitmap;
    }

    private static SD.Color DocumentPixel(int x, int documentRow)
    {
        int h = Mix(x, documentRow, 71);
        return SD.Color.FromArgb(255, 35 + (h & 0x7F), 45 + ((h >> 8) & 0x7F), 55 + ((h >> 16) & 0x7F));
    }

    private static SD.Color ChromePixel(int x, int y, int seed)
    {
        int h = Mix(x / 7, y / 3, seed);
        return SD.Color.FromArgb(255, 160 + (h & 0x3F), 160 + ((h >> 8) & 0x3F), 160 + ((h >> 16) & 0x3F));
    }

    private static SD.Color FooterPixel(int x, int y, int footer, int frameIndex,
        int underlyingDocumentRow, bool translucent)
    {
        SD.Color chrome = ChromePixel(x, y, 5_000_000);
        // A two-pixel blinking caret changes between frames but occupies far below 12% of a row.
        if (x is >= 176 and <= 177 && y >= footer / 3 && y < footer - 8 && frameIndex % 2 != 0)
            chrome = SD.Color.FromArgb(255, 25, 25, 25);
        if (!translucent)
            return chrome;
        SD.Color body = DocumentPixel(x, underlyingDocumentRow);
        return Blend(chrome, body, 0.94);
    }

    private static SD.Color Blend(SD.Color foreground, SD.Color background, double opacity) =>
        SD.Color.FromArgb(255,
            (int)Math.Round(foreground.R * opacity + background.R * (1 - opacity)),
            (int)Math.Round(foreground.G * opacity + background.G * (1 - opacity)),
            (int)Math.Round(foreground.B * opacity + background.B * (1 - opacity)));

    private static int Mix(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (int)(h ^ (h >> 16));
        }
    }
}
