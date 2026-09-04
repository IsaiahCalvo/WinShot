using System.Drawing.Text;
using System.Linq;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// The one place WinForms (Fast*) surfaces read theme colors from, mirroring
/// <c>Theme/Theme.xaml</c> so the app shows a single accent identity and a single
/// dark palette everywhere. Before this existed, the Fast selectors hardcoded
/// #4DA3FF, Settings/History used #2D7DFF, and the theme declared #0A84FF — three
/// different blues. Keep these values in sync with Theme.xaml.
/// </summary>
public static class ThemePalette
{
    // Surfaces (opaque) — match the dark elevation scale in Theme.xaml.
    public static readonly SD.Color WindowBg = SD.Color.FromArgb(0x1C, 0x1C, 0x1E);
    public static readonly SD.Color ToolbarBg = SD.Color.FromArgb(0x26, 0x26, 0x28);
    public static readonly SD.Color SurfaceAlt = SD.Color.FromArgb(0x38, 0x38, 0x3B);
    public static readonly SD.Color Elevated = SD.Color.FromArgb(0x32, 0x32, 0x36);
    public static readonly SD.Color SurfaceHover = SD.Color.FromArgb(0x45, 0x45, 0x4A);

    // Single accent identity (macOS dark-mode system blue).
    public static readonly SD.Color Accent = SD.Color.FromArgb(0x0A, 0x84, 0xFF);
    public static readonly SD.Color AccentHover = SD.Color.FromArgb(0x40, 0x9C, 0xFF);

    // Status colors, paired with the system-blue identity (macOS system red/orange).
    // Red doubles as the recording dot and destructive actions; Warn as paused/warning.
    public static readonly SD.Color Red = SD.Color.FromArgb(0xFF, 0x45, 0x3A);
    public static readonly SD.Color Warn = SD.Color.FromArgb(0xFF, 0x9F, 0x0A);

    // Text.
    public static readonly SD.Color TextPrimary = SD.Color.FromArgb(0xF2, 0xF2, 0xF4);
    // Theme.xaml paints secondary text as #B3FFFFFF; this is that 70% white composited
    // over ToolbarBg, so a WinForms label matches the WPF one beside it.
    public static readonly SD.Color TextSecondary = SD.Color.FromArgb(0xBE, 0xBE, 0xBF);
    // Red as type rather than fill, mirroring Theme.xaml's TextErrorBrush: Red itself is a
    // stroke colour and only reads 4.43:1 on ToolbarBg, which is what these surfaces are.
    public static readonly SD.Color TextError = SD.Color.FromArgb(0xFF, 0x6B, 0x60);

    // White-alpha interaction fills / hairlines (GDI+ blends the alpha over dark).
    public static readonly SD.Color HoverFill = SD.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
    public static readonly SD.Color Border = SD.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
    public static readonly SD.Color BorderStrong = SD.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

    // ============================ TYPE SCALE ============================
    // The same six steps Theme.xaml defines in DIP, in points, because GDI+ sizes
    // fonts in points and WPF in DIP: 1 pt = 96/72 DIP. A WinForms label and the
    // WPF one beside it are the same size only if both come from here.
    public const float TextMicroPt = 7.5f;      // 10 DIP
    public const float TextCaptionPt = 8.25f;   // 11 DIP
    public const float TextBodyPt = 9f;         // 12 DIP
    public const float TextBodyLgPt = 9.75f;    // 13 DIP
    public const float TextTitlePt = 12f;       // 16 DIP
    public const float TextDisplayPt = 16.5f;   // 22 DIP

    /// <summary>
    /// The icon on an action button, in points — Theme.xaml's GlyphAction step. Icon-font
    /// glyphs are pictures, not type: matching one to a text step makes it read as too
    /// small inside a button box that did not shrink with it. WinForms had no glyph step
    /// at all, which is how the All-In-One selector's toolbar icons ended up 20% smaller
    /// than they render on the branch point.
    /// </summary>
    public const float GlyphActionPt = 15f;     // 20 DIP

    /// <summary>
    /// Off the scale deliberately: the quick-actions card is pinned, and its menu
    /// was measured to 202x357 by hand at this size. TextCaptionPt reflows it.
    /// </summary>
    public const float PinnedMenuRowPt = 8.5f;

    /// <summary>
    /// Off the scale deliberately: the quick-actions card is pinned, and its action
    /// labels were sized to fit under the card's glyphs at this value.
    /// QuickAccessOverlayInteractionTests pins it.
    /// </summary>
    public const float PinnedCardLabelPt = 7f;

    /// <summary>
    /// Off the scale deliberately: the quick-access tooltip is a 2 mm-tall chip whose
    /// width QuickAccessTooltipLayoutTests pins. TextMicroPt widens it by a character.
    /// </summary>
    public const float PinnedTooltipPt = 6.5f;

    private static readonly bool HasFluentIcons = IsFontInstalled("Segoe Fluent Icons");

    /// <summary>
    /// Win11 ships "Segoe Fluent Icons"; Win10 only has "Segoe MDL2 Assets". The glyph
    /// codepoints WinShot uses exist in both, so we just pick whichever is installed
    /// (GDI+ would silently substitute the wrong font and draw tofu otherwise).
    /// </summary>
    public static SD.Font IconFont(float sizePt, SD.FontStyle style = SD.FontStyle.Regular)
        => new(HasFluentIcons ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", sizePt, style, SD.GraphicsUnit.Point);

    public static SD.Font UiFont(float sizePt, SD.FontStyle style = SD.FontStyle.Regular)
        => new("Segoe UI", sizePt, style, SD.GraphicsUnit.Point);

    private static bool IsFontInstalled(string name)
    {
        try
        {
            using var installed = new InstalledFontCollection();
            return installed.Families.Any(f =>
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
