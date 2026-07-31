namespace WinShot.Recording;

internal enum RecordingRecoveryDecision
{
    KeepForLater,
    Recover,
}

internal enum RecordingRecoveryRunState
{
    NotReady,
    AlreadyChecked,
    NoCandidates,
    KeptForLater,
    Recovered,
}

internal sealed record RecordingRecoveryFailure(string TempPath, Exception Error);

internal sealed record RecordingRecoveryRunResult(
    RecordingRecoveryRunState State,
    IReadOnlyList<string> RecoveredPaths,
    IReadOnlyList<RecordingRecoveryFailure> Failures,
    IReadOnlyList<string> HistoryWarnings)
{
    internal static RecordingRecoveryRunResult Empty(RecordingRecoveryRunState state) =>
        new(state, Array.Empty<string>(), Array.Empty<RecordingRecoveryFailure>(), Array.Empty<string>());
}

/// <summary>
/// Runs the conservative startup recovery check once per App instance. The UI
/// decision is injected so discovery, keep, recover, and failure behavior stay
/// deterministic in tests.
/// </summary>
internal sealed class RecordingRecoveryStartup
{
    private readonly Func<IEnumerable<string>?, IReadOnlyList<RecoverableRecordingTempFile>> _discover;
    private readonly Func<RecoverableRecordingTempFile, RecordingFinalizationResult> _recover;
    private readonly Action<string> _addToHistory;
    private int _checkedThisStart;

    internal RecordingRecoveryStartup(
        Func<IEnumerable<string>?, IReadOnlyList<RecoverableRecordingTempFile>> discover,
        Func<RecoverableRecordingTempFile, RecordingFinalizationResult> recover,
        Action<string> addToHistory)
    {
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
        _recover = recover ?? throw new ArgumentNullException(nameof(recover));
        _addToHistory = addToHistory ?? throw new ArgumentNullException(nameof(addToHistory));
    }

    internal RecordingRecoveryRunResult Run(
        bool settingsReady,
        bool historyReady,
        bool recordingActive,
        string? activeTempPath,
        Func<IReadOnlyList<RecoverableRecordingTempFile>, RecordingRecoveryDecision> prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        // Do not consume this launch's single check until startup is actually safe.
        if (!settingsReady || !historyReady || recordingActive)
            return RecordingRecoveryRunResult.Empty(RecordingRecoveryRunState.NotReady);

        if (Interlocked.Exchange(ref _checkedThisStart, 1) != 0)
            return RecordingRecoveryRunResult.Empty(RecordingRecoveryRunState.AlreadyChecked);

        IEnumerable<string>? exclusions = string.IsNullOrWhiteSpace(activeTempPath)
            ? null
            : new[] { activeTempPath };
        IReadOnlyList<RecoverableRecordingTempFile> candidates = _discover(exclusions);
        if (candidates.Count == 0)
            return RecordingRecoveryRunResult.Empty(RecordingRecoveryRunState.NoCandidates);

        if (prompt(candidates) != RecordingRecoveryDecision.Recover)
            return RecordingRecoveryRunResult.Empty(RecordingRecoveryRunState.KeptForLater);

        var recovered = new List<string>();
        var failures = new List<RecordingRecoveryFailure>();
        var historyWarnings = new List<string>();

        foreach (RecoverableRecordingTempFile candidate in candidates)
        {
            try
            {
                RecordingFinalizationResult result = _recover(candidate);
                recovered.Add(result.FinalPath);
                try
                {
                    _addToHistory(result.FinalPath);
                }
                catch (Exception ex)
                {
                    // The recording is already safely finalized. A History copy failure
                    // must not turn a successful recovery into a destructive retry.
                    historyWarnings.Add($"{result.FinalPath}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                // RecordingTempRecovery preserves the source on every failed finalization.
                failures.Add(new RecordingRecoveryFailure(candidate.Path, ex));
            }
        }

        return new RecordingRecoveryRunResult(
            RecordingRecoveryRunState.Recovered,
            recovered,
            failures,
            historyWarnings);
    }
}
