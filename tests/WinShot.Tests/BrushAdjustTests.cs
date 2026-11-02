using System.Windows.Input;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>Survey's Photoshop-style brush drag (utils/brushAdjustGesture.js), pure.</summary>
public class BrushAdjustTests
{
    [Theory]
    [InlineData(MouseButton.Right, ModifierKeys.Alt, true)]
    [InlineData(MouseButton.Left, ModifierKeys.Alt | ModifierKeys.Control, true)]
    [InlineData(MouseButton.Left, ModifierKeys.Alt, false)]   // Alt+left stays a normal drag
    [InlineData(MouseButton.Right, ModifierKeys.None, false)] // a plain right-click is the menu
    [InlineData(MouseButton.Left, ModifierKeys.Control, false)]
    public void PressStartsTheGestureOnlyWithTheRightKeys(MouseButton button, ModifierKeys mods, bool expected) =>
        Assert.Equal(expected, EditorWindow.IsBrushAdjustPress(button, mods));

    [Fact]
    public void SmallWobble_ChangesNothingAndLocksNoAxis()
    {
        var r = BrushAdjust.Resolve(4, 100, 2, 3, BrushAdjustAxis.None, 1, 50, true);
        Assert.Equal(BrushAdjustAxis.None, r.Axis);
        Assert.Equal(4, r.Width);
        Assert.Equal(100, r.Opacity);
    }

    [Fact]
    public void Sideways_DoublesWidthEvery120Px()
    {
        var r = BrushAdjust.Resolve(4, 100, BrushAdjust.DoublingPx, 0, BrushAdjustAxis.None, 1, 50, true);
        Assert.Equal(BrushAdjustAxis.Width, r.Axis);
        Assert.Equal(8, r.Width);
        var halved = BrushAdjust.Resolve(4, 100, -BrushAdjust.DoublingPx, 0, BrushAdjustAxis.None, 1, 50, true);
        Assert.Equal(2, halved.Width);
    }

    [Fact]
    public void Width_StaysInsideTheToolsLimits()
    {
        Assert.Equal(50, BrushAdjust.Resolve(20, 100, 1000, 0, BrushAdjustAxis.None, 1, 50, true).Width);
        Assert.Equal(1, BrushAdjust.Resolve(2, 100, -1000, 0, BrushAdjustAxis.None, 1, 50, true).Width);
    }

    [Fact]
    public void Up_MakesItMoreSolid_Down_LessSolid()
    {
        var down = BrushAdjust.Resolve(4, 80, 0, 40, BrushAdjustAxis.None, 1, 50, true);
        Assert.Equal(BrushAdjustAxis.Opacity, down.Axis);
        Assert.Equal(60, down.Opacity);
        var up = BrushAdjust.Resolve(4, 80, 0, -100, BrushAdjustAxis.None, 1, 50, true);
        Assert.Equal(100, up.Opacity);
    }

    [Fact]
    public void Opacity_NeverDropsBelowFivePercent()
    {
        var r = BrushAdjust.Resolve(4, 80, 0, 1000, BrushAdjustAxis.None, 1, 50, true);
        Assert.Equal(BrushAdjust.MinOpacity, r.Opacity);
    }

    [Fact]
    public void LockedAxis_IgnoresTheOtherDirection()
    {
        // Locked to width: a later vertical move must not touch opacity.
        var r = BrushAdjust.Resolve(4, 80, 0, 200, BrushAdjustAxis.Width, 1, 50, true);
        Assert.Equal(80, r.Opacity);
        Assert.Equal(4, r.Width);
    }

    [Fact]
    public void Eraser_HasNoOpacity_SoVerticalMovesChangeSize()
    {
        Assert.False(BrushAdjust.HasOpacity(EditorTool.Eraser));
        var r = BrushAdjust.Resolve(16, 100, 0, 50, BrushAdjustAxis.None, 1, 100, canOpacity: false);
        Assert.Equal(BrushAdjustAxis.Width, r.Axis);
        Assert.Equal(100, r.Opacity);
    }

    [Theory]
    [InlineData(EditorTool.Freehand, true)]
    [InlineData(EditorTool.Rectangle, true)]
    [InlineData(EditorTool.Polyline, true)]
    [InlineData(EditorTool.Eraser, true)]
    [InlineData(EditorTool.Select, false)]
    [InlineData(EditorTool.Text, false)]
    [InlineData(EditorTool.Redact, false)]
    public void OnlyStrokeToolsTakeTheGesture(EditorTool tool, bool expected) =>
        Assert.Equal(expected, BrushAdjust.AppliesTo(tool));

    [Fact]
    public void Label_ReadsLikeTheToolbar()
    {
        Assert.Equal("Width 6 · Opacity 75%", BrushAdjust.Label(6, 75, eraser: false));
        Assert.Equal("Size 24", BrushAdjust.Label(24, 100, eraser: true));
    }
}
