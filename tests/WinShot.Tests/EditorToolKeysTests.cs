using System.Windows.Input;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The one table of tool letters (Survey utils/toolShortcuts.js): the key handler, the
/// tooltips and the shortcut list all read it.
/// </summary>
public class EditorToolKeysTests
{
    [Theory]
    [InlineData(Key.V, false, EditorTool.Select)]
    [InlineData(Key.P, false, EditorTool.Freehand)]
    [InlineData(Key.H, false, EditorTool.Highlighter)]
    [InlineData(Key.E, false, EditorTool.Eraser)]
    [InlineData(Key.E, true, EditorTool.Eraser)]
    [InlineData(Key.R, false, EditorTool.Rectangle)]
    [InlineData(Key.O, false, EditorTool.Ellipse)]
    [InlineData(Key.G, false, EditorTool.Polygon)]
    [InlineData(Key.K, false, EditorTool.Polyline)]
    [InlineData(Key.L, false, EditorTool.Line)]
    [InlineData(Key.A, false, EditorTool.Arrow)]
    [InlineData(Key.C, false, EditorTool.Step)]
    [InlineData(Key.T, false, EditorTool.Text)]
    [InlineData(Key.Q, false, EditorTool.Callout)]
    // WinShot's own tools, on letters Survey leaves free.
    [InlineData(Key.X, false, EditorTool.Redact)]
    [InlineData(Key.C, true, EditorTool.Crop)]
    [InlineData(Key.M, true, EditorTool.Magnifier)]
    [InlineData(Key.M, false, EditorTool.Pan)]
    public void LettersArmSurveysTools(Key key, bool shift, EditorTool expected) =>
        Assert.Equal(expected, EditorToolKeys.Resolve(key, shift)?.Tool);

    [Fact]
    public void BAndIRunTheImageActions()
    {
        Assert.Equal(EditorToolKeys.ActionBackground, EditorToolKeys.Resolve(Key.B, false)?.Action);
        Assert.Equal(EditorToolKeys.ActionAddImage, EditorToolKeys.Resolve(Key.I, false)?.Action);
    }

    [Fact]
    public void UnboundLetterIsNotHandled()
    {
        Assert.Null(EditorToolKeys.Resolve(Key.S, false));
        Assert.Null(EditorToolKeys.Resolve(Key.R, true));
    }

    [Fact]
    public void NoKeyIsBoundTwice()
    {
        var combos = EditorToolKeys.All.Select(k => (k.Key, k.Shift)).ToList();
        Assert.Equal(combos.Count, combos.Distinct().Count());
    }

    [Fact]
    public void BadgesReadLikeTheTooltips()
    {
        Assert.Equal("R", EditorToolKeys.BadgeFor(EditorTool.Rectangle));
        Assert.Equal("E", EditorToolKeys.BadgeFor(EditorTool.Eraser));
        Assert.Equal("Shift+C", EditorToolKeys.BadgeFor(EditorTool.Crop));
        Assert.Null(EditorToolKeys.BadgeFor(EditorTool.Spotlight));
    }

    [Fact]
    public void ShortcutListNamesEveryToolKey()
    {
        var rows = EditorShortcutSheet.Sections().SelectMany(s => s.Rows).Select(r => r.Keys).ToHashSet();
        foreach (ToolKey k in EditorToolKeys.All)
            Assert.Contains(k.Badge, rows);
    }

    [Theory]
    [InlineData(Key.X, ModifierKeys.Control, "Cut")]
    [InlineData(Key.OemQuestion, ModifierKeys.Control, "ShowShortcuts")]
    public void CutAndShortcutListHaveKeys(Key key, ModifierKeys mods, string expected) =>
        Assert.Equal(expected, EditorShortcut.Resolve(key, mods).ToString());
}
