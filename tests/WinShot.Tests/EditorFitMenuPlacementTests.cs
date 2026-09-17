using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Isaiah, 2026-09-17: "the fit menu must open where Survey's does."
///
/// Survey's collapsed rail opens it with <c>{ right: '100%', bottom: 0, marginRight: '6px' }</c>
/// (AppShell.jsx:3236) — flying out LEFT over the page, right edge against the trigger's left
/// edge with a 6px gap, BOTTOM edges level. WPF's PlacementMode.Left aligns TOPS, so the menu
/// hung ~62 DIP low off a 28 DIP button at the bottom of the rail and WPF flipped it back on
/// screen, making the landing spot depend on window height.
///
/// The placement is a pure function of two sizes, so it is asserted as one — no window, no
/// Application, no dispatcher, and therefore it runs in a full suite rather than under a filter.
/// </summary>
public class EditorFitMenuPlacementTests
{
    private static CustomPopupPlacement[] Place(Size popup, Size target) =>
        (CustomPopupPlacement[])typeof(EditorWindow)
            .GetMethod("PlaceFitMenu", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { popup, target, new Point(0, 0) })!;

    [Theory]
    // popup w/h,        trigger w/h  — the rail's fit cell is 36x28 (AppShell.jsx:3066).
    [InlineData(140, 90, 36, 28)]
    [InlineData(160, 62, 36, 28)]
    [InlineData(140, 28, 36, 28)]   // a menu exactly as tall as its trigger
    [InlineData(200, 140, 48, 36)]
    public void TheMenuHangsLeftOfTheTriggerWithItsBottomEdgesLevel(
        double pw, double ph, double tw, double th)
    {
        var placements = Place(new Size(pw, ph), new Size(tw, th));
        Assert.NotEmpty(placements);
        var primary = placements[0];

        // Right edge of the popup sits 6 DIP left of the trigger's left edge.
        double popupRight = primary.Point.X + pw;
        Assert.Equal(-6, popupRight, 6);

        // Bottom edges level.
        double popupBottom = primary.Point.Y + ph;
        Assert.Equal(th, popupBottom, 6);
    }

    /// <summary>
    /// With no room on the left the menu mirrors to the right of the trigger rather than
    /// being slid to wherever it happens to fit, which is what WPF does unaided.
    /// </summary>
    [Fact]
    public void TheFallbackMirrorsToTheRightAndKeepsTheBottomsLevel()
    {
        var placements = Place(new Size(140, 90), new Size(36, 28));
        Assert.True(placements.Length >= 2, "there is no fallback placement.");
        var fallback = placements[1];
        Assert.Equal(36 + 6, fallback.Point.X, 6);
        Assert.Equal(28, fallback.Point.Y + 90, 6);
    }
}
