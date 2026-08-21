using System.Threading;
using System.Windows.Media.Imaging;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class DragPreviewTests
{
    [Theory]
    [InlineData(1600, 800, 1.0, 180, 90)]   // long edge clamped, aspect kept
    [InlineData(1000, 1000, 1.5, 270, 270)] // thumbnail is built in physical pixels
    [InlineData(20, 1, 1.0, 20, 1)]         // never upscaled, never collapses to zero
    public void CreateThumbnail_ScalesToLongEdge(int w, int h, double dpi, int expectW, int expectH)
    {
        using var source = new SD.Bitmap(w, h);
        var thumb = CreateThumbnailOnSta(source, dpi);

        Assert.Equal(expectW, thumb.PixelWidth);
        Assert.Equal(expectH, thumb.PixelHeight);
        Assert.True(thumb.IsFrozen);
    }

    [Fact]
    public void CreateThumbnail_FlattensTransparencyOntoWhite()
    {
        using var source = new SD.Bitmap(10, 10, SD.Imaging.PixelFormat.Format32bppArgb);
        var thumb = CreateThumbnailOnSta(source, 1.0);

        var pixel = new byte[4];
        thumb.CopyPixels(new System.Windows.Int32Rect(5, 5, 1, 1), pixel, 4, 0);
        Assert.Equal(255, pixel[3]); // opaque, not a see-through (or black) block
    }

    [Fact]
    public void ShowAndHide_SurviveWithoutADrag()
    {
        RunSta(() =>
        {
            using var source = new SD.Bitmap(400, 200);
            var thumb = DragPreview.CreateThumbnail(source, 1.0);

            var preview = new DragPreview();
            preview.Show(thumb, 1.0);
            preview.MoveToCursor();
            preview.Hide();
            preview.Dispose();
            preview.Dispose(); // idempotent
        });
    }

    private static BitmapSource CreateThumbnailOnSta(SD.Bitmap source, double dpi)
    {
        BitmapSource? result = null;
        RunSta(() => result = DragPreview.CreateThumbnail(source, dpi));
        return result!;
    }

    /// <summary>
    /// No Application instance here on purpose — xUnit runs test classes in parallel and
    /// constructing one races other STA tests over the WPF Application singleton.
    /// </summary>
    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
