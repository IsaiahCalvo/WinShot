using System.Windows.Media;

namespace WinShot.Editor;

public static class ShapeFillBrush
{
    public static Brush? Create(ShapeFillMode mode, Color color) =>
        mode switch
        {
            ShapeFillMode.Quarter => new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)),
            ShapeFillMode.Solid => new SolidColorBrush(color),
            _ => null,
        };

    public static Brush? CreateFromName(string? mode, Color color) =>
        Enum.TryParse(mode, out ShapeFillMode fill)
            ? Create(fill, color)
            : null;
}
