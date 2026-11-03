using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class ResizeArrowCursorsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 45)]
    [InlineData(225, 45)]   // a double arrow reads the same turned half a circle
    [InlineData(315, 135)]
    [InlineData(-30, 150)]
    [InlineData(179.6, 0)]
    public void FoldsToHalfCircleWholeDegrees(double angle, int folded) =>
        Assert.Equal(folded, ResizeArrowCursors.Fold(angle));

    [Fact]
    public void GrabbersPullOutward()
    {
        // 0 TL, 1 TR, 2 BR, 3 BL, 4 T, 5 R, 6 B, 7 L, screen degrees with y down.
        Assert.Equal(new double[] { 225, 315, 45, 135, 270, 0, 90, 180 },
            Enumerable.Range(0, 8).Select(ResizeArrowCursors.PullAngleOf));
    }

    [Fact]
    public void CurFileHasOneCursorImageWithCentreHotspot()
    {
        byte[] file = ResizeArrowCursors.CurFile(new byte[18 * 18 * 4], 18, 9);
        Assert.Equal(2, BitConverter.ToUInt16(file, 2));   // type: cursor
        Assert.Equal(1, BitConverter.ToUInt16(file, 4));   // one image
        Assert.Equal(18, file[6]);
        Assert.Equal(9, BitConverter.ToUInt16(file, 10));  // hotspot x
        Assert.Equal(9, BitConverter.ToUInt16(file, 12));  // hotspot y
        Assert.Equal(22 + (int)BitConverter.ToUInt32(file, 14), file.Length);
        Assert.Equal(36, BitConverter.ToInt32(file, 22 + 8)); // XOR + AND halves
    }
}
