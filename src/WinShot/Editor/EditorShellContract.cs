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
    EffectStrength = 1 << 5,
    TextStyle = 1 << 6,
    CropRatio = 1 << 7,
    StepMode = 1 << 8,
    LineStyle = 1 << 9,
    FillStrokeTabs = 1 << 10,
}

/// <summary>
/// The verified CleanShot-derived hierarchy expressed with WinShot tool names. It drives
/// contextual visibility and gives tests a stable order/layout contract without coupling to glyphs.
/// </summary>
internal static class EditorShellContract
{
    public static readonly string[] PrimaryToolOrder =
    {
        "Select",
        "Draw",
        "Shape",
        "Text",
        "Pixelate",
        "Spotlight",
        "Step",
    };

    public static readonly string[] DrawGroupTools = { "Freehand", "Highlighter" };
    public static readonly string[] ShapeGroupTools = { "Rectangle", "FilledRectangle", "Ellipse", "Line", "Arrow" };
    public static readonly string[] TextGroupTools = { "Text", "Callout" };

    public static readonly string[] MoreToolOrder =
    {
        "Pan",
        "CurvedArrow",
        "Blur",
        "Eyedropper",
    };

    // 3 leading actions + grouped annotation tools + Pixelate/Spotlight/Step + More + Save/Done.
    // Groups collapse eight individual 36-DIP radios into three, so the 980-DIP minimum still fits.
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
            EditorContextControls.Color | EditorContextControls.Thickness | EditorContextControls.Fill |
            EditorContextControls.Opacity | EditorContextControls.FillStrokeTabs | EditorContextControls.LineStyle,
        EditorTool.Rectangle or EditorTool.Ellipse =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.Fill | EditorContextControls.Opacity |
            EditorContextControls.FillStrokeTabs | EditorContextControls.LineStyle,
        EditorTool.Line or EditorTool.CurvedArrow or EditorTool.Freehand or EditorTool.Highlighter =>
            EditorContextControls.Color | EditorContextControls.Thickness | EditorContextControls.Opacity |
            (tool is EditorTool.Line or EditorTool.CurvedArrow ? EditorContextControls.LineStyle : 0),
        EditorTool.Arrow =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.ArrowStyle | EditorContextControls.Opacity | EditorContextControls.LineStyle,
        EditorTool.Text =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.TextStyle | EditorContextControls.Opacity |
            EditorContextControls.FillStrokeTabs | EditorContextControls.LineStyle,
        EditorTool.Callout =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.ArrowStyle | EditorContextControls.Opacity |
            EditorContextControls.FillStrokeTabs | EditorContextControls.LineStyle | EditorContextControls.TextStyle,
        EditorTool.Step =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.StepMode | EditorContextControls.Opacity,
        EditorTool.Blur or EditorTool.Pixelate => EditorContextControls.EffectStrength,
        EditorTool.Crop => EditorContextControls.CropRatio,
        _ => EditorContextControls.None,
    };

    public static string GroupFor(EditorTool tool, bool filledRectangle) =>
        tool switch
        {
            EditorTool.Freehand or EditorTool.Highlighter => "Draw",
            EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow => "Shape",
            EditorTool.Text or EditorTool.Callout => "Text",
            _ => "",
        };
}
