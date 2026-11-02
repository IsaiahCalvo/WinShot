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
