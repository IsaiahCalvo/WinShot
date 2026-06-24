using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class RecordingPauseCoordinatorTests
{
    [Fact]
    public void Pause_RunsRecorderBeforePausingOverlays()
    {
        var events = new List<string>();

        var result = RecordingPauseCoordinator.Pause(
            isRecording: true,
            isStopping: false,
            isPaused: false,
            pauseRecorder: () => events.Add("recorder:pause"),
            setOverlaysPaused: paused => events.Add($"overlays:{paused}"),
            logFailure: (message, _) => events.Add(message));

        Assert.True(result.IsPaused);
        Assert.True(result.Changed);
        Assert.Equal(new[] { "recorder:pause", "overlays:True" }, events);
    }

    [Fact]
    public void Pause_DoesNotChangeStateWhenRecorderPauseFails()
    {
        var events = new List<string>();

        var result = RecordingPauseCoordinator.Pause(
            isRecording: true,
            isStopping: false,
            isPaused: false,
            pauseRecorder: () => throw new InvalidOperationException("pause failed"),
            setOverlaysPaused: paused => events.Add($"overlays:{paused}"),
            logFailure: (message, _) => events.Add(message));

        Assert.False(result.IsPaused);
        Assert.False(result.Changed);
        Assert.Equal(new[] { "Failed to pause recording" }, events);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Pause_IgnoresInvalidStates(bool isRecording, bool isStopping, bool isPaused)
    {
        var result = RecordingPauseCoordinator.Pause(
            isRecording,
            isStopping,
            isPaused,
            pauseRecorder: () => throw new InvalidOperationException("should not pause"),
            setOverlaysPaused: _ => throw new InvalidOperationException("should not touch overlays"),
            logFailure: (_, _) => throw new InvalidOperationException("should not log"));

        Assert.Equal(isPaused, result.IsPaused);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Resume_RunsRecorderBeforeResumingOverlays()
    {
        var events = new List<string>();

        var result = RecordingPauseCoordinator.Resume(
            isRecording: true,
            isStopping: false,
            isPaused: true,
            resumeRecorder: () => events.Add("recorder:resume"),
            setOverlaysPaused: paused => events.Add($"overlays:{paused}"),
            logFailure: (message, _) => events.Add(message));

        Assert.False(result.IsPaused);
        Assert.True(result.Changed);
        Assert.Equal(new[] { "recorder:resume", "overlays:False" }, events);
    }

    [Fact]
    public void Resume_KeepsPausedWhenRecorderResumeFails()
    {
        var events = new List<string>();

        var result = RecordingPauseCoordinator.Resume(
            isRecording: true,
            isStopping: false,
            isPaused: true,
            resumeRecorder: () => throw new InvalidOperationException("resume failed"),
            setOverlaysPaused: paused => events.Add($"overlays:{paused}"),
            logFailure: (message, _) => events.Add(message));

        Assert.True(result.IsPaused);
        Assert.False(result.Changed);
        Assert.Equal(new[] { "Failed to resume recording" }, events);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void Resume_IgnoresInvalidStates(bool isRecording, bool isStopping, bool isPaused)
    {
        var result = RecordingPauseCoordinator.Resume(
            isRecording,
            isStopping,
            isPaused,
            resumeRecorder: () => throw new InvalidOperationException("should not resume"),
            setOverlaysPaused: _ => throw new InvalidOperationException("should not touch overlays"),
            logFailure: (_, _) => throw new InvalidOperationException("should not log"));

        Assert.Equal(isPaused, result.IsPaused);
        Assert.False(result.Changed);
    }
}
