using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class PostCaptureActionTests
{
    [Theory]
    [InlineData("overlay")]
    [InlineData("copy")]
    [InlineData("save")]
    [InlineData("edit")]
    [InlineData("pin")]
    [InlineData("background")]
    public void Normalize_KeepsKnownActions(string value)
    {
        Assert.Equal(value, PostCaptureAction.Normalize(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void Normalize_FallsBackToOverlayForUnknownActions(string? value)
    {
        Assert.Equal(PostCaptureAction.Overlay, PostCaptureAction.Normalize(value));
    }

    [Theory]
    [InlineData("overlay", false)]
    [InlineData("copy", true)]
    [InlineData("save", true)]
    [InlineData("edit", true)]
    [InlineData("pin", true)]
    [InlineData("background", true)]
    public void OpensOverlay_ReturnsFalseOnlyForDirectActions(string value, bool direct)
    {
        Assert.Equal(direct, PostCaptureAction.IsDirectAction(value));
    }

    [Theory]
    [InlineData("copy", true)]
    [InlineData("save", true)]
    [InlineData("edit", false)]
    [InlineData("pin", false)]
    [InlineData("background", false)]
    [InlineData("overlay", false)]
    public void NeedsCallerThreadHistoryClone_IsLimitedToActionsThatTakeOwnership(string value, bool expected)
    {
        Assert.Equal(expected, PostCaptureAction.NeedsCallerThreadHistoryClone(value));
    }
}
