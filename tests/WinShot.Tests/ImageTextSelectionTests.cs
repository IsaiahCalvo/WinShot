using System.Windows;
using System.Windows.Input;
using WinShot.Editor;
using WinShot.Ocr;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Text select (Shift+V): drag across the words in a picture and copy them, the way text is
/// picked everywhere else (Survey's "Text Select" mode; Live Text on a Mac).
/// </summary>
public class ImageTextSelectionTests
{
    // Two lines of two words, then a third line of one:
    //   "Hello world"
    //   "Second line"
    //   "End"
    private static readonly IReadOnlyList<TextWord> Words = new[]
    {
        new TextWord("Hello", new Rect(10, 10, 50, 20), 0),
        new TextWord("world", new Rect(70, 10, 50, 20), 0),
        new TextWord("Second", new Rect(10, 40, 60, 20), 1),
        new TextWord("line", new Rect(80, 40, 40, 20), 1),
        new TextWord("End", new Rect(10, 70, 30, 20), 2),
    };

    [Fact]
    public void APointOnAWordHitsIt()
    {
        Assert.Equal(1, ImageTextSelection.Hit(Words, new Point(90, 20)));
        Assert.Equal(-1, ImageTextSelection.Hit(Words, new Point(300, 300)));
    }

    [Fact]
    public void DragEndsLandOnTheNearestLineThenTheNearestWord()
    {
        // In the gap between "Second" and "line", nearer "line".
        Assert.Equal(3, ImageTextSelection.Nearest(Words, new Point(77, 50)));
        // Far right of line 0 still lands on its last word.
        Assert.Equal(1, ImageTextSelection.Nearest(Words, new Point(500, 15)));
        // Above everything: the first line. Below everything: the last.
        Assert.Equal(0, ImageTextSelection.Nearest(Words, new Point(5, -100)));
        Assert.Equal(4, ImageTextSelection.Nearest(Words, new Point(15, 900)));
    }

    [Fact]
    public void TwoColumnsOnOneRowPickTheColumnUnderThePointer()
    {
        var columns = new[]
        {
            new TextWord("Left", new Rect(10, 10, 40, 20), 0),
            new TextWord("Right", new Rect(300, 10, 40, 20), 1),
        };
        Assert.Equal(1, ImageTextSelection.Nearest(columns, new Point(320, 20)));
        Assert.Equal(0, ImageTextSelection.Nearest(columns, new Point(30, 20)));
    }

    [Fact]
    public void ADragPicksEveryWordBetweenItsEndsInEitherDirection()
    {
        Assert.Equal(((int, int)?)(1, 3), ImageTextSelection.Range(3, 1));
        Assert.Equal(((int, int)?)(1, 3), ImageTextSelection.Range(1, 3));
        Assert.Null(ImageTextSelection.Range(-1, 2));
    }

    [Fact]
    public void CopiedTextKeepsSpacesAndLineBreaks()
    {
        Assert.Equal("world\r\nSecond line\r\nEnd", ImageTextSelection.TextFor(Words, 1, 4));
        Assert.Equal("Hello world Second line End", ImageTextSelection.TextFor(Words, 0, 4, joinLines: true));
    }

    [Fact]
    public void ChineseAndJapaneseCharactersJoinWithoutSpaces()
    {
        var cjk = new[]
        {
            new TextWord("文", new Rect(0, 0, 10, 10), 0),
            new TextWord("字", new Rect(12, 0, 10, 10), 0),
            new TextWord("OK", new Rect(24, 0, 10, 10), 0),
        };
        Assert.Equal("文字 OK", ImageTextSelection.TextFor(cjk, 0, 2));
    }

    [Fact]
    public void TripleClickPicksTheWholeLine()
    {
        Assert.Equal((2, 3), ImageTextSelection.LineOf(Words, 3));
        Assert.Equal((4, 4), ImageTextSelection.LineOf(Words, 4));
    }

    [Fact]
    public void ThePickIsPaintedAsOneBandPerLine()
    {
        var bands = ImageTextSelection.Bands(Words, 1, 3);
        Assert.Equal(2, bands.Count);
        Assert.Equal(new Rect(70, 10, 50, 20), bands[0]);
        Assert.Equal(new Rect(10, 40, 110, 20), bands[1]);
    }

    [Fact]
    public void OcrWordsComeBackGroupedByLineInReadingOrder()
    {
        var words = ImageTextSelection.FromOcr(new[]
        {
            new OcrTextWord("b", new SD.Rectangle(0, 30, 5, 5), 1),
            new OcrTextWord("a", new SD.Rectangle(0, 0, 5, 5), 0),
            new OcrTextWord("c", new SD.Rectangle(10, 30, 5, 5), 1),
        });
        Assert.Equal(new[] { "a", "b", "c" }, words.Select(w => w.Text));
        Assert.Equal(2, ImageTextSelection.Lines(words).Count);
    }

    [Fact]
    public void ShiftVArmsTextSelect()
    {
        Assert.Equal(EditorToolKeys.ActionTextSelect, EditorToolKeys.Resolve(Key.V, true)?.Action);
        Assert.Equal(EditorTool.Select, EditorToolKeys.Resolve(Key.V, false)?.Tool);
    }

    [Fact]
    public void AFullScreenshotIsReadLargerThanItsOwnSize()
    {
        // Screen text is small; a 1080p capture reads at the engine's cap, not at 1x.
        double scale = OcrService.LayoutOcrScale(1920, 1080);
        Assert.True(scale > 1.2 && scale <= 2.0, $"scale was {scale}");
        // Never past what the engine accepts.
        double cap = Windows.Media.Ocr.OcrEngine.MaxImageDimension / 8000.0;
        Assert.True(OcrService.LayoutOcrScale(8000, 4000) <= cap + 1e-9);
    }

    [Fact]
    public async Task WordsAreReadWithBoxesInThePicturesOwnPixels()
    {
        using var bmp = new SD.Bitmap(600, 120);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.Clear(SD.Color.White);
            g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new SD.Font("Arial", 36, SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
            g.DrawString("Hello WinShot", font, SD.Brushes.Black, 20, 30);
        }

        IReadOnlyList<OcrTextWord> words;
        try
        {
            words = await OcrService.ExtractWordLayoutAsync(bmp);
        }
        catch (OcrUnavailableException)
        {
            return; // no OCR language pack on this machine
        }

        Assert.NotEmpty(words);
        Assert.Contains(words, w => w.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase));
        foreach (OcrTextWord w in words)
            Assert.True(new SD.Rectangle(0, 0, 600, 120).Contains(w.Box), $"{w.Text} box {w.Box} is off the picture");
    }
}
