using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

/// <summary>How the keystroke overlay behaves for one take.</summary>
/// <param name="Render">False makes the overlay a keyboard hook and nothing else — used when
/// the user wants "hide cursor while typing" without a keystroke pill on screen.</param>
/// <param name="CommandOnly">Show chords only; plain typing is not drawn.</param>
/// <param name="Position">One of <see cref="RecordingOptions.NormalizeKeystrokePosition"/>.</param>
public readonly record struct KeystrokeOverlayOptions(
    bool Render,
    bool CommandOnly,
    string Position)
{
    public static KeystrokeOverlayOptions FromSettings(Settings settings) => new(
        settings.ShowKeystrokes,
        settings.KeystrokesCommandOnly,
        RecordingOptions.NormalizeKeystrokePosition(settings.KeystrokePosition));

    public static KeystrokeOverlayOptions Default => new(true, false, RecordingOptions.KeystrokeDefaultPosition);
}

public sealed class FastKeystrokeOverlayWindow : AlphaOverlayWindow, IRecordingOverlay
{
    private const int MaxRunLength = 18;
    private const int VisibleMs = 1500;
    private const int FadeMs = 350;
    private static readonly TimeSpan AppendWindow = TimeSpan.FromSeconds(1.2);
    private static readonly SD.Color PillBack = ThemePalette.ToolbarBg;
    private static readonly SD.Color PillBorder = ThemePalette.BorderStrong;
    private static readonly SD.Color TextColor = ThemePalette.TextPrimary;

    private readonly SD.Rectangle _regionPx;
    private readonly KeystrokeOverlayOptions _options;
    private readonly HookProc _hookProc;
    private readonly bool _installHook;
    private readonly WF.Timer _timer = new() { Interval = WinShot.Core.Motion.FrameIntervalMs };
    private IDisposable? _motionClock;
    private readonly double _scale;
    private readonly SD.Font _font;
    private IntPtr _hook;
    private volatile bool _paused;

    private string _run = "";
    private bool _runAppendable;
    private DateTime _lastKeyAt = DateTime.MinValue;
    private bool _modifierDownWithoutKey;
    private long _shownAtMs;
    private double _opacity;
    private int _renderedWidth;
    private bool _typing;

    public FastKeystrokeOverlayWindow(SD.Rectangle regionScreenPx)
        : this(regionScreenPx, KeystrokeOverlayOptions.Default, installHook: true)
    {
    }

    public FastKeystrokeOverlayWindow(SD.Rectangle regionScreenPx, KeystrokeOverlayOptions options)
        : this(regionScreenPx, options, installHook: true)
    {
    }

    public static FastKeystrokeOverlayWindow CreateForSmokeTest(SD.Rectangle regionScreenPx) =>
        new(regionScreenPx, KeystrokeOverlayOptions.Default, installHook: false);

    /// <summary>
    /// Raised on the UI thread when typing starts and when it stops. "Hide the cursor while
    /// typing" rides this: the hook is already the only thing in the app that knows.
    /// </summary>
    public event Action<bool>? TypingChanged;

    private int S(int logical) => (int)Math.Round(logical * _scale);

    private FastKeystrokeOverlayWindow(
        SD.Rectangle regionScreenPx,
        KeystrokeOverlayOptions options,
        bool installHook)
    {
        _regionPx = regionScreenPx;
        _options = options;
        _installHook = installHook;
        _hookProc = KeyboardHookCallback;
        _scale = RecordingMonitorDpi.ScaleFor(regionScreenPx);
        // Pixel-unit font so the glyphs scale with the pill box, not the monitor DPI.
        _font = new SD.Font("Segoe UI", S(30), SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);

        _timer.Tick += (_, _) => AdvanceFade();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            StopMotion();
            _opacity = 0;
            _runAppendable = false;
            _renderedWidth = 0;
            SetTyping(false);
            PresentEmpty();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PresentEmpty();
        if (_installHook)
            InstallHook();
    }

