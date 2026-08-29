using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class EditorShellContractTests
{
    [Fact]
    public void PrimaryAndMoreTools_FollowVerifiedHierarchyWithoutEmojiTool()
    {
        Assert.Equal(new[]
        {
            "Select", "Draw", "Shape", "Text", "Pixelate", "Spotlight", "Step",
        }, EditorShellContract.PrimaryToolOrder);
        Assert.Equal(new[] { "Freehand", "Highlighter" }, EditorShellContract.DrawGroupTools);
        Assert.Equal(new[] { "Rectangle", "FilledRectangle", "Ellipse", "Line", "Arrow" },
            EditorShellContract.ShapeGroupTools);
        Assert.Equal(new[] { "Text", "Callout" }, EditorShellContract.TextGroupTools);
        Assert.Equal(new[] { "Pan", "CurvedArrow", "Blur", "Eyedropper" },
            EditorShellContract.MoreToolOrder);

        string[] uniqueTools = new[] { "Select", "Pixelate", "Spotlight", "Step", "Crop" }
            .Concat(EditorShellContract.DrawGroupTools)
            .Concat(EditorShellContract.ShapeGroupTools)
            .Concat(EditorShellContract.TextGroupTools)
            .Concat(EditorShellContract.MoreToolOrder)
            .Where(name => name != "FilledRectangle")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(17, uniqueTools.Length);
        Assert.DoesNotContain(nameof(EditorTool.Emoji), uniqueTools);
        Assert.All(Enum.GetNames<EditorTool>().Where(name => name != nameof(EditorTool.Emoji)),
            name => Assert.Contains(name, uniqueTools));
    }

    [Fact]
    public void Contexts_PutImmediateControlsFirstAndKeepAdvancedWinShotOptions()
    {
        Assert.Equal(EditorContextControls.None,
            EditorShellContract.ContextFor(EditorTool.Select, filledRectangle: false));
        Assert.Equal(EditorContextControls.Color | EditorContextControls.Thickness |
                     EditorContextControls.Fill | EditorContextControls.Opacity |
                     EditorContextControls.LineStyle | EditorContextControls.FillStrokeTabs,
            EditorShellContract.ContextFor(EditorTool.Rectangle, filledRectangle: false));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Rectangle, filledRectangle: true)
            .HasFlag(EditorContextControls.FillStrokeTabs));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Rectangle, filledRectangle: true)
            .HasFlag(EditorContextControls.Thickness));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Callout, false)
            .HasFlag(EditorContextControls.ArrowStyle));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Line, false)
            .HasFlag(EditorContextControls.LineStyle));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Arrow, false)
            .HasFlag(EditorContextControls.ArrowStyle));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Text, false)
            .HasFlag(EditorContextControls.TextStyle));
        Assert.Equal(EditorContextControls.EffectStrength,
            EditorShellContract.ContextFor(EditorTool.Pixelate, false));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Step, false)
            .HasFlag(EditorContextControls.StepMode));
        Assert.Equal(EditorContextControls.CropRatio,
            EditorShellContract.ContextFor(EditorTool.Crop, false));
    }

    [Theory]
    [InlineData(2, 19)]
    [InlineData(4, 27)]
    [InlineData(6, 35)]
    public void TextSizePresentation_MapsCompatibleThicknessValuesToActualPoints(
        double compatibleThickness, double expectedPoints)
    {
        Assert.Equal(expectedPoints, AnnotationFactory.FontSizeFor(compatibleThickness));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void PrimaryToolbar_FitsMinimumWindowAtCommonWindowsDpi(double dpiScale)
    {
        Assert.True(EditorShellContract.FitsPrimaryToolbar(
            EditorShellContract.MinimumEditorLogicalWidth, dpiScale));
        Assert.False(EditorShellContract.FitsPrimaryToolbar(880, dpiScale));
    }
}
