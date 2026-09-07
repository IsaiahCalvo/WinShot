using System.IO;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// "Ask for name after every capture" — the row existed and was collapsed. These cover the
/// two things that decide whether a typed name is safe to write and whether it can quietly
/// destroy an existing capture.
/// </summary>
public class AskForNameTests
{
    [Theory]
    [InlineData("Design review", "Design review")]
    [InlineData("  padded  ", "padded")]
    [InlineData("12/06 sketch", "12-06 sketch")]
    [InlineData("a:b*c?d", "a-b-c-d")]
    // Legal to type, illegal to create on Windows.
    [InlineData("trailing dot.", "trailing dot")]
    [InlineData("trailing space ", "trailing space")]
    public void SanitizeMakesATypedNameWritable(string entered, string expected)
    {
        Assert.Equal(expected, ThemedInputDialog.SanitizeFileName(entered, "fallback"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    [InlineData(null)]
    public void AnEmptyAnswerFallsBackToWhatWasOffered(string? entered)
    {
        // The prompt is pre-filled with the resolved template; erasing it should not
        // produce a file called "---" or a crash.
        Assert.Equal("WinShot 2026-09-04", ThemedInputDialog.SanitizeFileName(entered, "WinShot 2026-09-04"));
    }

    [Fact]
    public void ATypedNameNeverOverwritesAnExistingCapture()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winshot-askname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string first = FileNamer.UniquePathFor(dir, "Sketch", ".png");
            Assert.Equal(Path.Combine(dir, "Sketch.png"), first);
            File.WriteAllText(first, "x");

            string second = FileNamer.UniquePathFor(dir, "Sketch", ".png");
            Assert.Equal(Path.Combine(dir, "Sketch (2).png"), second);
            File.WriteAllText(second, "x");

            Assert.Equal(Path.Combine(dir, "Sketch (3).png"), FileNamer.UniquePathFor(dir, "Sketch", ".png"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ExtensionsAreAcceptedWithOrWithoutTheDot()
    {
        string dir = Path.GetTempPath();
        Assert.EndsWith(".png", FileNamer.UniquePathFor(dir, Guid.NewGuid().ToString("N"), "png"));
        Assert.EndsWith(".jpg", FileNamer.UniquePathFor(dir, Guid.NewGuid().ToString("N"), ".jpg"));
    }
}
