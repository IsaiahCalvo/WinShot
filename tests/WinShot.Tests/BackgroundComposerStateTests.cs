using WinShot.Editor.Background;
using Xunit;

namespace WinShot.Tests;

public class BackgroundComposerStateTests
{
    [Fact]
    public void RoundTripsEveryField()
    {
        var state = new BackgroundComposerState(
            Backdrop: "Purple Haze",
            Padding: 88,
            Inset: 12,
            AutoBalance: true,
            Shadow: false,
            ShadowBlur: 47,
            ShadowOpacity: 0.42,
            CornerRadius: 7,
            Anchor: "BR",
            Ratio: "16:9");

        Assert.Equal(state, BackgroundComposerState.Parse(state.Serialize()));
    }

    [Fact]
    public void EmptyAndGarbageFallBackToTheDefault()
    {
        Assert.Equal(BackgroundComposerState.Default, BackgroundComposerState.Parse(null));
        Assert.Equal(BackgroundComposerState.Default, BackgroundComposerState.Parse(""));
        Assert.Equal(BackgroundComposerState.Default, BackgroundComposerState.Parse("not a state"));
    }

    [Fact]
    public void AMalformedFieldFallsBackWithoutLosingTheRest()
    {
        var d = BackgroundComposerState.Default;
        var parsed = BackgroundComposerState.Parse("Emerald,zzz,4,1,1,20,0.5,9,TL,1:1");

        Assert.Equal("Emerald", parsed.Backdrop);
        Assert.Equal(d.Padding, parsed.Padding);   // the one bad field
        Assert.Equal(4, parsed.Inset);
        Assert.Equal(9, parsed.CornerRadius);
        Assert.Equal("TL", parsed.Anchor);
    }

    [Fact]
    public void OutOfRangeValuesAreClampedRatherThanTrusted()
    {
        var parsed = BackgroundComposerState.Parse("Azure,9999,9999,0,1,9999,9.9,9999,CC,auto");

        Assert.Equal(200, parsed.Padding);
        Assert.Equal(BackgroundLayout.MaxInset, parsed.Inset);
        Assert.Equal(100, parsed.ShadowBlur);
        Assert.Equal(0.8, parsed.ShadowOpacity);
        Assert.Equal(32, parsed.CornerRadius);
    }

    [Fact]
    public void PresetSetRoundTrips()
    {
        var presets = new List<(string, BackgroundComposerState)>
        {
            ("Talk slides", BackgroundComposerState.Default with { Padding = 120, Ratio = "16:9" }),
            ("Tight", BackgroundComposerState.Default with { Padding = 8, Shadow = false }),
        };

        var back = BackgroundComposerState.ParseAll(BackgroundComposerState.SerializeAll(presets));

        Assert.Equal(2, back.Count);
        Assert.Equal("Talk slides", back[0].Name);
        Assert.Equal(120, back[0].State.Padding);
        Assert.False(back[1].State.Shadow);
    }

    [Fact]
    public void APresetNameCarryingTheDelimitersSurvives()
    {
        // A derived name can contain a hex backdrop or a ratio, and the store's two
        // delimiters have to survive that rather than splitting the entry in half.
        var presets = new List<(string, BackgroundComposerState)>
        {
            ("a,b;c=d%", BackgroundComposerState.Default),
        };

        var back = BackgroundComposerState.ParseAll(BackgroundComposerState.SerializeAll(presets));

        Assert.Single(back);
        Assert.Equal("a,b;c=d%", back[0].Name);
    }

    [Fact]
    public void ResavingANameReplacesRatherThanDoubles()
    {
        string text = BackgroundComposerState.SerializeAll(new List<(string, BackgroundComposerState)>
        {
            ("Same", BackgroundComposerState.Default with { Padding = 10 }),
            ("Same", BackgroundComposerState.Default with { Padding = 90 }),
        });

        var back = BackgroundComposerState.ParseAll(text);

        Assert.Single(back);
        Assert.Equal(90, back[0].State.Padding);
    }

    [Fact]
    public void ParseAllSurvivesACorruptEntry()
    {
        var back = BackgroundComposerState.ParseAll("good=Azure,10,0,0,1,20,0.5,4,CC,auto;;=;rubbish");

        Assert.Single(back);
        Assert.Equal("good", back[0].Name);
    }
}
