using System.Windows.Media;
using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class HighlighterBlendTests
{
    [Fact]
    public void MultiplyChannel_OpaqueYellowOnWhiteStaysYellowish()
    {
        // White * yellow = yellow. Full alpha.
        Assert.Equal(255, HighlighterBlend.MultiplyChannel(255, 255, 255));
        Assert.Equal(235, HighlighterBlend.MultiplyChannel(255, 235, 255));
        Assert.Equal(59, HighlighterBlend.MultiplyChannel(255, 59, 255));
    }

    [Fact]
    public void MultiplyChannel_HalfAlphaYellowOnBlueTintsWithoutWashingToWhite()
    {
        byte live = HighlighterBlend.MultiplyChannel(backdrop: 200, source: 255, sourceAlpha: 128);
        // src-over yellow would be ~227; multiply is darker.
        Assert.InRange(live, 200, 230);
        Assert.True(live < 227);
    }

    [Fact]
    public void MultiplyOnto_OpaqueRedOnWhiteBecomesRed()
    {
        using var dest = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        using var overlay = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        dest.SetPixel(0, 0, SD.Color.FromArgb(255, 255, 255, 255));
        overlay.SetPixel(0, 0, SD.Color.FromArgb(255, 255, 0, 0));
        HighlighterBlend.MultiplyOnto(dest, overlay);
        var c = dest.GetPixel(0, 0);
        Assert.Equal(255, c.R);
        Assert.Equal(0, c.G);
        Assert.Equal(0, c.B);
    }

    [Fact]
    public void MultiplyOnto_HalfYellowDarkensBlueBackdrop()
    {
        using var dest = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        using var overlay = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        dest.SetPixel(0, 0, SD.Color.FromArgb(255, 0, 0, 200));
        overlay.SetPixel(0, 0, SD.Color.FromArgb(128, 255, 235, 59));
        HighlighterBlend.MultiplyOnto(dest, overlay);
        var c = dest.GetPixel(0, 0);
        byte expectedB = HighlighterBlend.MultiplyChannel(200, 59, 128);
        Assert.Equal(expectedB, c.B);
        Assert.True(c.B < 200, $"blue channel should darken, got {c}");
    }

    [Fact]
    public void SrcOver_TransparentOverlayLeavesDest()
    {
        using var dest = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        using var overlay = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        dest.SetPixel(0, 0, SD.Color.FromArgb(255, 10, 20, 30));
        overlay.SetPixel(0, 0, SD.Color.FromArgb(0, 255, 0, 0));
        HighlighterBlend.SrcOver(dest, overlay);
        Assert.Equal(SD.Color.FromArgb(255, 10, 20, 30), dest.GetPixel(0, 0));
    }

    [Fact]
    public void BlendBrush_FiltersLikeThePageUnderneath()
    {
        // Survey's mix-blend-mode is evaluated by the browser per DEVICE pixel, so content under
        // a highlight is exactly as crisp as content beside it at any zoom
        // (svgAnnotationRenderers.jsx:272, :300-318). WPF has to bake the blend into an
        // ImageBrush and resample it through the zoom transform, so the least it can do is use
        // the SAME filter as the page: EditorWindow.SetBaseTiles pins BaseTiles to HighQuality
        // (Fant). The highlighter brush was the one bitmap in the editor with no mode set, so it
        // resampled on the default Linear and the highlighted band read softer than everything
        // around it at every zoom other than 100%.
        using var backdrop = new SD.Bitmap(40, 20, SD.Imaging.PixelFormat.Format32bppArgb);
        var brush = HighlighterBlend.PreviewBrush(backdrop, Color.FromArgb(0x4D, 0xFF, 0xC1, 0x07));
        Assert.IsType<ImageBrush>(brush);
        Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(brush));
    }
}
