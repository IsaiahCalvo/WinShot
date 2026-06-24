namespace WinShot.Core;

public static class PostCaptureAction
{
    public const string Overlay = "overlay";
    public const string Copy = "copy";
    public const string Save = "save";
    public const string Edit = "edit";
    public const string Pin = "pin";
    public const string Background = "background";

    private static readonly HashSet<string> KnownActions =
    [
        Overlay,
        Copy,
        Save,
        Edit,
        Pin,
        Background,
    ];

    public static string Normalize(string? action) =>
        action is not null && KnownActions.Contains(action) ? action : Overlay;

    public static bool IsDirectAction(string? action) =>
        Normalize(action) is not Overlay;

    public static bool NeedsCallerThreadHistoryClone(string? action) =>
        Normalize(action) is Copy or Save;
}
