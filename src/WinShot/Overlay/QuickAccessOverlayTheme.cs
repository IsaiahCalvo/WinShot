using SD = System.Drawing;
using WinShot.Core;

namespace WinShot.Overlay;

internal enum QuickAccessOverlayTheme
{
    Light,
    Dark,
}

internal readonly record struct QuickAccessOverlayVisuals(
    SD.Color CardFill,
    /// <summary>Faint hairline around the card. A capture of a dark window on a dark
    /// desktop is otherwise its own background, and the card has no visible edge at all.</summary>
    SD.Color CardEdge,
    /// <summary>Wash over the frosted capture while the pointer is on the card. Kept light:
    /// the blur already hides the detail, and a heavy wash made the whole card read as dark.</summary>
    SD.Color HoverTint,
    /// <summary>Wash behind a status message. Heavier than <see cref="HoverTint"/> because the
    /// message is bare text on the capture, with no button face of its own under it.</summary>
    SD.Color StatusTint,
    /// <summary>Button face. In the dark theme it is dark glass, so the white glyph keeps its
    /// contrast over a bright capture now that the hover wash is light.</summary>
    SD.Color ControlFace,
    SD.Color ControlHover,
    SD.Color ControlPressed,
    SD.Color ControlBorder,
    SD.Color ControlShadow,
    SD.Color Glyph,
    SD.Color FocusRing,
    SD.Color TooltipFill,
    SD.Color TooltipBorder,
    SD.Color TooltipText,
    /// <summary>Flat scrim under the recording card's duration/size strip, with the lead-in
    /// gradient above it ramping from the same colour at zero alpha. Graded per theme against
    /// <see cref="Glyph"/> over a WHITE capture — a recording of a bright document is the
    /// common case, and that is where a black-on-black ramp falls under 4.5:1.</summary>
    SD.Color MediaStripScrim);

internal static class QuickAccessOverlayThemePalette
{
    // WinShot is dark-only today. Keeping the selection here makes the overlay ready for
    // a future app theme service without changing its rendering or interaction code.
    public static QuickAccessOverlayTheme Current => QuickAccessOverlayTheme.Dark;

    public static QuickAccessOverlayVisuals For(QuickAccessOverlayTheme theme) => theme switch
    {
        QuickAccessOverlayTheme.Light => new(
            CardFill: SD.Color.FromArgb(0xEE, 0xF4, 0xFF),
            CardEdge: SD.Color.FromArgb(38, 17, 24, 32),
            HoverTint: SD.Color.FromArgb(107, 236, 243, 252),
            StatusTint: SD.Color.FromArgb(107, 236, 243, 252),
            ControlFace: SD.Color.FromArgb(204, 255, 255, 255),
            ControlHover: SD.Color.FromArgb(245, 255, 255, 255),
            ControlPressed: SD.Color.FromArgb(224, 239, 243, 248),
            ControlBorder: SD.Color.FromArgb(245, 255, 255, 255),
            ControlShadow: SD.Color.FromArgb(46, 59, 79, 110),
            Glyph: SD.Color.FromArgb(0x11, 0x18, 0x20),
            FocusRing: SD.Color.FromArgb(0x24, 0x86, 0xFF),
            TooltipFill: SD.Color.FromArgb(0xF7, 0xFB, 0xFF),
            TooltipBorder: SD.Color.FromArgb(42, 17, 24, 32),
            TooltipText: SD.Color.FromArgb(0x11, 0x18, 0x20),
            // Light's glyph is near-black, so its scrim has to be near-white or the strip
            // would print black text on a black bar.
            MediaStripScrim: SD.Color.FromArgb(170, 255, 255, 255)),
        _ => new(
            CardFill: ThemePalette.WindowBg,
            CardEdge: SD.Color.FromArgb(46, 255, 255, 255),
            HoverTint: SD.Color.FromArgb(64, 15, 18, 25),
            StatusTint: SD.Color.FromArgb(140, 15, 18, 25),
            ControlFace: SD.Color.FromArgb(150, 24, 26, 32),
            ControlHover: SD.Color.FromArgb(175, 40, 43, 50),
            ControlPressed: SD.Color.FromArgb(195, 56, 60, 68),
            ControlBorder: SD.Color.FromArgb(36, 255, 255, 255),
            ControlShadow: SD.Color.FromArgb(61, 0, 0, 0),
            Glyph: SD.Color.FromArgb(0xF7, 0xF9, 0xFC),
            FocusRing: SD.Color.FromArgb(0x24, 0x86, 0xFF),
            TooltipFill: SD.Color.FromArgb(0x16, 0x18, 0x1F),
            TooltipBorder: SD.Color.FromArgb(48, 255, 255, 255),
            TooltipText: SD.Color.FromArgb(0xF7, 0xF9, 0xFC),
            MediaStripScrim: SD.Color.FromArgb(150, 0, 0, 0)),
    };
}
