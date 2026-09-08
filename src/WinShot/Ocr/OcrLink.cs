namespace WinShot.Ocr;

/// <summary>
/// Whether an OCR result is a link the user might want opened. A decoded QR code is
/// usually a URL, and offering to open it is the difference between "copied" and "done" --
/// but only for a whole, bare http(s) URL: anything else is text that happens to contain
/// one, and turning a paragraph into a navigation offer is how a scanner becomes a hazard.
/// </summary>
public static class OcrLink
{
    /// <summary>
    /// The link to offer for <paramref name="clipboardText"/>, or null when there is nothing
    /// to offer. <paramref name="detectLinks"/> is the user's standing answer (Settings ▸ OCR,
    /// and the toast's own "Don't detect links"); false means never ask.
    /// </summary>
    public static Uri? Detect(string? clipboardText, bool detectLinks)
    {
        if (!detectLinks) return null;

        string text = clipboardText?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;

        // A single token. Text with a URL in the middle of it is text, and OCR of a page
        // full of links must not turn into an offer to open whichever one came first.
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
            return null;

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }
}
