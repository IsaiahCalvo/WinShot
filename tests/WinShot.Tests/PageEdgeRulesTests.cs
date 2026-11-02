using System.IO;
using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Page-edge rules (docs/PAGE-EDGE-RULES.md, Isaiah 2026-11-02): the selection chrome draws
/// over the image edge, the rotate button can always be reached, marks are cut at the edge,
/// and a move stops at the edge.
/// </summary>
public class PageEdgeRulesTests
{
    // A 600 x 400 capture at 100 % in a 1000 x 700 viewport: it fits, so ClampView centres
    // it with 200 px of desk left and right and 150 px above and below.
    private static Rect Reach(double topInset = 0, double zoom = 1, double w = 600, double h = 400) =>
        RotateGripPlacement.ReachableContent(1000, 700, w, h, zoom, 12, topInset);

    [Fact]
    public void ReachableContent_FittingImage_IsTheCentredViewInImagePixels()
    {
        Rect r = Reach();
        Assert.Equal(-200, r.Left, 6);
        Assert.Equal(-150, r.Top, 6);
        Assert.Equal(800, r.Right, 6);
        Assert.Equal(550, r.Bottom, 6);
    }

    [Fact]
    public void ReachableContent_ImageLargerThanTheView_PansToTheMarginAtEitherEnd()
    {
        // 3000 x 2000 at 100 %: pans until 12 px of desk shows past either edge.
        Rect r = Reach(w: 3000, h: 2000);
        Assert.Equal(-12, r.Left, 6);
        Assert.Equal(-12, r.Top, 6);
        Assert.Equal(3012, r.Right, 6);
        Assert.Equal(2012, r.Bottom, 6);
    }

    [Fact]
    public void ReachableContent_LessTheSettingsRowOverTheTop()
    {
        Rect r = Reach(topInset: 42, w: 3000, h: 2000);
        Assert.Equal(30, r.Top, 6); // the row covers 42 px; the clamp leaves 12 px of desk
    }

    [Fact]
    public void RotateButton_StaysAboveWhenItFits()
    {
        var above = new Point(300, 100 - 38);
        var below = new Point(300, 200 + 38);
        Assert.False(RotateGripPlacement.ShouldPlaceBelow(above, below, 10, Reach()));
    }

    [Fact]
    public void RotateButton_MovesBelowAtTheTopOfALargeImage()
    {
        // A mark 4 px under the top of a big capture: 38 px above it is past the 12 px the
        // clamp leaves above the image, so the button goes below the frame.
        Rect reach = Reach(w: 3000, h: 2000);
        var above = new Point(300, 4 - 38);
        var below = new Point(300, 60 + 38);
        Assert.True(RotateGripPlacement.ShouldPlaceBelow(above, below, 10, reach));
    }

    [Fact]
    public void RotateButton_MovesBelowWhenTheSettingsRowWouldCoverIt()
    {
        // A fitting image with a tall settings row over the top of the canvas.
        Rect reach = Reach(topInset: 180);
        var above = new Point(300, 4 - 38);
        var below = new Point(300, 60 + 38);
        Assert.True(RotateGripPlacement.ShouldPlaceBelow(above, below, 10, reach));
    }

    [Fact]
    public void RotateButton_StaysPutWhenNeitherSideFits()
    {
        Rect reach = new(0, 0, 100, 100);
        Assert.False(RotateGripPlacement.ShouldPlaceBelow(new Point(50, -40), new Point(50, 140), 10, reach));
        Assert.False(RotateGripPlacement.ShouldPlaceBelow(new Point(50, -40), new Point(50, 60), 10, Rect.Empty));
    }

    [Fact]
    public void Move_StopsAtTheImageEdge()
    {
        var start = new Rect(10, 10, 50, 50);
        Vector d = PageEdgeMove.ClampDelta(start, new Vector(-40, -40), 600, 400);
        Assert.Equal(-10, d.X, 6);
        Assert.Equal(-10, d.Y, 6);
        d = PageEdgeMove.ClampDelta(start, new Vector(1000, 1000), 600, 400);
        Assert.Equal(540, d.X, 6);
        Assert.Equal(340, d.Y, 6);
    }

    [Fact]
    public void Move_NeverPullsAMarkThatAlreadyHangsOverBackIn()
    {
        // Already 20 px off the left edge: it may stay or move right, never further left,
        // and a move that is not toward the edge is not changed.
        var start = new Rect(-20, 100, 50, 50);
        Assert.Equal(0, PageEdgeMove.ClampDelta(start, new Vector(-5, 0), 600, 400).X, 6);
        Assert.Equal(0, PageEdgeMove.ClampDelta(start, new Vector(0, 7), 600, 400).X, 6);
        Assert.Equal(7, PageEdgeMove.ClampDelta(start, new Vector(0, 7), 600, 400).Y, 6);
        Assert.Equal(15, PageEdgeMove.ClampDelta(start, new Vector(15, 0), 600, 400).X, 6);
    }

    [Fact]
    public void SelectionChrome_LivesOnAnUnclippedCanvas_MarksStayClipped()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "WinShot", "Editor", "EditorWindow.xaml"));
        Assert.Contains("<Canvas x:Name=\"AnnotationCanvas\" Background=\"Transparent\" ClipToBounds=\"True\"/>", xaml);
        int chrome = xaml.IndexOf("<Canvas x:Name=\"ChromeCanvas\" IsHitTestVisible=\"False\" ClipToBounds=\"False\">", StringComparison.Ordinal);
        Assert.True(chrome > 0, "ChromeCanvas must exist and not clip");
        int chromeEnd = xaml.IndexOf("</Canvas>", chrome, StringComparison.Ordinal);
        foreach (string name in new[] { "SelectionRect", "GroupRect", "HandleLayer", "CurveHandle", "MagnifierFocusHandle" })
        {
            int at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(at > chrome && at < chromeEnd, $"{name} must sit on the unclipped ChromeCanvas");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
