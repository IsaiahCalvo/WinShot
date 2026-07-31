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
        "Rectangle",
        "FilledRectangle",
        "Ellipse",
        "Line",
        "Arrow",
        "Text",
        "Pixelate",
        "Spotlight",
        "Step",
        "Freehand",
        "Highlighter",
    };

    public static readonly string[] MoreToolOrder =
    {
        "Pan",
        "CurvedArrow",
        "Blur",
        "Eyedropper",
    };

    // 3 leading actions + 12 primary tools + Emoji/More + Save/Done and separators,
    // using the shared 36-DIP controls and margins from Theme.xaml.
    public const double PrimaryToolbarLogicalWidth = 914;
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
        EditorTool.Line or EditorTool.CurvedArrow or EditorTool.Freehand or EditorTool.Highlighter =>
            EditorContextControls.Color | EditorContextControls.Thickness | EditorContextControls.Opacity,
        EditorTool.Arrow =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.ArrowStyle | EditorContextControls.Opacity,
        EditorTool.Text =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.TextStyle | EditorContextControls.Opacity,
        EditorTool.Step =>
            EditorContextControls.Color | EditorContextControls.Thickness |
            EditorContextControls.StepMode | EditorContextControls.Opacity,
        EditorTool.Blur or EditorTool.Pixelate => EditorContextControls.EffectStrength,
        EditorTool.Crop => EditorContextControls.CropRatio,
        _ => EditorContextControls.None,
    };
}
