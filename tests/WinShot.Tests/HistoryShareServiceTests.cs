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
}
