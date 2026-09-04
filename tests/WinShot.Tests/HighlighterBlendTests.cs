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

    [Fact]
    public void ApplyLiveFill_MapsTheBlendOntoTheSourcePixelGridOneToOne()
    {
        // This is why WinShot does NOT need to re-composite the backdrop at the current device
        // scale. The blended crop is one bitmap pixel per SOURCE pixel, pinned to an absolute
        // viewport whose origin is integral and whose size equals the pixel size - the very same
        // grid, at the very same scale, as BaseTiles. Both therefore reach the screen through
        // exactly ONE HighQuality (Fant) resample, so the highlighted band is as crisp as the
        // page beside it (harness ink-eraser-audit.txt: HL-FIDELITY dy=0 meanAbsErr=0.02,
        // worstPixelErr=1 against the exact CSS multiply). Building the backdrop at zoom*dpi
        // instead would upsample the screenshot and then resample THAT - two filters over source
        // pixels that hold no extra detail - which is strictly worse than this.
        RunSta(() =>
        {
            var ink = new System.Windows.Shapes.Path
            {
                Data = new RectangleGeometry(new System.Windows.Rect(10.5, 20.25, 30, 12)),
            };
            using var backdrop = new SD.Bitmap(200, 100, SD.Imaging.PixelFormat.Format32bppArgb);
            HighlighterBlend.ApplyLiveFill(ink, backdrop, Color.FromArgb(0x4D, 0xFF, 0xC1, 0x07));

            var brush = Assert.IsType<ImageBrush>(ink.Fill);
            Assert.Equal(BrushMappingMode.Absolute, brush.ViewportUnits);
            Assert.Equal(Stretch.Fill, brush.Stretch);
            var src = (System.Windows.Media.Imaging.BitmapSource)brush.ImageSource;
            Assert.Equal(brush.Viewport.X, Math.Floor(brush.Viewport.X));
            Assert.Equal(brush.Viewport.Y, Math.Floor(brush.Viewport.Y));
            Assert.Equal(src.PixelWidth, (int)brush.Viewport.Width);
            Assert.Equal(src.PixelHeight, (int)brush.Viewport.Height);
            // ...and it actually covers the ink, so the 1:1 map is not vacuously true.
            Assert.True(brush.Viewport.Contains(ink.Data!.Bounds));
        });
    }

    /// <summary>
    /// A WPF Path is a FrameworkElement, so it can only be constructed on an STA thread. No
    /// Application instance on purpose - xUnit runs test classes in parallel and creating one
    /// races the other STA tests over the WPF Application singleton (as DragPreviewTests notes).
    /// </summary>
    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
