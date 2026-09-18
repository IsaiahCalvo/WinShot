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

    // Interaction shades for a SATURATED fill, mirroring Theme.xaml's HoverShade/PressedShade.
    // Layer these over the fill; they are alpha, not colours of their own.
    public static readonly SD.Color HoverShade = SD.Color.FromArgb(0x1F, 0x00, 0x00, 0x00);
    public static readonly SD.Color PressedShade = SD.Color.FromArgb(0x2E, 0x00, 0x00, 0x00);

    /// <summary>
    /// The accent hovered, as one opaque colour for the WinForms fills that cannot layer
    /// (<c>FlatAppearance.MouseOverBackColor</c> takes a single colour). Lightening the accent
    /// instead — the old #409CFF — took the white label on top to 2.83:1, below even the 3:1
    /// floor for graphics, on the affirmative button of every dialog. Dipping keeps it at 4.58:1.
    /// </summary>
    public static readonly SD.Color AccentHovered = Over(HoverShade, Accent);

    /// <summary>The accent pressed — Theme.xaml's AccentPressedColor, so both halves press alike.</summary>
    public static readonly SD.Color AccentPressed = SD.Color.FromArgb(0x00, 0x60, 0xDF);

    /// <summary>
    /// The accent as a fill that carries WHITE TEXT — Theme.xaml's AccentDeepColor, which is
    /// why it exists there. White on <see cref="Accent"/> is 3.65:1, under the 4.5:1 text
    /// floor; on this it is 4.6:1. Same hue, so it still reads as the one accent — use
    /// <see cref="Accent"/> everywhere the accent is a graphic rather than a text ground.
    /// </summary>
    public static readonly SD.Color AccentDeep = SD.Color.FromArgb(0x0A, 0x73, 0xE0);

    /// <summary>The destructive red as a fill that carries white text; Theme.xaml's
    /// RedDeepColor. White on <see cref="Red"/> is 3.4:1, on this 5.0:1.</summary>
    public static readonly SD.Color RedDeep = SD.Color.FromArgb(0xD4, 0x2B, 0x21);

    /// <summary>The accent's gradient partner: the second stop of the editor's "Azure"
    /// background preset. Named here because the headless capture-path backdrop needs the
    /// same colour and cannot reach a WPF Brush for it — two hex literals for one colour
    /// is how a palette drifts.</summary>
    public static readonly SD.Color AccentGradientEnd = SD.Color.FromArgb(0x6D, 0xD5, 0xED);

    /// <summary>
    /// The keyboard focus RING on a dark field. Never a fill: nothing white may sit on top
    /// of it — use <see cref="AccentHovered"/> for a hovered accent surface. This is
    /// Theme.xaml's InfoColor, which is what the WPF half already strokes its focus rings
    /// with, so both halves ring a focused control the same colour. It is drawn opaque:
    /// the old #409CFF at 150 alpha composited over the field it rings to 2.37:1, under
    /// WCAG 1.4.11's 3:1 floor for a control boundary. Opaque, it is 5.21:1.
    /// </summary>
    public static readonly SD.Color AccentRing = SD.Color.FromArgb(0x6B, 0xB1, 0xFF);

    // Status colors, paired with the system-blue identity (macOS system red/orange).
    // Red doubles as the recording dot and destructive actions; Warn as paused/warning.
    public static readonly SD.Color Red = SD.Color.FromArgb(0xFF, 0x45, 0x3A);
    public static readonly SD.Color Warn = SD.Color.FromArgb(0xFF, 0x9F, 0x0A);

    /// <summary>
    /// Measurement crimson. Colour is the grammar of the measure overlay and it must not
    /// blur: crimson is a distance the app computed, Accent blue is a guide the user placed.
    /// Red is the recording dot and destructive actions, and is orange-leaning besides, so
    /// measurement gets its own value rather than borrowing one. WinForms-only, and so the
    /// one colour here with no Theme.xaml twin: no WPF surface shows a measurement.
    /// </summary>
    public static readonly SD.Color Measure = SD.Color.FromArgb(0xE0, 0x24, 0x4A);

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
    /// A word standing in for an icon — the All-In-One selector's "Aa" cell, sitting in the
    /// same glyph row as five icon-font glyphs. Two bold letters carry more ink than one
    /// icon, so this sits a step under <see cref="GlyphActionPt"/>; sizing it off a TEXT step
    /// instead shrank it to 12 pt and opened a visible gap beside its unchanged neighbours.
    /// </summary>
    public const float GlyphTextPt = 13f;       // 17.3 DIP

    /// <summary>
    /// A glyph inside a pill or a badge — Theme.xaml's GlyphBadge step. The selector's size
    /// pill cross-fades to a camera at this size; sized off <see cref="TextBodyPt"/> instead,
    /// the camera read as a text character rather than as the button the pill has become.
    /// </summary>
    public const float GlyphBadgePt = 10.5f;    // 14 DIP

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
    /// Off the scale deliberately: the recording card's duration and size sit in a 17 px
    /// strip inside the pinned card, and anything larger crowds the two ends together on
    /// the smallest card size — the same face the quick-access tooltip needs, so it IS that
    /// token rather than a second constant holding the same number. Two copies of 6.5 is
    /// exactly the drift one type scale exists to prevent: move one and the strip and the
    /// tooltip stop matching with nothing failing.
    /// </summary>
    public const float MediaStripLabelPt = PinnedTooltipPt;

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

    /// <summary>
    /// The same two fonts in PIXELS, for owner-drawn surfaces that measure with
    /// <c>TextRenderer</c> (GDI) and draw with <c>Graphics.DrawString</c> (GDI+). A
    /// GraphicsUnit.Point font resolves its size against the screen DC captured when the
    /// Font was constructed on the GDI side, and against the target Graphics' own DpiY on
    /// the GDI+ side; under PerMonitorV2 those are two different monitors, so the pill is
    /// measured at one display's text size and painted at another's. Pixels are unambiguous
    /// — the caller multiplies by its own monitor's scale.
    /// </summary>
    public static SD.Font UiFontPx(int sizePx, SD.FontStyle style = SD.FontStyle.Regular)
        => new("Segoe UI", Math.Max(1, sizePx), style, SD.GraphicsUnit.Pixel);

    /// <inheritdoc cref="UiFontPx"/>
    public static SD.Font IconFontPx(int sizePx, SD.FontStyle style = SD.FontStyle.Regular)
        => new(HasFluentIcons ? "Segoe Fluent Icons" : "Segoe MDL2 Assets",
            Math.Max(1, sizePx), style, SD.GraphicsUnit.Pixel);

    /// <summary>A point-unit type token as physical pixels on a monitor at
    /// <paramref name="scale"/> (72 pt to the inch, 96 px to the inch at 100%).</summary>
    public static int PxForPt(float sizePt, double scale) =>
        Math.Max(1, (int)Math.Round(sizePt * (96f / 72f) * (scale <= 0 ? 1d : scale)));

    /// <summary>
    /// A type token at the size a surface laid out for <paramref name="scale"/> needs — and the
    /// only font a hand-scaled WinForms surface may use.
    ///
    /// A <see cref="SD.GraphicsUnit.Point"/> font resolves its pixel size ONCE, against the
    /// device context it was constructed on, which is the primary monitor's. A surface that
    /// multiplies every box by its own target monitor's scale therefore grows the box and leaves
    /// the type exactly where it was: on a 150 % primary with a 100 % second display, the first-run
    /// bubble's boxes came out at 1x and its type at 1.5x, and every row title on the app's very
    /// first screen was sliced through the middle. On a 200 % display the opposite — type at two
    /// thirds of the size the layout was drawn for. The comment on several of these surfaces says
    /// "point-based fonts render at the monitor's DPI"; they do not, and
    /// <see cref="UiFontPx"/>'s own remarks have said so all along.
    ///
    /// One box scale and one type scale, from the same number.
    /// </summary>
    public static SD.Font ScaledUiFont(float sizePt, double scale, SD.FontStyle style = SD.FontStyle.Regular)
        => UiFontPx(PxForPt(sizePt, scale), style);

    /// <inheritdoc cref="ScaledUiFont"/>
    public static SD.Font ScaledIconFont(float sizePt, double scale, SD.FontStyle style = SD.FontStyle.Regular)
        => IconFontPx(PxForPt(sizePt, scale), style);

    /// <summary>
    /// The app's one monospaced face, for numeric readouts that must not jitter as their
    /// digits change — the recording timer, the editors' time and frame counts. Named here
    /// rather than typed out per surface so the choice is one decision.
    /// </summary>
    public const string MonoFamily = "Consolas";

    public static SD.Font MonoFontPx(int sizePx, SD.FontStyle style = SD.FontStyle.Regular)
        => new(MonoFamily, Math.Max(1, sizePx), style, SD.GraphicsUnit.Pixel);

    /// <summary>
    /// Composites a straight-alpha colour over an opaque one, the way GDI+ paints it.
    /// Public because <see cref="HoverShade"/> and <see cref="PressedShade"/> are alpha layers:
    /// a WinForms fill that cannot stack layers has to be handed the composited result.
    /// </summary>
    public static SD.Color Over(SD.Color top, SD.Color under)
    {
        double a = top.A / 255d;
        // The composite keeps the UNDER layer's alpha: a shade laid over a translucent fill
        // leaves it as translucent as it was. Forcing 255 here made StateFill silently
        // opaque a translucent saturated fill while leaving a translucent neutral one alone.
        return SD.Color.FromArgb(
            under.A,
            (int)Math.Round((top.R * a) + (under.R * (1 - a))),
            (int)Math.Round((top.G * a) + (under.G * (1 - a))),
            (int)Math.Round((top.B * a) + (under.B * (1 - a))));
    }

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
