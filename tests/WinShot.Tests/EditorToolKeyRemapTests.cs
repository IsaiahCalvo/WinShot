using System.Windows.Input;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>Survey customKeyboardShortcuts.js: a tool's key can be changed in the shortcut list.</summary>
public class EditorToolKeyRemapTests
{
    private static ToolKey Row(IReadOnlyDictionary<string, string> map, string label) =>
        EditorToolKeys.Effective(map).Single(k => k.Label == label);

    [Fact]
    public void ANewLetterReplacesTheShippedOne()
    {
        var map = new Dictionary<string, string>();
        Assert.Null(EditorToolKeys.Assign(map, "Polyline", Key.J, shift: false));
        Assert.Equal("J", map["Polyline"]);
        Assert.Equal(Key.J, Row(map, "Polyline").Key);
        Assert.Equal("J", Row(map, "Polyline").Badge);
    }

    [Fact]
    public void ShiftLettersAreAllowed()
    {
        var map = new Dictionary<string, string>();
        Assert.Null(EditorToolKeys.Assign(map, "Magnifier", Key.Z, shift: true));
        Assert.Equal("Shift+Z", Row(map, "Magnifier").Badge);
    }

    [Fact]
    public void AKeyAnotherToolHasIsRefused()
    {
        var map = new Dictionary<string, string>();
        string? error = EditorToolKeys.Assign(map, "Polyline", Key.R, shift: false);
        Assert.Contains("Rectangle", error);
        Assert.Empty(map);
    }

    [Theory]
    [InlineData(Key.Space, false)]
    [InlineData(Key.D1, false)]
    [InlineData(Key.Escape, false)]
    [InlineData(Key.S, true)] // Shift+S turns snapping on and off
    public void OnlyFreeLettersCanBeUsed(Key key, bool shift)
    {
        var map = new Dictionary<string, string>();
        Assert.NotNull(EditorToolKeys.Assign(map, "Polyline", key, shift));
        Assert.Empty(map);
    }

    [Fact]
    public void PickingTheShippedKeyAgainDropsTheChange()
    {
        var map = new Dictionary<string, string> { ["Polyline"] = "J" };
        Assert.Null(EditorToolKeys.Assign(map, "Polyline", Key.K, shift: false));
        Assert.Empty(map);
    }

    [Fact]
    public void ResetIsRefusedWhileAnotherToolHoldsTheShippedKey()
    {
        var map = new Dictionary<string, string>();
        Assert.Null(EditorToolKeys.Assign(map, "Polyline", Key.J, false));
        Assert.Null(EditorToolKeys.Assign(map, "Line", Key.K, false));
        Assert.NotNull(EditorToolKeys.Reset(map, "Polyline"));
        Assert.Null(EditorToolKeys.Assign(map, "Line", Key.L, false));
        Assert.Null(EditorToolKeys.Reset(map, "Polyline"));
        Assert.Empty(map);
    }

    [Fact]
    public void BrokenOrUnknownEntriesAreIgnored()
    {
        var map = new Dictionary<string, string> { ["Polyline"] = "Ctrl+J", ["Nope"] = "J", ["Line"] = "" };
        Assert.Equal(EditorToolKeys.Defaults, EditorToolKeys.Effective(map));
    }
}
