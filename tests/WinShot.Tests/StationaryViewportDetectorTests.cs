using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>Sanitized off-screen fixtures for a fixed inspector plus a fixed bottom affordance.</summary>
public class StationaryViewportDetectorTests
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Sidebar = 144;

    [Fact]
    public void DetectsFixedRightPanelAndInteriorBottomArrow()
    {
        foreach (int offset in new[] { 51, 59, 73, 87 })
        {
            using var previous = MakeFrame(0);
            using var current = MakeFrame(offset);
            StationaryViewportRegions regions = StationaryViewportDetector.Detect(previous, current, offset);

            Assert.Equal(0, regions.LeftSide);
            Assert.Equal(Sidebar, regions.RightSide);
            Assert.True(regions.RightMatchInset > regions.RightSide);
            Assert.InRange(regions.BottomOverlay, 20, 64);
        }
    }

    [Fact]
    public void ProvisionalSideCropLetsTheMovingDocumentDetermineOffset()
    {
        using var previous = MakeFrame(0);
        using var current = MakeFrame(73);
        StationaryViewportRegions provisional = StationaryViewportDetector.Detect(previous, current);
        Assert.True(provisional.RightSide >= Sidebar - 24, $"right={provisional.RightSide}");

        FrameSignature a = FrameSignature.Build(previous,
            provisional.LeftMatchInset, provisional.RightMatchInset);
        FrameSignature b = FrameSignature.Build(current,
            provisional.LeftMatchInset, provisional.RightMatchInset);
        Assert.Equal(73, ScrollMatcher.FindOffset(a, b, 0, 52));
    }

    [Fact]
    public void MovingDocumentEdgesAreNotMisclassifiedAsSidebars()
    {
        foreach (int offset in new[] { 37, 51, 73, 111 })
        {
            using var previous = MakeFrame(0, sidebar: 0);
            using var current = MakeFrame(offset, sidebar: 0);
            StationaryViewportRegions regions = StationaryViewportDetector.Detect(previous, current, offset);
            Assert.Equal(0, regions.LeftSide);
            Assert.Equal(0, regions.RightSide);
        }
    }

    [Fact]
    public void CroppedCanvasProfileRelocksAfterAFrameJump()
    {
        const int footer = 36;
        using var first = MakeFrame(0);
        using var second = MakeFrame(73);
        using var jumpedBack = MakeFrame(20);
        FrameSignature a = FrameSignature.Build(first, 0, Sidebar);
        FrameSignature b = FrameSignature.Build(second, 0, Sidebar);
        FrameSignature c = FrameSignature.Build(jumpedBack, 0, Sidebar);
        var canvas = new CanvasProfile();
        canvas.Append(a, 0, Height - footer);
        canvas.Append(b, Height - footer - 73, 73);

        CanvasLock? result = ScrollMatcher.LocateInCanvas(canvas, c, int.MaxValue, 0, footer);

        Assert.NotNull(result);
        Assert.Equal(20, result.Value.Position);
    }

    [Fact]
    public void CompositorKeepsSidebarOnceAndRemovesRepeatedCopiesBelowFirstViewport()
    {
        using var first = MakeFrame(0);
        using var stitched = new SD.Bitmap(Width, Height + 220, PixelFormat.Format32bppArgb);
        using (var graphics = SD.Graphics.FromImage(stitched))
        {
            graphics.DrawImageUnscaled(first, 0, 0);
            graphics.DrawImageUnscaled(first, 0, 220);
        }
        using var rightSidebar = first.Clone(
            new SD.Rectangle(Width - Sidebar, 0, Sidebar, Height),
            PixelFormat.Format32bppArgb);

        StationaryViewportCompositor.RemoveRepeatedSidebars(stitched, null, rightSidebar, Height);

        Assert.True(CountMarkerPixels(stitched, 0, Height) > 20);
        Assert.Equal(0, CountMarkerPixels(stitched, Height, stitched.Height));
        AssertSidebarEquals(first, stitched);
    }

    [Fact]
    public async Task CaptureStitchesMovingDocumentAndPlacesFixedRegionsOnce()
    {
        int[] positions = { 0, 73, 151, 233, 320 };
        using var stitched = await RunCapture(positions);

        Assert.NotNull(stitched);
        Assert.Equal(positions[^1] + Height, stitched!.Height);
        for (int y = 0; y < stitched.Height - 48; y += 37)
            Assert.Equal(DocumentColor(101, y).ToArgb(), stitched.GetPixel(101, y).ToArgb());

        // The byte-static sidebar is cropped out by whole-run evidence (CleanShot
        // behavior); document geometry and the fixed arrow must survive.
        Assert.InRange(stitched.Width, 300, Width - Sidebar);
        AssertAdjacentMovingStrip(stitched);
        int firstArrow = FindFirstRedRow(stitched);
        Assert.InRange(firstArrow, stitched.Height - 48, stitched.Height - 1);

        string? evidenceDirectory = Environment.GetEnvironmentVariable(
            "WINSHOT_STATIONARY_SCROLL_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Directory.CreateDirectory(evidenceDirectory);
            using var first = MakeFrame(positions[0]);
            using var final = MakeFrame(positions[^1]);
            first.Save(Path.Combine(evidenceDirectory, "frame-first.png"), ImageFormat.Png);
            final.Save(Path.Combine(evidenceDirectory, "frame-final.png"), ImageFormat.Png);
            stitched.Save(Path.Combine(evidenceDirectory, "stitched-fixed-regions-once.png"),
                ImageFormat.Png);
        }
    }

    [Fact]
    public async Task RepeatedIrregularCapturesDoNotDuplicateStationaryRegions()
    {
        for (int run = 0; run < 10; run++)
        {
            int a = 51 + run * 2;
            int b = a + 67 + run;
            int c = b + 59 + run * 3;
            int d = c + 81 - run;
            int[] positions = { 0, a, b, c, d };
            using var stitched = await RunCapture(positions);

            Assert.Equal(d + Height, stitched.Height);
            Assert.Equal(0, CountMarkerPixels(stitched, Height, stitched.Height));
            Assert.InRange(FindFirstRedRow(stitched), stitched.Height - 48, stitched.Height - 1);
        }
    }

    [Fact]
    public async Task FixedSidebarRecoversAfterMissReverseAndRelock()
    {
        int[] positions = { 0, 73, 800, 20, 73, 151, 233, 320 };
        using var stitched = await RunCapture(positions);
        using var first = MakeFrame(0);

        Assert.Equal(320 + Height, stitched.Height);
        AssertAdjacentMovingStrip(stitched);
        for (int y = 0; y < stitched.Height - 48; y += 37)
            Assert.Equal(DocumentColor(101, y).ToArgb(), stitched.GetPixel(101, y).ToArgb());
        Assert.InRange(FindFirstRedRow(stitched), stitched.Height - 48, stitched.Height - 1);
    }

    [Fact]
    public void FullHdAnalysisStaysInteractive()
    {
        using var previous = MakeFrame(0, 1725, 712, 390);
        using var current = MakeFrame(137, 1725, 712, 390);
        _ = StationaryViewportDetector.Detect(previous, current, 137);
        long start = Stopwatch.GetTimestamp();
        StationaryViewportRegions result = StationaryViewportDetector.Detect(previous, current, 137);
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        Assert.True(result.RightSide > 300, $"right={result.RightSide}");
        Assert.InRange(milliseconds, 0, 75);
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

    private static void AssertSidebarEquals(SD.Bitmap expectedFrame, SD.Bitmap actual)
    {
        for (int y = 0; y < Height; y++)
        for (int x = Width - Sidebar; x < Width; x++)
            Assert.Equal(expectedFrame.GetPixel(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb());
    }

    private static void AssertAdjacentMovingStrip(SD.Bitmap actual)
    {
        // The static sidebar (and its pad) may be cropped from the output — verify the
        // document columns adjacent to whatever the output's right edge now is.
        int edge = Math.Min(actual.Width, Width - Sidebar);
        for (int y = Height + 11; y < actual.Height - 64; y += 31)
        for (int x = edge - 24; x < edge; x += 7)
            Assert.Equal(DocumentColor(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb());
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

    private static async Task<SD.Bitmap> RunCapture(IReadOnlyList<int> positions)
    {
        using var cancellation = new CancellationTokenSource();
        int index = 0;
        SD.Bitmap Grab(SD.Rectangle _)
        {
            if (index >= positions.Count)
            {
                cancellation.Cancel();
                return MakeFrame(positions[^1]);
            }
            return MakeFrame(positions[index++]);
        }

        return (await ScrollingCaptureService.RunAsync(
            new SD.Rectangle(0, 0, Width, Height), ScrollDirection.Vertical,
            () => false, _ => { }, _ => { }, cancellation.Token,
            preview: null, frameSource: Grab))!;
    }

    private static SD.Bitmap MakeFrame(int top, int width = Width, int height = Height, int sidebar = Sidebar)
    {
        var bitmap = new SD.Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new SD.Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    SD.Color color;
                    if (x >= width - sidebar)
                    {
                        bool marker = ((y / 19) % 3 == 0 && x % 37 is >= 4 and <= 10) ||
                            (y % 61 is >= 8 and <= 12 && x < width - 12);
                        color = marker ? SD.Color.FromArgb(255, 32, 214, 105) : SD.Color.FromArgb(255, 35, 38, 43);
                    }
                    else
                    {
                        int documentY = top + y;
                        color = DocumentColor(x, documentY);
                    }

                    // Stationary interior affordance near the bottom. It is deliberately not
                    // full-width, so the legacy row-band detector cannot see it.
                    int arrowX = (width - sidebar) / 2;
                    if (y >= height - 32 && y < height - 14 &&
                        Math.Abs(x - arrowX) <= (y - (height - 32)) / 2 + 2)
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

    private static SD.Color DocumentColor(int x, int documentY)
    {
        int hash = Mix(x, documentY);
        if (documentY % 29 is >= 3 and <= 6 && x % 113 < 74)
            return SD.Color.FromArgb(255, 150, 154, 162);
        return SD.Color.FromArgb(255, 22 + (hash & 63),
            25 + ((hash >> 8) & 63), 30 + ((hash >> 16) & 63));
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

