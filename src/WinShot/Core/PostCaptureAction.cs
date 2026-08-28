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

    // Every direct action hands the bitmap to another owner that uses it immediately
    // (Copy/Save dispose it; Pin/Background paint it), so the history add must clone
    // on the caller thread before the handoff — a background clone raced the new
    // owner's LockBits/Dispose on the same single-accessor GDI+ bitmap.
    public static bool NeedsCallerThreadHistoryClone(string? action) =>
        IsDirectAction(Normalize(action));
}
