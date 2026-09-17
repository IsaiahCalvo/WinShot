using System.Linq;
using System.Xml.Linq;
using WinShot.Core;
using Xunit;
using W = System.Windows;

namespace WinShot.Tests;

/// <summary>
/// The quick-actions card is a PINNED surface, and its glyphs are part of what is pinned.
///
/// The sweep replaced <see cref="SvgIcons"/>' viewBox fit with a content-bounds fit that
/// normalized every glyph in the set to one optical size. The X is the smallest art in the
/// set, so "normalizing" scaled the pin, the edit glyph, the save glyph and the copy pill's
/// mark DOWN to it — Isaiah read the whole card as shrunken on the first build he ran, with
/// only the X unchanged. It also swapped the card's angled thumbtack for an upright pushpin
/// so that a solid twin could be swapped in when a capture was pinned.
///
/// All of it is reverted. These tests are the guard: the numbers below are measured off
/// <c>main</c> (21c9677), asset by asset, at the three sizes the card actually asks for, so
/// the next tidy-up of the icon family fails here instead of on Isaiah's screen.
/// </summary>
public class IconFamilyTests
{
    /// <summary>Every glyph the card and its context menu draw.</summary>
    private static readonly string[] CardAssets =
    [
        "quick-access-background.svg", "quick-access-camcorder.svg", "quick-access-close.svg",
        "quick-access-copy.svg", "quick-access-edit.svg", "quick-access-flip-horizontal.svg",
        "quick-access-flip-vertical.svg", "quick-access-lock.svg", "quick-access-pin.svg",
        "quick-access-resize.svg", "quick-access-rotate-left.svg", "quick-access-rotate-right.svg",
        "quick-access-save.svg", "quick-access-text.svg", "quick-access-unlock.svg",
    ];

    private static XElement Root(string asset)
    {
        using var stream = typeof(SvgIcons).Assembly.GetManifestResourceStream($"WinShot.Assets.{asset}");
        Assert.NotNull(stream);
        return XDocument.Load(stream!).Root!;
    }

    /// <summary>
    /// The list above is the whole family, not a sample of it: quick-access-camcorder.svg
    /// shipped outside it once and nothing would have noticed the day it drifted.
    /// </summary>
    [Fact]
    public void TheFamilyListCoversEveryGlyphInTheAssetsFolder()
    {
        string[] onDisk = System.IO.Directory
            .GetFiles(System.IO.Path.Combine(TestPaths.RepoRoot(), "src", "WinShot", "Assets"), "*.svg")
            .Select(System.IO.Path.GetFileName)
            .Order()
            .ToArray()!;
        Assert.Equal(onDisk, CardAssets.Order().ToArray());
    }

    /// <summary>
    /// The card's own pin is a FILLED angled thumbtack and has been since it shipped. The
    /// outline/filled pair the sweep introduced needed a stroked default, which is a different
    /// drawing on a pinned surface — this is the assertion that says so.
    /// </summary>
    [Fact]
    public void TheCardsPinIsTheShippedFilledThumbtack()
    {
        XElement pin = Root("quick-access-pin.svg");
        Assert.Equal("currentColor", pin.Attribute("fill")?.Value);
        Assert.Null(pin.Attribute("stroke-width"));
        Assert.False(
            System.IO.File.Exists(System.IO.Path.Combine(
                TestPaths.RepoRoot(), "src", "WinShot", "Assets", "quick-access-pin-filled.svg")),
            "the solid twin is back; the card's pin is filled already and the swap was reverted");
    }

    /// <summary>
    /// Stroke weight is part of the shipped drawing too. The sweep thinned the seven context-
    /// menu glyphs from 2 to 1.85 to match the card's buttons; on a pinned surface that is a
    /// restyle, and it went out in the same build as the shrink.
    /// </summary>
    [Theory]
    [InlineData("quick-access-close.svg", "1.85")]
    [InlineData("quick-access-copy.svg", "1.85")]
    [InlineData("quick-access-edit.svg", "1.85")]
    [InlineData("quick-access-save.svg", "1.85")]
    [InlineData("quick-access-lock.svg", "1.85")]
    [InlineData("quick-access-unlock.svg", "1.85")]
    [InlineData("quick-access-camcorder.svg", "1.85")]
    [InlineData("quick-access-background.svg", "2")]
    [InlineData("quick-access-flip-horizontal.svg", "2")]
    [InlineData("quick-access-flip-vertical.svg", "2")]
    [InlineData("quick-access-resize.svg", "2")]
    [InlineData("quick-access-rotate-left.svg", "2")]
    [InlineData("quick-access-rotate-right.svg", "2")]
    [InlineData("quick-access-text.svg", "2")]
    public void EachGlyphKeepsTheStrokeWeightItShipped(string asset, string weight)
        => Assert.Equal(weight, Root(asset).Attribute("stroke-width")?.Value);

