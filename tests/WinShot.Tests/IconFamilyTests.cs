using System.Xml.Linq;
using WinShot.Core;
using Xunit;
using W = System.Windows;

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

    // Optical size is measured through SvgIcons.OpticalExtent — the renderer's own content
    // bounds and scale — not by scanning a rasterized bitmap. RenderTargetBitmap produces a
    // fully transparent image in a test host that has never shown a window, so a pixel scan
    // here measures nothing and passes or fails on test ordering rather than on the assets.
    private static double LongEdge(string asset, int pixelSize = 20)
    {
        W.Size? extent = SvgIcons.OpticalExtent(asset, pixelSize);
        Assert.NotNull(extent);
        return Math.Max(extent!.Value.Width, extent.Value.Height);
    }

    /// <summary>
    /// The size the card already shipped: the X drew 10.2px inside a 20px button, and every
    /// glyph is normalized DOWN to that. Fitting content to the whole button instead is also
    /// "content-scaled", but it equalizes the family by inflating the X 73% and the lock
    /// 144% — a restyle of the pinned quick-actions card rather than a tidy-up of it.
    /// </summary>
    private const double ShippedEdgeIn20pxBox = 10.2;

    [Fact]
    public void GlyphsAreScaledByContent_SoTheyShareAnOpticalSize()
    {
        // Every icon draws the same optical extent: that is what "content-scaled" buys.
        // Before, the pin's art occupied ~55% of its 24-grid and rendered small next to the
        // X's ~75%, or huge, depending on which asset drifted.
        foreach (string asset in StrokedAssets)
        {
            double edge = LongEdge(asset);
            Assert.True(
                Math.Abs(edge - ShippedEdgeIn20pxBox) <= 0.5,
                $"{asset} draws {edge:0.0}px in a 20px box; the family sits at {ShippedEdgeIn20pxBox}px");
        }
    }

    [Fact]
    public void TheFamilyIsNormalisedDownToTheSizeTheCardShipped()
    {
        // The guard on the guard. A content fit that targets the whole button passes the
        // "they all match" test above just as happily while making every glyph on the pinned
        // card ~73% wider, which is exactly how that shipped once already.
        double close = LongEdge("quick-access-close.svg");
        Assert.True(close < 12, $"close draws {close:0.0}px in a 20px box — the family grew");
        Assert.True(close > 8, $"close draws {close:0.0}px in a 20px box — the family shrank");
    }

    [Fact]
    public void PinAndCloseRenderAtTheSameOpticalSize()
    {
        // The original defect: a filled pin among stroked siblings, fitted to its viewBox
        // rather than its art, rendering ~75% larger than the X next to it on the same card.
        double pin = LongEdge("quick-access-pin.svg");
        double close = LongEdge("quick-access-close.svg");
        Assert.True(Math.Abs(pin - close) <= 1, $"pin {pin:0.0}px vs close {close:0.0}px");
    }

    [Fact]
    public void FittingUsesTheArt_NotTheDeclaredViewBox()
    {
        // Guards the fix itself: the pin's art does not fill its 24-grid, so a viewBox fit
        // would leave it visibly small. If someone reverts to scaling by the box, the pin
        // stops filling its 20px button and this fails before the eye catches it.
        XElement pin = Root("quick-access-pin.svg");
        Assert.Equal("0 0 24 24", pin.Attribute("viewBox")?.Value);

        List<XElement> paths = pin.Descendants().Where(e => e.Name.LocalName == "path").ToList();
        Assert.NotEmpty(paths);
        // A viewBox fit would leave the pin's art at ~16px where the X sits at ~10 — the
        // 57% mismatch the swap exists to remove. Matching its sibling is the proof.
        Assert.True(
            Math.Abs(LongEdge("quick-access-pin.svg") - LongEdge("quick-access-close.svg")) <= 0.5);
    }
}
