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
}

/// <summary>Windows-native editor shortcuts kept separate from the window handler for focused tests.</summary>
internal static class EditorShortcut
{
    public static EditorShortcutCommand Resolve(Key key, ModifierKeys modifiers) =>
        (key, modifiers) switch
        {
            (Key.Z, ModifierKeys.Control) => EditorShortcutCommand.Undo,
            (Key.Y, ModifierKeys.Control) => EditorShortcutCommand.Redo,
            (Key.D0 or Key.NumPad0, ModifierKeys.Control) => EditorShortcutCommand.FitAndCenter,
            (Key.C, ModifierKeys.Control) => EditorShortcutCommand.Copy,
            (Key.S, ModifierKeys.Control | ModifierKeys.Shift) => EditorShortcutCommand.SaveAs,
            _ => EditorShortcutCommand.None,
        };
}
