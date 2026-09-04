using System.Xml.Linq;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

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

    [Fact]
    public void GlyphsAreScaledByContent_SoTheyShareAnOpticalSize()
    {
        // Every icon should fill its 20px box to within a pixel: that is what "content-scaled"
        // buys. Before, the pin's art occupied ~55% of its 24-grid and rendered small next to
        // the X's ~75%, or huge, depending on which asset drifted.
        foreach (string asset in StrokedAssets)
        {
            SD.Bitmap? bmp = SvgIcons.Get(asset, 20, SD.Color.White);
            Assert.NotNull(bmp);
            SD.Rectangle ink = InkBounds(bmp!);
            Assert.True(Math.Max(ink.Width, ink.Height) >= 17,
                $"{asset} renders {ink.Width}x{ink.Height} in a 20px box — it is not filling it");
            Assert.True(Math.Max(ink.Width, ink.Height) <= 20,
                $"{asset} renders {ink.Width}x{ink.Height} and overflows its 20px box");
        }
    }

    [Fact]
    public void PinAndCloseRenderAtTheSameOpticalSize()
    {
        SD.Bitmap pin = SvgIcons.Get("quick-access-pin.svg", 20, SD.Color.White)!;
        SD.Bitmap close = SvgIcons.Get("quick-access-close.svg", 20, SD.Color.White)!;

        SD.Rectangle a = InkBounds(pin);
        SD.Rectangle b = InkBounds(close);
        Assert.True(Math.Abs(Math.Max(a.Width, a.Height) - Math.Max(b.Width, b.Height)) <= 1,
            $"pin {a.Width}x{a.Height} vs close {b.Width}x{b.Height}");
    }

    /// <summary>Bounding box of everything the glyph actually painted.</summary>
    private static SD.Rectangle InkBounds(SD.Bitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).A <= 24) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? SD.Rectangle.Empty : SD.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
}
