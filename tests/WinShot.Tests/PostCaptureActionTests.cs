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

    // Every direct action transfers the bitmap to a new owner that uses it at once
    // (Copy/Save dispose it, Pin/Background/Edit paint it), so all of them need the
    // caller-thread clone; only the overlay path keeps ownership and can clone lazily.
    [Theory]
    [InlineData("copy", true)]
    [InlineData("save", true)]
    [InlineData("edit", true)]
    [InlineData("pin", true)]
    [InlineData("background", true)]
    [InlineData("overlay", false)]
    public void NeedsCallerThreadHistoryClone_IsLimitedToActionsThatTransferOwnershipImmediately(string value, bool expected)
    {
        Assert.Equal(expected, PostCaptureAction.NeedsCallerThreadHistoryClone(value));
    }
}
