using SD = System.Drawing;

namespace WinShot.Capture;

public static class PreviousRegion
{
    public static bool TryParse(string? text, out SD.Rectangle rect)
    {
        rect = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Split(',');
        if (parts.Length != 4)
            return false;

        if (!int.TryParse(parts[0].Trim(), out int x) ||
            !int.TryParse(parts[1].Trim(), out int y) ||
            !int.TryParse(parts[2].Trim(), out int width) ||
            !int.TryParse(parts[3].Trim(), out int height))
            return false;

        if (width < 1 || height < 1)
            return false;

        rect = new SD.Rectangle(x, y, width, height);
        return true;
    }

    public static string Format(SD.Rectangle screenPx) =>
        $"{screenPx.X},{screenPx.Y},{screenPx.Width},{screenPx.Height}";
}
