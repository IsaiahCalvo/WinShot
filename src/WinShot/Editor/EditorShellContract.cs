namespace WinShot.Editor;

[Flags]
internal enum EditorContextControls
{
    None = 0,
    Color = 1 << 0,
    Thickness = 1 << 1,
    Fill = 1 << 2,
    Opacity = 1 << 3,
    ArrowStyle = 1 << 4,
    /// <summary>Which of the four ways a redaction hides what is under it.</summary>
    RedactStyle = 1 << 5,
    TextStyle = 1 << 6,
    CropRatio = 1 << 7,
    StepMode = 1 << 8,

    /// <summary>Rotate / flip / image size — the transforms that used to hide in "More".</summary>
    CropTools = 1 << 9,

    /// <summary>
    /// The continuous redaction strength. It HIDES for Black Out rather than greying out,
    /// so the toolbar shrinks to the controls that mean something.
    /// </summary>
    RedactStrength = 1 << 10,

    /// <summary>How much a magnifier callout blows the picture up.</summary>
    Magnification = 1 << 11,

    /// <summary>
    /// The outline's own two choices: the dash pattern, and the hand-drawn line quality.
    /// Every mark that owns an outline gets them — the shapes AND the arrows, which is the
    /// whole point: a sketchy rectangle next to a ruled arrow is not a hand-drawn diagram.
    /// </summary>
    Style = 1 << 12,
}

/// <summary>The five tool families the toolbar's category buttons switch between.</summary>
internal enum EditorToolCategory
{
    Draw,
    Shapes,
    Text,
    Redact,
    Image,
}

/// <summary>
/// The shape of the editor's toolbar, expressed with WinShot tool names. It drives
/// contextual visibility and the category sub-row, and gives tests a stable
/// order/layout contract without coupling to glyphs.
///
/// The toolbar is ONE row: undo/redo, the two pointer tools, five category buttons,
/// then the active tool's own controls, with Save as… / Done docked right. A
/// category's tools live in a 34 DIP sub-row that floats over the canvas, so
/// switching category never moves the image. Nothing is behind an overflow popup.
/// </summary>
internal static class EditorShellContract
{
    /// <summary>The two tools that are always visible, left of the categories.</summary>
    public static readonly string[] PointerToolOrder =
    {
        "Select",
        "Pan",
    };

    public static readonly EditorToolCategory[] CategoryOrder =
    {
        EditorToolCategory.Draw,
        EditorToolCategory.Shapes,
        EditorToolCategory.Text,
        EditorToolCategory.Redact,
        EditorToolCategory.Image,
    };

