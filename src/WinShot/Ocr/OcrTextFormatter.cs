namespace WinShot.Ocr;

public static class OcrTextFormatter
{
    public static string Format(IEnumerable<string> lines, bool joinLines)
    {
        string text = joinLines
            ? string.Join(" ", lines)
            : string.Join(Environment.NewLine, lines);

        return text.Trim();
    }
}
