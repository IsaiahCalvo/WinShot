using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class EditorScrollBarsTests
{
    private static readonly Size Viewport = new(800, 600);

    [Fact]
    public void ContentThatFitsGetsNoBars()
    {
        var (h, v) = EditorScrollBars.Calculate(Viewport, new Size(400, 300), zoom: 1, new Vector(200, 150));
        Assert.Null(h);
        Assert.Null(v);
    }

    /// <summary>
    /// A capture that fits exactly, or misses by a rounding pixel, must not sprout a bar —
    /// otherwise the indicator appears on images with nothing to scroll.
    /// </summary>
    [Fact]
    public void AnExactFitIsNotScrollable()
    {
        var (h, v) = EditorScrollBars.Calculate(Viewport, new Size(800, 600), zoom: 1, default);
        Assert.Null(h);
        Assert.Null(v);
    }

    [Fact]
    public void OnlyTheOverflowingAxisGetsABar()
    {
        // Twice as tall as the viewport, narrower than it.
        var (h, v) = EditorScrollBars.Calculate(Viewport, new Size(400, 1200), zoom: 1, new Vector(200, 0));
        Assert.Null(h);
        Assert.NotNull(v);
        Assert.Equal(EditorScrollBars.Thickness, v!.Value.Width, 6);
        Assert.Equal(Viewport.Width - EditorScrollBars.Margin - EditorScrollBars.Thickness, v.Value.X, 6);
    }

    /// <summary>
    /// The bar's length is the fraction of the content on screen, and its position is how
    /// far through the scroll the view is — pinned to the ends at the ends.
    /// </summary>
    [Fact]
    public void TheBarTracksTheViewFromTopToBottom()
    {
        var content = new Size(400, 2400); // 4x the viewport height

        var (_, atTop) = EditorScrollBars.Calculate(Viewport, content, 1, new Vector(0, 0));
        var (_, atBottom) = EditorScrollBars.Calculate(Viewport, content, 1, new Vector(0, -1800));
        var (_, middle) = EditorScrollBars.Calculate(Viewport, content, 1, new Vector(0, -900));

        Assert.NotNull(atTop);
        Assert.NotNull(atBottom);
        Assert.NotNull(middle);

        double track = Viewport.Height - EditorScrollBars.Margin * 2;
        Assert.Equal(track / 4, atTop!.Value.Height, 3);

        Assert.Equal(EditorScrollBars.Margin, atTop.Value.Y, 3);
        Assert.Equal(Viewport.Height - EditorScrollBars.Margin - atBottom!.Value.Height, atBottom.Value.Y, 3);
        Assert.True(middle!.Value.Y > atTop.Value.Y && middle.Value.Y < atBottom.Value.Y);
    }

    /// <summary>Zoom is what makes the content overflow, so it has to be in the maths.</summary>
    [Fact]
    public void ZoomDecidesWhetherThereIsAnythingToScroll()
    {
        var content = new Size(700, 500);
        Assert.Null(EditorScrollBars.Calculate(Viewport, content, zoom: 1, default).Horizontal);

        var (h, v) = EditorScrollBars.Calculate(Viewport, content, zoom: 4, default);
        Assert.NotNull(h);
        Assert.NotNull(v);
    }

    /// <summary>A bar for a 30 000 px scroll capture must still be grabbable-looking, not a dot.</summary>
    [Fact]
    public void AVeryLongCaptureStillGetsAVisibleBar()
    {
        var (_, v) = EditorScrollBars.Calculate(Viewport, new Size(400, 30000), 1, new Vector(0, -1000));
        Assert.NotNull(v);
        Assert.True(v!.Value.Height >= EditorScrollBars.Thickness * 3);
        Assert.True(v.Value.Y + v.Value.Height <= Viewport.Height);
    }

    [Fact]
    public void DegenerateInputsAskForNothing()
    {
        Assert.Equal((null, null), EditorScrollBars.Calculate(new Size(0, 0), new Size(10, 10), 1, default));
        Assert.Equal((null, null), EditorScrollBars.Calculate(Viewport, new Size(0, 0), 1, default));
        Assert.Equal((null, null), EditorScrollBars.Calculate(Viewport, new Size(10, 10), 0, default));
        Assert.Equal((null, null), EditorScrollBars.Calculate(Viewport, new Size(10, 10), double.NaN, default));
    }
}
