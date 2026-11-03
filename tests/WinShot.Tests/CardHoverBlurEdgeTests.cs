using System.Drawing.Imaging;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Hovering the corner card swaps in a frosted copy of the capture. That copy used to fade to
/// clear for several pixels at every edge, so the dark card fill showed through as a heavy
/// border that the sharp card does not have.
/// </summary>
public class CardHoverBlurEdgeTests
{
    [Theory]
    [InlineData(296, 168)]   // a 150 % card
    [InlineData(197, 112)]   // a 100 % card
    public void TheFrostedCopyKeepsItsEdgesSolid(int width, int height)
    {
        using var src = new SD.Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(src))
            g.Clear(SD.Color.FromArgb(255, 150, 150, 150));

        using SD.Bitmap blurred = FastQuickActionsWindow.CreateBlurred(src);

        SD.Color centre = blurred.GetPixel(width / 2, height / 2);
        foreach ((int x, int y) in new[]
                 {
                     (0, height / 2), (width - 1, height / 2), (width / 2, 0), (width / 2, height - 1),
                     (2, 2), (width - 3, height - 3), (5, height / 2), (width - 6, height / 2),
                 })
        {
            SD.Color edge = blurred.GetPixel(x, y);
            Assert.True(edge.A >= 250, $"({x},{y}) alpha {edge.A}: the card fill shows through the edge");
            Assert.True(Math.Abs(edge.R - centre.R) <= 6, $"({x},{y}) is {edge.R}, centre {centre.R}: the edge is darker");
        }
    }
}
