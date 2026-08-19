using System.Drawing;
using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Layout seams found from pixels: the thing that has to work when an app's accessibility
/// tree says nothing at all about its sidebar.
/// </summary>
public class PaneGridDetectorTests
{
    /// <summary>A 800x600 window: 240px dark sidebar on the left, 60px header across the top.</summary>
    static Bitmap Layout()
    {
        var bmp = new Bitmap(800, 600);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(250, 250, 250));                                    // content
        g.FillRectangle(new SolidBrush(Color.FromArgb(30, 30, 34)), 0, 0, 240, 600);   // sidebar
        g.FillRectangle(new SolidBrush(Color.FromArgb(120, 120, 130)), 240, 0, 560, 60); // header
        return bmp;
    }

    [Fact]
    public void FindsTheSidebarSeamAndHighlightsTheSidebar()
    {
        using var bmp = Layout();
        var window = new Rectangle(0, 0, 800, 600);
        var grid = PaneGridDetector.Build(bmp, window);

        var pane = PaneGridDetector.PaneAt(grid, new Point(100, 400), window);
        Assert.NotNull(pane);
        Assert.Equal(0, pane!.Value.Left);
        Assert.InRange(pane.Value.Right, 236, 244); // the seam, within the 2x downsample
        // The header rule spans most of the width, so it cuts the sidebar too: seams form a
        // grid rather than independent columns. That is the intended behaviour — the pane
        // under the cursor is the sidebar BELOW the header, not the sidebar plus a title.
        Assert.InRange(pane.Value.Top, 56, 64);
        Assert.Equal(600, pane.Value.Bottom);
    }

    [Fact]
    public void ContentAreaExcludesBothTheSidebarAndTheHeader()
    {
        using var bmp = Layout();
        var window = new Rectangle(0, 0, 800, 600);
        var grid = PaneGridDetector.Build(bmp, window);

        var pane = PaneGridDetector.PaneAt(grid, new Point(500, 400), window);
        Assert.NotNull(pane);
        Assert.InRange(pane!.Value.Left, 236, 244);
        Assert.Equal(800, pane.Value.Right);
        Assert.InRange(pane.Value.Top, 56, 64);
    }

    [Fact]
    public void APlainWindowWithNoSeamsOffersNoPane()
    {
        using var bmp = new Bitmap(800, 600);
        using (var g = Graphics.FromImage(bmp))
            g.Clear(Color.FromArgb(250, 250, 250));

        var window = new Rectangle(0, 0, 800, 600);
        var grid = PaneGridDetector.Build(bmp, window);
        Assert.Null(PaneGridDetector.PaneAt(grid, new Point(400, 300), window));
    }

    [Fact]
    public void PointsOutsideTheWindowGetNothing()
    {
        using var bmp = Layout();
        var window = new Rectangle(0, 0, 800, 600);
        var grid = PaneGridDetector.Build(bmp, window);
        Assert.Null(PaneGridDetector.PaneAt(grid, new Point(2000, 300), window));
    }
}
