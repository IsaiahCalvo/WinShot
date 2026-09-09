using System.Windows.Input;

namespace WinShot.Editor;

internal enum EditorShortcutCommand
{
    None,
    Undo,
    Redo,
    FitAndCenter,
    FitWidth,
    FitHeight,
    ZoomIn,
    ZoomOut,
    Copy,
    SaveAs,
    Paste,
    Duplicate,
    ActualSize,
}

/// <summary>Windows-native editor shortcuts kept separate from the window handler for focused tests.</summary>
internal static class EditorShortcut
{
    public static EditorShortcutCommand Resolve(Key key, ModifierKeys modifiers) =>
        (key, modifiers) switch
        {
            // Survey's owner decision (undoRedoHotkeys.js:4-6): UNDO = Ctrl+Z; REDO = Ctrl+Shift+Z
            // AND Ctrl+Y — "ALL FOUR must work". isUndoKeyEvent excludes shiftKey, so Ctrl+Shift+Z
            // is unambiguously redo.
            (Key.Z, ModifierKeys.Control) => EditorShortcutCommand.Undo,
            (Key.Z, ModifierKeys.Control | ModifierKeys.Shift) => EditorShortcutCommand.Redo,
            (Key.Y, ModifierKeys.Control) => EditorShortcutCommand.Redo,
            // Ctrl+0 is 100 % and Ctrl+1 is fit, which is the way round every other image
            // app on Windows has it. Survey's own Ctrl+0/1/2 triple would put fit-width on
            // the key Windows users expect to mean 100 %, so its two extra fit modes move
            // down one to Ctrl+2 / Ctrl+3 rather than being lost.
            (Key.D0 or Key.NumPad0, ModifierKeys.Control) => EditorShortcutCommand.ActualSize,
            (Key.D1 or Key.NumPad1, ModifierKeys.Control) => EditorShortcutCommand.FitAndCenter,
            (Key.D2 or Key.NumPad2, ModifierKeys.Control) => EditorShortcutCommand.FitWidth,
            (Key.D3 or Key.NumPad3, ModifierKeys.Control) => EditorShortcutCommand.FitHeight,
            (Key.OemPlus or Key.Add, ModifierKeys.Control) => EditorShortcutCommand.ZoomIn,
            // Ctrl+Shift+= is the same key without releasing Shift; Survey binds it too.
            (Key.OemPlus or Key.Add, ModifierKeys.Control | ModifierKeys.Shift) => EditorShortcutCommand.ZoomIn,
            (Key.OemMinus or Key.Subtract, ModifierKeys.Control) => EditorShortcutCommand.ZoomOut,
            (Key.C, ModifierKeys.Control) => EditorShortcutCommand.Copy,
            (Key.V, ModifierKeys.Control) => EditorShortcutCommand.Paste,
            (Key.D, ModifierKeys.Control) => EditorShortcutCommand.Duplicate,
            (Key.S, ModifierKeys.Control | ModifierKeys.Shift) => EditorShortcutCommand.SaveAs,
            _ => EditorShortcutCommand.None,
        };
}
