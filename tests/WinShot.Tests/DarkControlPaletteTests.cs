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

    private static void AssertInstant(Storyboard board, string where)
    {
        foreach (Timeline child in board.Children)
            Assert.True(
                child.Duration == new Duration(TimeSpan.Zero),
                $"{where} still animates over {child.Duration} with animations turned off");
    }

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
