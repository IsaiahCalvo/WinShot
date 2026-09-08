using System.Windows.Input;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class EditorShortcutTests
{
    [Theory]
    [InlineData(Key.C, ModifierKeys.Control, "Copy")]
    [InlineData(Key.V, ModifierKeys.Control, "Paste")]
    [InlineData(Key.D, ModifierKeys.Control, "Duplicate")]
    [InlineData(Key.S, ModifierKeys.Control | ModifierKeys.Shift, "SaveAs")]
    [InlineData(Key.Z, ModifierKeys.Control, "Undo")]
    // Survey mandates all four undo/redo bindings (undoRedoHotkeys.js:4-6, 33-39).
    [InlineData(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, "Redo")]
    [InlineData(Key.Y, ModifierKeys.Control, "Redo")]
    // Ctrl+0 is 100 % and Ctrl+1 is fit, the way every other Windows image app has it;
    // Survey's own two extra fit modes move down one rather than being lost.
    [InlineData(Key.D0, ModifierKeys.Control, "ActualSize")]
    [InlineData(Key.D1, ModifierKeys.Control, "FitAndCenter")]
    [InlineData(Key.D2, ModifierKeys.Control, "FitWidth")]
    [InlineData(Key.NumPad3, ModifierKeys.Control, "FitHeight")]
    [InlineData(Key.OemPlus, ModifierKeys.Control, "ZoomIn")]
    [InlineData(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, "ZoomIn")]
    [InlineData(Key.Add, ModifierKeys.Control, "ZoomIn")]
    [InlineData(Key.OemMinus, ModifierKeys.Control, "ZoomOut")]
    [InlineData(Key.Subtract, ModifierKeys.Control, "ZoomOut")]
    public void Resolve_MapsWindowsNativeEditorCommands(
        Key key, ModifierKeys modifiers, string expected) =>
        Assert.Equal(expected, EditorShortcut.Resolve(key, modifiers).ToString());

    /// <summary>
    /// Ctrl+0 is 100 % and Ctrl+1 is fit, the way every other image app on Windows has it.
    /// WinShot had them inverted — Ctrl+0 fitted and nothing reached 100 % — which is the
    /// one shortcut a user arrives already knowing.
    /// </summary>
    [Theory]
    [InlineData(Key.D0, "ActualSize")]
    [InlineData(Key.NumPad0, "ActualSize")]
    [InlineData(Key.D1, "FitAndCenter")]
    [InlineData(Key.NumPad1, "FitAndCenter")]
    public void Resolve_PutsHundredPercentOnCtrl0AndFitOnCtrl1(Key key, string expected) =>
        Assert.Equal(expected, EditorShortcut.Resolve(key, ModifierKeys.Control).ToString());

    [Fact]
    public void Resolve_DoesNotTreatMacModifiersOrBareKeysAsWindowsCommands()
    {
        Assert.Equal(EditorShortcutCommand.None, EditorShortcut.Resolve(Key.C, ModifierKeys.None));
        Assert.Equal(EditorShortcutCommand.None, EditorShortcut.Resolve(Key.S, ModifierKeys.Control));
        Assert.Equal(EditorShortcutCommand.None, EditorShortcut.Resolve(Key.C, ModifierKeys.Alt));
        Assert.Equal(EditorShortcutCommand.None, EditorShortcut.Resolve(Key.V, ModifierKeys.None));
        Assert.Equal(EditorShortcutCommand.None, EditorShortcut.Resolve(Key.D, ModifierKeys.Shift));
    }
}
