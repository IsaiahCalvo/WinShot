using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// Where the editor window lands, and what it grows out of. Two jobs that are really
/// one sum: the window opens on the display the capture came from (Shottr's "editor
/// opens on the capture's display") and tweens out of the card's rect to get there,
/// so the target rect must be computed once and shared by both.
///
/// Everything here is in DIPs. The device-to-DIP matrix belongs to the window's own
/// PresentationSource — on a mixed-DPI desktop the primary monitor's scale is the
/// wrong answer — so the caller converts and this stays pure and testable.
/// </summary>
internal static class EditorGrowLayout
{
    /// <summary>
    /// The most of a monitor's work area the editor will take. A window that fills its
    /// display edge to edge reads as maximised, and the grow tween has nowhere to land.
    /// </summary>
    public const double MaxWorkAreaFraction = 0.9;

    /// <summary>
    /// The editor's resting rect: its desired size, capped at 90 % of the work area on
    /// each axis, centred in that work area. The work area is the capture display's, so
    /// a shot taken on a monitor above the laptop (negative Y) opens up there and not on
    /// the primary.
    /// </summary>
    public static Rect Target(Rect workArea, Size desired)
    {
        double width = Math.Min(desired.Width, workArea.Width * MaxWorkAreaFraction);
        double height = Math.Min(desired.Height, workArea.Height * MaxWorkAreaFraction);
        return new Rect(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width,
            height);
    }

    /// <summary>
    /// The rect the tween starts from. A degenerate origin is floored to 1×1 rather than
    /// rejected: Width/Height are set every frame and WPF throws on a negative one, so a
    /// card that reported a zero-height rect must still produce a runnable tween.
    /// </summary>
    public static Rect Origin(Rect originDip) => new(
        originDip.X,
        originDip.Y,
        Math.Max(1, originDip.Width),
        Math.Max(1, originDip.Height));
}
