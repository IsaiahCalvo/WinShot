namespace WinShot.Core;

/// <summary>The three confirmations quiet enough that people want them configured separately.</summary>
public enum NotifiedAction
{
    /// <summary>Text or a code recognised and copied.</summary>
    Ocr,

    /// <summary>Something put on the clipboard — a capture today, a colour when the loupe
    /// copy lands.</summary>
    Copy,

    /// <summary>A capture written to disk.</summary>
    Save,
}

/// <summary>
/// How one action confirms itself. WinShot has a single feedback language on purpose, so
/// this is deliberately not a per-toast knob — it is three settings for the three routine
/// successes, where "every capture tells me it copied" stops being information after the
/// first week.
/// </summary>
/// <remarks>
/// Failures are never routed through here. An error is not a confirmation, and a user who
/// silenced "Saved" did not ask to stop hearing that a save failed.
/// </remarks>
public static class NotificationStyle
{
    /// <summary>WinShot's own toast, anchored near what just happened. The default.</summary>
    public const string InApp = "in-app";

    /// <summary>The Windows shell notification. Carries no buttons, and Focus Assist may
    /// hold it back — which is exactly why it is not the default.</summary>
    public const string System = "system";

    /// <summary>No confirmation at all, tray beat included.</summary>
    public const string Off = "off";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        System => System,
        Off => Off,
        _ => InApp,
    };

    public static string For(NotifiedAction action, Settings settings) => Normalize(action switch
    {
        NotifiedAction.Ocr => settings.NotifyStyleOcr,
        NotifiedAction.Copy => settings.NotifyStyleCopy,
        _ => settings.NotifyStyleSave,
    });
}
