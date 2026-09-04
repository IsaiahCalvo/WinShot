using System.IO;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class CommandServerTests
{
    [Theory]
    [InlineData("capture-display")]
    [InlineData("winshot://capture-display")]
    [InlineData("winshot://capture-display/")]
    public void ParseCommand_AcceptsCaptureDisplay(string input)
    {
        Assert.Equal("capture-display", CommandServer.ParseCommand(input));
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("winshot://exit")]
    [InlineData("winshot://exit/")]
    public void ParseCommand_AcceptsExit(string input)
    {
        Assert.Equal("exit", CommandServer.ParseCommand(input));
    }

    [Theory]
    [InlineData("winshot://capture-area?copy=1", "capture-area")]
    [InlineData("winshot://capture-area/?copy=1#done", "capture-area")]
    [InlineData("\"winshot://history?open=1\"", "history")]
    public void ParseCommand_AcceptsUrlQueryAndFragment(string input, string expected)
    {
        Assert.Equal(expected, CommandServer.ParseCommand(input));
    }

    [Theory]
    [InlineData("--record", "record")]
    [InlineData("--record-display", "record-display")]
    [InlineData("--capture-window-background", "capture-window-background")]
    [InlineData("--scroll-horizontal", "scroll-horizontal")]
    [InlineData("--settings", "settings")]
    [InlineData("-history", "history")]
    public void ParseCommand_AcceptsCliFlags(string input, string expected)
    {
        Assert.Equal(expected, CommandServer.ParseCommand(input));
    }

    [Theory]
    [InlineData("capture-window-background")]
    [InlineData("winshot://capture-window-background")]
    [InlineData("winshot://capture-window-background?padding=1#open")]
    public void ParseCommand_AcceptsWindowBackgroundCapture(string input)
    {
        Assert.Equal("capture-window-background", CommandServer.ParseCommand(input));
    }

    [Fact]
    public void ParseCommand_MapsExistingImagePathToEditCommand()
    {
        string path = Path.Combine(Path.GetTempPath(), $"winshot-openwith-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, []);
        try
        {
            Assert.Equal("edit:" + path, CommandServer.ParseCommand(path));
            Assert.Equal("edit:" + path, CommandServer.ParseCommand($"\"{path}\""));
            Assert.Equal("edit:" + path, CommandServer.ParseCommand("edit:" + path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseCommand_RejectsMissingAndNonImagePaths()
    {
        // Nonexistent image path — must not become an edit command.
        Assert.Null(CommandServer.ParseCommand(Path.Combine(Path.GetTempPath(), $"winshot-missing-{Guid.NewGuid():N}.png")));

        // Existing file with a non-image extension.
        string txt = Path.Combine(Path.GetTempPath(), $"winshot-openwith-{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(txt, []);
        try
        {
            Assert.Null(CommandServer.ParseCommand(txt));
        }
        finally
        {
            File.Delete(txt);
        }
    }

    [Theory]
    [InlineData("self-timer-region")]
    [InlineData("winshot://self-timer-region")]
    [InlineData("--self-timer-region")]
    public void ParseCommand_AcceptsTimedRegionCapture(string input)
    {
        // The self-timer used to reach the whole desktop only; a timed REGION shot had no
        // command, no tray row and no way to be bound.
        Assert.Equal("self-timer-region", CommandServer.ParseCommand(input));
    }

    [Fact]
    public void EveryDocumentedCommandIsAcceptedByTheParser()
    {
        string doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "winshot-url-commands.md"));
        var documented = doc.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- `", StringComparison.Ordinal) &&
                           line.EndsWith("`", StringComparison.Ordinal))
            .Select(line => line[3..^1])
            .ToList();

        Assert.NotEmpty(documented);
        foreach (string command in documented)
            Assert.Equal(command, CommandServer.ParseCommand(command));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
