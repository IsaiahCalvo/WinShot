using WinShot.Core;
using WinShot.SettingsUi;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The Keep-history slider carries two settings at once: the far-left "Never" stop switches
/// History off entirely, every other stop is an age-pruning window. These pin the mapping in
/// both directions so the privacy stop cannot be reached (or lost) by accident.
/// </summary>
public class HistoryRetentionSliderTests
{
    [Theory]
    [InlineData(0, SettingsWindow.HistoryOffStop)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 7)]
    [InlineData(5, 30)]
    public void SliderStop_MapsToRetention(int index, int expected) =>
        Assert.Equal(expected, SettingsWindow.SliderIndexToRetentionDays(index));

    [Fact]
    public void SliderStop_ClampsOutOfRangeIndexes()
    {
        Assert.Equal(SettingsWindow.HistoryOffStop, SettingsWindow.SliderIndexToRetentionDays(-4));
        Assert.Equal(30, SettingsWindow.SliderIndexToRetentionDays(99));
    }

    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(true, 1, 2)]
    [InlineData(true, 30, 5)]
    // An unrecognised stored value snaps to the nearest real stop, never to "Never".
    [InlineData(true, 5, 3)]
    [InlineData(true, -9, 1)]
    public void EnabledHistory_NeverLandsOnTheOffStop(bool enabled, int days, int expectedIndex) =>
        Assert.Equal(expectedIndex, SettingsWindow.RetentionToSliderIndex(enabled, days));

    [Fact]
    public void DisabledHistory_WinsOverTheStoredRetention() =>
        Assert.Equal(0, SettingsWindow.RetentionToSliderIndex(historyEnabled: false, days: 7));

    [Fact]
    public void HistoryIsOnByDefault() => Assert.True(new Settings().HistoryEnabled);
}
