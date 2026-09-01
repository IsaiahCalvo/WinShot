using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class AnnotationSizeTests
{
    [Fact]
    public void Presets_MatchSurveysWidthList()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 32, 50 }, AnnotationSize.Presets);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]      // the whole point: a value the old 2/4/6 buttons could not express
    [InlineData(50, 50)]
    [InlineData(999, 50)]
    [InlineData(-4, 1)]
    public void Clamp_HoldsTheOneToFiftyRange(int input, int expected)
    {
        Assert.Equal(expected, AnnotationSize.Clamp(input));
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("  12  ", 12)]
    [InlineData("3px", 3)]      // stray characters are ignored, digits win
    [InlineData("0", 1)]        // clamped, not rejected
    [InlineData("500", 50)]
    public void TryParse_AcceptsDigitsAndClamps(string text, int expected)
    {
        Assert.True(AnnotationSize.TryParse(text, out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData(null)]
    public void TryParse_LeavesValueUnchangedWhenNothingNumericWasTyped(string? text)
    {
        // Mid-edit an empty box must not snap the size to a default.
        Assert.False(AnnotationSize.TryParse(text, out _));
    }

    [Fact]
    public void PreviewThickness_RisesWithThePresetAndStaysBounded()
    {
        double thinnest = AnnotationSize.PreviewThickness(AnnotationSize.Presets[0]);
        double thickest = AnnotationSize.PreviewThickness(AnnotationSize.Presets[^1]);

        Assert.Equal(1, thinnest);
        Assert.Equal(14, thickest);

        for (int i = 1; i < AnnotationSize.Presets.Length; i++)
        {
            double prev = AnnotationSize.PreviewThickness(AnnotationSize.Presets[i - 1]);
            double cur = AnnotationSize.PreviewThickness(AnnotationSize.Presets[i]);
            Assert.True(cur >= prev, $"preset {AnnotationSize.Presets[i]} previewed thinner than the one before it");
        }
    }

    [Fact]
    public void Label_ReadsAsSizeForTextAndCounterAndWidthForStrokes()
    {
        Assert.Equal("Size", AnnotationSize.LabelFor(EditorTool.Text));
        Assert.Equal("Size", AnnotationSize.LabelFor(EditorTool.Step));
        Assert.Equal("Width", AnnotationSize.LabelFor(EditorTool.Rectangle));
        Assert.Equal("Width", AnnotationSize.LabelFor(EditorTool.Freehand));
    }
}

public class ToolStyleTests
{
    private static ToolStyle Red => ToolPreferences.DefaultFor(EditorTool.Rectangle);

    [Fact]
    public void FillOpacityZero_MeansNoFill()
    {
        var style = Red.WithFillOpacity(0);
        Assert.False(style.HasFill);
        Assert.Null(style.FillBrushColor);
    }

    [Fact]
    public void FillOpacity_BakesIntoTheAlphaChannel()
    {
        var style = Red.WithFillColor(Colors.White).WithFillOpacity(50);
        Assert.True(style.HasFill);
        Color fill = style.FillBrushColor!.Value;
        Assert.Equal(128, fill.A);
        Assert.Equal(0xFF, fill.R);
    }

    [Fact]
    public void BorderOpacity_NeverDropsBelowTheLegibilityFloor()
    {
        var style = Red.WithBorderOpacity(0);
        Assert.Equal(ToolStyle.MinBorderOpacity, style.BorderOpacity);
        Assert.Equal((byte)Math.Round(255 * ToolStyle.MinBorderOpacity / 100.0), style.BorderBrushColor.A);
    }

    [Fact]
    public void BorderOpacity_ClampsAboveOneHundred()
    {
        Assert.Equal(100, Red.WithBorderOpacity(400).BorderOpacity);
    }

    [Fact]
    public void WithWidth_ClampsToTheSizeRange()
    {
        Assert.Equal(50, Red.WithWidth(9999).Width);
        Assert.Equal(1, Red.WithWidth(0).Width);
        Assert.Equal(7, Red.WithWidth(7).Width);
    }
}

public class ToolPreferencesTests
{
    [Fact]
    public void Defaults_MirrorSurveysPerToolPreferences()
    {
        var prefs = new ToolPreferences();

        // Pen: thin red line.
        var pen = prefs.For(EditorTool.Freehand);
        Assert.Equal(3, pen.Width);
        Assert.Equal(100, pen.BorderOpacity);
        Assert.Equal(ToolPreferences.DefaultRed, pen.BorderColor);

        // Highlighter: fat, yellow, half transparent.
        var hl = prefs.For(EditorTool.Highlighter);
        Assert.Equal(20, hl.Width);
        Assert.Equal(50, hl.BorderOpacity);
        Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0x00), hl.BorderColor);

        // Shapes: thin red outline, no fill.
        var rect = prefs.For(EditorTool.Rectangle);
        Assert.Equal(2, rect.Width);
        Assert.False(rect.HasFill);
    }

    [Fact]
    public void EachToolKeepsItsOwnStyle()
    {
        var prefs = new ToolPreferences();

        prefs.Update(EditorTool.Freehand, s => s.WithWidth(12).WithBorderColor(Colors.Blue));

        // The tool that was changed remembers it...
        Assert.Equal(12, prefs.For(EditorTool.Freehand).Width);
        Assert.Equal(Colors.Blue, prefs.For(EditorTool.Freehand).BorderColor);

        // ...and its neighbours are untouched. This is the whole point of per-tool memory.
        Assert.Equal(20, prefs.For(EditorTool.Highlighter).Width);
        Assert.Equal(2, prefs.For(EditorTool.Rectangle).Width);
        Assert.Equal(ToolPreferences.DefaultRed, prefs.For(EditorTool.Rectangle).BorderColor);
    }

    [Fact]
    public void OnlyRectangleAndEllipseCarryAFillChannel()
    {
        Assert.True(ToolPreferences.SupportsFill(EditorTool.Rectangle));
        Assert.True(ToolPreferences.SupportsFill(EditorTool.Ellipse));
        Assert.False(ToolPreferences.SupportsFill(EditorTool.Arrow));
        Assert.False(ToolPreferences.SupportsFill(EditorTool.Freehand));
        Assert.False(ToolPreferences.SupportsFill(EditorTool.Text));
    }

    [Fact]
    public void EveryToolHasADefault()
    {
        var prefs = new ToolPreferences();
        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
        {
            var style = prefs.For(tool);
            Assert.InRange(style.Width, AnnotationSize.MinWidth, AnnotationSize.MaxWidth);
            Assert.InRange(style.BorderOpacity, ToolStyle.MinBorderOpacity, 100);
        }
    }

    [Fact]
    public void RoundTripsThroughSettings()
    {
        var prefs = new ToolPreferences();
        prefs.Update(EditorTool.Rectangle, s => s
            .WithBorderColor(Color.FromRgb(0x12, 0x34, 0x56))
            .WithBorderOpacity(60)
            .WithFillColor(Color.FromRgb(0xAB, 0xCD, 0xEF))
            .WithFillOpacity(35)
            .WithWidth(17));
        prefs.Update(EditorTool.Highlighter, s => s.WithWidth(44));

        var restored = ToolPreferences.Deserialize(prefs.Serialize());

        var rect = restored.For(EditorTool.Rectangle);
        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), rect.BorderColor);
        Assert.Equal(60, rect.BorderOpacity);
        Assert.Equal(Color.FromRgb(0xAB, 0xCD, 0xEF), rect.FillColor);
        Assert.Equal(35, rect.FillOpacity);
        Assert.Equal(17, rect.Width);
        Assert.Equal(44, restored.For(EditorTool.Highlighter).Width);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("Rectangle=notacolor,100,FFFFFF,0,2")]
    [InlineData("NotATool=FF0000,100,FFFFFF,0,2")]
    [InlineData("Rectangle=FF0000,100,FFFFFF,0")]      // too few fields
    public void Deserialize_FallsBackToDefaultsOnJunk(string? stored)
    {
        // A corrupt setting must never stop the editor opening.
        var prefs = ToolPreferences.Deserialize(stored);
        Assert.Equal(2, prefs.For(EditorTool.Rectangle).Width);
        Assert.Equal(ToolPreferences.DefaultRed, prefs.For(EditorTool.Rectangle).BorderColor);
    }

    [Fact]
    public void Deserialize_ClampsOutOfRangeStoredValues()
    {
        var prefs = ToolPreferences.Deserialize("Rectangle=FF0000,5,FFFFFF,900,900");
        var rect = prefs.For(EditorTool.Rectangle);
        Assert.Equal(ToolStyle.MinBorderOpacity, rect.BorderOpacity);
        Assert.Equal(100, rect.FillOpacity);
        Assert.Equal(AnnotationSize.MaxWidth, rect.Width);
    }

    [Fact]
    public void Reset_RestoresOneToolWithoutTouchingTheRest()
    {
        var prefs = new ToolPreferences();
        prefs.Update(EditorTool.Freehand, s => s.WithWidth(30));
        prefs.Update(EditorTool.Highlighter, s => s.WithWidth(30));

        prefs.Reset(EditorTool.Freehand);

        Assert.Equal(3, prefs.For(EditorTool.Freehand).Width);
        Assert.Equal(30, prefs.For(EditorTool.Highlighter).Width);
    }
}
