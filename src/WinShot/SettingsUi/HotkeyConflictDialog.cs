using System.Windows;
using WinShot.Core;

namespace WinShot.SettingsUi;

internal enum HotkeyConflictChoice
{
    Keep,
    Change,
    FindApp,
}

/// <summary>
/// "That gesture is taken — what now?" Built on ThemedMessageDialog so it gets the dark
/// title bar and the app's pill buttons instead of a white Windows frame over the dark
/// Settings shell.
/// </summary>
internal static class HotkeyConflictDialog
{
    public static HotkeyConflictChoice Show(Window owner, string actionLabel, string gesture, HotkeyConflictSource source)
    {
        string ownerText = source.IsExactMatch
            ? $"{gesture} is used by {source.DisplayName}."
            : $"{gesture} is taken. Best match: {source.DisplayName}.";

        var (dialog, _, footer) = ThemedMessageDialog.Build(
            owner,
            $"{gesture} is already used",
            $"{ownerText}\n\nFind app lets you press the hotkey once while WinShot watches what comes " +
            $"forward. Change opens that app or its settings. Keep means {actionLabel} needs a different " +
            "WinShot hotkey.");

        var choice = HotkeyConflictChoice.Keep;

        // Keep is the default outcome, so its button only has to close: IsCancel does that
        // and makes Escape mean the same thing.
        ThemedMessageDialog.AddButton(footer, "Keep", primary: false, () => { }).IsCancel = true;

        ThemedMessageDialog.AddButton(footer, "Change", primary: false, () =>
        {
            choice = HotkeyConflictChoice.Change;
            dialog.DialogResult = true;
        });

        ThemedMessageDialog.AddButton(footer, "Find app", primary: true, () =>
        {
            choice = HotkeyConflictChoice.FindApp;
            dialog.DialogResult = true;
        }).IsDefault = true;

        dialog.ShowDialog();
        return choice;
    }
}
