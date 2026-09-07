using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The query half of a winshot:// URL. The docs used to say query strings were "accepted
/// and ignored"; these cover what they now do instead, and the cases where they must still
/// be ignored so an existing launcher link keeps working.
/// </summary>
public class CaptureCommandOptionsTests
{
    [Fact]
    public void AUrlWithNoQueryLeavesTheUsersSettingsInCharge()
    {
        Assert.Same(CaptureCommandOptions.None, CommandServer.ParseOptions("winshot://capture-area"));
        Assert.Same(CaptureCommandOptions.None, CommandServer.ParseOptions("winshot://capture-area?"));
        Assert.Same(CaptureCommandOptions.None, CommandServer.ParseOptions("capture-area"));
        Assert.Same(CaptureCommandOptions.None, CommandServer.ParseOptions(""));
    }

    [Fact]
    public void UnrecognisedParametersAreStillIgnored()
    {
        // The documented "still runs capture-area" behaviour of an existing launcher link.
        Assert.Same(CaptureCommandOptions.None, CommandServer.ParseOptions("winshot://capture-area?from=launcher#now"));
        Assert.Equal("capture-area", CommandServer.ParseCommand("winshot://capture-area?from=launcher#now"));
    }

    [Fact]
    public void ThenReplacesTheAfterCaptureSetForThisCaptureOnly()
    {
        CaptureCommandOptions options = CommandServer.ParseOptions("winshot://capture-area?then=copy,save");
        Assert.NotNull(options.Then);
        PostCaptureSet then = options.Then!.Value;

        Assert.True(then.Copy);
        Assert.True(then.Save);
        Assert.False(then.Annotate);
        Assert.False(then.Pin);
        Assert.False(then.Overlay);
        Assert.Equal(0, options.DelaySeconds);
    }

    [Theory]
    // Shottr's vocabulary on the left, WinShot's surfaces on the right.
    [InlineData("thumbnail", false, false, false, false, true)]
    [InlineData("overlay", false, false, false, false, true)]
    [InlineData("edit", false, false, true, false, false)]
    [InlineData("annotate", false, false, true, false, false)]
    [InlineData("pin", false, false, false, true, false)]
    [InlineData("copy,save,edit,pin,thumbnail", true, true, true, true, true)]
    // Separators people actually type, and casing.
    [InlineData("COPY+Save", true, true, false, false, false)]
    public void EveryDocumentedThenWordMapsToASurface(
        string list, bool copy, bool save, bool annotate, bool pin, bool overlay)
    {
        PostCaptureSet? set = PostCaptureSet.TryParseThen(list);
        Assert.NotNull(set);
        Assert.Equal(new PostCaptureSet(copy, save, annotate, pin, overlay), set!.Value);
    }

    [Theory]
    [InlineData("winshot://capture-area?then=")]
    [InlineData("winshot://capture-area?then=nonsense")]
    public void AThenListWithNothingUsableInItDoesNotOverrideAnything(string url)
    {
        // Silently capturing with every action off would look like the capture vanished.
        Assert.Null(CommandServer.ParseOptions(url).Then);
    }

    [Theory]
    [InlineData("winshot://capture-fullscreen?delayed=5", 5)]
    [InlineData("winshot://capture-fullscreen?delay=5", 5)]
    [InlineData("winshot://capture-fullscreen?delayed=60", 60)]
    // Refused rather than honoured: a countdown nobody can cancel is worse than no delay.
    [InlineData("winshot://capture-fullscreen?delayed=0", 0)]
    [InlineData("winshot://capture-fullscreen?delayed=-3", 0)]
    [InlineData("winshot://capture-fullscreen?delayed=soon", 0)]
    [InlineData("winshot://capture-fullscreen?delayed=99999", 60)]
    public void DelayIsSecondsAndIsClamped(string url, int expected)
    {
        Assert.Equal(expected, CommandServer.ParseOptions(url).DelaySeconds);
    }

    [Fact]
    public void BothParametersRideTogether()
    {
        CaptureCommandOptions options =
            CommandServer.ParseOptions("\"winshot://capture-window?delayed=5&then=pin\"");
        Assert.Equal(5, options.DelaySeconds);
        Assert.True(options.Then!.Value.Pin);
        Assert.Equal("capture-window", CommandServer.ParseCommand("\"winshot://capture-window?delayed=5&then=pin\""));
    }

    [Fact]
    public void AForwardedCommandKeepsItsQuery()
    {
        // A second process resolves the verb (so a relative Open-With path becomes absolute)
        // and hands it to the running instance — which must still see the ?then=.
        Assert.Equal(
            "capture-area?then=copy,save",
            CommandServer.WithQuery("capture-area", "winshot://capture-area?then=copy,save"));
        Assert.Equal("capture-area", CommandServer.WithQuery("capture-area", "winshot://capture-area"));
        Assert.Equal("capture-area", CommandServer.WithQuery("capture-area", null));

        // And the round trip the primary instance actually performs on the forwarded line.
        string forwarded = CommandServer.WithQuery("capture-area", "\"winshot://capture-area?then=pin\"");
        Assert.Equal("capture-area", CommandServer.ParseCommand(forwarded));
        Assert.True(CommandServer.ParseOptions(forwarded).Then!.Value.Pin);
    }
}
