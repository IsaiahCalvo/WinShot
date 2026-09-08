using WinShot.Core;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// What the keystroke pill says, where it sits, and how it grows. The chord vocabulary and the
/// command-only filter are the load-bearing parts: one of them decides whether a password
/// typed during a recording ends up on screen.
/// </summary>
public class KeystrokeDisplayTests
{
    private const int VkA = 0x41;
    private const int VkEnter = 0x0D;
    private const int VkLeft = 0x25;
    private const int VkF5 = 0x74;

    [Fact]
    public void Chords_AreOnePillWithNoPlusSigns()
    {
        (string text, bool appendable) = KeystrokeDisplay.Build(VkA, ctrl: true, shift: true, alt: false, win: false);

        Assert.DoesNotContain("+", text);
        Assert.Contains("Ctrl", text);
        Assert.Contains("Shift", text);
        Assert.EndsWith("A", text);
        Assert.False(appendable, "a chord is one gesture, not a letter in a word");
    }

    [Fact]
    public void Chords_KeepTheModifiersInAStableOrder()
    {
        (string text, _) = KeystrokeDisplay.Build(VkA, ctrl: true, shift: true, alt: true, win: true);

        int ctrl = text.IndexOf("Ctrl", StringComparison.Ordinal);
        int alt = text.IndexOf("Alt", StringComparison.Ordinal);
        int shift = text.IndexOf("Shift", StringComparison.Ordinal);
        int win = text.IndexOf("Win", StringComparison.Ordinal);
        Assert.True(ctrl < alt && alt < shift && shift < win, text);
    }

    [Fact]
    public void PlainTyping_AppendsIntoARun()
    {
        (string lower, bool appendable) = KeystrokeDisplay.Build(VkA, ctrl: false, shift: false, alt: false, win: false);
        Assert.Equal("a", lower);
        Assert.True(appendable);

        (string upper, bool upperAppendable) = KeystrokeDisplay.Build(VkA, ctrl: false, shift: true, alt: false, win: false);
        Assert.Equal("A", upper);
        Assert.True(upperAppendable);
    }

    [Fact]
    public void NamedKeys_RenderAsSymbolsWhereTheSymbolIsUnambiguous()
    {
        Assert.Equal("↵", KeystrokeDisplay.KeyName(VkEnter, upperCase: false));
        Assert.Equal("←", KeystrokeDisplay.KeyName(VkLeft, upperCase: false));
        Assert.Equal("⇥", KeystrokeDisplay.KeyName(0x09, upperCase: false));
        Assert.Equal("⎋", KeystrokeDisplay.KeyName(0x1B, upperCase: false));
        Assert.Equal("␣", KeystrokeDisplay.KeyName(0x20, upperCase: false));
        // And keep a word where a symbol would be a guess.
        Assert.Equal("F5", KeystrokeDisplay.KeyName(VkF5, upperCase: false));
        Assert.Equal("Home", KeystrokeDisplay.KeyName(0x24, upperCase: false));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void CommandChord_IsAnythingHeldWithCtrlAltOrWin(bool ctrl, bool alt, bool win, bool expected) =>
        Assert.Equal(expected, KeystrokeDisplay.IsCommandChord(ctrl, alt, win));

    /// <summary>Shift alone is typing, not a command — "Show only command keys" has to keep
    /// suppressing capital letters.</summary>
    [Fact]
    public void CommandChord_DoesNotCountShiftOnTheirOwn() =>
        Assert.False(KeystrokeDisplay.IsCommandChord(ctrl: false, alt: false, win: false));

    // ---- pill width glide ---------------------------------------------------------

    [Fact]
    public void Width_LandsImmediatelyOnFirstAppearance() =>
        Assert.Equal(200, KeystrokeDisplay.EaseWidth(current: 0, target: 200));

    [Fact]
    public void Width_MovesPartWayEachFrameAndAlwaysArrives()
    {
        int width = 120;
        int frames = 0;
        while (width != 300 && frames < 200)
        {
            int next = KeystrokeDisplay.EaseWidth(width, 300);
            Assert.True(next > width, $"the glide stalled at {width}");
            width = next;
            frames++;
        }

        Assert.Equal(300, width);
        Assert.InRange(frames, 2, 60); // a glide, not a snap and not a crawl
    }

    [Fact]
    public void Width_ShrinksTheSameWay()
    {
        int width = 300;
        for (int i = 0; i < 200 && width != 100; i++)
            width = KeystrokeDisplay.EaseWidth(width, 100);

        Assert.Equal(100, width);
    }

    // ---- placement ----------------------------------------------------------------

    private static readonly SD.Rectangle Region = new(100, 200, 800, 600);
    private static readonly SD.Size Pill = new(240, 52);

    [Fact]
    public void Anchor_DefaultsToBottomCentre()
    {
        SD.Point at = KeystrokeDisplay.Anchor(Region, Pill, "nonsense", inset: 14);

        Assert.Equal(Region.X + ((Region.Width - Pill.Width) / 2), at.X);
        Assert.Equal(Region.Bottom - Pill.Height - 14, at.Y);
    }

    [Theory]
    [InlineData("top-left")]
    [InlineData("top-center")]
    [InlineData("top-right")]
    [InlineData("bottom-left")]
    [InlineData("bottom-center")]
    [InlineData("bottom-right")]
    public void Anchor_KeepsThePillInsideTheRecordedRectangle(string position)
    {
        SD.Point at = KeystrokeDisplay.Anchor(Region, Pill, position, inset: 14);

        Assert.True(Region.Contains(new SD.Rectangle(at, Pill)), $"{position} put the pill at {at}");
    }

    [Fact]
    public void Anchor_ClampsWhenThePillIsWiderThanTheRegion()
    {
        var tiny = new SD.Rectangle(10, 10, 40, 30);

        SD.Point at = KeystrokeDisplay.Anchor(tiny, Pill, "bottom-right", inset: 14);

        Assert.Equal(tiny.Location, at);
    }

    [Fact]
    public void Position_NormalizesToTheSixOfferedSlots()
    {
        Assert.Equal("top-left", RecordingOptions.NormalizeKeystrokePosition("top-left"));
        Assert.Equal(RecordingOptions.KeystrokeDefaultPosition, RecordingOptions.NormalizeKeystrokePosition(null));
        Assert.Equal(RecordingOptions.KeystrokeDefaultPosition, RecordingOptions.NormalizeKeystrokePosition("fullscreen"));
        Assert.Equal(RecordingOptions.KeystrokeDefaultPosition, RecordingOptions.NormalizeKeystrokePosition("off"));
    }

    [Fact]
    public void Options_ReadTheirWholeShapeFromSettings()
    {
        var settings = new Settings
        {
            ShowKeystrokes = true,
            KeystrokesCommandOnly = true,
            KeystrokePosition = "top-right",
        };

        KeystrokeOverlayOptions options = KeystrokeOverlayOptions.FromSettings(settings);

        Assert.True(options.Render);
        Assert.True(options.CommandOnly);
        Assert.Equal("top-right", options.Position);
    }
}
