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

/// <summary>
/// What happens after a screenshot — a <b>set</b>, not a choice. Overlay AND copy AND save
/// AND annotate AND pin can all be on at once; that is the point of the after-capture matrix
/// and the reason the old single-choice combo was the wrong shape.
/// </summary>
/// <remarks>
/// The order the fields are declared in is the order the app runs them, and it matters:
/// clipboard and disk first (they finish and let go of their copy), then the windows that
/// stay open, and the Quick Access card last so it lands on top of anything else that opened.
/// </remarks>
public readonly record struct PostCaptureSet(
    bool Copy,
    bool Save,
    bool Annotate,
    bool Pin,
    bool Overlay)
{
    /// <summary>Nothing selected. The capture still reaches history; the user just asked
    /// for no surface — so the caller shows the card anyway rather than swallowing the shot.</summary>
    public bool IsEmpty => !Copy && !Save && !Annotate && !Pin && !Overlay;

    /// <summary>How many owners need their own copy of the bitmap.</summary>
    public int Count => (Copy ? 1 : 0) + (Save ? 1 : 0) + (Annotate ? 1 : 0) + (Pin ? 1 : 0) + (Overlay ? 1 : 0);

    public static PostCaptureSet ForScreenshot(Settings settings) => new(
        Copy: settings.ScreenshotCopy,
        Save: settings.ScreenshotSave,
        Annotate: settings.ScreenshotOpenAnnotate,
        Pin: settings.ScreenshotPin,
        Overlay: settings.ScreenshotShowOverlay);

    /// <summary>
    /// One action, for a per-invocation override — the tray's "Capture window with
    /// background", a <c>winshot://…?then=copy</c> URL, or a flow that has already decided.
    /// <see cref="PostCaptureAction.Background"/> has no matrix row and is handled by the
    /// caller; it maps to nothing here.
    /// </summary>
    /// <param name="alsoCopy">Carries the user's "copy every screenshot" preference through
    /// an override, which is what the single-action path always did.</param>
    public static PostCaptureSet Single(string? action, bool alsoCopy = false) =>
        PostCaptureAction.Normalize(action) switch
        {
            PostCaptureAction.Copy => new PostCaptureSet(true, false, false, false, false),
            PostCaptureAction.Save => new PostCaptureSet(alsoCopy, true, false, false, false),
            PostCaptureAction.Edit => new PostCaptureSet(alsoCopy, false, true, false, false),
            PostCaptureAction.Pin => new PostCaptureSet(alsoCopy, false, false, true, false),
            _ => new PostCaptureSet(alsoCopy, false, false, false, true),
        };

    /// <summary>Parses a <c>?then=copy,save,edit,pin,thumbnail</c> list. Unknown words are
    /// ignored; an empty or unusable list returns null so the caller keeps the user's setting.</summary>
    public static PostCaptureSet? TryParseThen(string? thenList)
    {
        if (string.IsNullOrWhiteSpace(thenList))
            return null;

        bool copy = false, save = false, annotate = false, pin = false, overlay = false, any = false;
        foreach (string raw in thenList.Split([',', '+', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "copy": copy = any = true; break;
                case "save": save = any = true; break;
                case "edit" or "annotate": annotate = any = true; break;
                case "pin": pin = any = true; break;
                // "thumbnail" is Shottr's name for the Quick Access card.
                case "thumbnail" or "overlay": overlay = any = true; break;
            }
        }
        return any ? new PostCaptureSet(copy, save, annotate, pin, overlay) : null;
    }
}

/// <summary>
/// One-time migration from the single-choice "After screenshot" combo to the after-capture
/// matrix. The old value is kept in the settings file as the seed, so a user who had
/// "Save to export location" selected keeps saving after upgrading.
/// </summary>
public static class PostCaptureMigration
{
    /// <summary>Schema version this migration produces.</summary>
    public const int MatrixSchemaVersion = 1;

    /// <summary>
    /// Returns true when it changed something — which <see cref="SettingsService.Load"/> only
    /// logs. Nothing persists the bumped schema version on its own: the first ordinary Save()
    /// carries it, and until one happens the migration simply re-derives the same matrix from
    /// the same seeds on the next launch. Idempotent by construction, so a session that never
    /// saves costs nothing.
    /// </summary>
    public static bool Apply(Settings s)
    {
        if (s.SettingsSchemaVersion >= MatrixSchemaVersion)
            return false;

        string action = PostCaptureAction.Normalize(s.PostCaptureAction);
        s.ScreenshotCopy = action == PostCaptureAction.Copy || s.AutoCopyToClipboard;
        s.ScreenshotSave = action == PostCaptureAction.Save;
        s.ScreenshotOpenAnnotate = action == PostCaptureAction.Edit;
        s.ScreenshotPin = action == PostCaptureAction.Pin;
        // "Add background" has no matrix row — it stays a per-invocation action (the tray's
        // "Capture window with background"). Someone who had it as their default lands on the
        // Quick Access card, which carries the same button.
        s.ScreenshotShowOverlay = action is PostCaptureAction.Overlay or PostCaptureAction.Background;

        // The Recording column absorbed the duplicate "Open video editor after recording"
        // checkbox that used to live on the Recording pane.
        s.RecordingOpenEditor = s.RecordingOpenEditor || s.OpenVideoEditorAfterRecording;

        s.SettingsSchemaVersion = MatrixSchemaVersion;
        return true;
    }
}
