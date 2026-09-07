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

    // Undo/redo + 2 pointer tools + the chevron + 5 categories + the widest contextual
    // group (colour well, size field, fill trio, opacity) + Save/Done and separators,
    // using the shared 36-DIP controls and margins from Theme.xaml.
    public const double PrimaryToolbarLogicalWidth = 872;
    public const double MinimumEditorLogicalWidth = 980;
    public const double ToolbarOuterMargin = 24;

    public static bool FitsPrimaryToolbar(double windowLogicalWidth, double dpiScale)
    {
        if (!double.IsFinite(windowLogicalWidth) || !double.IsFinite(dpiScale) || dpiScale <= 0)
            return false;
        double availablePhysical = Math.Max(0, windowLogicalWidth - ToolbarOuterMargin) * dpiScale;
        double requiredPhysical = PrimaryToolbarLogicalWidth * dpiScale;
        return availablePhysical >= requiredPhysical;
    }

    public static EditorContextControls ContextFor(EditorTool tool, bool filledRectangle) => tool switch
    {
        EditorTool.Rectangle when filledRectangle =>
            EditorContextControls.Color | EditorContextControls.Fill | EditorContextControls.Opacity,
        EditorTool.Rectangle or EditorTool.Ellipse =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.Fill | EditorContextControls.Opacity,
        EditorTool.Line or EditorTool.Freehand or EditorTool.Highlighter =>
            EditorContextControls.Color | EditorContextControls.Thickness | EditorContextControls.Opacity,
        EditorTool.Arrow or EditorTool.CurvedArrow =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.ArrowStyle | EditorContextControls.Opacity,
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
