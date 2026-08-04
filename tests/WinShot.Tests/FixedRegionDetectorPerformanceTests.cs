using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>Measured, off-screen detector harness on a full-HD viewport.</summary>
public class FixedRegionDetectorPerformanceTests
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Iterations = 12;

    [Fact]
    public void FullHdDetector_RemainsWithinTimingAndAllocationBudget()
    {
        using var verticalA = MakeVerticalFrame(0);
        using var verticalB = MakeVerticalFrame(64);
        using var horizontalA = MakeHorizontalFrame(0);
        using var horizontalB = MakeHorizontalFrame(64);

        Assert.Equal(new FixedBands(96, 128),
            FixedRegionDetector.DetectVertical(verticalA, verticalB, 64));
        Assert.Equal(new FixedBands(160, 180),
            FixedRegionDetector.DetectHorizontal(horizontalA, horizontalB, 64));

        // Steady state needs only the refined scan. Discovery fallback adds a provisional scan.
        Measurement verticalSteady = Measure(() =>
            FixedRegionDetector.DetectVertical(verticalA, verticalB, 64));
        Measurement horizontalSteady = Measure(() =>
            FixedRegionDetector.DetectHorizontal(horizontalA, horizontalB, 64));
        Measurement verticalDiscovery = Measure(() =>
        {
            _ = FixedRegionDetector.DetectVertical(verticalA, verticalB);
            return FixedRegionDetector.DetectVertical(verticalA, verticalB, 64);
        });
        Measurement horizontalDiscovery = Measure(() =>
        {
            _ = FixedRegionDetector.DetectHorizontal(horizontalA, horizontalB);
            return FixedRegionDetector.DetectHorizontal(horizontalA, horizontalB, 64);
        });

        foreach (Measurement measurement in new[]
                 { verticalSteady, horizontalSteady, verticalDiscovery, horizontalDiscovery })
        {
            Assert.InRange(measurement.MeanMilliseconds, 0, 75);
            Assert.InRange(measurement.MaxMilliseconds, 0, 150);
            Assert.InRange(measurement.AllocatedBytesPerFrame, 0, 512 * 1024);
        }

        string? outputPath = Environment.GetEnvironmentVariable("WINSHOT_SCROLL_BENCHMARK_PATH");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                viewport = $"{Width}x{Height}",
                iterations = Iterations,
                verticalSteadyFrame = verticalSteady,
                horizontalSteadyFrame = horizontalSteady,
                verticalDiscoveryFrame = verticalDiscovery,
                horizontalDiscoveryFrame = horizontalDiscovery,
                timingBudgetMilliseconds = 75,
                allocationBudgetBytesPerFrame = 512 * 1024,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static Measurement Measure(Func<FixedBands> operation)
    {
        operation(); // JIT/warm-up outside the sample
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var milliseconds = new double[Iterations];
        long allocated = 0;
        for (int i = 0; i < Iterations; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            _ = operation();
            milliseconds[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(milliseconds);
        return new Measurement(
            Math.Round(milliseconds.Average(), 3),
            Math.Round(milliseconds[Iterations / 2], 3),
            Math.Round(milliseconds[^1], 3),
            allocated / Iterations);
    }

    private static SD.Bitmap MakeVerticalFrame(int top)
    {
        const int header = 96, footer = 128;
        return MakeBitmap((x, y) => y < header
            ? Pixel(x, y, 1_000_000)
            : y >= Height - footer
                ? Pixel(x, y - (Height - footer), 2_000_000)
                : Pixel(x, top + y - header, 0));
    }

    private static SD.Bitmap MakeHorizontalFrame(int left)
    {
        const int leftBand = 160, rightBand = 180;
        return MakeBitmap((x, y) => x < leftBand
            ? Pixel(x, y, 3_000_000)
            : x >= Width - rightBand
                ? Pixel(x - (Width - rightBand), y, 4_000_000)
                : Pixel(left + x - leftBand, y, 0));
    }

    private static SD.Bitmap MakeBitmap(Func<int, int, SD.Color> pixel)
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
                    SD.Color color = pixel(x, y);
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
        int hash = Mix(x, y, seed);
        return SD.Color.FromArgb(255, 35 + (hash & 0x7F), 45 + ((hash >> 8) & 0x7F),
            55 + ((hash >> 16) & 0x7F));
    }

    private static int Mix(int x, int y, int seed)
    {
        unchecked
        {
            uint hash = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            return (int)(hash ^ (hash >> 16));
        }
    }

    private readonly record struct Measurement(
        double MeanMilliseconds,
        double MedianMilliseconds,
        double MaxMilliseconds,
        long AllocatedBytesPerFrame);
}
