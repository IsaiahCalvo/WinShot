using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Recording;

/// <summary>
/// Click-through keystroke display shown while recording: a dark pill at the
/// bottom-center of the recorded region showing recent keys and combos
/// ("Ctrl+Shift+P"), fading out 1.5 s after the last keystroke. Uses a
/// WH_KEYBOARD_LL hook that lives only as long as this window — close the
/// window to release the hook. Deliberately NOT excluded from capture: the
/// pill is meant to be visible in the recording.
/// </summary>
public partial class KeystrokeOverlayWindow : Window
{
    private const int MaxRunLength = 18;
    private static readonly TimeSpan AppendWindow = TimeSpan.FromSeconds(1.2);

    private readonly SD.Rectangle _regionPx;
    private readonly HookProc _hookProc; // field keeps the delegate alive for the native hook
    private readonly DispatcherTimerWrapper _fade;
    private IntPtr _hook;
    private volatile bool _paused;

    // Display run state (UI thread only — LL hooks call back on the installing thread).
    private string _run = "";
    private bool _runAppendable;
    private DateTime _lastKeyAt = DateTime.MinValue;
    private bool _modifierDownWithoutKey;

    public KeystrokeOverlayWindow(SD.Rectangle regionScreenPx)
    {
        InitializeComponent();
        _regionPx = regionScreenPx;
        _hookProc = KeyboardHookCallback;
        _fade = new DispatcherTimerWrapper(TimeSpan.FromSeconds(1.5), BeginFadeOut);

        SourceInitialized += (_, _) =>
        {
            MakeClickThrough();
            PositionOverRegion();
        };
        Loaded += (_, _) =>
        {
            PositionOverRegion(); // WPF layout may resize between init and show
            InstallHook();
        };
        Closed += (_, _) =>
        {
            _fade.Stop();
            RemoveHook();
        };
    }

    /// <summary>While paused, keystrokes are ignored and the pill is hidden.</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            _fade.Stop();
            Pill.BeginAnimation(OpacityProperty, null);
            Pill.Opacity = 0;
            _runAppendable = false;
        }
    }

    private void MakeClickThrough()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLongW(handle, GwlExstyle);
        SetWindowLongW(handle, GwlExstyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>Covers a strip at the bottom of the recorded region (physical pixels).</summary>
    private void PositionOverRegion()
    {
        int stripHeight = Math.Min(_regionPx.Height, 160);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, HwndTopmost,
            _regionPx.X, _regionPx.Bottom - stripHeight, _regionPx.Width, stripHeight, SwpNoActivate);
    }

    private void InstallHook()
    {
        _hook = SetWindowsHookExW(WhKeyboardLl, _hookProc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Error($"Failed to install keyboard hook for keystroke overlay (error {Marshal.GetLastWin32Error()})");
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        if (!UnhookWindowsHookEx(_hook))
            Log.Error("Failed to remove keystroke overlay keyboard hook");
        _hook = IntPtr.Zero;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_paused)
        {
            long msg = wParam.ToInt64();
            bool down = msg is WmKeyDown or WmSysKeyDown;
            bool up = msg is WmKeyUp or WmSysKeyUp;
            if (down || up)
            {
                int vk = (int)Marshal.PtrToStructure<KbdllHookStruct>(lParam).vkCode;
                // Keep the hook callback fast: queue the visual work.
                Dispatcher.InvokeAsync(() => OnKeyEvent(down, vk));
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void OnKeyEvent(bool down, int vk)
    {
        try
        {
            if (_paused) return;
            bool isModifier = IsModifierVk(vk);
            if (down)
            {
                if (isModifier)
                {
                    // Candidate for a modifier-only display; resolved on key-up.
                    _modifierDownWithoutKey = true;
                    return;
                }
                _modifierDownWithoutKey = false;
                var (text, appendable) = BuildDisplay(vk);
                Display(text, appendable);
            }
            else if (isModifier && _modifierDownWithoutKey && !AnyOtherModifierDown(vk))
            {
                // The modifier was pressed and released without any other key:
                // collapse to just the modifier name ("Ctrl").
                _modifierDownWithoutKey = false;
                Display(ModifierName(vk), appendable: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Keystroke overlay update failed", ex);
        }
    }

    /// <summary>Builds the text for a non-modifier key, including any held modifiers.</summary>
    private static (string Text, bool Appendable) BuildDisplay(int vk)
    {
        bool ctrl = IsDown(0x11);
        bool shift = IsDown(0x10);
        bool alt = IsDown(0x12);
        bool win = IsDown(0x5B) || IsDown(0x5C);

        if (ctrl || alt || win)
        {
            var parts = new List<string>(5);
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (win) parts.Add("Win");
            parts.Add(KeyName(vk, upperCase: true));
            return (string.Join("+", parts), false);
        }

        string name = KeyName(vk, upperCase: shift);
        if (name.Length == 1)
            return (name, true); // plain typing: appended into a run
        return (shift ? "Shift+" + name : name, false);
    }

    private void Display(string text, bool appendable)
    {
        var now = DateTime.UtcNow;
        if (appendable && _runAppendable && now - _lastKeyAt < AppendWindow && _run.Length < MaxRunLength)
            _run += text;
        else
            _run = text;
        _runAppendable = appendable;
        _lastKeyAt = now;

        KeysText.Text = _run;
        Pill.BeginAnimation(OpacityProperty, null);
        Pill.Opacity = 1.0;
        _fade.Restart();
    }

    private void BeginFadeOut()
    {
        _runAppendable = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350)) { FillBehavior = FillBehavior.HoldEnd };
        Pill.BeginAnimation(OpacityProperty, fade);
    }

    // ---- key naming ----

    private static bool IsModifierVk(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    private static string ModifierName(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => "Shift",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 or 0xA5 => "Alt",
        _ => "Win",
    };

    private static bool AnyOtherModifierDown(int releasedVk)
    {
        foreach (int vk in ModifierVks)
        {
            if (vk != releasedVk && IsDown(vk))
                return true;
        }
        return false;
    }

    private static readonly int[] ModifierVks = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static string KeyName(int vk, bool upperCase)
    {
        if (vk is >= 0x41 and <= 0x5A) // A–Z
            return upperCase ? ((char)vk).ToString() : ((char)(vk + 32)).ToString();
        if (vk is >= 0x30 and <= 0x39) // 0–9
            return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) // numpad digits
            return ((char)('0' + vk - 0x60)).ToString();
        if (vk is >= 0x70 and <= 0x87) // F1–F24
            return "F" + (vk - 0x6F);
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x14 => "CapsLock",
            0x20 => "Space",
            0x21 => "PgUp",
            0x22 => "PgDn",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2C => "PrtSc",
            0x2D => "Ins",
            0x2E => "Del",
            0x5D => "Menu",
            0x6A => "*",
            0x6B => "+",
            0x6D => "-",
            0x6E => ".",
            0x6F => "/",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => KeyInterop.KeyFromVirtualKey(vk).ToString(),
        };
    }

    /// <summary>Tiny helper: a restartable one-shot dispatcher timer.</summary>
    private sealed class DispatcherTimerWrapper
    {
        private readonly System.Windows.Threading.DispatcherTimer _timer;

        public DispatcherTimerWrapper(TimeSpan delay, Action callback)
        {
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                callback();
            };
        }

        public void Restart()
        {
            _timer.Stop();
            _timer.Start();
        }

        public void Stop() => _timer.Stop();
    }

    // ---- native ----

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdllHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
