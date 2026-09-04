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
    // Survey mandates all four undo/redo bindings (undoRedoHotkeys.js:4-6, 33-39).
    [InlineData(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, "Redo")]
    [InlineData(Key.Y, ModifierKeys.Control, "Redo")]
    [InlineData(Key.D0, ModifierKeys.Control, "FitAndCenter")]
    // Survey's zoom shortcuts (PDFViewer.jsx:23456-23484).
    [InlineData(Key.D1, ModifierKeys.Control, "FitWidth")]
    [InlineData(Key.NumPad2, ModifierKeys.Control, "FitHeight")]
    [InlineData(Key.OemPlus, ModifierKeys.Control, "ZoomIn")]
    [InlineData(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, "ZoomIn")]
    [InlineData(Key.Add, ModifierKeys.Control, "ZoomIn")]
    [InlineData(Key.OemMinus, ModifierKeys.Control, "ZoomOut")]
    [InlineData(Key.Subtract, ModifierKeys.Control, "ZoomOut")]
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