    protected override void OnClosed(EventArgs e)
    {
        RemoveHook();
        StopMotion();
        SetTyping(false);
        _timer.Dispose();
        _font.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Draws the pill into an ARGB frame and presents it at the chosen corner of the
    /// recorded region. The fade lives in the bitmap's alpha, so anti-aliased edges and the
    /// fading pill blend against the real desktop.</summary>
    private void RenderFrame()
    {
        if (!_options.Render || _opacity <= 0 || string.IsNullOrEmpty(_run))
        {
            PresentEmpty();
            return;
        }

        int height = S(52);
        SD.Size textSize = WF.TextRenderer.MeasureText(_run, _font);
        int target = Math.Min(
            Math.Max(16, _regionPx.Width - S(16)),
            Math.Max(S(96), textSize.Width + S(44)));
        // The pill glides to its new width instead of snapping there as the chord grows. With
        // animation off system-wide it lands immediately, like every other tween in the app.
        _renderedWidth = WinShot.Core.Motion.AnimationsEnabled()
            ? KeystrokeDisplay.EaseWidth(_renderedWidth, target)
            : target;
        int width = Math.Max(1, _renderedWidth);

        using var frame = new SD.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(frame))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int fillAlpha = (int)Math.Round(235 * _opacity);
            using var path = GdiPaths.RoundedRect(new SD.Rectangle(0, 0, width - 1, height - 1), height / 2);
            using var brush = new SD.SolidBrush(SD.Color.FromArgb(fillAlpha, PillBack));
            using var pen = new SD.Pen(SD.Color.FromArgb((int)Math.Round(PillBorder.A * _opacity), PillBorder), 1);
            g.FillPath(brush, path);
            g.DrawPath(pen, path);

            // Centred in the pill's CURRENT width, so the text stays put while the pill grows
            // around it rather than sliding as the edges move.
            var textBounds = new SD.Rectangle(S(10), 0, Math.Max(1, width - S(20)), height);
            WF.TextRenderer.DrawText(
                g,
                _run,
                _font,
                textBounds,
                SD.Color.FromArgb((int)Math.Round(255 * _opacity), TextColor),
                WF.TextFormatFlags.HorizontalCenter |
                WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine |
                WF.TextFormatFlags.EndEllipsis);
        }

        Present(frame, KeystrokeDisplay.Anchor(
            _regionPx,
            new SD.Size(width, height),
            _options.Position,
            S(14)));
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
            {
                _renderedWidth = 0;
                SetTyping(false);
                StopMotion();
            }
        }

        RenderFrame();
    }

    private void OnKeyEvent(bool down, int vk)
    {
        try
        {
            if (_paused)
                return;

            bool isModifier = KeystrokeDisplay.IsModifierVk(vk);
            if (down)
            {
                if (isModifier)
                {
                    _modifierDownWithoutKey = true;
                    return;
                }

                _modifierDownWithoutKey = false;
                bool ctrl = IsDown(0x11);
                bool shift = IsDown(0x10);
                bool alt = IsDown(0x12);
                bool win = IsDown(0x5B) || IsDown(0x5C);
                var (text, appendable) = KeystrokeDisplay.Build(vk, ctrl, shift, alt, win);
                Display(text, appendable, KeystrokeDisplay.IsCommandChord(ctrl, alt, win));
            }
            else if (isModifier && _modifierDownWithoutKey && !AnyOtherModifierDown(vk))
            {
                _modifierDownWithoutKey = false;
                Display(KeystrokeDisplay.ModifierName(vk), appendable: false, isCommandChord: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fast keystroke overlay update failed", ex);
        }
    }

    private void Display(string text, bool appendable, bool isCommandChord)
    {
        // Typing is happening either way — that is what "hide the cursor while typing" needs
        // to know — even when the pill itself is filtered out or turned off.
        SetTyping(true);

        if (_options.CommandOnly && !isCommandChord)
        {
            _shownAtMs = Environment.TickCount64; // keep the typing state alive while they type
            if (!_timer.Enabled)
                StartMotion();
            return;
        }

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
            StartMotion();
        RenderFrame();
    }

    private void SetTyping(bool typing)
    {
        if (_typing == typing)
            return;
        _typing = typing;
        try { TypingChanged?.Invoke(typing); }
        catch (Exception ex) { Log.Error("Keystroke typing-state handler failed", ex); }
    }

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

    /// <summary>
    /// Animation ticks only land on time while the high-resolution clock is held, so it is
    /// acquired with the timer and released with it — never for longer.
    /// </summary>
    private void StartMotion()
    {
        _motionClock ??= WinShot.Core.Motion.Acquire();
        _timer.Start();
    }

    private void StopMotion()
    {
        _timer.Stop();
        _motionClock?.Dispose();
        _motionClock = null;
    }
}
