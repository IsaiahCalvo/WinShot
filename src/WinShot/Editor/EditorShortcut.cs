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
            // Survey's zoom shortcuts (PDFViewer.jsx:23456-23484): Ctrl+0/1/2 pick the fit
            // modes, Ctrl+= / Ctrl++ / Ctrl+- step by TOOLBAR_ZOOM_STEP_FACTOR.
            (Key.D0 or Key.NumPad0, ModifierKeys.Control) => EditorShortcutCommand.FitAndCenter,
            (Key.D1 or Key.NumPad1, ModifierKeys.Control) => EditorShortcutCommand.FitWidth,
            (Key.D2 or Key.NumPad2, ModifierKeys.Control) => EditorShortcutCommand.FitHeight,
            (Key.OemPlus or Key.Add, ModifierKeys.Control) => EditorShortcutCommand.ZoomIn,
            (Key.OemPlus or Key.Add, ModifierKeys.Control | ModifierKeys.Shift) => EditorShortcutCommand.ZoomIn,
            (Key.OemMinus or Key.Subtract, ModifierKeys.Control) => EditorShortcutCommand.ZoomOut,
            (Key.C, ModifierKeys.Control) => EditorShortcutCommand.Copy,
            (Key.S, ModifierKeys.Control | ModifierKeys.Shift) => EditorShortcutCommand.SaveAs,
            _ => EditorShortcutCommand.None,
        };
}
