using System.IO;
using System.Xml.Linq;
using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class VideoEditorInteractionTests
{
    [Theory]
    [InlineData(false, false, 0.1)]
    [InlineData(true, false, 1.0)]
    [InlineData(false, true, 5.0)]
    [InlineData(true, true, 5.0)]
    public void TrimStepSeconds_UsesDocumentedModifierScale(bool shift, bool control, double expected) =>
        Assert.Equal(expected, VideoEditorInteraction.TrimStepSeconds(shift, control), 3);

    [Fact]
    public void KeyboardTrimAdjustments_KeepMinimumRangeAndDurationBounds()
    {
        var start = VideoEditorInteraction.AdjustTrimStart(4.9, 5, 10, 5);
        var end = VideoEditorInteraction.AdjustTrimEnd(5, 5.1, 10, -5);

        Assert.Equal(4.9, start.StartSeconds, 3);
        Assert.Equal(5, start.EndSeconds, 3);
        Assert.Equal(5, end.StartSeconds, 3);
        Assert.Equal(5.1, end.EndSeconds, 3);
        Assert.True(start.IsExportable);
        Assert.True(end.IsExportable);
    }

    [Fact]
    public void CancellationRecognition_CoversManagedAndWinRtAbort()
    {
        Assert.True(VideoEditorInteraction.IsCancellation(new OperationCanceledException()));
        Assert.True(VideoEditorInteraction.IsCancellation(new Exception("outer", new WinRtAbortException())));
        Assert.False(VideoEditorInteraction.IsCancellation(new IOException("disk full")));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "permissions")]
    [InlineData(typeof(FileNotFoundException), "Restore")]
    [InlineData(typeof(IOException), "free space")]
    [InlineData(typeof(InvalidOperationException), "source video was not changed")]
    public void ExportFailureMessage_IsActionableAndDoesNotExposeExceptionText(Type type, string expectedPhrase)
    {
        var exception = (Exception)Activator.CreateInstance(type, "secret codec detail")!;

        string message = VideoEditorInteraction.ExportFailureMessage(exception);

        Assert.Contains(expectedPhrase, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret codec detail", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialOutputCleanup_DeletesOnlyTheCandidateOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winshot-video-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source.mp4");
        string partial = Path.Combine(directory, "source (edited).mp4");
        await File.WriteAllTextAsync(source, "source stays");
        await File.WriteAllTextAsync(partial, "partial goes");

        try
        {
            Assert.True(await VideoEditorInteraction.TryDeletePartialOutputAsync(partial));
            Assert.False(File.Exists(partial));
            Assert.Equal("source stays", await File.ReadAllTextAsync(source));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void VideoEditorXaml_ExposesKeyboardAccessibilityAndAdaptiveLayout()
    {
        string xamlPath = FindRepoFile("src", "WinShot", "Recording", "VideoEditorWindow.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement Named(string name) => document
            .Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == name);

        Assert.Equal("True", (string?)document.Root!.Attribute("UseLayoutRounding"));
        Assert.Equal("OnWindowKeyDown", (string?)document.Root.Attribute("KeyDown"));
        Assert.Equal("True", (string?)Named("PositionSlider").Attribute("Focusable"));
        Assert.Equal("True", (string?)Named("VolumeSlider").Attribute("Focusable"));
        Assert.Equal("OnTrimStartKeyDown", (string?)Named("TrimStartThumb").Attribute("KeyDown"));
        Assert.Equal("OnTrimEndKeyDown", (string?)Named("TrimEndThumb").Attribute("KeyDown"));
        Assert.False(string.IsNullOrWhiteSpace((string?)Named("TrimStartThumb").Attribute("AutomationProperties.HelpText")));
        Assert.False(string.IsNullOrWhiteSpace((string?)Named("BtnCancelExport").Attribute("AutomationProperties.HelpText")));
        Assert.Equal("Collapsed", (string?)Named("BtnCancelExport").Attribute("Visibility"));
        Assert.Equal("_Cancel", (string?)Named("BtnClose").Attribute("Content"));
        Assert.Equal("_Trim & Export", (string?)Named("BtnExport").Attribute("Content"));
        Assert.Equal("Output summary", (string?)Named("OutputSummaryText").Attribute("AutomationProperties.Name"));
        Assert.Contains(document.Descendants(presentation + "WrapPanel"), panel =>
            (string?)panel.Attribute("Grid.Row") == "3");

        string xamlText = File.ReadAllText(xamlPath);
        Assert.Contains("IsKeyboardFocused", xamlText, StringComparison.Ordinal);
        Assert.Contains("IsKeyboardFocusWithin", xamlText, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find repository file: {Path.Combine(parts)}");
    }

    private sealed class WinRtAbortException : Exception
    {
        public WinRtAbortException() => HResult = unchecked((int)0x80004004);
    }
}
