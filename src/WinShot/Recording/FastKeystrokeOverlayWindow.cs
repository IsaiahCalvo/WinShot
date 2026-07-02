using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastKeystrokeOverlayWindow : WF.Form, IRecordingOverlay
{
    private const int MaxRunLength = 18;
    private const int VisibleMs = 1500;
    private const int FadeMs = 350;
    private static readonly TimeSpan AppendWindow = TimeSpan.FromSeconds(1.2);
    private static readonly SD.Color TransparentKey = SD.Color.Magenta;
    private static readonly SD.Color PillBack = SD.Color.FromArgb(30, 30, 30);
    private static readonly SD.Color PillBorder = SD.Color.FromArgb(54, 255, 255, 255);
    private static readonly SD.Color TextColor = SD.Color.White;

    private readonly SD.Rectangle _regionPx;
    private readonly HookProc _hookProc;
    private readonly bool _installHook;
    private readonly WF.Timer _timer = new() { Interval = 33 };
    private readonly SD.Font _font = new("Segoe UI Semibold", 15f, SD.FontStyle.Bold);
    private IntPtr _hook;
    private volatile bool _paused;

    private string _run = "";
    private bool _runAppendable;
    private DateTime _lastKeyAt = DateTime.MinValue;
    private bool _modifierDownWithoutKey;
    private long _shownAtMs;
    private double _opacity;

    public FastKeystrokeOverlayWindow(SD.Rectangle regionScreenPx)
        : this(regionScreenPx, installHook: true)
    {
    }

    public static FastKeystrokeOverlayWindow CreateForSmokeTest(SD.Rectangle regionScreenPx) =>
        new(regionScreenPx, installHook: false);

    private FastKeystrokeOverlayWindow(SD.Rectangle regionScreenPx, bool installHook)
    {
        _regionPx = regionScreenPx;
        _installHook = installHook;
        _hookProc = KeyboardHookCallback;

        int stripHeight = Math.Min(Math.Max(1, regionScreenPx.Height), 160);
        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = TransparentKey;
        ClientSize = new SD.Size(Math.Max(1, regionScreenPx.Width), stripHeight);
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        TransparencyKey = TransparentKey;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        _timer.Tick += (_, _) => AdvanceFade();
    }

    protected override bool ShowWithoutActivation => true;

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            _timer.Stop();
            _opacity = 0;
            _runAppendable = false;
            Invalidate();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        MakeClickThrough();
        PositionOverRegion();
        if (_installHook)
            InstallHook();
    }

    protected override void OnClosed(EventArgs e)
    {
        RemoveHook();
        _timer.Stop();
        _timer.Dispose();
        _font.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_opacity <= 0 || string.IsNullOrEmpty(_run))
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        SD.Size textSize = WF.TextRenderer.MeasureText(_run, _font);
        int width = Math.Min(Width - 16, Math.Max(74, textSize.Width + 32));
        int height = 36;
        int x = Math.Max(8, (Width - width) / 2);
        int y = Math.Max(0, Height - height - 14);
        int alpha = (int)Math.Round(230 * _opacity);
        using var path = GdiPaths.RoundedRect(new SD.Rectangle(x, y, width, height), 17);
        using var brush = new SD.SolidBrush(SD.Color.FromArgb(alpha, PillBack));
        using var pen = new SD.Pen(SD.Color.FromArgb((int)Math.Round(54 * _opacity), PillBorder), 1);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);

        var textBounds = new SD.Rectangle(x + 16, y + 7, width - 32, height - 14);
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.EndEllipsis;
        WF.TextRenderer.DrawText(
            e.Graphics,
            _run,
            _font,
            textBounds,
            SD.Color.FromArgb((int)Math.Round(255 * _opacity), TextColor),
            flags);
    }

    private void AdvanceFade()
    {
        long elapsed = Environment.TickCount64 - _shownAtMs;
        if (elapsed <= VisibleMs)
        {
            _opacity = 1;
        }
        else
        {
            _runAppendable = false;
            _opacity = Math.Max(0, 1 - ((elapsed - VisibleMs) / (double)FadeMs));
            if (_opacity <= 0)
                _timer.Stop();
        }

        Invalidate();
    }

    private void OnKeyEvent(bool down, int vk)
    {
        try
        {
            if (_paused)
                return;

            bool isModifier = IsModifierVk(vk);
            if (down)
            {
                if (isModifier)
                {
                    _modifierDownWithoutKey = true;
                    return;
                }

                _modifierDownWithoutKey = false;
                var (text, appendable) = BuildDisplay(vk);
                Display(text, appendable);
            }
            else if (isModifier && _modifierDownWithoutKey && !AnyOtherModifierDown(vk))
            {
                _modifierDownWithoutKey = false;
                Display(ModifierName(vk), appendable: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fast keystroke overlay update failed", ex);
        }
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
        _shownAtMs = Environment.TickCount64;
        _opacity = 1;
        if (!_timer.Enabled)
            _timer.Start();
        Invalidate();
    }

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
            return (name, true);
        return (shift ? "Shift+" + name : name, false);
    }

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
        if (vk is >= 0x41 and <= 0x5A)
            return upperCase ? ((char)vk).ToString() : ((char)(vk + 32)).ToString();
        if (vk is >= 0x30 and <= 0x39)
            return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69)
            return ((char)('0' + vk - 0x60)).ToString();
        if (vk is >= 0x70 and <= 0x87)
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
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
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
            _ => ((WF.Keys)vk).ToString(),
        };
    }

    private void MakeClickThrough()
    {
        int style = GetWindowLongW(Handle, GwlExstyle);
        SetWindowLongW(Handle, GwlExstyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    private void PositionOverRegion()
    {
        int stripHeight = Math.Min(_regionPx.Height, 160);
        SetWindowPos(
            Handle,
            HwndTopmost,
            _regionPx.X,
            _regionPx.Bottom - stripHeight,
            _regionPx.Width,
            stripHeight,
            SwpNoActivate);
    }

    private void InstallHook()
    {
        _hook = SetWindowsHookExW(WhKeyboardLl, _hookProc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Error($"Failed to install keyboard hook for fast keystroke overlay (error {Marshal.GetLastWin32Error()})");
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero)
            return;
        if (!UnhookWindowsHookEx(_hook))
            Log.Error("Failed to remove fast keystroke overlay keyboard hook");
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
                if (!IsDisposed)
                    BeginInvoke(new Action(() => OnKeyEvent(down, vk)));
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

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
