using System.Text;
using System.Windows;
using WinShot.Ocr;

namespace WinShot.Editor;

/// <summary>One word the OCR engine found in the picture, in canvas (source-pixel) space.</summary>
internal sealed record TextWord(string Text, Rect Bounds, int Line);

/// <summary>
/// The rules behind the editor's Text select (Survey's Shift+V "Text Select", which picks a
/// PDF's own words off the page; here the words come from OCR). It works the way selecting
/// text works everywhere else: press, drag, and every word in reading order between the two
/// ends is picked, whole lines in between included. Double-click picks a word, triple-click
/// a line. Pure maths, so the tests can pin every rule without a window.
/// </summary>
internal static class ImageTextSelection
{
    /// <summary>A press this close to a word's box (canvas px) counts as on it, for the I-beam.</summary>
    public const double HitPad = 3;

    /// <summary>
    /// Turns the engine's words into canvas words, grouped by line in reading order. The
    /// engine already reads top to bottom; this only guards against a line coming back split
    /// so that every line's words sit next to each other, which the range rules rely on.
    /// </summary>
    public static IReadOnlyList<TextWord> FromOcr(IEnumerable<OcrTextWord> words) =>
        words
            .Select((w, i) => (Word: w, Order: i))
            .OrderBy(x => x.Word.Line)
            .ThenBy(x => x.Order)
            .Select(x => new TextWord(x.Word.Text,
                new Rect(x.Word.Box.X, x.Word.Box.Y, x.Word.Box.Width, x.Word.Box.Height),
                x.Word.Line))
            .ToArray();

    /// <summary>Each line as its first and last word and the band its words cover.</summary>
    public static IReadOnlyList<(int First, int Last, Rect Band)> Lines(IReadOnlyList<TextWord> words)
    {
        var lines = new List<(int First, int Last, Rect Band)>();
        int i = 0;
        while (i < words.Count)
        {
            int first = i;
            Rect band = words[i].Bounds;
            while (i + 1 < words.Count && words[i + 1].Line == words[first].Line)
            {
                i++;
                band.Union(words[i].Bounds);
            }
            lines.Add((first, i, band));
            i++;
        }
        return lines;
    }

    /// <summary>The word whose (padded) box holds <paramref name="p"/>, or -1.</summary>
    public static int Hit(IReadOnlyList<TextWord> words, Point p, double pad = HitPad)
    {
        for (int i = 0; i < words.Count; i++)
        {
            Rect r = words[i].Bounds;
            r.Inflate(pad, pad);
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    /// <summary>
    /// The word a drag end at <paramref name="p"/> lands on, the way a text editor reads the
    /// pointer: first the nearest line (by height; when two lines sit level, as two columns
    /// do, the one nearer sideways), then the nearest word along it. A pointer above every
    /// line still lands on the first line and one below on the last, so dragging off the
    /// text keeps the selection growing instead of dropping it. -1 only when there are no words.
    /// </summary>
    public static int Nearest(IReadOnlyList<TextWord> words, Point p)
    {
        if (words.Count == 0) return -1;
        var lines = Lines(words);

        double bestDy = double.PositiveInfinity;
        foreach (var line in lines)
            bestDy = Math.Min(bestDy, Distance(p.Y, line.Band.Top, line.Band.Bottom));

        int bestLine = -1;
        double bestDx = double.PositiveInfinity;
        for (int l = 0; l < lines.Count; l++)
        {
            Rect band = lines[l].Band;
            if (Distance(p.Y, band.Top, band.Bottom) > bestDy + 0.5) continue;
            double dx = Distance(p.X, band.Left, band.Right);
            if (dx < bestDx)
            {
                bestDx = dx;
                bestLine = l;
            }
        }

        var (first, last, _) = lines[bestLine];
        int best = first;
        double bestWordDx = double.PositiveInfinity;
        for (int i = first; i <= last; i++)
        {
            double dx = Distance(p.X, words[i].Bounds.Left, words[i].Bounds.Right);
            if (dx < bestWordDx)
            {
                bestWordDx = dx;
                best = i;
            }
        }
        return best;
    }

    /// <summary>The picked span between a drag's two ends, low end first. Null when either end is unset.</summary>
    public static (int Start, int End)? Range(int anchor, int focus) =>
        anchor < 0 || focus < 0 ? null : (Math.Min(anchor, focus), Math.Max(anchor, focus));

    /// <summary>The first and last word of the line word <paramref name="index"/> sits on (triple-click).</summary>
    public static (int Start, int End) LineOf(IReadOnlyList<TextWord> words, int index)
    {
        int start = index, end = index;
        while (start > 0 && words[start - 1].Line == words[index].Line) start--;
        while (end + 1 < words.Count && words[end + 1].Line == words[index].Line) end++;
        return (start, end);
    }

    /// <summary>
    /// The text of words <paramref name="start"/> to <paramref name="end"/>: a space between
    /// words on a line (none between two Chinese or Japanese characters, which the engine
    /// hands back one per "word"), a line break between lines — or a space when the user
    /// has asked Settings to join lines, the same choice every other text grab follows.
    /// </summary>
    public static string TextFor(IReadOnlyList<TextWord> words, int start, int end, bool joinLines = false)
    {
        if (words.Count == 0) return string.Empty;
        start = Math.Clamp(start, 0, words.Count - 1);
        end = Math.Clamp(end, 0, words.Count - 1);
        var sb = new StringBuilder();
        for (int i = start; i <= end; i++)
        {
            if (i > start)
            {
                if (words[i].Line != words[i - 1].Line)
                    sb.Append(joinLines ? " " : "\r\n");
                else if (NeedsSpace(words[i - 1].Text, words[i].Text))
                    sb.Append(' ');
            }
            sb.Append(words[i].Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// What to paint as picked: one box per line, from the first picked word on it to the
    /// last, so the gaps between words are covered the way a text selection covers spaces.
    /// </summary>
    public static IReadOnlyList<Rect> Bands(IReadOnlyList<TextWord> words, int start, int end)
    {
        var bands = new List<Rect>();
        if (words.Count == 0 || start > end) return bands;
        start = Math.Clamp(start, 0, words.Count - 1);
        end = Math.Clamp(end, 0, words.Count - 1);
        Rect current = words[start].Bounds;
        for (int i = start + 1; i <= end; i++)
        {
            if (words[i].Line != words[i - 1].Line)
            {
                bands.Add(current);
                current = words[i].Bounds;
            }
            else
            {
                current.Union(words[i].Bounds);
            }
        }
        bands.Add(current);
        return bands;
    }

    internal static bool NeedsSpace(string before, string after) =>
        before.Length == 0 || after.Length == 0 || !(IsCjk(before[^1]) && IsCjk(after[0]));

    /// <summary>Han, kana and full-width forms: scripts written without spaces. Hangul is not — Korean spaces its words.</summary>
    private static bool IsCjk(char c) =>
        c is >= '　' and <= 'ヿ'
            or >= '㐀' and <= '䶿'
            or >= '一' and <= '鿿'
            or >= '豈' and <= '﫿'
            or >= '＀' and <= '￯';

    private static double Distance(double v, double low, double high) =>
        v < low ? low - v : v > high ? v - high : 0;
}
