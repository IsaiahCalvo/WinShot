using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class EditorShellContractTests
{
    /// <summary>
    /// Every tool has a home in the one-row toolbar: the two pointer tools sit outside the
    /// categories, everything else is in exactly one category's sub-row. Nothing is behind
    /// an overflow popup any more, so a tool missing from this set is a tool no click can
    /// reach. CurvedArrow is the one deliberate exception — it is the Curved row of the
    /// arrow-style menu, not a button.
    /// </summary>
    [Fact]
    public void EveryToolIsReachableFromThePointerPairOrOneCategory()
    {
        Assert.Equal(new[] { "Select", "Pan" }, EditorShellContract.PointerToolOrder);
        Assert.Equal(new[] { "Freehand", "Highlighter", "Eyedropper" },
            EditorShellContract.ToolsIn(EditorToolCategory.Draw));
        Assert.Equal(new[] { "Rectangle", "FilledRectangle", "Ellipse", "Line", "Arrow", "Step" },
            EditorShellContract.ToolsIn(EditorToolCategory.Shapes));
        Assert.Equal(new[] { "Text" }, EditorShellContract.ToolsIn(EditorToolCategory.Text));
        // One redaction tool, not two: pixelate and blur are styles on the mark it draws.
        // The three region tools that act on the pixels underneath share a row.
        Assert.Equal(new[] { "Redact", "Spotlight", "Magnifier" },
            EditorShellContract.ToolsIn(EditorToolCategory.Redact));
        Assert.Equal(new[] { "Crop" }, EditorShellContract.ToolsIn(EditorToolCategory.Image));

        string[] buttons = EditorShellContract.PointerToolOrder
            .Concat(EditorShellContract.CategoryOrder.SelectMany(EditorShellContract.ToolsIn))
            .ToArray();
        Assert.Equal(buttons.Length, buttons.Distinct(StringComparer.Ordinal).Count());

        string[] reachable = buttons
            .Where(name => name != "FilledRectangle")
            .Append(nameof(EditorTool.CurvedArrow))
            .ToArray();
        Assert.DoesNotContain(nameof(EditorTool.Emoji), reachable);
        Assert.All(Enum.GetNames<EditorTool>().Where(name => name != nameof(EditorTool.Emoji)),
            name => Assert.Contains(name, reachable));
    }

    [Fact]
    public void CategoryOfMatchesTheRowEachToolWasPlacedIn()
    {
        foreach (EditorToolCategory category in EditorShellContract.CategoryOrder)
        {
            foreach (string name in EditorShellContract.ToolsIn(category))
            {
                // FilledRectangle is a second button for the Rectangle tool.
                string toolName = name == "FilledRectangle" ? "Rectangle" : name;
                var tool = Enum.Parse<EditorTool>(toolName);
                Assert.Equal(category, EditorShellContract.CategoryOf(tool));
            }
        }

        // The pointer pair belongs to no category, so picking one leaves the sub-row alone.
        Assert.Null(EditorShellContract.CategoryOf(EditorTool.Select));
        Assert.Null(EditorShellContract.CategoryOf(EditorTool.Pan));
        // The curved arrow lights the Shapes row, because it is still the arrow tool.
        Assert.Equal(EditorToolCategory.Shapes, EditorShellContract.CategoryOf(EditorTool.CurvedArrow));
    }

    /// <summary>
    /// Clicking a category arms its first tool — except Image, whose row is one-shot
    /// actions plus crop: arming crop there would drop the user into a mode they did not ask
    /// for, and every armed default has to belong to the category that armed it.
    /// </summary>
    [Fact]
    public void CategoryDefaultsArmAToolFromTheirOwnRowExceptImage()
    {
        Assert.Null(EditorShellContract.DefaultToolFor(EditorToolCategory.Image));
        foreach (EditorToolCategory category in EditorShellContract.CategoryOrder)
        {
            if (EditorShellContract.DefaultToolFor(category) is not EditorTool tool) continue;
            Assert.Equal(category, EditorShellContract.CategoryOf(tool));
            Assert.Contains(tool.ToString(), EditorShellContract.ToolsIn(category));
        }
    }

    [Fact]
    public void Contexts_PutImmediateControlsFirstAndKeepAdvancedWinShotOptions()
    {
        Assert.Equal(EditorContextControls.None,
            EditorShellContract.ContextFor(EditorTool.Select, filledRectangle: false));
        Assert.Equal(EditorContextControls.Color | EditorContextControls.Thickness |
                     EditorContextControls.Fill | EditorContextControls.Opacity,
            EditorShellContract.ContextFor(EditorTool.Rectangle, filledRectangle: false));
        Assert.Equal(EditorContextControls.Color | EditorContextControls.Fill |
                     EditorContextControls.Opacity,
            EditorShellContract.ContextFor(EditorTool.Rectangle, filledRectangle: true));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Arrow, false)
            .HasFlag(EditorContextControls.ArrowStyle));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Text, false)
            .HasFlag(EditorContextControls.TextStyle));
        // Redaction shows its style menu and a continuous strength, and nothing else:
        // colour and thickness mean nothing to a mark whose job is to hide pixels.
        Assert.Equal(EditorContextControls.RedactStyle | EditorContextControls.RedactStrength,
            EditorShellContract.ContextFor(EditorTool.Redact, false));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Step, false)
            .HasFlag(EditorContextControls.StepMode));
        // Rotate / flip / image size came out of the "More" popup and onto the crop bar.
        Assert.Equal(EditorContextControls.CropRatio | EditorContextControls.CropTools,
            EditorShellContract.ContextFor(EditorTool.Crop, false));
        // The curved arrow wears the arrow tool's controls, style menu included.
        Assert.Equal(EditorShellContract.ContextFor(EditorTool.Arrow, false),
            EditorShellContract.ContextFor(EditorTool.CurvedArrow, false));
    }

    [Fact]
    public void MarqueePreference_OverridesTheDragDirectionWithoutBreakingAuto()
    {
        var start = new System.Windows.Point(10, 10);
        var right = new System.Windows.Point(60, 40);
        var left = new System.Windows.Point(-20, 40);

        Assert.Equal(MarqueeMode.Window, MarqueeSelection.ModeFor(start, right, MarqueeModePreference.Auto));
        Assert.Equal(MarqueeMode.Crossing, MarqueeSelection.ModeFor(start, left, MarqueeModePreference.Auto));

        Assert.Equal(MarqueeMode.Window, MarqueeSelection.ModeFor(start, left, MarqueeModePreference.AlwaysWindow));
        Assert.Equal(MarqueeMode.Crossing, MarqueeSelection.ModeFor(start, right, MarqueeModePreference.AlwaysCrossing));
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
