using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class DarkControlPaletteTests
{
    /// <summary>
    /// DarkContextMenu and DarkMenuItem are KEYED, not implicit, so a WPF menu built in code
    /// and left unstyled falls back to Aero2: a white popup with black text and the system
    /// highlight, hanging inside a dark window. The "Copy as" format list shipped that way and
    /// nothing caught it, because every other code-built menu happened to apply both by hand.
    /// </summary>
    [Fact]
    public void EveryCodeBuiltMenu_CarriesTheKeyedDarkStyles()
    {
        string root = FindRepoDirectory("src", "WinShot");
        var unstyled = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("new ContextMenu", StringComparison.Ordinal) &&
                    !lines[i].Contains("new MenuItem", StringComparison.Ordinal))
                    continue;

                // The style is set in the object initializer, which is this line or the few
                // that close it.
                string block = string.Join(' ', lines.Skip(i).Take(8));
                int close = block.IndexOf("};", StringComparison.Ordinal);
                if (close >= 0)
                    block = block[..close];
                if (!block.Contains("Style", StringComparison.Ordinal))
                    unstyled.Add($"{Path.GetFileName(file)}:{i + 1} {lines[i].Trim()}");
            }
        }

        Assert.True(unstyled.Count == 0,
            "code-built menus with no Style, so they render in stock Aero2 chrome: " +
            string.Join("; ", unstyled));
    }

    private static string FindRepoDirectory(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException($"Could not find repository directory: {Path.Combine(parts)}");
    }

    // Regression: Accent's red channel is 0x0A; the pressed state darkens by 12,
    // which used to produce red = -2 and crash inside OnPaint — WinForms then
    // replaces the control with a permanent red-X placeholder (Isaiah's Start button).
    [Theory]
    [InlineData(-12)]
    [InlineData(-255)]
    [InlineData(18)]
    [InlineData(255)]
    public void Lighten_ClampsEveryChannelIntoRange(int amount)
    {
        foreach (SD.Color color in new[] { ThemePalette.Accent, SD.Color.Black, SD.Color.White })
        {
            SD.Color result = DarkControlPalette.Lighten(color, amount);
            Assert.InRange(result.R, 0, 255);
            Assert.InRange(result.G, 0, 255);
            Assert.InRange(result.B, 0, 255);
            Assert.Equal(color.A, result.A);
        }
    }

    /// <summary>
    /// Press does not stack on top of hover — the pointer is necessarily hovering
    /// while it presses, and a button that lightened and then darkened by the sum
    /// would read as a different colour than the one this app shipped for a year.
    /// </summary>
    [Theory]
    [InlineData(0d, 0d, 0d)]
    [InlineData(1d, 0d, 18d)]     // hovering: the old discrete +18
    [InlineData(1d, 1d, -12d)]    // pressed while hovering: the old discrete -12
    [InlineData(0.5d, 0d, 9d)]
    public void HoverPressLighten_LandsOnTheOldDiscreteShadesAtTheEnds(
        double hover, double press, double expected)
        => Assert.Equal(expected, DarkControlPalette.HoverPressLighten(hover, press), 6);

    [Fact]
    public void HoverPressLighten_ClampsAmountsFromAnOverrunTween()
    {
        Assert.Equal(18d, DarkControlPalette.HoverPressLighten(4d, 0d), 6);
        Assert.Equal(-12d, DarkControlPalette.HoverPressLighten(1d, 4d), 6);
    }

    [Fact]
    public void Mix_EndsOnTheEndpointsAndMeetsInTheMiddle()
    {
        SD.Color from = SD.Color.FromArgb(255, 0, 20, 40);
        SD.Color to = SD.Color.FromArgb(255, 100, 120, 140);

        Assert.Equal(from, DarkControlPalette.Mix(from, to, 0));
        Assert.Equal(to, DarkControlPalette.Mix(from, to, 1));
        Assert.Equal(SD.Color.FromArgb(255, 50, 70, 90), DarkControlPalette.Mix(from, to, 0.5));

        // A tween that overshoots or arrives late must never produce a channel out of range.
        Assert.Equal(from, DarkControlPalette.Mix(from, to, -3));
        Assert.Equal(to, DarkControlPalette.Mix(from, to, 3));
    }

    /// <summary>
    /// The segmented pill is one rect that slides, so the cells it slides between
    /// have to tile the track exactly: no gap and no overlap where a rounded cell
    /// edge meets the next one, at any width that does not divide evenly.
    /// </summary>
    [Theory]
    [InlineData(100, 3)]
    [InlineData(101, 3)]
    [InlineData(103, 4)]
    [InlineData(61, 2)]
    public void PillBounds_TileTheTrackWithoutSeamsOrOverlap(int width, int count)
    {
        var size = new SD.Size(width, 26);
        const int inset = 3;

        SD.Rectangle previous = SD.Rectangle.Empty;
        for (int i = 0; i < count; i++)
        {
            SD.Rectangle pill = DarkSegmented.PillBounds(size, count, i, inset);

            Assert.Equal(inset, pill.Y);
            Assert.Equal(size.Height - (inset * 2), pill.Height);
            Assert.True(pill.Width >= 1);
            Assert.True(pill.Right <= size.Width);

            if (i > 0)
            {
                // Neighbouring cells butt up: the gap between two pills is exactly
                // the two insets that separate them.
                Assert.Equal(inset * 2, pill.Left - previous.Right);
            }

            previous = pill;
        }
    }

    /// <summary>
    /// A track too narrow for its insets must still hand back a paintable rect —
    /// GdiPaths.RoundedRect on a zero or negative width throws inside OnPaint, and
    /// WinForms brands the control with a permanent red-X after one paint exception.
    /// </summary>
    [Fact]
    public void PillBounds_StaysPaintableOnATrackTooNarrowForItsInsets()
    {
        SD.Rectangle pill = DarkSegmented.PillBounds(new SD.Size(7, 4), 2, 0, 3);
        Assert.True(pill.Width >= 1);
        Assert.True(pill.Height >= 1);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(3, 3)]
    [InlineData(3, -1)]
    public void PillBounds_IsEmptyWhenThereIsNoSuchCell(int count, int index)
        => Assert.Equal(SD.Rectangle.Empty, DarkSegmented.PillBounds(new SD.Size(90, 26), count, index, 3));

    /// <summary>
    /// The active annotation tool must not be solid accent blue. It was, which put
    /// the app's one loud colour on whichever pen happened to be selected and left
    /// nothing louder for the states that are genuinely modal. Selected reads as a
    /// neutral chip now; the accent is reserved for ModeToolButton (crop).
    /// </summary>
    [Fact]
    public void ToolButton_ChecksIntoANeutralChip_NotTheAccent() => OnTheme(theme =>
    {
        Color accent = ((SolidColorBrush)theme["AccentBrush"]).Color;

        Assert.NotEqual(accent, CheckedChipColor(theme, "ToolButton"));
        Assert.Equal(((SolidColorBrush)theme["ToolChipBrush"]).Color, CheckedChipColor(theme, "ToolButton"));

        // The mode variant is the one place the accent is still the right answer.
        Assert.Equal(accent, CheckedChipColor(theme, "ModeToolButton"));
    });

    /// <summary>
    /// Theme.xaml sizes type in DIP and ThemePalette in points, because WPF and GDI+
    /// disagree about the unit. They are the same scale, so every step has to convert
    /// exactly (1 pt = 96/72 DIP) — this file already drifted once on colours, where
    /// a WinForms label quietly stopped matching the WPF one beside it.
    /// </summary>
    [Theory]
    [InlineData("TextMicro", ThemePalette.TextMicroPt)]
    [InlineData("TextCaption", ThemePalette.TextCaptionPt)]
    [InlineData("TextBody", ThemePalette.TextBodyPt)]
    [InlineData("TextBodyLg", ThemePalette.TextBodyLgPt)]
    [InlineData("TextTitle", ThemePalette.TextTitlePt)]
    [InlineData("TextDisplay", ThemePalette.TextDisplayPt)]
    [InlineData("GlyphAction", ThemePalette.GlyphActionPt)]
    public void TypeScale_MeasuresTheSameInBothUnits(string dipToken, float points)
        => OnTheme(theme => Assert.Equal((double)points * 96d / 72d, (double)theme[dipToken], 6));

    /// <summary>
    /// The steps have to stay distinct and ordered, or "one size up" stops meaning
    /// anything and the scale is just six names for the same number.
    /// </summary>
    [Fact]
    public void TypeScale_StepsRiseAndNeverRepeat() => OnTheme(theme =>
    {
        double[] steps = new[] { "TextMicro", "TextCaption", "TextBody", "TextBodyLg", "TextTitle", "TextDisplay" }
            .Select(k => (double)theme[k]).ToArray();

        Assert.Equal(steps.OrderBy(v => v), steps);
        Assert.Equal(steps.Length, steps.Distinct().Count());
    });

    /// <summary>
    /// Settings' control column is the label column plus one gutter. It used to be
    /// spelled 162 in four margins with nothing tying it to the 150 it derives from.
    /// </summary>
    [Fact]
    public void SettingsRowIndent_IsTheLabelColumnPlusOneGutter() => OnTheme(theme =>
        Assert.Equal(
            (double)theme["SettingsRowLabelWidth"] + (double)theme["Space3"],
            (double)theme["SettingsRowIndent"]));

    /// <summary>
    /// XAML cannot compose a Thickness out of a sys:Double, so the four margin tokens the
    /// UI actually consumes spell their values out and name the derivation in a comment.
    /// A comment does not fail, so a step could move and leave every margin behind. This
    /// is that comment, machine-checked.
    /// </summary>
    [Theory]
    [InlineData("GroupLabelMargin", 0, "Space2", 0, "Space1")]
    [InlineData("GroupSepMargin", 0, "Space2", 0, "Space2")]
    [InlineData("LabelGutterMargin", 0, 0, "Space3", 0)]
    [InlineData("ChipGutterMargin", 0, 0, "Space2", 0)]
    public void SpacingTokens_ComposeFromTheScale(string key, object left, object top, object right, object bottom)
        => OnTheme(theme =>
        {
            double Step(object step) => step is string name ? (double)theme[name] : Convert.ToDouble(step);
            var margin = (Thickness)theme[key];
            Assert.Equal(new Thickness(Step(left), Step(top), Step(right), Step(bottom)), margin);
        });

    /// <summary>
    /// A tool that IS selected has to read as more selected than a tool the pointer merely
    /// happens to be over. It did not: the neutral chip composited to 1.34:1 over the
    /// toolbar while the hover wash reached 1.46:1, so sweeping the pointer across the row
    /// lit every unselected tool brighter than the active one. ToolButton_ChecksIntoA-
    /// NeutralChip only pins that the chip is not the accent — it cannot see this.
    /// </summary>
    [Fact]
    public void ToolButton_CheckedChipOutranksAPlainHover() => OnTheme(theme =>
    {
        Color ring = ((SolidColorBrush)theme["ToolChipRingBrush"]).Color;
        Color window = ((SolidColorBrush)theme["WindowBgBrush"]).Color;
        Color hoverWash = ((SolidColorBrush)theme["HoverFillBrush"]).Color;

        // Every ground a ToolButton actually lands on, not just the toolbar. Measuring one
        // ground missed that PanToolBtn / CurvedArrowToolBtn / BlurToolBtn / EyedropperToolBtn
        // live in MoreToolsPopup, which is ElevatedBrush — where the ring read 2.61:1 and the
        // selected tool in that popup was, to the eye, not selected at all. Floating is
        // translucent, so it is measured composited over the canvas it floats above.
        foreach (string key in new[] { "ToolbarBgBrush", "ElevatedBrush", "WindowBgBrush", "FloatingBgBrush" })
        {
            Color raw = ((SolidColorBrush)theme[key]).Color;
            Color ground = raw.A == 255 ? raw : Over(raw, window);
            Color hovered = Over(hoverWash, ground);

            Assert.True(
                Luminance(ring) > Luminance(hovered),
                $"on {key} the checked ring {ring} is not brighter than a hovered sibling {hovered}");

            // WCAG 1.4.11: a non-text state indicator needs 3:1 against what it sits on.
            Assert.True(
                Contrast(ring, ground) >= 3d,
                $"on {key} the checked ring {ring} is only {Contrast(ring, ground):0.00}:1 over {ground}");
        }
    });

    /// <summary>
    /// Five ToolButtons render real text ("S"/"M"/"L", "1"/"A") at 13 DIP, so no large-text
    /// exemption applies: the glyph has to clear 4.5:1 on the chip — and on the chip with
    /// the hover and press washes stacked over it, which the template deliberately allows.
    /// The first attempt at "the chip must beat a hover" brightened the fill until the glyph
    /// on it read 4.38:1 at rest and 2.56:1 checked+hovered+pressed; nothing computed the
    /// foreground pair, so nothing caught it.
    /// </summary>
    [Fact]
    public void ToolButton_GlyphStaysReadableOnTheChipUnderEveryWash() => OnTheme(theme =>
    {
        Color text = ((SolidColorBrush)theme["TextPrimaryBrush"]).Color;
        Color chip = ((SolidColorBrush)theme["ToolChipBrush"]).Color;
        Color hover = ((SolidColorBrush)theme["HoverFillBrush"]).Color;
        Color press = ((SolidColorBrush)theme["PressedShadeBrush"]).Color;

        foreach ((string state, Color fill) in new[]
        {
            ("checked", chip),
            ("checked + hovered", Over(hover, chip)),
            ("checked + pressed", Over(press, chip)),
            ("checked + hovered + pressed", Over(press, Over(hover, chip))),
        })
        {
            Assert.True(
                Contrast(text, fill) >= 4.5d,
                $"a {state} ToolButton renders its label at {Contrast(text, fill):0.00}:1 on {fill}");
        }
    });

    /// <summary>
    /// The primary pill's label is white on the accent, and hovering used to make that pair
    /// WORSE — the wash was a brighter blue, which took white to 2.83:1, below even the 3:1
    /// floor for graphics, on the affirmative button of every dialog. Nothing computed the
    /// hovered pair, so nothing caught it. Hover on a saturated fill dips instead, so every
    /// interactive state has to beat the button sitting there doing nothing.
    ///
    /// The bar here is RELATIVE on purpose, and the exemption it grants has to be said out
    /// loud: white on the bare accent is 3.65:1, under the 4.5:1 a text pair owes. That is
    /// the accent identity itself (#0A84FF is fixed by the hard constraints), not something
    /// this button chose, and raising the rest fill to clear 4.5:1 would mean a second blue.
    /// So rest is held to the 3:1 graphics floor and every state has to beat it, which is
    /// what stops the ramp drifting further down. Closing the gap for good means changing
    /// the accent or the label, which is a whole-app decision and no track's to take alone.
    ///
    /// The cheapest version of that decision, for whoever takes it: AccentPressedBrush
    /// (#0060DF) is already in the palette and carries white at 5.28:1. Making it the primary
    /// button's REST fill and re-deriving the hover/press ramp from there clears 4.5:1 without
    /// inventing a second blue and without touching the accent identity.
    /// </summary>
    [Fact]
    public void PillButtonPrimary_EveryStateReadsBetterThanAtRest() => OnTheme(theme =>
    {
        Color white = Colors.White;
        Color accent = ((SolidColorBrush)theme["AccentBrush"]).Color;
        Color hover = ((SolidColorBrush)theme["HoverShadeBrush"]).Color;
        Color press = ((SolidColorBrush)theme["PressedShadeBrush"]).Color;
        double rest = Contrast(white, accent);
        Assert.True(rest >= 3d, $"White on the bare accent is {rest:0.00}:1, under the 3:1 graphics floor");

        foreach ((string state, Color fill) in new[]
        {
            ("hovered", Over(hover, accent)),
            ("pressed", Over(press, accent)),
            ("hovered + pressed", Over(press, Over(hover, accent))),
        })
        {
            double now = Contrast(white, fill);
            Assert.True(
                now >= rest && now >= 3d,
                $"a {state} PillButtonPrimary renders White at {now:0.00}:1, against {rest:0.00}:1 at rest");
        }
    });

    /// <summary>
    /// Text brushes have to clear 4.5:1 on every ground the app puts them on. The failure
    /// line on a dialog is the case that broke: AnnotationRedBrush is a stroke/fill colour
    /// that clears the bar on WindowBg (4.99:1) and misses it on the ToolbarBg a dialog card
    /// is painted with (4.43:1) - so the one message the user has to read was the one below
    /// the bar. TextErrorBrush exists to be red that is type rather than fill.
    /// </summary>
    [Fact]
    public void Theme_TextBrushesClearTheBarOnEveryGroundTheySitOn() => OnTheme(theme =>
    {
        Color window = ((SolidColorBrush)theme["WindowBgBrush"]).Color;

        foreach (string textKey in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "InfoBrush", "TextErrorBrush" })
        {
            Color text = ((SolidColorBrush)theme[textKey]).Color;
            foreach (string groundKey in new[] { "WindowBgBrush", "ToolbarBgBrush", "SurfaceBrush", "ElevatedBrush" })
            {
                Color raw = ((SolidColorBrush)theme[groundKey]).Color;
                Color ground = raw.A == 255 ? raw : Over(raw, window);
                Color over = text.A == 255 ? text : Over(text, ground);

                Assert.True(
                    Contrast(over, ground) >= 4.5d,
                    $"{textKey} on {groundKey} is {Contrast(over, ground):0.00}:1");
            }
        }
    });

    /// <summary>
    /// TextDisabledBrush is the one text brush the test above deliberately leaves out, and
    /// leaving it out silently would let the suite certify the theme as clear while three
    /// surfaces read ENABLED copy through it, below the bar:
    ///
    ///   SettingsWindow.xaml   Hint / SubHint, and the retention tick labels
    ///   HistoryWindow.xaml    "Screenshots and recordings you take will show up here."
    ///
    /// 4.5:1 is the right bar for those and they miss it (3.80:1 on WindowBg), but the fix is
    /// a hint colour, which is T7's "hint contrast >= 4.5:1" row and a visible change to every
    /// settings pane — not something to slip into a foundations sweep. What this pins instead
    /// is the floor the brush owes AS a disabled brush, so the gap cannot widen unnoticed
    /// while T7 gets to it, and the exemption is written down where the next reviewer reads it
    /// rather than inferred from an absence.
    /// </summary>
    [Fact]
    public void Theme_DisabledTextIsStillLegible_AndItsEnabledMisusesAreT7s() => OnTheme(theme =>
    {
        Color window = ((SolidColorBrush)theme["WindowBgBrush"]).Color;
        Color text = ((SolidColorBrush)theme["TextDisabledBrush"]).Color;

        foreach (string groundKey in new[] { "WindowBgBrush", "ToolbarBgBrush", "SurfaceBrush", "ElevatedBrush" })
        {
            Color raw = ((SolidColorBrush)theme[groundKey]).Color;
            Color ground = raw.A == 255 ? raw : Over(raw, window);
            Color over = text.A == 255 ? text : Over(text, ground);

            Assert.True(
                Contrast(over, ground) >= 3d,
                $"TextDisabledBrush on {groundKey} is {Contrast(over, ground):0.00}:1");
        }
    });

    /// <summary>
    /// Every FontSize the theme sets is a step on the type scale or the glyph scale.
    /// The first migration only rewrote the attribute form and left ten Setters behind,
    /// so the file that DEFINES the scale was the biggest place still inventing numbers.
    /// </summary>
    [Fact]
    public void Theme_SizesTypeOnlyFromTheScales() => OnTheme(theme =>
    {
        double[] scale = new[]
        {
            "TextMicro", "TextCaption", "TextBody", "TextBodyLg", "TextTitle", "TextDisplay",
            "GlyphMicro", "GlyphBadge", "GlyphAction", "GlyphIllustration", "GlyphHero",
            "ToolGlyphSize", "CircleGlyphSize",
        }.Select(k => (double)theme[k]).ToArray();

        foreach (object key in theme.Keys)
        {
            if (theme[key] is not Style style) continue;
            foreach (SetterBase setterBase in style.Setters)
            {
                if (setterBase is not Setter { Property.Name: "FontSize", Value: double size }) continue;
                Assert.True(
                    scale.Contains(size),
                    $"Style '{key}' sizes type at {size}, which is not a step on either scale.");
            }
        }
    });

    /// <summary>
    /// "Animation effects: off" has to mean the same thing on both stacks. Motion.Run
    /// already applies the end state inline; the WPF templates' cross-fades did not, so
    /// a user with animation off got a fading pill button beside a snapping WinForms one.
    ///
    /// The assertion that matters is the SECOND one: the first version of this test only
    /// re-read the dictionary entries the method had just overwritten, so it passed while
    /// every control template went on running the 140 ms original it had captured at parse
    /// time. A storyboard is only off if the BeginStoryboard holding it says so.
    /// </summary>
    [Fact]
    public void Theme_StoryboardsAreInstantWhenTheUserTurnedAnimationOff()
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            OnTheme(theme =>
            {
                ThemeResources.ApplyAnimationPreference(theme);

                var boards = theme.Keys.Cast<object>()
                    .Select(k => theme[k])
                    .OfType<Storyboard>()
                    .ToArray();

                Assert.NotEmpty(boards);
                foreach (Storyboard board in boards)
                    AssertInstant(board, "dictionary entry");

                int templateBoards = 0;
                foreach (object key in theme.Keys)
                {
                    foreach (TriggerBase trigger in TriggersOf(theme[key]))
                    {
                        foreach (Storyboard board in StoryboardsOf(trigger))
                        {
                            templateBoards++;
                            AssertInstant(board, $"'{key}'");
                        }
                    }
                }

                // ToolButton alone carries six (hover/press/chip, in and out).
                Assert.True(templateBoards >= 6, $"only found {templateBoards} storyboards inside templates");
            });
        }
        finally
        {
            Motion.AnimationsEnabled = previous;
        }
    }

    /// <summary>
    /// Why App.OnStartup has to call EnsureLoaded before the first window exists. WPF seals
    /// a ControlTemplate's trigger actions the first time the style is applied, and the
    /// storyboards come out of the dictionary frozen — so a sealed BeginStoryboard can
    /// neither be repointed nor edited in place, and the preference is lost for the rest of
    /// the process. This pins that limit: the fix is ordering, not cleverness, and if WPF
    /// ever stops sealing this test says so.
    /// </summary>
    [Fact]
    public void Theme_ASealedTemplateIsBeyondReach_WhichIsWhyEnsureLoadedRunsAtStartup()
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            OnTheme(theme =>
            {
                var style = (Style)theme["ToolButton"];
                var template = (ControlTemplate)style.Setters.OfType<Setter>()
                    .Single(x => x.Property == Control.TemplateProperty).Value;

                // What applying the style to a live control does to the template.
                template.Seal();

                ThemeResources.ApplyAnimationPreference(theme);

                Storyboard[] boards = template.Triggers
                    .SelectMany(StoryboardsOf)
                    .ToArray();

                Assert.NotEmpty(boards);
                Assert.All(boards, board => Assert.All(
                    board.Children,
                    child => Assert.NotEqual(new Duration(TimeSpan.Zero), child.Duration)));
            });
        }
        finally
        {
            Motion.AnimationsEnabled = previous;
        }
    }

    /// <summary>
    /// The third place animation-off has to reach. The two passes above only walk the
    /// dictionary's own keys, so a storyboard declared inline in a WINDOW's XAML —
    /// HistoryWindow's overlay-action fades are the ones that exist — is invisible to them
    /// and kept animating with the setting off. Zeroing the shared Duration tokens covers
    /// them, because a window is parsed long after App.OnStartup runs the preference, so its
    /// {StaticResource MotionQuick} resolves against the zeroed value. That only holds while
    /// window XAML spends the tokens instead of writing its own literal.
    /// </summary>
    [Fact]
    public void Theme_MotionDurationTokensAreInstantWhenTheUserTurnedAnimationOff()
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            OnTheme(theme =>
            {
                ThemeResources.ApplyAnimationPreference(theme);

                foreach (string token in new[] { "MotionQuick", "MotionSwap", "MotionEnter" })
                    Assert.True(
                        (Duration)theme[token] == new Duration(TimeSpan.Zero),
                        $"{token} still reads {theme[token]} with animations turned off, so any " +
                        "window-local storyboard spending it goes on animating");
            });
        }
        finally
        {
            Motion.AnimationsEnabled = previous;
        }
    }

    /// <summary>
    /// Layer order is per-template, not a house rule, and the two directions look identical
    /// in the XAML. ToolButton's chip is dark (#3A3A40), so the hover wash belongs on top —
    /// it only adds contrast. SizePresetRow's chip is the accent, and a 12 % white wash over
    /// it drops the white label from 3.65:1 to 3.13:1, so there the accent has to paint last.
    /// Pinned because "make these consistent" is the obvious wrong tidy-up.
    ///
    /// ColorSwatch is here for an accessibility reason rather than a contrast one: its focus
    /// ring has to sit above the checked ring. Those swatches are one RadioButton group, so an
    /// arrow key moves focus and the selection in the same keystroke — checked+focused is the
    /// ordinary keyboard state, not an edge one — and as a thickness on the base ring the
    /// focus colour was painted over by the 2px white check stacked above it.
    /// </summary>
    [Theory]
    [InlineData("ToolButton", "Chip", "Hover")]
    [InlineData("SizePresetRow", "Hover", "Check")]
    [InlineData("ColorSwatch", "Check", "Focus")]
    public void Theme_HoverAndCheckStackInTheOrderTheirGroundNeeds(
        string styleKey, string first, string second)
    {
        OnTheme(theme =>
        {
            string[] names = LayerNames(theme, styleKey);
            int firstAt = Array.IndexOf(names, first);
            int secondAt = Array.IndexOf(names, second);

            Assert.True(firstAt >= 0, $"{styleKey} has no '{first}' layer; found {string.Join(", ", names)}");
            Assert.True(secondAt >= 0, $"{styleKey} has no '{second}' layer; found {string.Join(", ", names)}");
            Assert.True(
                firstAt < secondAt,
                $"{styleKey} paints '{second}' under '{first}'; the later one wins the pixels");
        });
    }

    /// <summary>The named children of a style's template root, in paint order.</summary>
    private static string[] LayerNames(ResourceDictionary theme, string styleKey)
    {
        var template = (ControlTemplate)((Style)theme[styleKey]).Setters.OfType<Setter>()
            .Single(x => x.Property == Control.TemplateProperty).Value;

        // Not template.VisualTree: that is only populated for templates built in code. One
        // parsed from XAML keeps its content as a deferred stream and hands it over only when
        // it is instantiated, so the order has to be read off a real instance.
        var root = template.LoadContent() as Panel
            ?? throw new Xunit.Sdk.XunitException($"{styleKey}'s template root is not a Panel.");

        return root.Children.OfType<FrameworkElement>()
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .Select(x => x.Name)
            .ToArray();
    }

    private static void AssertInstant(Storyboard board, string where)
    {
        foreach (Timeline child in board.Children)
            Assert.True(
                child.Duration == new Duration(TimeSpan.Zero),
                $"{where} still animates over {child.Duration} with animations turned off");
    }

    /// <summary>
    /// The wave converted the templates it could name and left five snapping, which put a
    /// 140 ms cross-fade and an instant swap inside one control cluster — the annotation size
    /// field's chevron and the colour swatches sit in the same editor bar as the ToolButtons
    /// that ease. Mixing the two reads worse than the uniform snap it replaced, so the rule is
    /// checked rather than the list: a state that repaints has to transition into it.
    ///
    /// Scoped to what actually repaints. A state trigger that only swaps Foreground or flips a
    /// glyph's Visibility is exempt — a check mark appearing is a discrete thing, not a fade,
    /// and ModeToolButton's white glyph is keyed to the accent chip that is itself fading
    /// under it. So is IsKeyboardFocused: a focus ring that waits 140 ms to confirm the key
    /// press is worse, not better, and every focus ring in the theme is deliberately a setter.
    /// IsHighlighted is left out for the same reason — a menu highlight tracks the pointer
    /// down a list, and a fade on each row would smear behind it. Two templates rely on that
    /// carve-out and are named here so the next reviewer does not re-litigate them from
    /// scratch: DarkMenuItem and DarkComboBoxItem both repaint their ground to the accent on
    /// IsHighlighted with a bare setter, and both are pointer-tracked lists.
    ///
    /// The scan is the theme dictionary only. Ten window-local styles in src/WinShot/*.xaml
    /// still snap (HistoryWindow, SettingsWindow's MatrixCheck, RecordingRecoveryWindow);
    /// they belong to T4 and T7, and green here is not app-wide coverage.
    /// </summary>
    [Fact]
    public void Theme_NoControlStateRepaintsWithoutATransition() => OnTheme(theme =>
    {
        string[] states = { "IsMouseOver", "IsPressed", "IsChecked" };
        string[] paint = { "Background", "BorderBrush", "Stroke", "Fill", "StrokeThickness", "Opacity" };
        int visited = 0;

        foreach (object key in theme.Keys.Cast<object>().ToList())
        {
            foreach (TriggerBase triggerBase in TriggersOf(theme[key]))
            {
                // MultiTrigger and DataTrigger count too. Theme.xaml has no repainting one
                // today, which is exactly why they were easy to leave out — and exactly how a
                // future multi-condition hover state would have slipped through green.
                (string state, SetterBaseCollection setters) = triggerBase switch
                {
                    Trigger t when states.Contains(t.Property.Name) && Equals(t.Value, true)
                        => (t.Property.Name, t.Setters),
                    MultiTrigger mt when mt.Conditions.Any(c => states.Contains(c.Property?.Name) && Equals(c.Value, true))
                        => (string.Join(" + ", mt.Conditions.Select(c => c.Property?.Name)), mt.Setters),
                    DataTrigger dt => ("a data state", dt.Setters),
                    MultiDataTrigger mdt => ("a data state", mdt.Setters),
                    _ => (string.Empty, null!),
                };

                if (setters is null) continue;
                visited++;

                string[] repainted = setters
                    .OfType<Setter>()
                    .Select(setter => setter.Property.Name)
                    .Where(paint.Contains)
                    .Distinct()
                    .ToArray();

                if (repainted.Length == 0) continue;

                // Not merely "carries a storyboard": a trigger that fades an unrelated layer
                // while setting Background outright still snaps its Background, which is the
                // half-converted template this rule exists to catch.
                string[] animated = StoryboardsOf(triggerBase)
                    .SelectMany(board => board.Children)
                    .Select(child => Storyboard.GetTargetProperty(child)?.Path)
                    .Where(path => !string.IsNullOrEmpty(path))
                    .Select(path => path!)
                    .ToArray();

                foreach (string property in repainted)
                    Assert.True(
                        animated.Any(path => path.Contains(property, StringComparison.Ordinal)),
                        $"{key}'s {state} state sets {property} with a bare setter and nothing " +
                        "animates it, so it snaps beside templates that cross-fade");
            }
        }

        // A rule that walks nothing passes forever. If a Uri change or a template move ever
        // empties this scan, the suite has to say so instead of going quietly green.
        Assert.True(visited >= 20, $"the rule only reached {visited} state triggers");
    });

    /// <summary>Every trigger a theme value can carry: its own, and its template's.</summary>
    private static IEnumerable<TriggerBase> TriggersOf(object? value)
    {
        switch (value)
        {
            case Style style:
                foreach (TriggerBase t in style.Triggers) yield return t;
                foreach (SetterBase setterBase in style.Setters)
                    if (setterBase is Setter { Value: ControlTemplate styleTemplate })
                        foreach (TriggerBase t in styleTemplate.Triggers) yield return t;
                break;
            case ControlTemplate template:
                foreach (TriggerBase t in template.Triggers) yield return t;
                break;
        }
    }

    private static IEnumerable<Storyboard> StoryboardsOf(TriggerBase trigger)
    {
        IEnumerable<TriggerAction> actions = trigger switch
        {
            Trigger t => t.EnterActions.Cast<TriggerAction>().Concat(t.ExitActions.Cast<TriggerAction>()),
            MultiTrigger mt => mt.EnterActions.Cast<TriggerAction>().Concat(mt.ExitActions.Cast<TriggerAction>()),
            DataTrigger dt => dt.EnterActions.Cast<TriggerAction>().Concat(dt.ExitActions.Cast<TriggerAction>()),
            EventTrigger et => et.Actions.Cast<TriggerAction>(),
            _ => Array.Empty<TriggerAction>(),
        };

        foreach (TriggerAction action in actions)
            if (action is BeginStoryboard { Storyboard: { } board })
                yield return board;
    }

    /// <summary>
    /// Theme.xaml's Durations and Core/Motion.cs's constants are two copies of one motion
    /// vocabulary. Colour drifted exactly this way once already (ThemePalette against the
    /// dictionary it claims to mirror); this pins the timings before they can.
    /// </summary>
    /// <summary>
    /// The animation-off tests build a standalone dictionary and call ApplyAnimationPreference
    /// directly, so nothing exercised the step before it: EnsureLoaded has to FIND the
    /// dictionary App.xaml already merged, by comparing Source.OriginalString against
    /// ThemePath. If that comparison stops matching — a Uri normalisation, a path move, a
    /// deeper merge — EnsureLoaded adds a second copy, zeroes that copy, and every window
    /// keeps resolving against the original: animation-off silently stops working while
    /// every existing test stays green.
    /// </summary>
    [Fact]
    public void Theme_EnsureLoadedFindsTheMergedDictionaryInsteadOfAddingASecond() => OnTheme(_ =>
    {
        // Shaped exactly like App.xaml: Application.Resources is a ResourceDictionary whose
        // MergedDictionaries holds Theme.xaml, so the entry is already one level down.
        var wrapper = new ResourceDictionary();
        wrapper.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(ThemeResources.ThemePath, UriKind.Absolute),
        });
        var appResources = new ResourceDictionary();
        appResources.MergedDictionaries.Add(wrapper);

        ResourceDictionary? found = ThemeResources.Find(appResources);

        Assert.NotNull(found);
        Assert.Equal(ThemeResources.ThemePath, found!.Source?.OriginalString);
        // And it does not invent one that was never merged.
        Assert.Null(ThemeResources.Find(new ResourceDictionary()));
    });

    /// <summary>The WinForms half of the same token: a toast's failure line and a WPF
    /// dialog's failure line have to be the same red, or the two stacks drift apart.</summary>
    [Fact]
    public void Theme_TextErrorMatchesOnBothStacks() => OnTheme(theme =>
    {
        Color wpf = ((SolidColorBrush)theme["TextErrorBrush"]).Color;
        SD.Color forms = ThemePalette.TextError;

        Assert.Equal((wpf.R, wpf.G, wpf.B), (forms.R, forms.G, forms.B));
    });

    [Fact]
    public void Theme_MotionDurationsMatchTheMotionConstants() => OnTheme(theme =>
    {
        Assert.Equal(TimeSpan.FromMilliseconds(Motion.QuickMs), ((Duration)theme["MotionQuick"]).TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(Motion.SwapMs), ((Duration)theme["MotionSwap"]).TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(Motion.EnterDurationMs), ((Duration)theme["MotionEnter"]).TimeSpan);
    });

    /// <summary>
    /// The WinForms half of the palette hovers the accent the same way the WPF half does.
    /// It did not: ThemePalette lightened the accent to #409CFF, which took the white label on
    /// "Start Capture", "Done" and the region selector's confirm button to 2.83:1 — below even
    /// the 3:1 floor for graphics — while the same button in a WPF dialog dipped to 4.58:1.
    /// Two halves of "one palette" disagreeing is exactly what this file exists to catch, so
    /// the WinForms fills are asserted against the theme's own composite, not against a number.
    /// </summary>
    [Fact]
    public void ThemePalette_HoversAndPressesTheAccentTheSameWayTheThemeDoes() => OnTheme(theme =>
    {
        static Color Wpf(SD.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        Color accent = ((SolidColorBrush)theme["AccentBrush"]).Color;
        Color hoverShade = ((SolidColorBrush)theme["HoverShadeBrush"]).Color;
        Color pressShade = ((SolidColorBrush)theme["PressedShadeBrush"]).Color;

        Assert.Equal(accent, Wpf(ThemePalette.Accent));
        Assert.Equal(hoverShade, Wpf(ThemePalette.HoverShade));
        Assert.Equal(pressShade, Wpf(ThemePalette.PressedShade));
        Assert.Equal(Over(hoverShade, accent), Wpf(ThemePalette.AccentHovered));
        Assert.Equal(((SolidColorBrush)theme["AccentPressedBrush"]).Color, Wpf(ThemePalette.AccentPressed));
        // The focus ring is part of that parity and was the half nobody checked: WinForms
        // strokes ThemePalette.AccentRing, WPF strokes InfoBrush, and while the two disagreed
        // the WinForms ring sat at 2.37:1 against the field it rings — under WCAG 1.4.11's 3:1.
        Assert.Equal(((SolidColorBrush)theme["InfoBrush"]).Color, Wpf(ThemePalette.AccentRing));
        double ring = Contrast(Wpf(ThemePalette.AccentRing), Wpf(DarkControlPalette.Field));
        Assert.True(ring >= 3d, $"the focus ring renders at {ring:0.00}:1 against the field it rings");

        double rest = Contrast(Colors.White, accent);
        foreach ((string state, SD.Color fill) in new[]
        {
            ("hovered", ThemePalette.AccentHovered),
            ("pressed", ThemePalette.AccentPressed),
        })
        {
            double now = Contrast(Colors.White, Wpf(fill));
            Assert.True(
                now >= rest && now >= 3d,
                $"a {state} WinForms accent fill renders White at {now:0.00}:1, against {rest:0.00}:1 at rest");
        }
    });

    /// <summary>
    /// And the fill a button actually PAINTS obeys it. The tokens above were right while
    /// DarkButton went on lightening its fill by +18 a channel regardless, so every accent
    /// button in the app — the toast's "Open link", the recorder's "Start", Resize, the display
    /// picker — still washed its label out on hover: the state the pointer is in at the moment
    /// the user is reading the button. Asserted through StateFill, which is what OnPaint calls.
    /// </summary>
    [Theory]
    [InlineData(0.5d, 0d)]
    [InlineData(1d, 0d)]
    [InlineData(1d, 0.5d)]
    [InlineData(1d, 1d)]
    public void StateFill_NeverMakesTheAccentLabelHarderToRead(double hover, double press)
    {
        static Color Wpf(SD.Color c) => Color.FromArgb(255, c.R, c.G, c.B);

        // The label the accent buttons actually paint. DarkButton picks white for a saturated
        // fill now, which is what ScrollControlsBar and the region selector's Done always
        // passed; measuring TextPrimary measured a colour no accent surface puts on screen.
        SD.Color label = SD.Color.White;
        double rest = Contrast(Wpf(label), Wpf(ThemePalette.Accent));
        SD.Color fill = DarkControlPalette.StateFill(ThemePalette.Accent, hover, press);
        double now = Contrast(Wpf(label), Wpf(fill));

        Assert.True(
            now >= rest,
            $"an accent fill at hover {hover}/press {press} renders its label at {now:0.00}:1, " +
            $"against {rest:0.00}:1 at rest — hovering must not make the label harder to read");
    }

    /// <summary>
    /// The ends of the saturated ramp are the tokens themselves, so a DarkButton and a
    /// FlatAppearance button beside it paint the one accent the same hovered and pressed.
    /// </summary>
    [Fact]
    public void StateFill_LandsOnTheAccentTokensAtTheEnds()
    {
        Assert.Equal(ThemePalette.Accent, DarkControlPalette.StateFill(ThemePalette.Accent, 0d, 0d));
        Assert.Equal(ThemePalette.AccentHovered, DarkControlPalette.StateFill(ThemePalette.Accent, 1d, 0d));
        Assert.Equal(ThemePalette.AccentPressed, DarkControlPalette.StateFill(ThemePalette.Accent, 1d, 1d));
    }

    /// <summary>
    /// A dark NEUTRAL fill still lifts — it has nowhere else to go on this ground — and still
    /// lands on the discrete shades the app shipped before any of this eased.
    /// </summary>
    [Fact]
    public void StateFill_LeavesTheNeutralButtonsLifting()
    {
        SD.Color rest = DarkControlPalette.Field;
        Assert.Equal(rest, DarkControlPalette.StateFill(rest, 0d, 0d));
        Assert.Equal(DarkControlPalette.Lighten(rest, 18), DarkControlPalette.StateFill(rest, 1d, 0d));
        Assert.Equal(DarkControlPalette.Lighten(rest, -12), DarkControlPalette.StateFill(rest, 1d, 1d));
    }

    /// <summary>0..1 relative luminance, WCAG 2.x.</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255d;
            return s <= 0.04045d ? s / 12.92d : Math.Pow((s + 0.055d) / 1.055d, 2.4d);
        }

        return (0.2126d * Channel(c.R)) + (0.7152d * Channel(c.G)) + (0.0722d * Channel(c.B));
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05d) / (Math.Min(la, lb) + 0.05d);
    }

    /// <summary>Source-over composite of a translucent wash on an opaque ground.</summary>
    private static Color Over(Color wash, Color ground)
    {
        double a = wash.A / 255d;
        static byte Mix(byte top, byte bottom, double alpha)
            => (byte)Math.Round((top * alpha) + (bottom * (1d - alpha)));
        return Color.FromRgb(Mix(wash.R, ground.R, a), Mix(wash.G, ground.G, a), Mix(wash.B, ground.B, a));
    }

    /// <summary>
    /// The chip paints from the style's Background, which is what lets ModeToolButton
    /// be one Setter instead of a second copy of the whole template.
    /// </summary>
    private static Color CheckedChipColor(ResourceDictionary theme, string styleKey)
    {
        for (Style? style = (Style)theme[styleKey]; style is not null; style = style.BasedOn)
        {
            foreach (SetterBase setterBase in style.Setters)
            {
                if (setterBase is Setter { Property.Name: "Background", Value: SolidColorBrush brush })
                    return brush.Color;
            }
        }

        throw new Xunit.Sdk.XunitException($"{styleKey} sets no Background, so it paints no chip.");
    }

    /// <summary>
    /// Loads Theme.xaml and runs the assertions on its own STA thread — WPF brushes
    /// have thread affinity, so they have to be read where they were made. It
    /// deliberately does NOT create an Application: a second System.Windows.Application
    /// in this assembly breaks ThemedWindowTests for every test in it.
    /// </summary>
    private static void OnTheme(Action<ResourceDictionary> assertions)
    {
        // Touching PackUriHelper registers the "pack" URI scheme. WPF does that when an
        // Application is created; this assembly deliberately never creates one, so whether
        // "pack://application:,,,/..." parses at all came down to whether some other test
        // class happened to run first — renaming a file was enough to break it with
        // "Invalid URI: Invalid port specified".
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = Application.ResourceAssembly;

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                assertions(new ResourceDictionary
                {
                    Source = new Uri(ThemeResources.ThemePath, UriKind.Absolute),
                });
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw failure;
    }
}
