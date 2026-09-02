using System.IO;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class QuickActionsMenuTests
{
    private static string[] Ids(bool mediaFile = false, bool canEdit = true, bool canShare = true) =>
        QuickActionsMenu.Rows(mediaFile, canEdit, canShare)
            .Where(r => r.Id != QuickActionsMenu.Separator)
            .Select(r => r.Id)
            .ToArray();

    [Fact]
    public void ImageMenuOffersEditingRowsAndNoCloud()
    {
        string[] ids = Ids();

        Assert.Equal(
            new[]
            {
                QuickActionsMenu.Annotate,
                QuickActionsMenu.Pin,
                QuickActionsMenu.ExtractText,
                QuickActionsMenu.Background,
                QuickActionsMenu.RotateLeft,
                QuickActionsMenu.RotateRight,
                QuickActionsMenu.FlipHorizontal,
                QuickActionsMenu.FlipVertical,
                QuickActionsMenu.Resize,
                QuickActionsMenu.Print,
                QuickActionsMenu.Save,
                QuickActionsMenu.SaveAs,
                QuickActionsMenu.OpenWith,
                QuickActionsMenu.ShowInFolder,
                QuickActionsMenu.MoveToRecycleBin,
                QuickActionsMenu.Share,
                QuickActionsMenu.Close,
            },
            ids);
        Assert.DoesNotContain(
            QuickActionsMenu.Rows(false, true, true),
            r => r.Text.Contains("Cloud", StringComparison.OrdinalIgnoreCase) ||
                 r.Text.Contains("Quick Look", StringComparison.OrdinalIgnoreCase) ||
                 r.Text.Contains("Temporarily", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecordingMenuDropsPixelEdits()
    {
        string[] ids = Ids(mediaFile: true);

        Assert.Contains(QuickActionsMenu.Open, ids);
        Assert.DoesNotContain(QuickActionsMenu.RotateLeft, ids);
        Assert.DoesNotContain(QuickActionsMenu.Resize, ids);
        Assert.DoesNotContain(QuickActionsMenu.Print, ids);
        Assert.DoesNotContain(QuickActionsMenu.ExtractText, ids);
    }

    [Fact]
    public void RecordingWithoutEditorHidesEditRow()
        => Assert.DoesNotContain(QuickActionsMenu.Annotate, Ids(mediaFile: true, canEdit: false));

    [Fact]
    public void ShareRowDropsOutWhenUnsupported()
        => Assert.DoesNotContain(QuickActionsMenu.Share, Ids(canShare: false));

    [Fact]
    public void MenuNeverEndsOrStartsWithASeparator()
    {
        foreach (bool media in new[] { false, true })
        {
            var rows = QuickActionsMenu.Rows(media, canEdit: false, canShare: false);
            Assert.NotEqual(QuickActionsMenu.Separator, rows[0].Id);
            Assert.NotEqual(QuickActionsMenu.Separator, rows[^1].Id);
        }
    }

    [Theory]
    [InlineData(QuickActionsMenu.RotateLeft, SD.RotateFlipType.Rotate270FlipNone)]
    [InlineData(QuickActionsMenu.RotateRight, SD.RotateFlipType.Rotate90FlipNone)]
    [InlineData(QuickActionsMenu.FlipHorizontal, SD.RotateFlipType.RotateNoneFlipX)]
    [InlineData(QuickActionsMenu.FlipVertical, SD.RotateFlipType.RotateNoneFlipY)]
    public void TransformForMapsEditingRows(string id, SD.RotateFlipType expected)
        => Assert.Equal(expected, QuickActionsMenu.TransformFor(id));

    [Fact]
    public void TransformForIgnoresEverythingElse()
        => Assert.Null(QuickActionsMenu.TransformFor(QuickActionsMenu.Print));

    [Fact]
    public void QuarterTurnSwapsTheDimensions()
    {
        using var bitmap = new SD.Bitmap(400, 200);
        bitmap.RotateFlip(QuickActionsMenu.TransformFor(QuickActionsMenu.RotateRight)!.Value);

        Assert.Equal(200, bitmap.Width);
        Assert.Equal(400, bitmap.Height);
    }

    [Theory]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(100, 100, 37, 37)]
    [InlineData(3, 2, 10, 7)]
    public void AspectHeightFollowsTheSource(int w, int h, int newWidth, int expected)
        => Assert.Equal(expected, QuickActionsMenu.AspectHeight(new SD.Size(w, h), newWidth));

    [Fact]
    public void AspectWidthIsTheInverse()
    {
        var source = new SD.Size(1600, 900);
        Assert.Equal(1600, QuickActionsMenu.AspectWidth(source, 900));
        Assert.Equal(900, QuickActionsMenu.AspectHeight(source, 1600));
    }

    [Fact]
    public void AspectHelpersNeverReturnZeroOrDivideByZero()
    {
        Assert.Equal(1, QuickActionsMenu.AspectHeight(new SD.Size(0, 10), 100));
        Assert.Equal(1, QuickActionsMenu.AspectWidth(new SD.Size(10, 0), 100));
        Assert.Equal(1, QuickActionsMenu.AspectHeight(new SD.Size(1000, 1), 1));
    }

    [Fact]
    public void PrintFitLettersboxesWideImagesOnAPortraitPage()
    {
        var fit = QuickActionsMenu.FitCentered(new SD.Size(1000, 500), new SD.Rectangle(50, 60, 800, 1000));

        Assert.Equal(new SD.Rectangle(50, 360, 800, 400), fit);
    }

    [Fact]
    public void PrintFitPillarboxesTallImages()
    {
        var fit = QuickActionsMenu.FitCentered(new SD.Size(500, 1000), new SD.Rectangle(0, 0, 800, 400));

        Assert.Equal(new SD.Rectangle(300, 0, 200, 400), fit);
    }

    [Fact]
    public void PrintFitFallsBackWhenTheSourceIsDegenerate()
    {
        var bounds = new SD.Rectangle(10, 10, 100, 100);
        Assert.Equal(bounds, QuickActionsMenu.FitCentered(new SD.Size(0, 0), bounds));
    }

    [Fact]
    public void ResizedProducesTheRequestedSize()
    {
        using var source = new SD.Bitmap(400, 200);
        using var scaled = QuickActionsMenu.Resized(source, 100, 50);

        Assert.Equal(100, scaled.Width);
        Assert.Equal(50, scaled.Height);
    }

    [Fact]
    public void ResizedClampsToAtLeastOnePixel()
    {
        using var source = new SD.Bitmap(400, 200);
        using var scaled = QuickActionsMenu.Resized(source, 0, -5);

        Assert.Equal(1, scaled.Width);
        Assert.Equal(1, scaled.Height);
    }

    [Theory]
    [InlineData(SD.RotateFlipType.Rotate90FlipNone, SD.RotateFlipType.Rotate270FlipNone)]
    [InlineData(SD.RotateFlipType.Rotate270FlipNone, SD.RotateFlipType.Rotate90FlipNone)]
    [InlineData(SD.RotateFlipType.RotateNoneFlipX, SD.RotateFlipType.RotateNoneFlipX)]
    [InlineData(SD.RotateFlipType.RotateNoneFlipY, SD.RotateFlipType.RotateNoneFlipY)]
    public void InverseReversesEveryTransform(SD.RotateFlipType transform, SD.RotateFlipType expected)
        => Assert.Equal(expected, QuickActionsMenu.Inverse(transform));

    [Fact]
    public void ApplyingATransformThenItsInverseRestoresTheBitmap()
    {
        using var bitmap = new SD.Bitmap(400, 200);
        var transform = QuickActionsMenu.TransformFor(QuickActionsMenu.RotateLeft)!.Value;

        bitmap.RotateFlip(transform);
        Assert.Equal(new SD.Size(200, 400), bitmap.Size);
        bitmap.RotateFlip(QuickActionsMenu.Inverse(transform));

        Assert.Equal(new SD.Size(400, 200), bitmap.Size);
    }

    [Fact]
    public void OnlyTheFirstTwoGroupsCarryIcons()
    {
        foreach (var row in QuickActionsMenu.Rows(false, true, true))
        {
            if (row.Id == QuickActionsMenu.Separator)
                continue;
            bool editingRow = row.Id is QuickActionsMenu.Annotate or QuickActionsMenu.Pin
                or QuickActionsMenu.ExtractText or QuickActionsMenu.Background
                or QuickActionsMenu.RotateLeft or QuickActionsMenu.RotateRight
                or QuickActionsMenu.FlipHorizontal or QuickActionsMenu.FlipVertical
                or QuickActionsMenu.Resize;
            Assert.Equal(editingRow, QuickActionsMenu.IconFor(row.Id) is not null);
        }
    }

    [Fact]
    public void EveryIconIsADistinctAsset()
    {
        string[] icons = QuickActionsMenu.Rows(false, true, true)
            .Select(r => QuickActionsMenu.IconFor(r.Id))
            .Where(i => i is not null)
            .ToArray()!;

        Assert.Equal(9, icons.Length);
        Assert.Equal(icons.Length, icons.Distinct().Count());
    }

    [Fact]
    public void FileReadyRejectsMissingAndNullPaths()
    {
        Assert.False(QuickActionsMenu.FileReady(null));
        Assert.False(QuickActionsMenu.FileReady(Path.Combine(Path.GetTempPath(), "winshot-not-a-real-file.png")));

        string path = Path.Combine(Path.GetTempPath(), $"winshot-menu-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, new byte[] { 1 });
        try
        {
            Assert.True(QuickActionsMenu.FileReady(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
