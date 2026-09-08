using System.IO;
using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// When the lossless trim path is on offer, what the primary button promises, and where an
/// export lands. All pure — the interesting cases are the ones that must NOT happen: offering
/// a trim that changes nothing, and failing an export because a save folder moved.
/// </summary>
public class VideoExportChoicesTests
{
    [Theory]
    [InlineData(0, 60, 60, false)]           // untouched
    [InlineData(0.01, 59.99, 60, false)]     // a fraction of a frame is not an edit
    [InlineData(5, 60, 60, true)]
    [InlineData(0, 55, 60, true)]
    [InlineData(5, 55, 60, true)]
    public void HasTrim_IsTrueOnlyOnceTheHandlesReallyMoved(
        double start, double end, double duration, bool expected) =>
        Assert.Equal(expected, VideoExportLabels.HasTrim(start, end, duration));

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(double.NaN, 10, 60)]
    [InlineData(0, double.PositiveInfinity, 60)]
    public void HasTrim_IsFalseForNonsense(double start, double end, double duration) =>
        Assert.False(VideoExportLabels.HasTrim(start, end, duration));

    [Fact]
    public void PrimaryLabel_StopsPromisingATrimItIsNotMaking()
    {
        Assert.Equal("Convert", VideoExportLabels.PrimaryLabel(hasTrim: false));
        Assert.Equal("Trim & convert", VideoExportLabels.PrimaryLabel(hasTrim: true));
        Assert.NotEqual(
            VideoExportLabels.PrimaryHelp(hasTrim: false),
            VideoExportLabels.PrimaryHelp(hasTrim: true));
    }

    // ---- destination ---------------------------------------------------------------

    private const string Source = @"C:\Videos\take.mp4";
    private const string SaveFolder = @"D:\Captures";
    private const string Chosen = @"E:\Elsewhere";

    private static bool Exists(string folder) =>
        folder is @"C:\Videos" or SaveFolder or Chosen;

    [Fact]
    public void Destination_DefaultsToTheSourceFolder() =>
        Assert.Equal(
            @"C:\Videos",
            VideoExportDestination.Resolve(
                VideoExportDestination.BesideOriginal, Source, SaveFolder, Chosen, Exists));

    [Fact]
    public void Destination_UsesTheSaveFolderWhenAsked() =>
        Assert.Equal(
            SaveFolder,
            VideoExportDestination.Resolve(
                VideoExportDestination.SaveFolder, Source, SaveFolder, Chosen, Exists));

    [Fact]
    public void Destination_UsesTheChosenFolderWhenAsked() =>
        Assert.Equal(
            Chosen,
            VideoExportDestination.Resolve(
                VideoExportDestination.ChooseFolder, Source, SaveFolder, Chosen, Exists));

    /// <summary>An export must never fail because a folder moved: the source's own folder is
    /// guaranteed to exist, because the file being edited is in it.</summary>
    [Theory]
    [InlineData(VideoExportDestination.SaveFolder, null)]
    [InlineData(VideoExportDestination.SaveFolder, "")]
    [InlineData(VideoExportDestination.SaveFolder, @"Z:\Gone")]
    [InlineData(VideoExportDestination.ChooseFolder, null)]
    [InlineData(VideoExportDestination.ChooseFolder, @"Z:\Gone")]
    public void Destination_FallsBackToTheSourceFolder(int index, string? folder)
    {
        string resolved = index == VideoExportDestination.SaveFolder
            ? VideoExportDestination.Resolve(index, Source, folder, null, Exists)
            : VideoExportDestination.Resolve(index, Source, null, folder, Exists);

        Assert.Equal(@"C:\Videos", resolved);
    }

    [Fact]
    public void EditedFileName_KeepsTheSourcesExtension()
    {
        Assert.Equal("take (edited).mp4", VideoExportDestination.EditedFileName(Source));
        Assert.Equal("clip (edited).gif", VideoExportDestination.EditedFileName(@"C:\a\clip.gif"));
    }

    [Fact]
    public void EditedFileName_IsAValidFileName() =>
        Assert.Equal(-1, VideoExportDestination.EditedFileName(Source).IndexOfAny(Path.GetInvalidFileNameChars()));
}
