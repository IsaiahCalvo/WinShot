using WinShot.Ocr;
using Xunit;

namespace WinShot.Tests;

public class OcrTextFormatterTests
{
    [Fact]
    public void Format_PreservesLineBreaksByDefault()
    {
        string text = OcrTextFormatter.Format(["First line", "Second line"], joinLines: false);

        Assert.Equal($"First line{Environment.NewLine}Second line", text);
    }

    [Fact]
    public void Format_JoinsLinesIntoParagraphWhenRequested()
    {
        string text = OcrTextFormatter.Format(["First line", "Second line"], joinLines: true);

        Assert.Equal("First line Second line", text);
    }

    [Fact]
    public void Format_TrimsOuterWhitespace()
    {
        string text = OcrTextFormatter.Format(["  First line  ", "Second line  "], joinLines: true);

        Assert.Equal("First line   Second line", text);
    }
}
