namespace WinShot.Editor;

public enum EditorTool
{
    Select,
    Pan,
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Freehand,
    Highlighter,
    Text,
    Step,
    Crop,
    Eyedropper,

    /// <summary>
    /// The one redaction tool. Pixelate / Blur (secure) / Blur (smooth) / Black Out are
    /// styles on the mark it draws, not four separate tools, and the mark stays editable.
    /// </summary>
    Redact,
    Spotlight,
    CurvedArrow,
    Emoji,
}

/// <summary>Fill applied to newly drawn rectangles and ellipses.</summary>
public enum ShapeFillMode
{
    None,
    Quarter, // 25% opacity of the stroke color
    Solid,
}

/// <summary>Visual style applied to newly committed text annotations.</summary>
public enum TextStyle
{
    Plain,
    Bold,
    Outline, // white stroke around the glyphs
    Pill,    // rounded dark background behind the text
    Huge,    // plain look at roughly double size
}
