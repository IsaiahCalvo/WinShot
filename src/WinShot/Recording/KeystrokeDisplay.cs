using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

/// <summary>
/// What the keystroke overlay says and how wide it says it — pure, so the chord vocabulary,
/// the command-only filter and the pill's width glide are pinned by tests rather than read
/// off a recording.
///
/// The chord is one pill, not per-key boxes and not a "+"-joined string: the parts are laid
/// out with an en space between them so "Ctrl Shift F" reads as one gesture.
/// </summary>
public static class KeystrokeDisplay
{
    /// <summary>Separator between the parts of a chord. An en space, present in every UI face,
    /// rather than a "+" glyph nobody presses.</summary>
    public const string PartSeparator = " ";

    /// <summary>Fraction of the remaining distance the pill's width covers each frame. An
    /// exponential approach rather than a fixed tween, because the target moves while the user
    /// is still typing.</summary>
    public const double WidthEaseFactor = 0.18;

    /// <summary>Whether the modifiers held make this a command chord rather than typing. This
    /// is the line "Show only command keys" draws.</summary>
    public static bool IsCommandChord(bool ctrl, bool alt, bool win) => ctrl || alt || win;

    public static bool IsModifierVk(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    public static string ModifierName(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => "Shift",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 or 0xA5 => "Alt",
        _ => "Win",
    };

    /// <summary>
    /// The text for one key press and whether it may be appended to a run of typing.
    /// A command chord is never appendable — it is a single gesture, not a word.
    /// </summary>
    public static (string Text, bool Appendable) Build(int vk, bool ctrl, bool shift, bool alt, bool win)
    {
        if (IsCommandChord(ctrl, alt, win))
        {
            var parts = new List<string>(5);
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (win) parts.Add("Win");
            parts.Add(KeyName(vk, upperCase: true));
            return (string.Join(PartSeparator, parts), false);
        }

        string name = KeyName(vk, upperCase: shift);
        if (name.Length == 1)
            return (name, true);
        return (shift ? "Shift" + PartSeparator + name : name, false);
    }

    /// <summary>
    /// The printable name of a virtual key. Arrows, Enter and Tab render as their symbols —
    /// they are unambiguous and short. Everything else keeps a word, because a symbol nobody
    /// recognises is worse than a name, and a missing glyph is worse than either.
    /// </summary>
    public static string KeyName(int vk, bool upperCase)
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
            0x09 => "⇥",   // ⇥
            0x0D => "↵",   // ↵
            0x1B => "⎋",   // ⎋
            0x14 => "CapsLock",
            0x20 => "␣",   // ␣
            0x21 => "PgUp",
            0x22 => "PgDn",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",   // ←
            0x26 => "↑",   // ↑
            0x27 => "→",   // →
            0x28 => "↓",   // ↓
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

    /// <summary>
    /// One frame of the pill's width glide. Appearing from nothing lands on the target
    /// immediately — a pill growing out of zero width reads as a glitch — and the last pixel
    /// always closes, so the glide can never stall a pixel short of the text it holds.
    /// </summary>
    public static int EaseWidth(int current, int target)
    {
        if (current <= 0 || target <= 0)
            return Math.Max(0, target);
        int delta = target - current;
        if (Math.Abs(delta) <= 1)
            return target;

        int step = (int)Math.Round(delta * WidthEaseFactor);
        if (step == 0)
            step = Math.Sign(delta);
        int next = current + step;
        return Math.Abs(target - next) <= 1 ? target : next;
    }

    /// <summary>
    /// Where the pill sits inside the recorded rectangle, given its size and the inset from
    /// the region's edges. The vocabulary mirrors the webcam overlay's corners, with the two
    /// centred positions the keystroke pill actually wants.
    /// </summary>
    public static SD.Point Anchor(SD.Rectangle region, SD.Size pill, string position, int inset)
    {
        int centreX = region.X + Math.Max(0, (region.Width - pill.Width) / 2);
        int leftX = region.X + inset;
        int rightX = region.Right - pill.Width - inset;
        int topY = region.Y + inset;
        int bottomY = region.Bottom - pill.Height - inset;

        (int X, int Y) at = WinShot.Core.RecordingOptions.NormalizeKeystrokePosition(position) switch
        {
            "top-left" => (leftX, topY),
            "top-center" => (centreX, topY),
            "top-right" => (rightX, topY),
            "bottom-left" => (leftX, bottomY),
            "bottom-right" => (rightX, bottomY),
            _ => (centreX, bottomY),
        };

        // Never outside the recorded rectangle, however small it is.
        return new SD.Point(
            Math.Clamp(at.X, region.X, Math.Max(region.X, region.Right - pill.Width)),
            Math.Clamp(at.Y, region.Y, Math.Max(region.Y, region.Bottom - pill.Height)));
    }
}
