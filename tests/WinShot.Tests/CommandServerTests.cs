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

    [Fact]
    public void OpenWith_OnAFileNameContainingAHash_StillReachesTheRunningInstance()
    {
        // Explorer "Open with" on "note#1.png" while WinShot is already running: the second
        // process resolves the path, then forwards it with WithQuery. '#' and '?' are legal
        // in a Windows file name, so the forwarder used to cut the path in half at the '#'
        // and the running instance opened nothing at all.
        string dir = Path.Combine(Path.GetTempPath(), "winshot-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "note#1.png");
            using (var bmp = new System.Drawing.Bitmap(4, 4))
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);

            string? resolved = CommandServer.ParseCommand(file);
            Assert.Equal("edit:" + file, resolved);

            string forwarded = CommandServer.WithQuery(resolved!, file);
            Assert.Equal("edit:" + file, CommandServer.ParseCommand(forwarded));
            Assert.Equal(CaptureCommandOptions.None, CommandServer.ParseOptions(file));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("winshot://capture-area?delayed=-5", 0)]
    [InlineData("winshot://capture-area?delayed=abc", 0)]
    [InlineData("winshot://capture-area?delayed=99999", 60)]
    [InlineData("winshot://capture-area?delayed=", 0)]
    [InlineData("winshot://capture-area?", 0)]
    [InlineData("winshot://capture-area#", 0)]
    public void ADelayNobodyCanCancelIsRefused(string url, int expected)
    {
        // A script that parks the app in an hour-long countdown is a hang, not a feature.
        Assert.Equal(expected, CommandServer.ParseOptions(url).DelaySeconds);
    }

    [Fact]
    public void AThenListWithNothingUsableInItLeavesTheUsersSettingsInCharge()
    {
        Assert.Null(CommandServer.ParseOptions("winshot://capture-area?then=nonsense").Then);
        Assert.Null(PostCaptureSet.TryParseThen("nonsense,more"));
        Assert.Null(PostCaptureSet.TryParseThen(""));
    }
}
