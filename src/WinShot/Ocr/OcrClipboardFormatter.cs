using System.Text;

namespace WinShot.Ocr;

public sealed record OcrClipboardPayload(string ClipboardText, string BalloonTitle, string Preview);

public static class OcrClipboardFormatter
{
    private const int PreviewLimit = 80;

    public static OcrClipboardPayload? Build(OcrCaptureResult result)
    {
        var text = BuildClipboardText(result);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return new OcrClipboardPayload(
            text,
            BuildTitle(result),
            BuildPreview(text, PreviewLimit));
    }

    private static string BuildClipboardText(OcrCaptureResult result) =>
        Combine(result.Text, result.QrCodes);

    /// <summary>
    /// What actually goes on the clipboard: the recognized text, then any decoded payloads
    /// underneath. Public because the result toast re-runs the line-break formatter over the
    /// raw lines and has to put the codes back -- re-copying the text alone silently dropped
    /// the QR payload the user could still see quoted in the toast.
    /// </summary>
    public static string Combine(string? text, IReadOnlyList<string> qrCodes)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(text))
            sb.Append(text.Trim());

        if (qrCodes.Count > 0)
        {
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();
            sb.Append(string.Join(Environment.NewLine, qrCodes));
        }

        return sb.ToString();
    }

    private static string BuildTitle(OcrCaptureResult result)
    {
        bool hasText = !string.IsNullOrWhiteSpace(result.Text);
        int codeCount = result.QrCodes.Count;

        if (hasText && codeCount > 0)
            return codeCount == 1 ? "Text + QR code copied" : "Text + codes copied";
        if (codeCount > 0)
            return codeCount == 1 ? "QR code copied" : "Codes copied";
        return "Text copied to clipboard";
    }

    private static string BuildPreview(string text, int limit)
    {
        if (limit <= 0 || text.Length <= limit)
            return text;

        return text[..limit] + "...";
    }
}
