using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class RecordingOverlayStartupTests
{
    [Fact]
    public void Start_DoesNotCreateDisabledOverlays()
    {
        var events = new List<string>();

        var result = RecordingOverlayStartup.Start(
            showClickHighlights: false,
            showKeystrokes: false,
            createClickOverlay: () => throw new InvalidOperationException("click"),
            createKeyOverlay: () => throw new InvalidOperationException("key"),
            logFailure: (message, _) => events.Add(message));

        Assert.Null(result.ClickOverlay);
        Assert.Null(result.KeyOverlay);
        Assert.Empty(events);
    }

    [Fact]
    public void Start_CreatesAndShowsRequestedOverlays()
    {
        var events = new List<string>();

        var result = RecordingOverlayStartup.Start(
            showClickHighlights: true,
            showKeystrokes: true,
            createClickOverlay: () => new FakeOverlay("click", events),
            createKeyOverlay: () => new FakeOverlay("key", events),
            logFailure: (message, _) => events.Add(message));

        Assert.NotNull(result.ClickOverlay);
        Assert.NotNull(result.KeyOverlay);
        Assert.Equal(new[] { "click:show", "key:show" }, events);
    }

    [Fact]
    public void Start_ClosesOverlayWhenShowFails()
    {
        var events = new List<string>();

        var result = RecordingOverlayStartup.Start(
            showClickHighlights: true,
            showKeystrokes: false,
            createClickOverlay: () => new FakeOverlay("click", events, failShow: true),
            createKeyOverlay: () => throw new InvalidOperationException("key"),
            logFailure: (message, _) => events.Add(message));

        Assert.Null(result.ClickOverlay);
        Assert.Null(result.KeyOverlay);
        Assert.Equal(
            new[] { "click:show", "click:close", "Failed to show click highlights; recording will continue without them." },
            events);
    }

    [Fact]
    public void Start_ContinuesWhenOneOverlayFails()
    {
        var events = new List<string>();

        var result = RecordingOverlayStartup.Start(
            showClickHighlights: true,
            showKeystrokes: true,
            createClickOverlay: () => throw new InvalidOperationException("click failed"),
            createKeyOverlay: () => new FakeOverlay("key", events),
            logFailure: (message, _) => events.Add(message));

        Assert.Null(result.ClickOverlay);
        Assert.NotNull(result.KeyOverlay);
        Assert.Equal(
            new[] { "Failed to show click highlights; recording will continue without them.", "key:show" },
            events);
    }

    private sealed class FakeOverlay : IRecordingOverlay
    {
        private readonly string _name;
        private readonly List<string> _events;
        private readonly bool _failShow;

        public FakeOverlay(string name, List<string> events, bool failShow = false)
        {
            _name = name;
            _events = events;
            _failShow = failShow;
        }

        public void Show()
        {
            _events.Add($"{_name}:show");
            if (_failShow)
                throw new InvalidOperationException($"{_name} failed");
        }

        public void Close() => _events.Add($"{_name}:close");

        public void SetPaused(bool paused) => _events.Add($"{_name}:paused:{paused}");
    }
}
