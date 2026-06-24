using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinShot.Core;

namespace WinShot.SettingsUi;

/// <summary>
/// A read-only text box that records a keyboard shortcut: focus it and press the
/// keys, and it writes the gesture (e.g. "Ctrl+Shift+1") that
/// <see cref="WinShot.Core.HotkeyManager.TryParseGesture"/> understands.
/// Escape cancels, Backspace/Delete clears.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private const string Prompt = "Press shortcut...";

    /// <summary>The last value the user actually committed, restored if they cancel.</summary>
    private string _committed = "";
    private IDisposable? _captureScope;

    public HotkeyBox()
    {
        IsReadOnly = true;             // blocks typing; keystrokes still reach PreviewKeyDown
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        Unloaded += (_, _) => EndCapture();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        BeginCapture();
        _committed = Text;
        Text = Prompt;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        EndCapture();
        base.OnLostKeyboardFocus(e);
        // Left while still prompting or mid-chord: keep whatever was there before.
        if (Text == Prompt || Text.EndsWith("...", StringComparison.Ordinal))
            Text = _committed;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Let Tab move focus normally; capture everything else.
        if (e.Key is Key.Tab) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.Escape:
                Text = _committed;
                Keyboard.ClearFocus();
                return;
            case Key.Back or Key.Delete:
                _committed = "";
                Text = "";
                return;
        }

        if (IsModifier(key))
        {
            // Show the modifiers held so far while we wait for the main key.
            string held = Describe(Keyboard.Modifiers, "+");
            Text = held.Length > 0 ? held + "+..." : Prompt;
            return;
        }

        string gesture = Describe(Keyboard.Modifiers, "+");
        gesture = gesture.Length > 0 ? gesture + "+" + KeyName(key) : KeyName(key);
        _committed = gesture;
        Text = gesture;
    }

    private void BeginCapture()
    {
        _captureScope?.Dispose();
        _captureScope = HotkeyInputCapture.Begin();
    }

    private void EndCapture()
    {
        _captureScope?.Dispose();
        _captureScope = null;
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static string Describe(ModifierKeys mods, string sep)
    {
        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join(sep, parts);
    }

    /// <summary>Maps a key to a name TryParseGesture round-trips (digits as "1", not "D1").</summary>
    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString(),
    };
}
