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
    SD.Color HoverTint,
    SD.Color ControlFace,
    SD.Color ControlHover,
    SD.Color ControlPressed,
    SD.Color ControlBorder,
    SD.Color ControlShadow,
    SD.Color Glyph,
    SD.Color FocusRing,
    SD.Color TooltipFill,
    SD.Color TooltipBorder,
    SD.Color TooltipText);

internal static class QuickAccessOverlayThemePalette
{
    // WinShot is dark-only today. Keeping the selection here makes the overlay ready for
    // a future app theme service without changing its rendering or interaction code.
    public static QuickAccessOverlayTheme Current => QuickAccessOverlayTheme.Dark;

    public static QuickAccessOverlayVisuals For(QuickAccessOverlayTheme theme) => theme switch
    {
        QuickAccessOverlayTheme.Light => new(
            CardFill: SD.Color.FromArgb(0xEE, 0xF4, 0xFF),
            HoverTint: SD.Color.FromArgb(107, 236, 243, 252),
            ControlFace: SD.Color.FromArgb(204, 255, 255, 255),
            ControlHover: SD.Color.FromArgb(245, 255, 255, 255),
            ControlPressed: SD.Color.FromArgb(224, 239, 243, 248),
            ControlBorder: SD.Color.FromArgb(245, 255, 255, 255),
            ControlShadow: SD.Color.FromArgb(46, 59, 79, 110),
            Glyph: SD.Color.FromArgb(0x11, 0x18, 0x20),
            FocusRing: SD.Color.FromArgb(0x24, 0x86, 0xFF),
            TooltipFill: SD.Color.FromArgb(0xF7, 0xFB, 0xFF),
            TooltipBorder: SD.Color.FromArgb(42, 17, 24, 32),
            TooltipText: SD.Color.FromArgb(0x11, 0x18, 0x20)),
        _ => new(
            CardFill: ThemePalette.WindowBg,
            HoverTint: SD.Color.FromArgb(140, 15, 18, 25),
            ControlFace: SD.Color.FromArgb(22, 255, 255, 255),
            ControlHover: SD.Color.FromArgb(36, 255, 255, 255),
            ControlPressed: SD.Color.FromArgb(50, 255, 255, 255),
            ControlBorder: SD.Color.FromArgb(36, 255, 255, 255),
            ControlShadow: SD.Color.FromArgb(61, 0, 0, 0),
            Glyph: SD.Color.FromArgb(0xF7, 0xF9, 0xFC),
            FocusRing: ThemePalette.SelectionRing,
            TooltipFill: SD.Color.FromArgb(0x16, 0x18, 0x1F),
            TooltipBorder: SD.Color.FromArgb(48, 255, 255, 255),
            TooltipText: SD.Color.FromArgb(0xF7, 0xF9, 0xFC)),
    };
}
