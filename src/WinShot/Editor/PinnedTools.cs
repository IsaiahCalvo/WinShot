using System.Globalization;
using WinShot.Core;

namespace WinShot.Editor;

/// <summary>A tool a pin can hold: its pin kind, the tool it arms, and how it is shown.</summary>
internal sealed record PinKind(string Id, EditorTool Tool, string Label, string Icon, LineBorderStyle? FixedLineStyle = null);

/// <summary>Where the dock sits on the canvas.</summary>
internal enum PinDockSide { Left, Right, Bottom }

/// <summary>
/// Survey's pinned tools (pinnedTools.js), the pure part. A dock of favourite tools, each pin a
/// tool WITH its own settings: three pens in three colours and widths are three pins (Drawboard's
/// Favourites toolbar is the model). A pin keeps what the toolbar shows for its tool — colour,
/// size, fill and line style — and only the fields that kind of tool really uses.
/// </summary>
internal static class PinnedTools
{
    /// <summary>The tools a pin can hold, in toolbar order. Cloud is a rectangle with the cloud line.</summary>
    public static readonly IReadOnlyList<PinKind> Kinds = new PinKind[]
    {
        new("pen", EditorTool.Freehand, "Pen", "SurveyIcon.Pen"),
        new("highlighter", EditorTool.Highlighter, "Highlighter", "SurveyIcon.Highlighter"),
        new("eraser", EditorTool.Eraser, "Eraser", "SurveyIcon.Eraser"),
        new("rect", EditorTool.Rectangle, "Rectangle", "SurveyIcon.Rect"),
        new("ellipse", EditorTool.Ellipse, "Ellipse", "SurveyIcon.Ellipse"),
        new("polygon", EditorTool.Polygon, "Polygon", "SurveyIcon.Polygon"),
        new("cloud", EditorTool.Rectangle, "Cloud", "SurveyIcon.Rect", LineBorderStyle.Cloud),
        new("polyline", EditorTool.Polyline, "Polyline", "SurveyIcon.Polyline"),
        new("line", EditorTool.Line, "Line", "SurveyIcon.Line"),
        new("arrow", EditorTool.Arrow, "Arrow", "SurveyIcon.Arrow"),
        new("counter", EditorTool.Step, "Counter", "SurveyIcon.Counter"),
        new("text", EditorTool.Text, "Text box", "SurveyIcon.TextBox"),
        new("callout", EditorTool.Callout, "Callout", "SurveyIcon.Callout"),
    };

    private static readonly HashSet<EditorTool> LineStyleTools = new()
    {
        EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Polygon, EditorTool.Polyline,
        EditorTool.Line, EditorTool.Arrow, EditorTool.Callout, EditorTool.Text,
    };
    private static readonly HashSet<EditorTool> CloudTools = new()
    {
        EditorTool.Ellipse, EditorTool.Polygon, EditorTool.Callout, EditorTool.Text,
    };
    private static readonly HashSet<EditorTool> FillTools = new()
    {
        EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Polygon, EditorTool.Callout, EditorTool.Step,
    };

    public static PinKind? Kind(string? id) => Kinds.FirstOrDefault(k => k.Id == id);

    /// <summary>The groups the "+" menu lists pinnable tools under, in Survey's order.</summary>
    public static readonly string[] MenuGroups = { "Draw", "Shapes", "Text" };

    /// <summary>The toolbar group a pin kind's tool belongs to (Draw, Shapes or Text).</summary>
    public static string Group(PinKind kind) => EditorShellContract.GroupFor(kind.Tool);

    /// <summary>The pin kind for an armed tool and line style (Rectangle + Cloud = the Cloud pin).</summary>
    public static PinKind? KindForTool(EditorTool tool, LineBorderStyle lineStyle)
    {
        if (tool == EditorTool.Rectangle && lineStyle == LineBorderStyle.Cloud) return Kind("cloud");
        return Kinds.FirstOrDefault(k => k.Tool == tool && k.FixedLineStyle is null);
    }

    /// <summary>
    /// A pin from the toolbar's current settings, keeping only the fields its kind uses. The
    /// eraser keeps a size and nothing else; a line or arrow is never clouded.
    /// </summary>
    public static PinnedToolSetting Make(PinKind kind, string strokeHex, double strokeOpacity, double width,
        string? fillHex, double fillOpacity, LineBorderStyle lineStyle)
    {
        var pin = new PinnedToolSetting { Kind = kind.Id };
        if (kind.Tool == EditorTool.Eraser)
        {
            pin.StrokeWidth = Math.Round(Math.Clamp(width, 1, 100));
            return pin;
        }
        pin.StrokeColor = strokeHex;
        pin.StrokeOpacity = Math.Round(Math.Clamp(strokeOpacity, 0, 100), 2);
        pin.StrokeWidth = Math.Round(Math.Clamp(width, 0.1, 200), 2);
        if (FillTools.Contains(kind.Tool) && fillHex is not null)
        {
            pin.FillColor = fillHex;
            pin.FillOpacity = Math.Round(Math.Clamp(fillOpacity, 0, 100), 2);
        }
        if (kind.FixedLineStyle is LineBorderStyle fixedStyle)
            pin.LineStyle = Name(fixedStyle);
        else if (LineStyleTools.Contains(kind.Tool))
            pin.LineStyle = lineStyle == LineBorderStyle.Cloud && !CloudTools.Contains(kind.Tool)
                ? Name(LineBorderStyle.Solid)
                : Name(lineStyle);
        return pin;
    }

    /// <summary>Whether two pins hold the same tool and settings (pinning the same thing twice is a no-op).</summary>
    public static bool SameAs(PinnedToolSetting a, PinnedToolSetting b) =>
        a.Kind == b.Kind
        && string.Equals(a.StrokeColor, b.StrokeColor, StringComparison.OrdinalIgnoreCase)
        && a.StrokeOpacity == b.StrokeOpacity
        && a.StrokeWidth == b.StrokeWidth
        && string.Equals(a.FillColor, b.FillColor, StringComparison.OrdinalIgnoreCase)
        && a.FillOpacity == b.FillOpacity
        && a.LineStyle == b.LineStyle;

    /// <summary>The small number in the pin's corner: its width, as Drawboard shows it.</summary>
    public static string Badge(PinnedToolSetting pin) =>
        pin.StrokeWidth is double w ? w.ToString("0.##", CultureInfo.InvariantCulture) : "";

    /// <summary>Plain words for a pin: "Pen, #FF0000, 3".</summary>
    public static string Describe(PinnedToolSetting pin)
    {
        if (Kind(pin.Kind) is not PinKind kind) return "";
        var parts = new List<string> { kind.Label };
        if (pin.StrokeColor is string c) parts.Add(c);
        string badge = Badge(pin);
        if (badge.Length > 0) parts.Add(badge);
        if (pin.LineStyle is string s && s != "solid" && kind.FixedLineStyle is null) parts.Add(s);
        return string.Join(", ", parts);
    }

    public static string Name(LineBorderStyle style) => style.ToString().ToLowerInvariant();

    /// <summary>The side nearest a point dropped in a viewer of the given size (the grip drag).</summary>
    public static PinDockSide NearestSide(double x, double y, double width, double height)
    {
        double left = x, right = width - x, bottom = height - y;
        if (bottom <= left && bottom <= right) return PinDockSide.Bottom;
        return left <= right ? PinDockSide.Left : PinDockSide.Right;
    }

    public static PinDockSide ParseSide(string? text) =>
        Enum.TryParse(text, ignoreCase: true, out PinDockSide side) ? side : PinDockSide.Left;
}
