using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class AnnotationToolsAdversarialTests
{
    [Fact]
    public void OneVisible_DoesNotInventAFill()
    {
        var stroke = Color.FromArgb(0, 10, 20, 30);
        var fill = Color.FromArgb(0, 40, 50, 60);
        var (s, f) = AnnotationStyle.EnforceOneVisible(stroke, fill);
        Assert.Equal(255, s.A);
        Assert.Equal(10, s.R);
        Assert.Equal(0, f.A);
        Assert.Equal(40, f.R);
    }

    [Fact]
    public void Compose_ZeroOpacityIsFullyTransparentNotClampedToQuarter()
    {
        var c = AnnotationStyle.Compose(Colors.Red, 0);
        Assert.Equal(0, c.A);
        Assert.Equal(255, c.R);
    }

    [Fact]
    public void ParseArrowhead_UnknownFallsBackToSolidTriangleNotNone()
    {
        Assert.Equal(ArrowheadStyle.SolidTriangle, AnnotationStyle.ParseArrowhead("not-a-head", out var start));
        Assert.Equal(ArrowheadStyle.None, start);
    }

    [Fact]
    public void LineStyle_CloudIsNotDashed()
    {
        Assert.Null(AnnotationStyle.DashArray(LineBorderStyle.Cloud));
        Assert.Equal("cloud", AnnotationStyle.ToStorageName(LineBorderStyle.Cloud));
    }

    [Fact]
    public void Callout_ZeroLengthDragStillProducesSeparatedTipAndKnee()
    {
        var layout = CalloutLayout.FromDrag(new Point(50, 50), new Point(50, 50));
        Assert.True((layout.Knee - layout.Tip).Length >= CalloutLayout.MinKneeToTip - 0.01);
        Assert.True(layout.Box.Width >= CalloutLayout.MinBoxWidth);
    }

    [Fact]
    public void Callout_NegativeBoxDragNormalizes()
    {
        var layout = CalloutLayout.FromParts(
            new Point(100, 100), new Point(80, 80), new Rect(new Point(20, 20), new Point(10, 10)));
        Assert.True(layout.Box.Width >= CalloutLayout.MinBoxWidth);
        Assert.True(layout.Box.Height >= CalloutLayout.MinBoxHeight);
    }

    [Theory]
    [InlineData(ArrowheadStyle.None)]
    [InlineData(ArrowheadStyle.SolidTriangle)]
    [InlineData(ArrowheadStyle.VShape)]
    [InlineData(ArrowheadStyle.OpenCircle)]
    [InlineData(ArrowheadStyle.OpenTriangle)]
    [InlineData(ArrowheadStyle.HorizontalLine)]
    public void Arrowhead_StorageRoundTrip(ArrowheadStyle style)
    {
        string stored = AnnotationStyle.ToStorageName(style);
        var parsed = AnnotationStyle.ParseArrowhead(stored, out var start);
        Assert.Equal(style, parsed);
        Assert.Equal(ArrowheadStyle.None, start);
    }

    [Fact]
    public void HeadsFrom_PrefersHeadFieldOverLegacyStyle()
    {
        var a = new AnnotationData { Style = "Double", Head = "openCircle" };
        var (end, start) = AnnotationStyle.HeadsFrom(a);
        Assert.Equal(ArrowheadStyle.OpenCircle, end);
        Assert.Equal(ArrowheadStyle.None, start);
    }

    [Fact]
    public void HeadsFrom_LegacyDoubleStillOpensBothEnds()
    {
        var a = new AnnotationData { Style = "Double" };
        var (end, start) = AnnotationStyle.HeadsFrom(a);
        Assert.Equal(ArrowheadStyle.SolidTriangle, end);
        Assert.Equal(ArrowheadStyle.SolidTriangle, start);
    }

    [Fact]
    public void GroupFor_MapsEveryImportedTool()
    {
        Assert.Equal("Draw", EditorShellContract.GroupFor(EditorTool.Freehand, false));
        Assert.Equal("Draw", EditorShellContract.GroupFor(EditorTool.Highlighter, false));
        Assert.Equal("Shape", EditorShellContract.GroupFor(EditorTool.Arrow, false));
        Assert.Equal("Shape", EditorShellContract.GroupFor(EditorTool.Line, false));
        Assert.Equal("Shape", EditorShellContract.GroupFor(EditorTool.Rectangle, true));
        Assert.Equal("Text", EditorShellContract.GroupFor(EditorTool.Text, false));
        Assert.Equal("Text", EditorShellContract.GroupFor(EditorTool.Callout, false));
        Assert.Equal("", EditorShellContract.GroupFor(EditorTool.Pixelate, false));
    }

    [Fact]
    public void SizePresets_IncludeSurveyWidthList()
    {
        Assert.Contains(1, AnnotationStyle.SizePresets);
        Assert.Contains(3, AnnotationStyle.SizePresets);
        Assert.Contains(50, AnnotationStyle.SizePresets);
        Assert.Equal(1, AnnotationStyle.SizePresets[0]);
        Assert.True(AnnotationStyle.SizePresets.Zip(AnnotationStyle.SizePresets.Skip(1), (a, b) => a < b).All(x => x));
    }

    [Fact]
    public void PickerPresets_StartWithTransparentThenSpectrum()
    {
        Assert.Equal(0, AnnotationStyle.PickerPresets[0].A);
        Assert.Equal(16, AnnotationStyle.PickerPresets.Length);
        Assert.Equal(Colors.Black, AnnotationStyle.PickerPresets[^1]);
    }
}
