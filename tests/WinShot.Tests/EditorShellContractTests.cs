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
        // Survey's bar, with WinShot's fifth group: the two navigation tools first, then
        // one button per group. Eyedropper is not on it — it lives inside the colour picker.
        Assert.Equal(new[] { "Pan", "Select" }, EditorShellContract.PointerToolOrder);
        Assert.Equal(new[]
        {
            "Pan", "Select", "Draw", "Shapes", "Text", "Effects", "Image",
        }, EditorShellContract.PrimaryToolOrder);
        Assert.Equal(new[] { "Freehand", "Highlighter", "Eraser" },
            EditorShellContract.ToolsIn(EditorToolCategory.Draw));
        // Survey's own order (PDFViewer.jsx:34035-34045): Polygon and Polyline sit between the
        // closed shapes and the open ones, because that is what they bridge — a polygon is a
        // many-sided Rectangle, a polyline a many-segment Line.
        Assert.Equal(new[] { "Rectangle", "Ellipse", "Polygon", "Polyline", "Line", "Arrow", "Step" },
            EditorShellContract.ToolsIn(EditorToolCategory.Shapes));
        Assert.Equal(new[] { "Text", "Callout" }, EditorShellContract.ToolsIn(EditorToolCategory.Text));
        // One redaction tool, not Survey's two: pixelate and blur are STYLES on the mark it
        // draws. The three marks that act on the pixels underneath share the Effects row.
        Assert.Equal(new[] { "Redact", "Spotlight", "Magnifier" },
            EditorShellContract.ToolsIn(EditorToolCategory.Redact));
        Assert.Equal(new[] { "Crop" }, EditorShellContract.ToolsIn(EditorToolCategory.Image));
        Assert.Empty(EditorShellContract.MoreToolOrder);

        string[] buttons = EditorShellContract.PointerToolOrder
            .Concat(EditorShellContract.CategoryOrder.SelectMany(EditorShellContract.ToolsIn))
            .ToArray();
        Assert.Equal(buttons.Length, buttons.Distinct(StringComparer.Ordinal).Count());

        string[] reachable = buttons
            .Append(nameof(EditorTool.Eyedropper))     // inside the colour picker
            .Append(nameof(EditorTool.CurvedArrow))    // the Curved row of the arrow menu
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
        // An outlined shape owns a stroke, so it owns the stroke's two choices too: the
        // dash pattern and the hand-drawn quality. A filled one has no stroke to dash,
        // but it keeps Style because "sketchy" is a property of the whole mark.
        Assert.Equal(EditorContextControls.Color | EditorContextControls.Thickness |
                     EditorContextControls.Fill | EditorContextControls.Opacity |
                     EditorContextControls.LineStyle | EditorContextControls.FillStrokeTabs |
                     EditorContextControls.CloudBump,
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
        Assert.False(EditorShellContract.ContextFor(EditorTool.Text, false)
            .HasFlag(EditorContextControls.LineStyle));
        // Redaction shows its style dropdown and a continuous strength, and nothing else:
        // colour and thickness mean nothing to a mark whose job is to hide pixels.
        Assert.Equal(EditorContextControls.RedactStyle | EditorContextControls.RedactStrength,
            EditorShellContract.ContextFor(EditorTool.Redact, false));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Magnifier, false)
            .HasFlag(EditorContextControls.Magnification));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Eraser, false)
            .HasFlag(EditorContextControls.EraserMode));
        Assert.True(EditorShellContract.ContextFor(EditorTool.Step, false)
            .HasFlag(EditorContextControls.StepMode));
        Assert.Equal("Shapes", EditorShellContract.GroupFor(EditorTool.Step, false));
        Assert.Equal("Effects", EditorShellContract.GroupFor(EditorTool.Redact, false));
        Assert.Equal("Image", EditorShellContract.GroupFor(EditorTool.Crop, false));
        Assert.Equal("", EditorShellContract.GroupFor(EditorTool.Select, false));
        Assert.Equal(EditorTool.Text, EditorShellContract.ToolForAnnotation(AnnotationData.TypeText));
        Assert.Equal(EditorTool.Callout, EditorShellContract.ToolForAnnotation(AnnotationData.TypeCallout));
        Assert.Equal(EditorTool.Arrow, EditorShellContract.ToolForAnnotation(AnnotationData.TypeArrow));
        Assert.Equal(EditorTool.Redact, EditorShellContract.ToolForAnnotation(AnnotationData.TypeRedact));
        Assert.Equal(EditorTool.Magnifier, EditorShellContract.ToolForAnnotation(AnnotationData.TypeMagnifier));
        Assert.Null(EditorShellContract.ToolForAnnotation(AnnotationData.TypeImage));
        // Rotate / flip / image size came out of Survey's "More" popup and onto the crop bar.
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

    /// <summary>
    /// The row does not wrap, so the minimum window width has to cover the widest the row
    /// ever gets. The measured worst case (the crop group) is asserted against the REAL
    /// controls in <c>ThemedWindowTests</c>; this is the arithmetic half.
    /// </summary>
    [Fact]
    public void PrimaryToolbar_FitsTheMinimumWindowAndNothingNarrower()
    {
        Assert.True(EditorShellContract.FitsPrimaryToolbar(
            EditorShellContract.MinimumEditorLogicalWidth));
        // One DIP under the budget must fail, or the check is decorative.
        Assert.False(EditorShellContract.FitsPrimaryToolbar(
            EditorShellContract.PrimaryToolbarLogicalWidth + EditorShellContract.ToolbarOuterMargin - 1));
    }

    /// <summary>
    /// The DPI check that means something: a monitor's DIP width is its pixels divided by
    /// the scale, and the editor's minimum has to fit inside it. The old form multiplied
    /// both sides of the comparison by the scale, so all four rows asserted the same
    /// scale-free inequality and 200 % was never actually tested.
    /// </summary>
    [Theory]
    [InlineData(1920, 1.0)]
    [InlineData(1920, 1.25)]
    [InlineData(1920, 1.5)]   // Isaiah's primary: 1280 DIP
    [InlineData(2560, 1.5)]
    [InlineData(3840, 2.0)]
    public void MinimumEditorWidth_FitsEveryDisplayWindowsRecommendsThatScaleFor(
        double monitorWidth, double dpiScale)
    {
        Assert.True(EditorShellContract.FitsOnMonitor(monitorWidth, dpiScale),
            $"{monitorWidth}px at {dpiScale:0.##}x is {monitorWidth / dpiScale:0} DIP, " +
            $"under the {EditorShellContract.MinimumEditorLogicalWidth} DIP minimum.");
    }

    /// <summary>The same check has to be able to say no: 1920 px at 200 % is 960 DIP.</summary>
    [Fact]
    public void MinimumEditorWidth_DoesNotFitA1920DisplayAtTwoHundredPercent()
    {
        Assert.False(EditorShellContract.FitsOnMonitor(1920, 2.0));
    }
}
