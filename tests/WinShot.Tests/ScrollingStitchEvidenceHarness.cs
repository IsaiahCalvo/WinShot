using System.Drawing.Imaging;
using System.IO;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Opt-in, fully synthetic evidence harness. It never captures a screen or creates a window.
/// </summary>
public class ScrollingStitchEvidenceHarness
{
    [Fact]
    public void RenderFixedRegionStitchToPng_WithPixelAssertions()
    {
        string? outputDirectory = Environment.GetEnvironmentVariable("WINSHOT_SCROLL_STITCH_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return;

        Directory.CreateDirectory(outputDirectory);
        const int header = 30, footer = 58;
        int[] positions = { 0, 23, 51, 88, 132, 181, 235 };
        using var first = ScrollingFixedRegionTests.MakeVerticalFrame(
            positions[0], header, footer, 0, translucentFooter: true, scrollbar: true);
        using var middle = ScrollingFixedRegionTests.MakeVerticalFrame(
            positions[3], header, footer, 3, translucentFooter: true, scrollbar: true);
        using var final = ScrollingFixedRegionTests.MakeVerticalFrame(
            positions[^1], header, footer, positions.Length - 1, translucentFooter: true, scrollbar: true);
        using var stitched = ScrollingFixedRegionTests.RunVertical(
            positions, header, footer, translucentFooter: true, scrollbar: true);

        ScrollingFixedRegionTests.AssertVerticalResult(stitched, positions[^1], header, footer,
            positions.Length - 1, translucentFooter: true);
        first.Save(Path.Combine(outputDirectory, "frame-01.png"), ImageFormat.Png);
        middle.Save(Path.Combine(outputDirectory, "frame-04-caret-on.png"), ImageFormat.Png);
        final.Save(Path.Combine(outputDirectory, "frame-07.png"), ImageFormat.Png);
        stitched.Save(Path.Combine(outputDirectory, "stitched-fixed-regions-once.png"), ImageFormat.Png);

        Assert.Equal(4, Directory.GetFiles(outputDirectory, "*.png").Length);
        Assert.Equal(positions[^1] + first.Height, stitched.Height);
        Assert.Equal(first.Width, stitched.Width);
    }
}
