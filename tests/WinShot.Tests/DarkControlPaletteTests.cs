using System.Threading;
using System.Windows;
using System.Windows.Media;
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
