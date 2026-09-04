using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// The user's own palette, most recent first. Every editor session used to start from the
/// same hard-coded red because the custom colour was a field on the window and nothing ever
/// wrote it anywhere -- so a colour matched by eyedropper survived exactly as long as the
/// window did. Pure list arithmetic; the persistence is Settings' job and the rendering is
/// the picker's.
/// </summary>
public static class SavedColors
{
    /// <summary>Ten slots, which is what fits one row of the picker without it resizing.</summary>
    public const int MaxCount = 10;

    /// <summary>
    /// The list with <paramref name="color"/> at the head. A colour already in the list moves
    /// there rather than appearing twice -- re-picking a saved colour is a vote for it, not a
    /// new entry -- and the oldest falls off the end at the cap.
    /// </summary>
    public static List<string> With(IEnumerable<string>? existing, SD.Color color)
    {
        string head = ColorFormatter.Format(color, ColorFormat.Hex);
        var result = new List<string>(MaxCount) { head };
        foreach (string entry in Sanitize(existing))
        {
            if (result.Count >= MaxCount) break;
            if (!string.Equals(entry, head, StringComparison.OrdinalIgnoreCase))
                result.Add(entry);
        }
        return result;
    }

    /// <summary>
    /// The stored list, normalised: unparseable and duplicate entries dropped, everything in
    /// one casing, capped. The settings file is user-editable and survives downgrades, so
    /// anything can be in there.
    /// </summary>
    public static List<string> Sanitize(IEnumerable<string>? stored)
    {
        var result = new List<string>(MaxCount);
        if (stored is null) return result;

        foreach (string? entry in stored)
        {
            if (result.Count >= MaxCount) break;
            if (!ColorFormatter.TryParse(entry, out SD.Color color)) continue;
            string hex = ColorFormatter.Format(color, ColorFormat.Hex);
            if (!result.Contains(hex, StringComparer.OrdinalIgnoreCase))
                result.Add(hex);
        }
        return result;
    }
}
