using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class ImageOverlayContextTests
{
    [Fact]
    public void ThePointerOffersAnImagesOpacity()
    {
        // Every other mark hides its transparency in its colour's alpha; an image has no
        // colour, so the pointer has to offer the dial or nothing does.
        EditorContextControls controls = EditorShellContract.ContextFor(
            EditorTool.Select, filledRectangle: false, selectedType: AnnotationData.TypeImage);

        Assert.Equal(EditorContextControls.Opacity, controls);
    }

    [Fact]
    public void ThePointerOffersNothingForEveryOtherSelection()
    {
        // The pointer draws nothing, so it must not grow a whole style bar; only the one
        // property that has nowhere else to live.
        foreach (string type in new[]
                 {
                     AnnotationData.TypeRectangle, AnnotationData.TypeArrow, AnnotationData.TypeText,
                     AnnotationData.TypeRedact, AnnotationData.TypeMagnifier, AnnotationData.TypeStep,
                 })
        {
            Assert.Equal(
                EditorContextControls.None,
                EditorShellContract.ContextFor(EditorTool.Select, false, type));
        }
    }

    [Fact]
    public void ThePointerWithNothingPickedStillOffersNothing() =>
        Assert.Equal(
            EditorContextControls.None,
            EditorShellContract.ContextFor(EditorTool.Select, false, selectedType: null));

    [Fact]
    public void ASelectionDoesNotOverrideAnArmedDrawingTool()
    {
        // The row describes what you are about to DRAW whenever you are about to draw
        // something; only the pointer defers to the selection.
        EditorContextControls armed = EditorShellContract.ContextFor(
            EditorTool.Rectangle, filledRectangle: false, selectedType: AnnotationData.TypeImage);

        Assert.Equal(EditorShellContract.ContextFor(EditorTool.Rectangle, false), armed);
        Assert.True(armed.HasFlag(EditorContextControls.Color));
    }

    [Fact]
    public void TheTwoArgumentFormStillMeansTheSameThing()
    {
        // The overload without a selection is the one every existing caller uses.
        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
        {
            Assert.Equal(
                EditorShellContract.ContextFor(tool, false),
                EditorShellContract.ContextFor(tool, false, selectedType: null));
        }
    }
}
