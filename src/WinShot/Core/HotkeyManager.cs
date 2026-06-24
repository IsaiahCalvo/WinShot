using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace WinShot.Core;

/// <summary>
/// Registers global hotkeys against a hidden message window and dispatches
/// WM_HOTKEY to the actions registered for them.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    internal const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("WinShotHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a gesture like "Ctrl+Shift+1". Returns false if parsing or registration fails.</summary>
    public bool Register(string gesture, Action handler)
    {
        if (!TryParseGesture(gesture, out var mods, out var key))
        {
            Log.Error($"Could not parse hotkey gesture '{gesture}'");
            return false;
        }

        uint fsMods = ToRegisterHotKeyModifiers(mods);

        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_source.Handle, id, fsMods, vk))
        {
            Log.Error($"RegisterHotKey failed for '{gesture}' (already in use by another app?)");
            return false;
        }

        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys)
            UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
    }

    public static bool TryParseGesture(string text, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                default:
                    string name = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
                    if (!Enum.TryParse(name, ignoreCase: true, out Key parsed)) return false;
                    key = parsed;
                    break;
            }
        }

        return key != Key.None;
    }

    public static bool TryNormalizeGesture(string text, out string? normalized)
    {
        normalized = null;
        if (!TryParseGesture(text, out var mods, out var key))
            return false;

        var parts = new List<string>(5);
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(KeyToDisplayName(key));
        normalized = string.Join("+", parts);
        return true;
    }

    internal static bool TryGetRegistrationParts(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (!TryParseGesture(gesture, out var mods, out var key))
            return false;

        modifiers = ToRegisterHotKeyModifiers(mods);
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    private static uint ToRegisterHotKeyModifiers(ModifierKeys mods)
    {
        uint fsMods = ModNoRepeat;
        if (mods.HasFlag(ModifierKeys.Alt)) fsMods |= ModAlt;
        if (mods.HasFlag(ModifierKeys.Control)) fsMods |= ModControl;
        if (mods.HasFlag(ModifierKeys.Shift)) fsMods |= ModShift;
        if (mods.HasFlag(ModifierKeys.Windows)) fsMods |= ModWin;
        return fsMods;
    }

    private static string KeyToDisplayName(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
            return ((int)key - (int)Key.D0).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return "Num" + ((int)key - (int)Key.NumPad0);

        string name = key.ToString();
        return name.Length == 1 ? name.ToUpperInvariant() : name;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            if (!HotkeyInputCapture.IsActive)
                handler();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
