using System.Text;

namespace WinShot.Ocr;

public sealed record OcrClipboardPayload(string ClipboardText, string BalloonTitle, string Preview);

public static class OcrClipboardFormatter
{
    public static OcrClipboardPayload? Build(OcrCaptureResult result, int previewLimit = 80)
    {
        var text = BuildClipboardText(result);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return new OcrClipboardPayload(
            text,
            BuildTitle(result),
            BuildPreview(text, previewLimit));
    }

    private static string BuildClipboardText(OcrCaptureResult result)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.Text))
            sb.Append(result.Text.Trim());

        if (result.QrCodes.Count > 0)
        {
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();
            sb.Append(string.Join(Environment.NewLine, result.QrCodes));
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
