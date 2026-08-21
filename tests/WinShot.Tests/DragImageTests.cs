using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class DragImageTests
{
    [Fact]
    public void CreateThumbnail_ScalesLongEdgeDownAndKeepsAspect()
    {
        using var source = new SD.Bitmap(1600, 800);
        using var thumb = DragImage.CreateThumbnail(source, dpiScale: 1.0);

        Assert.Equal(180, thumb.Width);
        Assert.Equal(90, thumb.Height);
    }

    [Fact]
    public void CreateThumbnail_HonorsDpiScale()
    {
        using var source = new SD.Bitmap(1000, 1000);
        using var thumb = DragImage.CreateThumbnail(source, dpiScale: 1.5);

        Assert.Equal(270, thumb.Width);
    }

    [Fact]
    public void CreateThumbnail_NeverUpscalesOrCollapsesToZero()
    {
        using var small = new SD.Bitmap(20, 1);
        using var thumb = DragImage.CreateThumbnail(small, dpiScale: 1.0);

        Assert.Equal(20, thumb.Width);
        Assert.Equal(1, thumb.Height);
    }

    [Fact]
    public void CreateThumbnail_FlattensTransparencyOntoOpaqueWhite()
    {
        using var source = new SD.Bitmap(10, 10, SD.Imaging.PixelFormat.Format32bppArgb);
        using var thumb = DragImage.CreateThumbnail(source, dpiScale: 1.0);

        Assert.Equal(255, thumb.GetPixel(5, 5).A);
    }
}
