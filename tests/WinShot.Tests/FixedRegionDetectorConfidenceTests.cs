using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class FixedRegionDetectorConfidenceTests
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Offset = 20;

    [Fact]
    public void NoFixedBand_ReturnsZeroAfterOffsetRefinement()
    {
        using var previous = MakeDocumentFrame(0);
        using var current = MakeDocumentFrame(Offset);

        Assert.Equal(default, FixedRegionDetector.DetectVertical(previous, current, Offset));
    }

    [Fact]
    public void OrdinaryRepetitiveLeadingContent_IsNotConfirmedAsFixed()
    {
        using var previous = MakeDocumentFrame(0, repetitiveLeading: true);
        using var current = MakeDocumentFrame(Offset, repetitiveLeading: true);

        FixedBands refined = FixedRegionDetector.DetectVertical(previous, current, Offset);

        Assert.Equal(0, refined.Leading);
        Assert.Equal(0, refined.Trailing);
    }

    [Fact]
    public void BlankLowInformationEdges_AreNeverFixedBands()
    {
        using var previous = MakeDocumentFrame(0, blankViewportEdges: true);
        using var current = MakeDocumentFrame(Offset, blankViewportEdges: true);

        Assert.Equal(default, FixedRegionDetector.DetectVertical(previous, current));
        Assert.Equal(default, FixedRegionDetector.DetectVertical(previous, current, Offset));
    }

    [Fact]
    public void ProvisionalTrailingFalsePositive_DisappearsAfterShiftAdvantageCheck()
    {
        using var previous = MakeDocumentFrame(0, repetitiveTrailing: true);
        using var current = MakeDocumentFrame(Offset, repetitiveTrailing: true);

        FixedBands provisional = FixedRegionDetector.DetectVertical(previous, current);
        FixedBands refined = FixedRegionDetector.DetectVertical(previous, current, Offset);

        Assert.True(provisional.Trailing >= 40); // same-position scan alone is fooled
        Assert.Equal(0, refined.Trailing);       // no same-vs-shifted advantage, so no promotion
    }

    private static SD.Bitmap MakeDocumentFrame(int top, bool repetitiveLeading = false,
        bool repetitiveTrailing = false, bool blankViewportEdges = false)
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
                for (int x = 0; x < Width; x++)
                {
                    SD.Color color;
                    if (blankViewportEdges && (y < 48 || y >= Height - 48))
                        color = SD.Color.FromArgb(255, 245, 245, 245);
                    else if (repetitiveLeading && documentRow < 140)
                        color = PeriodicPixel(x, documentRow);
                    else if (repetitiveTrailing && documentRow >= 120)
                        color = PeriodicPixel(x, documentRow);
                    else
                        color = DocumentPixel(x, documentRow);
                    int i = x * 4;
                    row[i] = color.B; row[i + 1] = color.G; row[i + 2] = color.R; row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static SD.Color PeriodicPixel(int x, int documentRow)
    {
        // Offset is exactly four periods, so same-position and shifted-position scores tie.
        int h = Mix(x / 6, documentRow % 5, 900);
        return SD.Color.FromArgb(255, 50 + (h & 0x7F), 60 + ((h >> 8) & 0x7F),
            70 + ((h >> 16) & 0x7F));
    }

    private static SD.Color DocumentPixel(int x, int documentRow)
    {
        int h = Mix(x / 6, documentRow, 17);
        return SD.Color.FromArgb(255, 50 + (h & 0x7F), 60 + ((h >> 8) & 0x7F),
            70 + ((h >> 16) & 0x7F));
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
}
