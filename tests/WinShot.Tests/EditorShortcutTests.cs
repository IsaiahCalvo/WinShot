using System.Windows.Input;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class EditorShortcutTests
{
    [Theory]
    [InlineData(Key.C, ModifierKeys.Control, "Copy")]
    [InlineData(Key.S, ModifierKeys.Control | ModifierKeys.Shift, "SaveAs")]
    [InlineData(Key.Z, ModifierKeys.Control, "Undo")]
    [InlineData(Key.Y, ModifierKeys.Control, "Redo")]
    [InlineData(Key.D0, ModifierKeys.Control, "FitAndCenter")]
    public void Resolve_MapsWindowsNativeEditorCommands(
        Key key, ModifierKeys modifiers, string expected) =>
        Assert.Equal(expected, EditorShortcut.Resolve(key, modifiers).ToString());

    [Fact]
    public void Resolve_DoesNotTreatMacModifiersOrBareKeysAsWindowsCommands()
    {
        Assert.Equal(EditorShortcutCommand.None, EditorShortcut.Resolve(Key.C, ModifierKeys.None));
        Assert.Equal(EditorShortcutCommand.None, EditorShortcut.Resolve(Key.S, ModifierKeys.Control));
        Assert.Equal(EditorShortcutCommand.None, EditorShortcut.Resolve(Key.C, ModifierKeys.Alt));
    }
}
