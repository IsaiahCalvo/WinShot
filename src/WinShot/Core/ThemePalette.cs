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

    // Text.
    public static readonly SD.Color TextPrimary = SD.Color.FromArgb(0xF2, 0xF2, 0xF4);
    public static readonly SD.Color TextSecondary = SD.Color.FromArgb(0xB8, 0xB8, 0xBC);

    // White-alpha interaction fills / hairlines (GDI+ blends the alpha over dark).
    public static readonly SD.Color HoverFill = SD.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
    public static readonly SD.Color Border = SD.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
    public static readonly SD.Color BorderStrong = SD.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

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
