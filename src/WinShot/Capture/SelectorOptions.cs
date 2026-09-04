using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Capture;

internal enum SelectorCrosshairMode
{
    Always,
    Command,
    Never,
}

/// <summary>Small shared mapping from persisted Settings to selector behavior.</summary>
internal readonly record struct SelectorOptions(
    bool FreezeScreen,
    bool ShowCrosshair,
    bool ShowMagnifier,
    SelectorCrosshairMode CrosshairMode,
    bool RememberLastRegion,
    bool AdjustBeforeCapture = false)
{
    public static SelectorOptions ForRegion(Settings? settings) =>
        FromSettings(settings, rememberLastRegion: false);

    public static SelectorOptions ForAllInOne(Settings? settings) =>
        FromSettings(settings, settings?.AllInOneRememberLast ?? true);

    private static SelectorOptions FromSettings(Settings? settings, bool rememberLastRegion)
    {
        if (settings is null)
            return new SelectorOptions(true, true, true, SelectorCrosshairMode.Always, rememberLastRegion);

        SelectorCrosshairMode crosshairMode = (settings.CrosshairMode ?? "always").Trim().ToLowerInvariant() switch
        {
            "never" => SelectorCrosshairMode.Never,
            "command" => SelectorCrosshairMode.Command,
            _ => SelectorCrosshairMode.Always,
        };

        return new SelectorOptions(
            settings.FreezeScreen,
            settings.ShowCrosshair,
            settings.ShowMagnifier,
            crosshairMode,
            rememberLastRegion,
            settings.AdjustSelectionBeforeCapturing);
    }

    /// <summary>
    /// Whether releasing a drag should leave an adjustable selection instead of taking the
    /// shot. Ctrl is a per-drag override of the setting, not an extra way to switch it on:
    /// whichever behaviour is configured, holding Ctrl at release gives you the other one,
    /// so neither audience is ever more than a modifier away from what they want.
    /// </summary>
    public bool ShouldAdjustSelection(bool controlPressed) =>
        AdjustBeforeCapture != controlPressed;

    public bool NeedsCursorFollow =>
        ShowMagnifier || (ShowCrosshair && CrosshairMode != SelectorCrosshairMode.Never);

    public bool IsCrosshairVisible(bool controlPressed) =>
        ShowCrosshair && CrosshairMode switch
        {
            SelectorCrosshairMode.Never => false,
            SelectorCrosshairMode.Command => controlPressed,
            _ => true,
        };

    public bool TryGetRememberedRegion(
        string savedScreenRegion,
        SD.Rectangle virtualScreen,
        out SD.Rectangle virtualRegion)
    {
        virtualRegion = default;
        if (!RememberLastRegion ||
            !PreviousRegion.TryParse(savedScreenRegion, out SD.Rectangle screenRegion))
        {
            return false;
        }

        virtualRegion = new SD.Rectangle(
            screenRegion.X - virtualScreen.X,
            screenRegion.Y - virtualScreen.Y,
            screenRegion.Width,
            screenRegion.Height);
        virtualRegion.Intersect(new SD.Rectangle(0, 0, virtualScreen.Width, virtualScreen.Height));
        return virtualRegion.Width > 0 && virtualRegion.Height > 0;
    }
}
