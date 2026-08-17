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
    public static readonly SD.Color CardGroupBg = SD.Color.FromArgb(0x23, 0x23, 0x26);
    public static readonly SD.Color ContextBarBg = SD.Color.FromArgb(0x1F, 0x1F, 0x22);
    public static readonly SD.Color SidebarBg = SD.Color.FromArgb(0x19, 0x19, 0x1B);
    public static readonly SD.Color CanvasBg = SD.Color.FromArgb(0x14, 0x14, 0x16);

    // Blue is STATE, never an action fill: toggles ON, checks, slider fills,
    // selected chips/tints. Actions are white (see Action* below).
    public static readonly SD.Color Accent = SD.Color.FromArgb(0x2E, 0x6F, 0xC1);
    public static readonly SD.Color AccentHover = SD.Color.FromArgb(0x3A, 0x7D, 0xD1);
    // Brand identity blue (app icon / titlebar dot only — never a control fill).
    public static readonly SD.Color BrandBlue = SD.Color.FromArgb(0x0A, 0x84, 0xFF);

    // Selected-tint surfaces (chips, checked tool buttons, sidebar selection).
    public static readonly SD.Color SelectedTintBg = SD.Color.FromArgb(0x40, 0x2E, 0x6F, 0xC1);
    public static readonly SD.Color SelectedTintBorder = SD.Color.FromArgb(0x59, 0x4E, 0x94, 0xE0);
    public static readonly SD.Color SelectedTintText = SD.Color.FromArgb(0x9C, 0xC7, 0xFF);
    public static readonly SD.Color SelectionRing = SD.Color.FromArgb(0xB2, 0x4E, 0x94, 0xE0);
    public static readonly SD.Color Link = SD.Color.FromArgb(0x7F, 0xB0, 0xE8);

    // White action buttons — the ONLY strong fill (Save, Stop, Done, Start, Edit, Export).
    public static readonly SD.Color ActionBg = SD.Color.FromArgb(0xF2, 0xF2, 0xF4);
    public static readonly SD.Color ActionBgHover = SD.Color.FromArgb(0xFF, 0xFF, 0xFF);
    public static readonly SD.Color ActionText = SD.Color.FromArgb(0x11, 0x11, 0x11);

    // Reds/amber: solid red only on destructive confirm; dot/paused indicators.
    public static readonly SD.Color DestructiveRed = SD.Color.FromArgb(0xC9, 0x40, 0x3F);
    public static readonly SD.Color RecordingRed = SD.Color.FromArgb(0xFF, 0x52, 0x52);
    public static readonly SD.Color PausedAmber = SD.Color.FromArgb(0xFF, 0xB0, 0x20);
    public static readonly SD.Color ErrorBorder = SD.Color.FromArgb(0x8C, 0xE8, 0x5C, 0x5C);
    public static readonly SD.Color ErrorText = SD.Color.FromArgb(0xE8, 0x85, 0x85);
    public static readonly SD.Color DangerTintBg = SD.Color.FromArgb(0x24, 0xE8, 0x5C, 0x5C);

    // Text.
    public static readonly SD.Color TextPrimary = SD.Color.FromArgb(0xF2, 0xF2, 0xF4);
    public static readonly SD.Color TextSecondary = SD.Color.FromArgb(0xB8, 0xB8, 0xBC);
    public static readonly SD.Color TextMuted = SD.Color.FromArgb(0x8A, 0x8A, 0x90);

    // White-alpha interaction fills / hairlines (GDI+ blends the alpha over dark).
    public static readonly SD.Color HoverFill = SD.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);      // ghost hover .08
    public static readonly SD.Color PressedFill = SD.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);    // pressed .14
    public static readonly SD.Color SecondaryBtnBg = SD.Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF); // secondary button .09
    public static readonly SD.Color InputBg = SD.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);        // inputs/combos .06
    public static readonly SD.Color RowDivider = SD.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);     // row hairlines .06
    public static readonly SD.Color CardBorder = SD.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);     // card borders .08
    public static readonly SD.Color Border = SD.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);         // control borders .14
    public static readonly SD.Color BorderStrong = SD.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);   // top edge-light .2

    // Toggle OFF state.
    public static readonly SD.Color ToggleOffBg = SD.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
    public static readonly SD.Color ToggleKnobOff = SD.Color.FromArgb(0x8A, 0x8A, 0x90);

    private static readonly bool HasFluentIcons = IsFontInstalled("Segoe Fluent Icons");

    /// <summary>
    /// Win11 ships "Segoe Fluent Icons"; Win10 only has "Segoe MDL2 Assets". The glyph
    /// codepoints WinShot uses exist in both, so we just pick whichever is installed
    /// (GDI+ would silently substitute the wrong font and draw tofu otherwise).
    /// </summary>
    public static SD.Font IconFont(float sizePt, SD.FontStyle style = SD.FontStyle.Regular)
        => new(HasFluentIcons ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", sizePt, style, SD.GraphicsUnit.Point);

    /// <summary>
    /// UI text — bundled Manrope (falls back to Segoe UI if the embedded fonts
    /// failed to register). GDI has no semibold FontStyle, so the 600 weight is
    /// its own family name ("Manrope SemiBold"); Bold maps to the real 700 cut.
    /// </summary>
    public static SD.Font UiFont(float sizePt, SD.FontStyle style = SD.FontStyle.Regular)
        => AppFonts.Loaded
            ? new("Manrope", sizePt, style, SD.GraphicsUnit.Point)
            : new("Segoe UI", sizePt, style, SD.GraphicsUnit.Point);

    /// <summary>Manrope SemiBold (600) — titles, button labels, section headers.</summary>
    public static SD.Font UiFontSemiBold(float size, SD.GraphicsUnit unit = SD.GraphicsUnit.Point)
        => AppFonts.Loaded
            ? new("Manrope SemiBold", size, SD.FontStyle.Regular, unit)
            : new("Segoe UI Semibold", size, SD.FontStyle.Regular, unit);

    /// <summary>JetBrains Mono — timers, dimensions, hotkeys, filenames, sizes, counts.</summary>
    public static SD.Font MonoFont(float size, bool semiBold = false, SD.GraphicsUnit unit = SD.GraphicsUnit.Point)
        => AppFonts.Loaded
            ? new(semiBold ? "JetBrains Mono SemiBold" : "JetBrains Mono", size, SD.FontStyle.Regular, unit)
            : new("Consolas", size, semiBold ? SD.FontStyle.Bold : SD.FontStyle.Regular, unit);

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
