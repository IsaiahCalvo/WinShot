using System.Linq;
using System.Xml.Linq;
using WinShot.Core;
using Xunit;
using W = System.Windows;
using WM = System.Windows.Media;

namespace WinShot.Tests;

/// <summary>
/// One icon family: one stroke weight, and glyphs scaled by their content so a pin and an X
/// beside it read at the same optical size. Both were audit findings — the pin shipped filled
/// among stroked siblings and rendered ~75% larger than the X on the same card.
/// </summary>
public class IconFamilyTests
{
    private static readonly string[] StrokedAssets =
    [
        "quick-access-background.svg", "quick-access-close.svg", "quick-access-copy.svg",
        "quick-access-edit.svg", "quick-access-flip-horizontal.svg", "quick-access-flip-vertical.svg",
        "quick-access-lock.svg", "quick-access-pin.svg", "quick-access-resize.svg",
        "quick-access-rotate-left.svg", "quick-access-rotate-right.svg", "quick-access-save.svg",
        "quick-access-text.svg", "quick-access-unlock.svg",
    ];

    private static XElement Root(string asset)
    {
        using var stream = typeof(SvgIcons).Assembly.GetManifestResourceStream($"WinShot.Assets.{asset}");
        Assert.NotNull(stream);
        return XDocument.Load(stream!).Root!;
    }

    [Fact]
    public void EveryStrokedGlyph_UsesTheOneStrokeWeight()
    {
        foreach (string asset in StrokedAssets)
        {
            XElement root = Root(asset);
            Assert.Equal("1.85", root.Attribute("stroke-width")?.Value);
            Assert.Equal("none", root.Attribute("fill")?.Value);
            Assert.Equal("currentColor", root.Attribute("stroke")?.Value);
        }
    }

    [Fact]
    public void PinShipsAsAnOutlineDefaultAndAFilledActiveState()
    {
        // Default: stroked, like its thirteen siblings.
        Assert.Equal("none", Root("quick-access-pin.svg").Attribute("fill")?.Value);
        // Active/pinned: the solid mark, the one sanctioned outline/filled pair in the set.
        XElement filled = Root("quick-access-pin-filled.svg");
        Assert.Equal("currentColor", filled.Attribute("fill")?.Value);
        Assert.Null(filled.Attribute("stroke-width"));
    }

    /// <summary>
    /// "Filled" has to actually fill. The asset this replaced was an OUTLINE drawn as an
    /// even-odd fill — an outer contour plus a counter subpath punching the hole back out —
    /// so pressing P swapped one hollow pin for another and no state change was legible at
    /// 16-32px. SvgIcons never reads fill-rule and WPF's Geometry.Parse defaults to EvenOdd,
    /// so a second subpath is a hole either way: one contour, no fill-rule, is the assertion.
    /// </summary>
    [Fact]
    public void ThePinnedGlyphIsSolid_NotAnOutlineDrawnAsAFill()
    {
        XElement filled = Root("quick-access-pin-filled.svg");
        var paths = filled.Descendants().Where(e => e.Name.LocalName == "path").ToArray();
        XElement path = Assert.Single(paths);

        string d = path.Attribute("d")!.Value;
        int subpaths = d.Count(c => c is 'M' or 'm');
        Assert.True(subpaths == 1, $"the pinned glyph has {subpaths} subpaths; a second one is a hole");
        Assert.Null(path.Attribute("fill-rule"));
        Assert.Null(path.Attribute("clip-rule"));

        // And the direct reading, through the same parser SvgIcons uses: the middle of the
        // pin's head is ink. On the outline-as-a-fill asset it was the hole.
        WM.Geometry solid = WM.Geometry.Parse(d);
        Assert.True(solid.FillContains(new W.Point(12, 12)), "the pinned glyph's head is hollow");
        Assert.False(solid.FillContains(new W.Point(2, 12)), "the pinned glyph bleeds outside the pin");
    }

    /// <summary>
    /// And it has to be the SAME pin. The asset this replaced was the previous diagonal
    /// thumbtack kept verbatim, so the active state substituted a different drawing rather
    /// than filling the one on screen.
    /// </summary>
    [Fact]
    public void ThePinnedGlyphIsTheSameSilhouetteAsTheOutline()
    {
        W.Rect outline = SvgIcons.ContentExtent("quick-access-pin.svg")!.Value;
        W.Rect solid = SvgIcons.ContentExtent("quick-access-pin-filled.svg")!.Value;

        // The solid path is the outline's centreline, so it sits inside the stroked ink by
        // about half a stroke on each side — but it is the same upright pin, not a diagonal
        // thumbtack with a tail (which measured 21.3 x 21.3, an aspect of 1.00 against 0.73).
        double outlineAspect = outline.Width / outline.Height;
        double solidAspect = solid.Width / solid.Height;
        Assert.True(
            Math.Abs(outlineAspect - solidAspect) < 0.08,
            $"outline is {outline.Width:0.0}x{outline.Height:0.0}, solid is {solid.Width:0.0}x{solid.Height:0.0}");
    }

