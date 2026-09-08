using System.Windows.Media;

namespace WinShot.Editor;

public static class ShapeFillBrush
{
    /// <param name="fillColor">
    /// The shape's own fill colour when one has been picked. Null derives the fill from
    /// the stroke, which is what every shape drawn before the picker grew a Fill tab did.
    /// </param>
    public static Brush? Create(ShapeFillMode mode, Color color, Color? fillColor = null)
    {
        Color c = fillColor ?? color;
        return mode switch
        {
            ShapeFillMode.Quarter => new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)),
            ShapeFillMode.Solid => new SolidColorBrush(c),
            _ => null,
        };
    }

    public static Brush? CreateFromName(string? mode, Color color, Color? fillColor = null) =>
        Enum.TryParse(mode, out ShapeFillMode fill)
            ? Create(fill, color, fillColor)
            : null;
}