    /// <summary>
    /// The rendered ink box, per asset, at the three sizes the card asks for —
    /// <c>max(14, round(IconSize * 0.56))</c> where <c>IconSize = Scale(28, dpi)</c>, so
    /// 16 px at 100 %, 24 px at 150 %, 32 px at 200 %.
    ///
    /// These are absolute pixel numbers read off <c>main</c>, not a comparison of two outputs
    /// of the current code — which is how the content fit slipped through review: every glyph
    /// agreed with every other glyph, because the fit had just made them all the same.
    ///
    /// WPF's RenderTargetBitmap draws nothing in a test host that has never shown a window, so
    /// the bitmap itself cannot be scanned here. <see cref="SvgIcons.OpticalExtent"/> is the
    /// renderer's own geometry, measured before rasterization.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedInk))]
    public void EveryCardGlyphDrawsTheInkItDidOnMain(string asset, int pixelSize, double w, double h)
    {
        W.Size? extent = SvgIcons.OpticalExtent(asset, pixelSize);
        Assert.NotNull(extent);
        Assert.Equal(w, extent!.Value.Width, 2);
        Assert.Equal(h, extent.Value.Height, 2);
    }

    /// <summary>
    /// Measured asset-by-asset against a checkout of 21c9677 by running main's SvgIcons and
    /// this one side by side over the same asset set: every glyph main has renders with ZERO
    /// differing pixels at all three sizes. quick-access-camcorder.svg is the one glyph main
    /// does not have (the recording strip is newer), so its row is this branch's own baseline.
    /// </summary>
    public static TheoryData<string, int, double, double> ShippedInk => new()
    {
        { "quick-access-background.svg", 16, 11.67d, 11.67d },
        { "quick-access-background.svg", 24, 17.60d, 17.60d },
        { "quick-access-background.svg", 32, 23.47d, 23.47d },
        { "quick-access-camcorder.svg", 16, 13.90d, 8.81d },
        { "quick-access-camcorder.svg", 24, 20.98d, 13.30d },
        { "quick-access-camcorder.svg", 32, 27.97d, 17.73d },
        { "quick-access-close.svg", 16, 8.08d, 8.08d },
        { "quick-access-close.svg", 24, 12.19d, 12.19d },
        { "quick-access-close.svg", 32, 16.25d, 16.25d },
        { "quick-access-copy.svg", 16, 9.83d, 9.83d },
        { "quick-access-copy.svg", 24, 14.83d, 14.83d },
        { "quick-access-copy.svg", 32, 19.77d, 19.77d },
        { "quick-access-edit.svg", 16, 9.83d, 9.83d },
        { "quick-access-edit.svg", 24, 14.83d, 14.83d },
        { "quick-access-edit.svg", 32, 19.77d, 19.77d },
        { "quick-access-flip-horizontal.svg", 16, 11.67d, 12.83d },
        { "quick-access-flip-horizontal.svg", 24, 17.60d, 19.36d },
        { "quick-access-flip-horizontal.svg", 32, 23.47d, 25.81d },
        { "quick-access-flip-vertical.svg", 16, 12.83d, 11.67d },
        { "quick-access-flip-vertical.svg", 24, 19.36d, 17.60d },
        { "quick-access-flip-vertical.svg", 32, 25.81d, 23.47d },
        { "quick-access-lock.svg", 16, 9.25d, 11.00d },
        { "quick-access-lock.svg", 24, 13.95d, 16.59d },
        { "quick-access-lock.svg", 32, 18.60d, 22.12d },
        { "quick-access-pin.svg", 16, 12.54d, 12.54d },
        { "quick-access-pin.svg", 24, 18.92d, 18.92d },
        { "quick-access-pin.svg", 32, 25.23d, 25.23d },
        { "quick-access-resize.svg", 16, 11.67d, 11.67d },
        { "quick-access-resize.svg", 24, 17.60d, 17.60d },
        { "quick-access-resize.svg", 32, 23.47d, 23.47d },
        { "quick-access-rotate-left.svg", 16, 11.64d, 11.64d },
        { "quick-access-rotate-left.svg", 24, 17.56d, 17.56d },
        { "quick-access-rotate-left.svg", 32, 23.41d, 23.41d },
        { "quick-access-rotate-right.svg", 16, 11.64d, 11.64d },
        { "quick-access-rotate-right.svg", 24, 17.56d, 17.56d },
        { "quick-access-rotate-right.svg", 32, 23.41d, 23.41d },
        { "quick-access-save.svg", 16, 9.25d, 10.41d },
        { "quick-access-save.svg", 24, 13.95d, 15.71d },
        { "quick-access-save.svg", 32, 18.60d, 20.94d },
        { "quick-access-text.svg", 16, 11.67d, 11.67d },
        { "quick-access-text.svg", 24, 17.60d, 17.60d },
        { "quick-access-text.svg", 32, 23.47d, 23.47d },
        { "quick-access-unlock.svg", 16, 9.25d, 10.95d },
        { "quick-access-unlock.svg", 24, 13.95d, 16.52d },
        { "quick-access-unlock.svg", 32, 18.60d, 22.02d },
    };

    /// <summary>
    /// The guard on the guard. The whole defect is that the family does NOT share one optical
    /// size and was never meant to — a camera, an X and a thumbtack are different drawings.
    /// If a future change quietly equalizes them again, the rows above would each have to be
    /// edited to match, which is visible in a diff; this fails on the property itself.
    /// </summary>
    [Fact]
    public void TheGlyphsAreDeliberatelyNotAllTheSameSize()
    {
        double Edge(string asset) => SvgIcons.OpticalExtent(asset, 24)!.Value.Width;
        double close = Edge("quick-access-close.svg");
        double pin = Edge("quick-access-pin.svg");
        Assert.True(pin - close > 5d,
            $"the pin draws {pin:0.0}px and the X {close:0.0}px in a 24px button — the family " +
            "has been normalized again, which is what shrank the pinned card");
    }
}
