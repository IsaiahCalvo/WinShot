using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class EditorZoomTests
{
    private static readonly Size Viewport = new(800, 600);
    private const double Min = 0.05;
    private const double Max = 16.0;

    /// <summary>Where a content point lands on screen for a given view.</summary>
    private static Point Screen(Point content, ZoomView v) =>
        new(content.X * v.Zoom + v.Translate.X, content.Y * v.Zoom + v.Translate.Y);

    [Fact]
    public void FitRectCentresWhatItZoomsTo()
    {
        var region = new Rect(100, 50, 200, 100);
        ZoomView v = EditorZoom.FitRect(region, Viewport, currentZoom: 1, Min, Max);

        Point mid = Screen(new Point(200, 100), v);
        Assert.Equal(400, mid.X, 6);
        Assert.Equal(300, mid.Y, 6);
    }

    [Fact]
    public void FitRectFillsTheViewportOnTheTighterAxis()
    {
        // A wide region is limited by width; the padding is real, so the region must not
        // reach the viewport edge.
        var region = new Rect(0, 0, 400, 50);
        ZoomView v = EditorZoom.FitRect(region, Viewport, currentZoom: 1, Min, Max, padding: 24);

        Assert.Equal((800 - 48) / 400.0, v.Zoom, 6);
        Point left = Screen(region.TopLeft, v);
        Assert.True(left.X >= 24 - 1e-6, "the fitted region ran into the padding");
    }

    [Fact]
    public void FitRectRespectsTheZoomCeiling()
    {
        // A one-pixel region would want a 700x zoom.
        ZoomView v = EditorZoom.FitRect(new Rect(10, 10, 1, 1), Viewport, currentZoom: 1, Min, Max);
        Assert.Equal(Max, v.Zoom);
    }

    [Fact]
    public void FitRectRespectsTheZoomFloor()
    {
        ZoomView v = EditorZoom.FitRect(new Rect(0, 0, 100_000, 100_000), Viewport, currentZoom: 1, Min, Max);
        Assert.Equal(Min, v.Zoom);
    }

    [Fact]
    public void ADegenerateRegionKeepsTheZoomAndOnlyRecentres()
    {
        // Otherwise a click that WPF reported as a zero-size drag would zoom to nothing.
        ZoomView v = EditorZoom.FitRect(new Rect(300, 200, 0, 0), Viewport, currentZoom: 2.5, Min, Max);

        Assert.Equal(2.5, v.Zoom);
        Point p = Screen(new Point(300, 200), v);
        Assert.Equal(400, p.X, 6);
        Assert.Equal(300, p.Y, 6);
    }

    [Fact]
    public void ACollapsedViewportDoesNotProduceAnInsaneZoom()
    {
        ZoomView v = EditorZoom.FitRect(new Rect(0, 0, 100, 100), new Size(0, 0), currentZoom: 1, Min, Max);
        Assert.InRange(v.Zoom, Min, Max);
    }

    [Fact]
    public void CentreOnPointPutsThatPointInTheMiddle()
    {
        // A corner zoom takes the corner to where you are LOOKING, not to the matching
        // corner of the screen.
        ZoomView v = EditorZoom.CentreOnPoint(new Point(640, 480), Viewport, zoom: 1.0, Min, Max);

        Point p = Screen(new Point(640, 480), v);
        Assert.Equal(400, p.X, 6);
        Assert.Equal(300, p.Y, 6);
    }

    [Fact]
    public void CentreOnPointClampsTheZoomItIsGiven()
    {
        Assert.Equal(Max, EditorZoom.CentreOnPoint(new Point(0, 0), Viewport, 99, Min, Max).Zoom);
        Assert.Equal(Min, EditorZoom.CentreOnPoint(new Point(0, 0), Viewport, 0.0001, Min, Max).Zoom);
    }

    [Fact]
    public void QTakesTheTopLeftAndWTheBottomRight()
    {
        var target = new Rect(20, 30, 200, 100);
        Assert.Equal(new Point(20, 30), EditorZoom.Corner(target, bottomRight: false));
        Assert.Equal(new Point(220, 130), EditorZoom.Corner(target, bottomRight: true));
    }
}
