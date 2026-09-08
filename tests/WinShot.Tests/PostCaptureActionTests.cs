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

/// <summary>
/// After-capture is a SET: overlay and copy and save and annotate and pin can all be on.
/// These pin the shape of that set, the fixed order it runs in, and the one-time migration
/// that carries an existing user's single-choice value across.
/// </summary>
public class PostCaptureSetTests
{
    [Fact]
    public void ForScreenshot_ReadsEveryMatrixCell_Independently()
    {
        var settings = new Settings
        {
            ScreenshotShowOverlay = true,
            ScreenshotCopy = true,
            ScreenshotSave = true,
            ScreenshotOpenAnnotate = true,
            ScreenshotPin = true,
        };

        var set = PostCaptureSet.ForScreenshot(settings);
        Assert.Equal(new PostCaptureSet(true, true, true, true, true), set);
        Assert.Equal(5, set.Count);
        Assert.False(set.IsEmpty);
    }

    [Fact]
    public void ForScreenshot_WithNothingTicked_IsEmpty()
    {
        var set = PostCaptureSet.ForScreenshot(new Settings
        {
            ScreenshotShowOverlay = false,
            ScreenshotCopy = false,
        });
        Assert.True(set.IsEmpty);
        Assert.Equal(0, set.Count);
    }

    [Theory]
    [InlineData("copy", true, false, false, false, false)]
    [InlineData("save", false, true, false, false, false)]
    [InlineData("edit", false, false, true, false, false)]
    [InlineData("pin", false, false, false, true, false)]
    [InlineData("overlay", false, false, false, false, true)]
    [InlineData("nonsense", false, false, false, false, true)]
    public void Single_MapsAnOverrideToOneAction(
        string action, bool copy, bool save, bool annotate, bool pin, bool overlay)
    {
        Assert.Equal(
            new PostCaptureSet(copy, save, annotate, pin, overlay),
            PostCaptureSet.Single(action));
    }

    [Fact]
    public void Single_CarriesTheCopyPreferenceThroughAnOverride()
    {
        // The single-action path always auto-copied alongside the chosen action; an
        // override must not silently turn "copy every screenshot" off.
        Assert.True(PostCaptureSet.Single("save", alsoCopy: true).Copy);
        Assert.True(PostCaptureSet.Single("pin", alsoCopy: true).Save == false);
        // ...except "copy", which is already the copy.
        Assert.Equal(PostCaptureSet.Single("copy"), PostCaptureSet.Single("copy", alsoCopy: true));
    }

    [Theory]
    [InlineData("copy,save", true, true, false, false, false)]
    [InlineData("edit", false, false, true, false, false)]
    [InlineData("annotate+pin", false, false, true, true, false)]
    [InlineData("thumbnail", false, false, false, false, true)]
    [InlineData("COPY, Thumbnail", true, false, false, false, true)]
    public void TryParseThen_ReadsAUrlActionList(
        string then, bool copy, bool save, bool annotate, bool pin, bool overlay)
    {
        Assert.Equal(
            new PostCaptureSet(copy, save, annotate, pin, overlay),
            PostCaptureSet.TryParseThen(then));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense,rubbish")]
    public void TryParseThen_KeepsTheUsersSettingWhenTheListSaysNothing(string? then)
    {
        Assert.Null(PostCaptureSet.TryParseThen(then));
    }

    [Theory]
    [InlineData("overlay", false, false, false, false, true)]
    [InlineData("copy", true, false, false, false, false)]
    [InlineData("save", false, true, false, false, false)]
    [InlineData("edit", false, false, true, false, false)]
    [InlineData("pin", false, false, false, true, false)]
    // "Add background" has no matrix row: the card carries the same button.
    [InlineData("background", false, false, false, false, true)]
    public void Migration_SeedsTheMatrixFromTheOldSingleChoice(
        string action, bool copy, bool save, bool annotate, bool pin, bool overlay)
    {
        var s = new Settings { PostCaptureAction = action, AutoCopyToClipboard = false };

        Assert.True(PostCaptureMigration.Apply(s));
        Assert.Equal(
            new PostCaptureSet(copy, save, annotate, pin, overlay),
            PostCaptureSet.ForScreenshot(s));
    }

    [Fact]
    public void Migration_KeepsTheOldAlsoCopyCheckbox()
    {
        var s = new Settings { PostCaptureAction = "save", AutoCopyToClipboard = true };
        PostCaptureMigration.Apply(s);
        Assert.True(s.ScreenshotCopy);
        Assert.True(s.ScreenshotSave);
    }

    [Fact]
    public void Migration_AbsorbsTheDuplicateVideoEditorCheckbox()
    {
        var s = new Settings { OpenVideoEditorAfterRecording = true, RecordingOpenEditor = false };
        PostCaptureMigration.Apply(s);
        Assert.True(s.RecordingOpenEditor);
    }

    [Fact]
    public void Migration_RunsExactlyOnce_SoAUserEditCannotBeOverwritten()
    {
        var s = new Settings { PostCaptureAction = "save" };
        Assert.True(PostCaptureMigration.Apply(s));
        Assert.Equal(PostCaptureMigration.MatrixSchemaVersion, s.SettingsSchemaVersion);

        // The user then unticks Save in the matrix. A second migration pass must not
        // resurrect it from the stale seed.
        s.ScreenshotSave = false;
        Assert.False(PostCaptureMigration.Apply(s));
        Assert.False(s.ScreenshotSave);
    }
}
