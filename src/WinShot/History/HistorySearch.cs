using System.Globalization;

namespace WinShot.History;

/// <summary>
/// The History search box's matching rule, kept apart from the window so it can be tested on
/// plain strings. Every word typed has to turn up somewhere in the capture's text — its file
/// name, the app it came from, its date and size line, or what kind of capture it is — in any
/// order and any case. "chrome gif" finds a GIF recorded over Chrome; "nov 2" finds the captures
/// from that day.
/// </summary>
internal static class HistorySearch
{
    public static bool Matches(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        CompareInfo compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        foreach (string word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            bool found = false;
            foreach (string? field in fields)
            {
                if (!string.IsNullOrEmpty(field) && compare.IndexOf(field, word, options) >= 0)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }

    /// <summary>The words a capture's kind answers to, so typing "video" or "screenshot"
    /// works the same as the chips.</summary>
    public static string KindWords(bool isImage, bool isVideo, bool isGif, bool isScrolling) =>
        isGif ? "gif animation recording"
        : isVideo ? "video recording mp4"
        : isScrolling ? "scrolling screenshot image"
        : isImage ? "screenshot image picture"
        : string.Empty;
}
