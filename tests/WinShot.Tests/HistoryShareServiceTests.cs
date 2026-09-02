using System.IO;
using WinShot.History;
using Xunit;

namespace WinShot.Tests;

public class HistoryShareServiceTests
{
    [Fact]
    public void BlipDetection_ChecksPathAndWindowsApps()
    {
        var checkedPaths = new List<string>();
        bool installed = HistoryShareService.IsBlipInstalled(
            @"C:\Tools;C:\Apps",
            @"C:\Users\Test\AppData\Local",
            path =>
            {
                checkedPaths.Add(path);
                return path.Equals(@"C:\Apps\blip.exe", StringComparison.OrdinalIgnoreCase);
            });

        Assert.True(installed);
        Assert.Contains(@"C:\Tools\blip.exe", checkedPaths);
        Assert.Contains(@"C:\Apps\blip.exe", checkedPaths);
    }

    [Fact]
    public void BlipArguments_PreserveEverySelectedFileAsIndependentArgument()
    {
        IReadOnlyList<string> args = HistoryShareService.BlipArguments(new[] { @"C:\One image.png", @"C:\Two.mp4" });

        Assert.Equal(new[] { "--file", @"C:\One image.png", "--file", @"C:\Two.mp4" }, args);
    }
    [Theory]
    [InlineData(true, false, true)]   // Blip up, socket gone — launching only raises its own error
    [InlineData(true, true, false)]   // Blip up and reachable
    [InlineData(false, false, false)] // Blip not running — a launch starts it fresh
    [InlineData(false, true, false)]
    public void BlipHandoffIsBrokenOnlyWhileBlipRunsWithoutItsSocket(
        bool running, bool socketExists, bool expected)
    {
        bool broken = HistoryShareService.IsBlipHandoffBroken(
            running,
            HistoryShareService.BlipHandoffSocketPath(),
            _ => socketExists);

        Assert.Equal(expected, broken);
    }

    [Fact]
    public void BlipHandoffSocketPathIsTheOneBlipListensOn()
    {
        string path = HistoryShareService.BlipHandoffSocketPath();

        Assert.Equal("ui.sock", Path.GetFileName(path));
        Assert.Equal("net.blip.desktop", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.StartsWith(Path.GetTempPath(), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharingNothingWithBlipNeverLaunchesIt()
        => Assert.Equal(BlipShareResult.NothingToShare, HistoryShareService.ShareWithBlip(Array.Empty<string>()));
}
