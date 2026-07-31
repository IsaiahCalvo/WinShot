using System.IO;
using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class RecordingRecoveryStartupTests
{
    [Fact]
    public void Run_WaitsForSettingsHistoryAndIdleRecordingState()
    {
        int discoveries = 0;
        var startup = CreateStartup(
            _ =>
            {
                discoveries++;
                return Array.Empty<RecoverableRecordingTempFile>();
            });

        Assert.Equal(RecordingRecoveryRunState.NotReady,
            startup.Run(false, true, false, null, _ => RecordingRecoveryDecision.KeepForLater).State);
        Assert.Equal(RecordingRecoveryRunState.NotReady,
            startup.Run(true, false, false, null, _ => RecordingRecoveryDecision.KeepForLater).State);
        Assert.Equal(RecordingRecoveryRunState.NotReady,
            startup.Run(true, true, true, null, _ => RecordingRecoveryDecision.KeepForLater).State);
        Assert.Equal(0, discoveries);

        Assert.Equal(RecordingRecoveryRunState.NoCandidates,
            startup.Run(true, true, false, null, _ => RecordingRecoveryDecision.KeepForLater).State);
        Assert.Equal(1, discoveries);
    }

    [Fact]
    public void Run_ExcludesActiveTempPathFromStrictDiscovery()
    {
        using var temp = new TempScope();
        DateTime now = DateTime.UtcNow;
        string active = temp.WriteRecording("mp4", "active", now.AddHours(-2));
        string orphan = temp.WriteRecording("gif", "orphan", now.AddHours(-2));
        var startup = CreateStartup(exclusions => RecordingTempRecovery.Discover(
            temp.Path,
            now,
            TimeSpan.FromMinutes(30),
            exclusions));
        IReadOnlyList<RecoverableRecordingTempFile>? prompted = null;

        RecordingRecoveryRunResult result = startup.Run(
            true,
            true,
            false,
            active,
            candidates =>
            {
                prompted = candidates;
                return RecordingRecoveryDecision.KeepForLater;
            });

        Assert.Equal(RecordingRecoveryRunState.KeptForLater, result.State);
        RecoverableRecordingTempFile candidate = Assert.Single(prompted!);
        Assert.Equal(orphan, candidate.Path, ignoreCase: true);
        Assert.True(File.Exists(active));
    }

    [Fact]
    public void Run_KeepForLaterLeavesEverySourceUntouched()
    {
        using var temp = new TempScope();
        string orphan = temp.WriteRecording("mp4", "video", DateTime.UtcNow.AddHours(-2));
        int recoveries = 0;
        int historyAdds = 0;
        var candidate = Candidate(orphan);
        var startup = new RecordingRecoveryStartup(
            _ => new[] { candidate },
            _ =>
            {
                recoveries++;
                throw new InvalidOperationException("Recovery must not run.");
            },
            _ => historyAdds++);

        RecordingRecoveryRunResult result = startup.Run(
            true, true, false, null, _ => RecordingRecoveryDecision.KeepForLater);

        Assert.Equal(RecordingRecoveryRunState.KeptForLater, result.State);
        Assert.True(File.Exists(orphan));
        Assert.Equal(0, recoveries);
        Assert.Equal(0, historyAdds);
    }

    [Fact]
    public void Run_RecoverFinalizesUniquelyAndAddsFinalPathToHistory()
    {
        using var temp = new TempScope();
        using var save = new TempScope();
        string orphan = temp.WriteRecording("mp4", "video", DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(Path.Combine(save.Path, "Recovered.mp4"), "existing");
        string? historyPath = null;
        var startup = new RecordingRecoveryStartup(
            _ => new[] { Candidate(orphan) },
            candidate => RecordingTempRecovery.Recover(
                candidate.Path,
                save.Path,
                "Recovered.mp4",
                temp.Path),
            path => historyPath = path);

        RecordingRecoveryRunResult result = startup.Run(
            true, true, false, null, _ => RecordingRecoveryDecision.Recover);

        string recovered = Assert.Single(result.RecoveredPaths);
        Assert.Equal(Path.Combine(save.Path, "Recovered (2).mp4"), recovered);
        Assert.Equal(recovered, historyPath);
        Assert.False(File.Exists(orphan));
        Assert.Equal("video", File.ReadAllText(recovered));
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Run_RecoveryFailureReportsLocationAndPreservesTempFile()
    {
        using var temp = new TempScope();
        string orphan = temp.WriteRecording("gif", "gif", DateTime.UtcNow.AddHours(-2));
        var startup = new RecordingRecoveryStartup(
            _ => new[] { Candidate(orphan) },
            _ => throw new IOException("Save folder is unavailable."),
            _ => throw new InvalidOperationException("History must not run."));

        RecordingRecoveryRunResult result = startup.Run(
            true, true, false, null, _ => RecordingRecoveryDecision.Recover);

        RecordingRecoveryFailure failure = Assert.Single(result.Failures);
        Assert.Equal(orphan, failure.TempPath, ignoreCase: true);
        Assert.Contains("unavailable", failure.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(orphan));
        Assert.Empty(result.RecoveredPaths);
    }

    [Fact]
    public void Run_ShowsAtMostOnePromptPerAppStart()
    {
        using var temp = new TempScope();
        string orphan = temp.WriteRecording("mp4", "video", DateTime.UtcNow.AddHours(-2));
        int prompts = 0;
        var startup = CreateStartup(_ => new[] { Candidate(orphan) });

        RecordingRecoveryRunResult first = startup.Run(true, true, false, null, _ =>
        {
            prompts++;
            return RecordingRecoveryDecision.KeepForLater;
        });
        RecordingRecoveryRunResult second = startup.Run(true, true, false, null, _ =>
        {
            prompts++;
            return RecordingRecoveryDecision.Recover;
        });

        Assert.Equal(RecordingRecoveryRunState.KeptForLater, first.State);
        Assert.Equal(RecordingRecoveryRunState.AlreadyChecked, second.State);
        Assert.Equal(1, prompts);
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public void Run_HistoryFailureDoesNotUndoSuccessfulRecovery()
    {
        using var temp = new TempScope();
        using var save = new TempScope();
        string orphan = temp.WriteRecording("gif", "gif", DateTime.UtcNow.AddHours(-2));
        var startup = new RecordingRecoveryStartup(
            _ => new[] { Candidate(orphan) },
            candidate => RecordingTempRecovery.Recover(
                candidate.Path, save.Path, "Recovered.gif", temp.Path),
            _ => throw new IOException("History folder unavailable."));

        RecordingRecoveryRunResult result = startup.Run(
            true, true, false, null, _ => RecordingRecoveryDecision.Recover);

        string recovered = Assert.Single(result.RecoveredPaths);
        Assert.True(File.Exists(recovered));
        Assert.False(File.Exists(orphan));
        Assert.Single(result.HistoryWarnings);
        Assert.Empty(result.Failures);
    }

    private static RecordingRecoveryStartup CreateStartup(
        Func<IEnumerable<string>?, IReadOnlyList<RecoverableRecordingTempFile>> discover) =>
        new(
            discover,
            candidate => new RecordingFinalizationResult(candidate.Path, TempFileRetained: true),
            _ => { });

    private static RecoverableRecordingTempFile Candidate(string path)
    {
        var info = new FileInfo(path);
        return new RecoverableRecordingTempFile(
            info.FullName,
            info.Extension.TrimStart('.'),
            info.Length,
            info.LastWriteTimeUtc);
    }

    private sealed class TempScope : IDisposable
    {
        internal TempScope()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"winshot-recording-startup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string WriteRecording(string extension, string content, DateTime lastWriteUtc)
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
