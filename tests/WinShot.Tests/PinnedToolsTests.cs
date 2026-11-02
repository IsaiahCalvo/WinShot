using WinShot.Core;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>Survey's pinned tools rules (pinnedTools.js), pure.</summary>
public class PinnedToolsTests
{
    [Fact]
    public void RectangleWithCloudLine_IsTheCloudPin()
    {
        Assert.Equal("cloud", PinnedTools.KindForTool(EditorTool.Rectangle, LineBorderStyle.Cloud)?.Id);
        Assert.Equal("rect", PinnedTools.KindForTool(EditorTool.Rectangle, LineBorderStyle.Solid)?.Id);
    }

    [Theory]
    [InlineData(EditorTool.Select)]
    [InlineData(EditorTool.Crop)]
    [InlineData(EditorTool.Redact)]
    public void NonDrawingTools_CannotBePinned(EditorTool tool) =>
        Assert.Null(PinnedTools.KindForTool(tool, LineBorderStyle.Solid));

    [Fact]
    public void EraserPin_KeepsOnlyItsSize()
    {
        var pin = PinnedTools.Make(PinnedTools.Kind("eraser")!, "#FF0000", 80, 24, "#00FF00", 50, LineBorderStyle.Dashed);
        Assert.Equal(24, pin.StrokeWidth);
        Assert.Null(pin.StrokeColor);
        Assert.Null(pin.FillColor);
        Assert.Null(pin.LineStyle);
    }

    [Fact]
    public void PenPin_HasNoFillOrLineStyle()
    {
        var pin = PinnedTools.Make(PinnedTools.Kind("pen")!, "#FF0000", 100, 3, "#00FF00", 50, LineBorderStyle.Dashed);
        Assert.Equal("#FF0000", pin.StrokeColor);
        Assert.Equal(3, pin.StrokeWidth);
        Assert.Null(pin.FillColor);
        Assert.Null(pin.LineStyle);
    }

    [Fact]
    public void RectanglePin_KeepsFillAndLineStyle()
    {
        var pin = PinnedTools.Make(PinnedTools.Kind("rect")!, "#0000FF", 100, 4, "#00FF00", 25, LineBorderStyle.Dotted);
        Assert.Equal("#00FF00", pin.FillColor);
        Assert.Equal(25, pin.FillOpacity);
        Assert.Equal("dotted", pin.LineStyle);
    }

    [Fact]
    public void ArrowIsNeverClouded()
    {
        var pin = PinnedTools.Make(PinnedTools.Kind("arrow")!, "#000000", 100, 2, null, 0, LineBorderStyle.Cloud);
        Assert.Equal("solid", pin.LineStyle);
    }

    [Fact]
    public void CloudPin_AlwaysCarriesTheCloudLine()
    {
        var pin = PinnedTools.Make(PinnedTools.Kind("cloud")!, "#000000", 100, 2, null, 0, LineBorderStyle.Solid);
        Assert.Equal("cloud", pin.LineStyle);
    }

    [Fact]
    public void SamePinTwice_IsRecognised()
    {
        var a = PinnedTools.Make(PinnedTools.Kind("pen")!, "#ff0000", 100, 3, null, 0, LineBorderStyle.Solid);
        var b = PinnedTools.Make(PinnedTools.Kind("pen")!, "#FF0000", 100, 3, null, 0, LineBorderStyle.Solid);
        var c = PinnedTools.Make(PinnedTools.Kind("pen")!, "#FF0000", 100, 6, null, 0, LineBorderStyle.Solid);
        Assert.True(PinnedTools.SameAs(a, b));
        Assert.False(PinnedTools.SameAs(a, c));
    }

    [Fact]
    public void BadgeAndDescriptionReadPlainly()
    {
        var pin = PinnedTools.Make(PinnedTools.Kind("rect")!, "#FF0000", 100, 2.5, null, 0, LineBorderStyle.Dashed);
        Assert.Equal("2.5", PinnedTools.Badge(pin));
        Assert.Equal("Rectangle, #FF0000, 2.5, dashed", PinnedTools.Describe(pin));
    }

    [Theory]
    [InlineData(5, 300, "Left")]
    [InlineData(795, 300, "Right")]
    [InlineData(400, 595, "Bottom")]
    public void GripDrop_PicksTheNearestSide(double x, double y, string expected) =>
        Assert.Equal(expected, PinnedTools.NearestSide(x, y, 800, 600).ToString());

    [Fact]
    public void UnknownSide_FallsBackToLeft() =>
        Assert.Equal(PinDockSide.Left, PinnedTools.ParseSide("nowhere"));
}