    /// <summary>
    /// The tools in each category's sub-row, in the order they appear. Survey's own
    /// groupings: draw = pen / highlighter (plus the eyedropper, which samples for
    /// them), shapes = rectangle / ellipse / line / arrow / counter.
    /// </summary>
    public static string[] ToolsIn(EditorToolCategory category) => category switch
    {
        EditorToolCategory.Draw => new[] { "Freehand", "Highlighter", "Eyedropper" },
        EditorToolCategory.Shapes => new[]
        {
            "Rectangle", "FilledRectangle", "Ellipse", "Line", "Arrow", "Step",
        },
        EditorToolCategory.Text => new[] { "Text" },
        EditorToolCategory.Redact => new[] { "Redact", "Spotlight", "Magnifier" },
        EditorToolCategory.Image => new[] { "Crop" },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// The category a tool belongs to, or null for the pointer tools and for
    /// CurvedArrow — which has no button of its own any more: it is the Curved entry
    /// in the arrow-style menu, so the Arrow button stays lit while it is active.
    /// </summary>
    public static EditorToolCategory? CategoryOf(EditorTool tool) => tool switch
    {
        EditorTool.Freehand or EditorTool.Highlighter or EditorTool.Eyedropper => EditorToolCategory.Draw,
        EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow
            or EditorTool.CurvedArrow or EditorTool.Step => EditorToolCategory.Shapes,
        EditorTool.Text => EditorToolCategory.Text,
        EditorTool.Redact or EditorTool.Spotlight or EditorTool.Magnifier => EditorToolCategory.Redact,
        EditorTool.Crop => EditorToolCategory.Image,
        _ => null,
    };

    /// <summary>
    /// The tool a category button arms when it is clicked. Image arms nothing: its row
    /// is mostly one-shot actions, and clicking it must not drop the user into a crop.
    /// </summary>
    public static EditorTool? DefaultToolFor(EditorToolCategory category) => category switch
    {
        EditorToolCategory.Draw => EditorTool.Freehand,
        EditorToolCategory.Shapes => EditorTool.Rectangle,
        EditorToolCategory.Text => EditorTool.Text,
        EditorToolCategory.Redact => EditorTool.Redact,
        _ => null,
    };

    /// <summary>
    /// The widest the one row ever gets, in DIP: undo/redo + the two pointer tools + the
    /// chevron + five categories + Save as… / Done, plus the fattest contextual group.
    /// That group is the CROP one (ratio, rotate x2, flip x2, image size, snap, and the
    /// Cancel / Apply crop pair docked right), which measures 1077 DIP — the number here is
    /// that measurement rounded up, not an estimate. <c>ThemedWindowTests</c> measures the
    /// real row for every tool and fails if any of them outgrows it, because a DockPanel
    /// does not wrap: it clips the right-hand end away in silence.
    /// </summary>
    public const double PrimaryToolbarLogicalWidth = 1080;

    /// <summary>
    /// The narrowest the editor window may be. It is a consequence of the number above, not
    /// a taste: one row that cannot wrap has to be given the width it needs. 1152 DIP is
    /// also exactly 90 % of a 1920 px display at 150 %, so the editor's own landing size on
    /// the most common Windows laptop already satisfies it.
    /// </summary>
    public const double MinimumEditorLogicalWidth = 1152;

    /// <summary>
    /// Everything between the window's width and the toolbar's content box, measured:
    /// the window frame, the toolbar's 12 DIP side margins, its 1 px border and its 8 DIP
    /// padding. At 980 DIP the row measured 922 DIP of usable width.
    /// </summary>
    public const double ToolbarOuterMargin = 58;

    /// <summary>
    /// Whether the one-row toolbar fits a window this wide. Both sides are LOGICAL, and
    /// that is the point: the previous form multiplied the available width and the required
    /// width by the same DPI scale, so the scale cancelled and the check could not tell
    /// 100 % from 200 %. What DPI actually decides is how wide a window a given MONITOR can
    /// hold, which is <see cref="FitsOnMonitor"/>.
    /// </summary>
    public static bool FitsPrimaryToolbar(double windowLogicalWidth)
    {
        if (!double.IsFinite(windowLogicalWidth)) return false;
        return Math.Max(0, windowLogicalWidth - ToolbarOuterMargin) >= PrimaryToolbarLogicalWidth;
    }

    /// <summary>
    /// Whether a monitor of this physical width at this scale can hold the editor at its
    /// minimum width. This is where the DPI scale belongs: a 1920 px display is 1920 DIP at
    /// 100 % and 1280 DIP at 150 %, and the toolbar has to fit the second one too.
    /// </summary>
    public static bool FitsOnMonitor(double monitorPhysicalWidth, double dpiScale)
    {
        if (!double.IsFinite(monitorPhysicalWidth) || !double.IsFinite(dpiScale) || dpiScale <= 0)
            return false;
        return monitorPhysicalWidth / dpiScale >= MinimumEditorLogicalWidth;
    }

    /// <summary>
    /// The controls the toolbar offers. Normally that follows the armed TOOL — the row
    /// describes what you are about to draw. The pointer is the exception: it draws
    /// nothing, so with it armed the row describes the mark you have PICKED instead, for
    /// the one property that has nowhere else to live. An image annotation's opacity is
    /// that property: every other kind carries its transparency in its colour's alpha, and
    /// an image has no colour.
    /// </summary>
    public static EditorContextControls ContextFor(
        EditorTool tool, bool filledRectangle, string? selectedType) =>
        tool == EditorTool.Select && selectedType == AnnotationData.TypeImage
            ? EditorContextControls.Opacity
            : ContextFor(tool, filledRectangle);

    public static EditorContextControls ContextFor(EditorTool tool, bool filledRectangle) => tool switch
    {
        EditorTool.Rectangle when filledRectangle =>
            EditorContextControls.Color | EditorContextControls.Fill |
            EditorContextControls.Style | EditorContextControls.Opacity,
        EditorTool.Rectangle or EditorTool.Ellipse =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.Fill | EditorContextControls.Style | EditorContextControls.Opacity,
        EditorTool.Line or EditorTool.Freehand or EditorTool.Highlighter =>
            EditorContextControls.Color | EditorContextControls.Thickness | EditorContextControls.Opacity,
        EditorTool.Arrow or EditorTool.CurvedArrow =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.ArrowStyle | EditorContextControls.Style |
            EditorContextControls.Opacity,
        EditorTool.Text =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.TextStyle | EditorContextControls.Opacity,
        EditorTool.Step =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.StepMode | EditorContextControls.Opacity,
        EditorTool.Redact => EditorContextControls.RedactStyle | EditorContextControls.RedactStrength,
        EditorTool.Magnifier =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.Magnification,
        EditorTool.Crop => EditorContextControls.CropRatio | EditorContextControls.CropTools,
        _ => EditorContextControls.None,
    };
}
