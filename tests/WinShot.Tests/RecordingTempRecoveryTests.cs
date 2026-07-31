using System.IO;
using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class RecordingTempRecoveryTests
{
    [Fact]
    public void Discover_ReturnsOnlyOldNonEmptyWinShotRecordingsAndHonorsExclusions()
    {
        using var scope = new TempScope();
        DateTime now = new(2026, 7, 31, 18, 0, 0, DateTimeKind.Utc);
        string oldMp4 = scope.WriteRecording("mp4", "video", now.AddHours(-2));
        string excludedGif = scope.WriteRecording("gif", "gif", now.AddHours(-2));
        scope.WriteRecording("mp4", "recent", now.AddMinutes(-5));
        scope.WriteRecording("gif", "", now.AddHours(-2));
        File.WriteAllText(Path.Combine(scope.Path, "recording-not-a-guid.mp4"), "foreign");
        File.WriteAllText(Path.Combine(scope.Path, $"recording-{Guid.NewGuid():N}.txt"), "foreign");

        IReadOnlyList<RecoverableRecordingTempFile> found = RecordingTempRecovery.Discover(
            scope.Path,
            now,
            TimeSpan.FromMinutes(30),
            new[] { excludedGif });

        RecoverableRecordingTempFile candidate = Assert.Single(found);
        Assert.Equal(oldMp4, candidate.Path, ignoreCase: true);
        Assert.Equal("mp4", candidate.Extension);
    }

    [Fact]
    public void Recover_CommitsValidatedTempFileToUniqueSavePath()
    {
        using var temp = new TempScope();
        using var save = new TempScope();
        string recording = temp.WriteRecording("gif", "gif-data", DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(save.Path, "capture.gif"), "existing");

        RecordingFinalizationResult result = RecordingTempRecovery.Recover(
            recording, save.Path, "capture.gif", temp.Path);

        Assert.Equal(Path.Combine(save.Path, "capture (2).gif"), result.FinalPath);
        Assert.False(File.Exists(recording));
        Assert.Equal("gif-data", File.ReadAllText(result.FinalPath));
    }

    [Fact]
    public void Recover_RejectsForeignOrNestedFile()
    {
        using var scope = new TempScope();
        string foreign = Path.Combine(scope.Path, "unrelated.mp4");
        File.WriteAllText(foreign, "data");

        Assert.Throws<ArgumentException>(() => RecordingTempRecovery.Recover(
            foreign, scope.Path, "capture.mp4", scope.Path));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void FinalizationMessage_ExplainsDestinationRecoveryPathAndNextStep()
    {
        string message = RecordingFailureMessage.ForFinalization(
            @"Z:\Unavailable", @"C:\Temp\WinShot\recording-a.mp4", new IOException("Drive offline"));

        Assert.Contains(@"Z:\Unavailable", message);
        Assert.Contains(@"C:\Temp\WinShot\recording-a.mp4", message);
        Assert.Contains("still safe", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("free space", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Drive offline", message);
    }

    private sealed class TempScope : IDisposable
    {
        public TempScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"winshot-recording-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteRecording(string extension, string content, DateTime lastWriteUtc)
        {
            string path = System.IO.Path.Combine(Path, $"recording-{Guid.NewGuid():N}.{extension}");
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
