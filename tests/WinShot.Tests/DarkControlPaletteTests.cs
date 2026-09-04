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
