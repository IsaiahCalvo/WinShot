using System.Windows.Input;

namespace WinShot.Editor;

internal enum EditorShortcutCommand
{
    None,
    Undo,
    Redo,
    FitAndCenter,
    Copy,
    SaveAs,
    Paste,
    Duplicate,
    ZoomIn,
    ZoomOut,
    ActualSize,
}

/// <summary>Windows-native editor shortcuts kept separate from the window handler for focused tests.</summary>
internal static class EditorShortcut
{
    public static EditorShortcutCommand Resolve(Key key, ModifierKeys modifiers) =>
        (key, modifiers) switch
        {
            (Key.Z, ModifierKeys.Control) => EditorShortcutCommand.Undo,
            (Key.Y, ModifierKeys.Control) => EditorShortcutCommand.Redo,
            // Ctrl+0 is 100 % and Ctrl+1 is fit, which is the way round every other image
            // app on Windows has it. It used to be the other way, with nothing bound to fit.
            (Key.D0 or Key.NumPad0, ModifierKeys.Control) => EditorShortcutCommand.ActualSize,
            (Key.D1 or Key.NumPad1, ModifierKeys.Control) => EditorShortcutCommand.FitAndCenter,
            (Key.OemPlus or Key.Add, ModifierKeys.Control) => EditorShortcutCommand.ZoomIn,
            (Key.OemMinus or Key.Subtract, ModifierKeys.Control) => EditorShortcutCommand.ZoomOut,
            (Key.C, ModifierKeys.Control) => EditorShortcutCommand.Copy,
            (Key.V, ModifierKeys.Control) => EditorShortcutCommand.Paste,
            (Key.D, ModifierKeys.Control) => EditorShortcutCommand.Duplicate,
            (Key.S, ModifierKeys.Control | ModifierKeys.Shift) => EditorShortcutCommand.SaveAs,
            _ => EditorShortcutCommand.None,
        };
}
