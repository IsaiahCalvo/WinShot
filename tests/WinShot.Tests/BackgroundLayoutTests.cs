using System.Drawing;
using WinShot.Editor.Background;
using Xunit;

namespace WinShot.Tests;

public class BackgroundLayoutTests
{
    [Fact]
    public void Calculate_AutoCanvasHugsImageWithPadding()
    {
        var layout = BackgroundLayout.Calculate(
            new Size(400, 200),
            padding: 50,
            aspectRatio: null);

        Assert.Equal(new Size(500, 300), layout.CanvasSize);
        Assert.Equal(50, layout.Margin);
    }

    [Fact]
    public void Calculate_WideRatioExpandsWidthOnly()
    {
        var layout = BackgroundLayout.Calculate(
            new Size(400, 300),
            padding: 50,
            aspectRatio: 16d / 9d);

        Assert.Equal(new Size(712, 400), layout.CanvasSize);
        Assert.Equal(50, layout.Margin);
    }

    [Fact]
    public void Calculate_TallRatioExpandsHeightOnly()
    {
        var layout = BackgroundLayout.Calculate(
            new Size(400, 300),
            padding: 50,
            aspectRatio: 1);

        Assert.Equal(new Size(500, 500), layout.CanvasSize);
    }

    [Fact]
    public void Calculate_ClampsNegativeInputsToSafeMinimums()
    {
        var layout = BackgroundLayout.Calculate(
            new Size(0, -5),
            padding: -10,
            aspectRatio: 0);

        Assert.Equal(new Size(1, 1), layout.SourceSize);
        Assert.Equal(new Size(1, 1), layout.CanvasSize);
        Assert.Equal(0, layout.Margin);
    }

    [Fact]
    public void Calculate_InsetGrowsTheThingBeingPadded()
    {
        var layout = BackgroundLayout.Calculate(
            new Size(400, 200), padding: 50, aspectRatio: null, inset: 10);

        // The framed shot is 420x220; the padding gutter is still 50 on every side.
        Assert.Equal(new Size(420, 220), layout.SourceSize);
        Assert.Equal(new Size(520, 320), layout.CanvasSize);
        Assert.Equal(50, layout.Margin);
    }

    [Fact]
    public void Calculate_InsetIsClampedToTheSliderRange()
    {
        var layout = BackgroundLayout.Calculate(
            new Size(100, 100), padding: 0, aspectRatio: null, inset: 5000);

        Assert.Equal(100 + 2 * BackgroundLayout.MaxInset, layout.SourceSize.Width);
    }

    [Fact]
    public void Balance_CentredContentKeepsUniformMargins()
    {
        var m = BackgroundLayout.Balance(
            new Size(200, 200), new Rectangle(20, 20, 160, 160), padding: 40);

        Assert.Equal(new BackgroundMargins(40, 40, 40, 40), m);
    }

    [Fact]
    public void Balance_ShadowOnOneSideIsPaidBackOnTheOther()
    {
        // A window capture whose baked shadow leaves 30px of dead space on the right
        // and 4px on the left: the left gutter grows by the difference so the window
        // itself lands centred, and no edge ever goes below the padding.
        var m = BackgroundLayout.Balance(
            new Size(200, 100), new Rectangle(4, 0, 166, 90), padding: 20);

        Assert.Equal(20 + 26, m.Left);   // 4px of dead space on the left, 30 on the right
        Assert.Equal(20, m.Right);
        Assert.Equal(20 + 10, m.Top);    // 0px above the content, 10 below it
        Assert.Equal(20, m.Bottom);
        Assert.Equal(m.Right + 30, m.Left + 4);   // optically centred across
        Assert.Equal(m.Bottom + 10, m.Top + 0);   // and down
    }

    [Fact]
    public void Balance_DegenerateContentFallsBackToUniform()
    {
        var m = BackgroundLayout.Balance(
            new Size(200, 200), new Rectangle(0, 0, 0, 0), padding: 12);

        Assert.Equal(new BackgroundMargins(12, 12, 12, 12), m);
    }

    [Fact]
    public void ContentBounds_TrimsAFlatBorder()
    {
        using var bmp = new Bitmap(60, 40);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, new Rectangle(10, 6, 30, 20));
        }

        Assert.Equal(new Rectangle(10, 6, 30, 20), BackgroundLayout.ContentBounds(bmp));
    }

    [Fact]
    public void ContentBounds_TrimsTransparencyWhenTheBitmapHasAlpha()
    {
        using var bmp = new Bitmap(50, 50, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(Brushes.White, new Rectangle(5, 8, 20, 30));
        }

        Assert.Equal(new Rectangle(5, 8, 20, 30), BackgroundLayout.ContentBounds(bmp));
    }

    [Fact]
    public void ContentBounds_AllOneColourReturnsTheWholeBitmap()
    {
        // Nothing to trim must read as "already balanced", not as an empty rect that
        // would make the balance a guess.
        using var bmp = new Bitmap(30, 30);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Gray);

        Rectangle r = BackgroundLayout.ContentBounds(bmp);
        Assert.True(r.Width >= 1 && r.Height >= 1);
        Assert.True(r.Width <= 30 && r.Height <= 30);
    }
}
