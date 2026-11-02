using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Survey's selection quick bar rules (utils/selectionQuickBar.js), pure: what the bar holds for
/// a pick, and where it sits relative to the selection and the viewer.
/// </summary>
public class SelectionQuickBarLayoutTests
{
    private static readonly Rect Viewer = new(0, 0, 800, 600);

    [Fact]
    public void NothingPicked_NoBar() =>
        Assert.Null(SelectionQuickBarLayout.Plan(Array.Empty<string?>()));

    [Fact]
    public void Rectangle_GetsEveryControl()
    {
        var plan = SelectionQuickBarLayout.Plan(new[] { AnnotationData.TypeRectangle })!.Value;
        Assert.True(plan.Colour);
        Assert.True(plan.Width);
        Assert.True(plan.Style);
        Assert.True(plan.Duplicate);
        Assert.True(plan.Delete);
        Assert.True(plan.More);
    }

    [Fact]
    public void Pen_HasWidthButNoLineStyle()
    {
        var plan = SelectionQuickBarLayout.Plan(new[] { AnnotationData.TypeFreehand })!.Value;
        Assert.True(plan.Colour);
        Assert.True(plan.Width);
        Assert.False(plan.Style);
    }

    [Theory]
    [InlineData(AnnotationData.TypeImage)]
    [InlineData(AnnotationData.TypeRedact)]
    [InlineData(AnnotationData.TypeSpotlight)]
    [InlineData(AnnotationData.TypeMagnifier)]
    [InlineData(AnnotationData.TypeEmoji)]
    public void MarksWithoutPaint_KeepOnlyDuplicateDeleteMore(string type)
    {
        var plan = SelectionQuickBarLayout.Plan(new[] { type })!.Value;
        Assert.False(plan.Colour);
        Assert.False(plan.Width);
        Assert.False(plan.Style);
        Assert.True(plan.Duplicate);
        Assert.True(plan.Delete);
        Assert.True(plan.More);
    }

    [Fact]
    public void SeveralMarks_ShowOnlyWhatAppliesToEveryOne()
    {
        // A rectangle has a line style; a pen stroke does not, so the pair has none.
        var plan = SelectionQuickBarLayout.Plan(new[] { AnnotationData.TypeRectangle, AnnotationData.TypeFreehand })!.Value;
        Assert.True(plan.Colour);
        Assert.True(plan.Width);
        Assert.False(plan.Style);
        // One mark with no paint takes colour away from the whole pick.
        var withImage = SelectionQuickBarLayout.Plan(new[] { AnnotationData.TypeRectangle, AnnotationData.TypeImage })!.Value;
        Assert.False(withImage.Colour);
    }

    [Fact]
    public void LockedPick_CannotBeRestyledOrDeleted()
    {
        var plan = SelectionQuickBarLayout.Plan(new[] { AnnotationData.TypeRectangle }, anyLocked: true)!.Value;
        Assert.False(plan.Colour);
        Assert.False(plan.Delete);
        Assert.True(plan.Duplicate);
        Assert.True(plan.More);
    }

    [Fact]
    public void CloudIsOfferedOnlyWhenEveryMarkCanTakeIt()
    {
        Assert.True(SelectionQuickBarLayout.AllowsCloud(new[] { AnnotationData.TypeRectangle, AnnotationData.TypeEllipse }));
        Assert.False(SelectionQuickBarLayout.AllowsCloud(new[] { AnnotationData.TypeRectangle, AnnotationData.TypeArrow }));
    }

    [Fact]
    public void BarSitsCentredJustAboveTheSelection()
    {
        var sel = new Rect(300, 300, 200, 100);
        var pos = SelectionQuickBarLayout.Position(sel, Array.Empty<Rect>(), false, Viewer, 180, 36)!.Value;
        Assert.Equal(QuickBarSide.Above, pos.Side);
        Assert.Equal(400 - 90, pos.Left);
        Assert.Equal(300 - SelectionQuickBarLayout.Gap - 36, pos.Top);
    }

    [Fact]
    public void NoRoomAbove_DropsBelow()
    {
        var sel = new Rect(300, 20, 200, 100);
        var pos = SelectionQuickBarLayout.Position(sel, Array.Empty<Rect>(), false, Viewer, 180, 36)!.Value;
        Assert.Equal(QuickBarSide.Below, pos.Side);
        Assert.Equal(120 + SelectionQuickBarLayout.Gap, pos.Top);
    }

    [Fact]
    public void RotateGripOnTop_BarGoesBelowAndClearsIt()
    {
        var sel = new Rect(300, 300, 200, 100);
        var grip = new Rect(390, 260, 20, 20);
        var pos = SelectionQuickBarLayout.Position(sel, new[] { grip }, preferBelow: true, Viewer, 180, 36)!.Value;
        Assert.Equal(QuickBarSide.Below, pos.Side);
        Assert.True(pos.Top >= 400);
    }

