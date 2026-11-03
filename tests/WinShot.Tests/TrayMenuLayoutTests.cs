using WinShot.Core;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The tray menu was 32 flat rows, taller than a 1080p work area at 150 %, so it opened with
/// scroll arrows and History, Settings and Exit below the fold. These pin the grouped layout
/// and check no row lost its action in the move.
/// </summary>
public class TrayMenuLayoutTests
{
    private static readonly string[] NonCaptureActions =
    {
        "record", "record-display", "clipboard", "open-image", "pin-file", "restore",
        "desktop-icons", "history", "settings", "pin-last", "pins-toggle", "pins-close",
        "pins-unlock", "install-update", "check-updates", "about", "exit",
    };

    [Fact]
    public void EveryRowKeepsItsAction()
    {
        var ids = new List<string>();
        WinShot.App.TrayMenuParts parts = WinShot.App.BuildTrayMenu(new Settings(), id =>
        {
            ids.Add(id);
            return () => { };
        });
        using WF.ContextMenuStrip menu = parts.Menu;

        string[] expectedCaptures =
        {
            "capture-area", "capture-window", "capture-fullscreen", "capture-previous", "self-timer",
            "all-in-one", "measure", "capture-window-background", "capture-display",
            "self-timer-region", "scroll-horizontal", "scroll-up", "ocr", "scrolling",
        };
        Assert.Equal(
            expectedCaptures.Concat(NonCaptureActions).OrderBy(x => x),
            ids.OrderBy(x => x));
        foreach (string id in ids.Except(NonCaptureActions))
            Assert.True(WinShot.App.IsCaptureCommand(id), $"'{id}' would be queued as a capture but is not one.");
    }

    [Fact]
    public void TopLevelFitsA1080pScreenAt150Percent()
    {
        WinShot.App.TrayMenuParts parts = WinShot.App.BuildTrayMenu(new Settings(), _ => () => { });
        using WF.ContextMenuStrip menu = parts.Menu;

        int rows = menu.Items.OfType<WF.ToolStripMenuItem>().Count(i => i != parts.Update);
        int separators = menu.Items.OfType<WF.ToolStripSeparator>().Count();
        // 1080 px at 150 % is 720 DIP, about 670 above the taskbar. At 22 DIP a row and 9 a
        // separator, 25 rows and 6 separators is 604: room to spare.
        Assert.InRange(rows, 1, 25);
        Assert.InRange(separators, 1, 6);
        Assert.Contains(menu.Items.OfType<WF.ToolStripMenuItem>(), i => i.Text == "History…");
        Assert.Contains(menu.Items.OfType<WF.ToolStripMenuItem>(), i => i.Text == "Exit");
    }

    [Fact]
    public void RarerVerbsSitOneLevelDown()
    {
        var settings = new Settings { HotkeyPinLast = "Ctrl+Shift+P" };
        WinShot.App.TrayMenuParts parts = WinShot.App.BuildTrayMenu(settings, _ => () => { });
        using WF.ContextMenuStrip menu = parts.Menu;

        WF.ToolStripMenuItem Parent(string text) =>
            menu.Items.OfType<WF.ToolStripMenuItem>().Single(i => i.Text == text);

        Assert.Equal(6, Parent("More ways to capture").DropDownItems.Count);
        Assert.Equal(5, Parent("Pins").DropDownItems.Count);
        // The pin shortcuts still show beside their rows inside the submenu.
        Assert.Contains(Parent("Pins").DropDownItems.OfType<WF.ToolStripMenuItem>(),
            i => i.Text == "Pin last screenshot" && !string.IsNullOrEmpty(i.ShortcutKeyDisplayString));
    }
}