    // Two different measurements, and the difference is the whole point of these tests.
    //
    //   Ink()      — the asset's raw stroke-inclusive art on its own 24-unit grid. Nothing
    //                has normalized it, so two assets genuinely differ here.
    //   Rendered() — what the renderer draws into a button of that many pixels. Fit() takes
    //                min(box/w, box/h), so the long edge always lands on box: this number is
    //                pixelSize x ContentBoxFraction for EVERY asset, by construction.
    //
    // Asserting that two Rendered() values match therefore proves nothing at all — that was
    // the hole this file shipped with. The assertions below compare Ink() to Ink(), and
    // Rendered() to the absolute pixel sizes the card drew before the family was normalized.
    private static double Ink(string asset)
    {
        W.Rect? extent = SvgIcons.ContentExtent(asset);
        Assert.NotNull(extent);
        return Math.Max(extent!.Value.Width, extent.Value.Height);
    }

    private static double Rendered(string asset, int pixelSize)
    {
        W.Size? extent = SvgIcons.OpticalExtent(asset, pixelSize);
        Assert.NotNull(extent);
        return Math.Max(extent!.Value.Width, extent.Value.Height);
    }

    /// <summary>What the old viewBox fit drew a 24-unit grid at: the button less a 6% inset
    /// each side, with the inset floored at a whole pixel (which only bites below 17 px).</summary>
    private static double ShippedEdge(string asset, int pixelSize)
    {
        double inset = Math.Max(1, pixelSize * 0.06);
        return Ink(asset) * (pixelSize - inset * 2) / 24.0;
    }

    [Fact]
    public void TheFractionIsReadOffTheCardsX_NotChosen()
    {
        // ContentBoxFraction is derived from this number. If the X's art ever changes, this
        // fails here rather than silently resizing all fourteen glyphs and the copy pill.
        Assert.Equal(SvgIcons.CloseInkOnTheGrid, Ink("quick-access-close.svg"), 2);
    }

    [Theory]
    // The three sizes the card actually asks for: max(14, round(IconSize * 0.56)) where
    // IconSize = Scale(28, dpi) — so 16 px at 100%, 24 px at 150%, 32 px at 200%.
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void TheCardsXStillDrawsTheSizeItShipped(int pixelSize)
    {
        // The pinned card's idle look is not this track's to change: normalizing the family
        // to the X's own size is the whole of the approved swap. A hand-typed fraction is
        // how that goes wrong silently, and it is not checkable by eye at 16px — a review
        // of this file mis-measured the X's ink and called the family 11-22% too small.
        // This asserts pixels against the pre-normalization algorithm instead.
        double shipped = ShippedEdge("quick-access-close.svg", pixelSize);
        double now = Rendered("quick-access-close.svg", pixelSize);
        Assert.True(
            Math.Abs(now - shipped) <= 0.1,
            $"the X draws {now:0.00}px in a {pixelSize}px button; it shipped at {shipped:0.00}px");
    }

    [Fact]
    public void TheArtGenuinelyDiffers_SoNormalisingIsDoingWork()
    {
        // The guard on the guard: if every asset's raw ink were already the same size there
        // would be nothing to equalize and the tests below would pass on an empty change.
        double close = Ink("quick-access-close.svg");
        double pin = Ink("quick-access-pin.svg");
        Assert.True(
            Math.Abs(pin - close) > 2,
            $"pin ink {pin:0.0} vs close ink {close:0.0} on the same 24-grid — nothing to normalize");
    }

    [Fact]
    public void GlyphsAreScaledByContent_SoTheyShareAnOpticalSize()
    {
        // Each glyph, measured from its own art, lands on the size the X shipped at. This is
        // an absolute check against ShippedEdge(close), not a comparison of two already-
        // normalized outputs.
        double target = ShippedEdge("quick-access-close.svg", 24);
        foreach (string asset in StrokedAssets)
        {
            double edge = Rendered(asset, 24);
            Assert.True(
                Math.Abs(edge - target) <= 0.1,
                $"{asset} draws {edge:0.00}px in a 24px button; the family sits at {target:0.00}px");
        }
    }

    [Fact]
    public void PinAndCloseRenderAtTheSameOpticalSize_AndWouldNotHaveUnderAViewBoxFit()
    {
        // The original defect: the pin fitted to its viewBox rather than its art rendered
        // ~75% larger than the X next to it on the same card.
        Assert.True(ShippedEdge("quick-access-pin.svg", 24) - ShippedEdge("quick-access-close.svg", 24) > 2,
            "a viewBox fit no longer mis-sizes the pin — this test is measuring nothing");
        Assert.Equal(Rendered("quick-access-close.svg", 24), Rendered("quick-access-pin.svg", 24), 1);
    }
}