    [Fact]
    public void KeepOutBoxesPushTheBarFurtherAway()
    {
        var sel = new Rect(300, 300, 200, 100);
        var pill = new Rect(370, 230, 60, 28);
        var pos = SelectionQuickBarLayout.Position(sel, new[] { pill }, false, Viewer, 180, 36)!.Value;
        Assert.Equal(QuickBarSide.Above, pos.Side);
        Assert.True(pos.Top + 36 <= 230);
    }

    [Fact]
    public void BarNeverLeavesTheViewer()
    {
        var sel = new Rect(-50, 300, 80, 60); // mostly off the left edge
        var pos = SelectionQuickBarLayout.Position(sel, Array.Empty<Rect>(), false, Viewer, 180, 36)!.Value;
        Assert.Equal(SelectionQuickBarLayout.Margin, pos.Left);
        var right = SelectionQuickBarLayout.Position(new Rect(780, 300, 80, 60), Array.Empty<Rect>(), false, Viewer, 180, 36)!.Value;
        Assert.Equal(800 - SelectionQuickBarLayout.Margin - 180, right.Left);
    }

    [Fact]
    public void SelectionScrolledAway_Hides() =>
        Assert.Null(SelectionQuickBarLayout.Position(new Rect(100, 700, 50, 50), Array.Empty<Rect>(), false, Viewer, 180, 36));

    [Fact]
    public void SelectionFillsTheViewer_PinsToAnEdge()
    {
        var sel = new Rect(0, 0, 800, 600);
        var pos = SelectionQuickBarLayout.Position(sel, Array.Empty<Rect>(), false, Viewer, 180, 36)!.Value;
        Assert.Equal(QuickBarSide.Pinned, pos.Side);
        Assert.True(pos.Top >= SelectionQuickBarLayout.Margin);
        Assert.True(pos.Top + 36 <= 600 - SelectionQuickBarLayout.Margin);
    }
}

/// <summary>Survey's lasso: a mark is caught only when it lies wholly inside the loop.</summary>
public class LassoSelectTests
{
    private static System.Windows.Media.Geometry Square(double x, double y, double size) =>
        EditorWindow.LassoGeometry(new[]
        {
            new Point(x, y), new Point(x + size, y), new Point(x + size, y + size), new Point(x, y + size),
        });

    [Fact]
    public void MarkWhollyInside_IsCaught() =>
        Assert.True(EditorWindow.LassoCatches(Square(0, 0, 100), new Rect(20, 20, 30, 30), null));

    [Fact]
    public void MarkPokingOut_IsNotCaught() =>
        Assert.False(EditorWindow.LassoCatches(Square(0, 0, 100), new Rect(80, 80, 40, 40), null));

    [Fact]
    public void MarkOutside_IsNotCaught() =>
        Assert.False(EditorWindow.LassoCatches(Square(0, 0, 100), new Rect(200, 200, 10, 10), null));

    [Fact]
    public void TriangleLoop_SkipsAMarkInTheCutAwayCorner()
    {
        var triangle = EditorWindow.LassoGeometry(new[] { new Point(0, 0), new Point(100, 0), new Point(0, 100) });
        Assert.True(EditorWindow.LassoCatches(triangle, new Rect(5, 5, 20, 20), null));
        Assert.False(EditorWindow.LassoCatches(triangle, new Rect(70, 70, 20, 20), null));
    }

    [Fact]
    public void CrossingLoop_CatchesAMarkItTouches()
    {
        Assert.True(EditorWindow.LassoCatches(Square(0, 0, 100), new Rect(80, 80, 40, 40), null, crossing: true));
        Assert.False(EditorWindow.LassoCatches(Square(0, 0, 100), new Rect(200, 200, 10, 10), null, crossing: true));
    }

    [Theory]
    [InlineData(10, 1, false)]
    [InlineData(-10, 1, true)]
    [InlineData(-3, 1, false)]   // under 5 screen px decides nothing
    [InlineData(-3, 2, true)]    // 6 screen px at 200%
    public void LoopModeFollowsTheFirstSidewaysTravel(double dx, double zoom, bool crossing)
    {
        var trail = new[] { new Point(50, 50), new Point(50, 70), new Point(50 + dx, 80), new Point(100, 90) };
        // The last point runs right; only the FIRST sideways travel that counts decides.
        if (Math.Abs(dx) * zoom < 5) trail = trail[..3];
        Assert.Equal(crossing, EditorWindow.LassoModeFromTrail(trail, zoom) == MarqueeMode.Crossing);
    }
}
