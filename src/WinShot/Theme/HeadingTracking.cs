using System.Globalization;
using System.Windows.Data;

namespace WinShot.Theme;

/// <summary>
/// Survey's popover section headings carry <c>letter-spacing: 0.065em</c>
/// (AnnotationSizeControl.css:78, AnnotationDropdown.css:77) — about 0.72px per character at
/// 11px, which is a visible part of the look. WPF has no LetterSpacing property, so interleave
/// U+200A HAIR SPACE, whose Arial advance is ~1/16 em ≈ 0.69px at that size.
/// </summary>
public sealed class HeadingTrackingConverter : IValueConverter
{
    /// <summary>Joining (not appending) omits tracking after the last character; both headings
    /// are left-aligned, so the missing trailing space is invisible.</summary>
    public static string Tracked(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : string.Join(' ', text.ToCharArray());

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Tracked(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
