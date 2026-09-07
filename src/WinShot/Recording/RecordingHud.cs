using WinShot.Core;
using SD = System.Drawing;


namespace WinShot.Recording;

/// <summary>Every hit target on the recording HUD, in visual order.</summary>
public enum RecordingHudCell
{
    Dimensions,
    Aspect,
    Microphone,
    SystemAudio,
    Webcam,
    Clicks,
    Keystrokes,
    RecordGif,
    RecordVideo,
}

/// <summary>The five toggle cells on the settings card.</summary>
public readonly record struct RecordingHudToggles(
    bool Microphone,
    bool SystemAudio,
    bool Webcam,
    bool Clicks,
    bool Keystrokes)
{
    public static RecordingHudToggles FromSettings(Settings settings) => new(
        settings.RecordAudio,
        settings.RecordSystemAudio,
        !string.Equals(
            RecordingOptions.NormalizeWebcamPosition(settings.WebcamOverlayPosition),
            RecordingOptions.WebcamOff,
            StringComparison.Ordinal),
        settings.ShowClickHighlights,
        settings.ShowKeystrokes);

    public bool this[RecordingHudCell cell] => cell switch
    {
        RecordingHudCell.Microphone => Microphone,
        RecordingHudCell.SystemAudio => SystemAudio,
        RecordingHudCell.Webcam => Webcam,
        RecordingHudCell.Clicks => Clicks,
        RecordingHudCell.Keystrokes => Keystrokes,
        _ => false,
    };

    public RecordingHudToggles Toggled(RecordingHudCell cell) => cell switch
    {
        RecordingHudCell.Microphone => this with { Microphone = !Microphone },
        RecordingHudCell.SystemAudio => this with { SystemAudio = !SystemAudio },
        RecordingHudCell.Webcam => this with { Webcam = !Webcam },
        RecordingHudCell.Clicks => this with { Clicks = !Clicks },
        RecordingHudCell.Keystrokes => this with { Keystrokes = !Keystrokes },
        _ => this,
    };
}

/// <summary>What the HUD sent back: the format chosen and the toggles as they stood.</summary>
public readonly record struct RecordingHudChoice(bool IsGif, RecordingHudToggles Toggles);

public readonly record struct RecordingAspectPreset(string Label, double? Ratio);

/// <summary>
/// The aspect choices on the HUD. Freeform first, then the ratios a screen recording is
/// actually delivered in, landscape before portrait.
/// </summary>
public static class RecordingAspectPresets
{
    public const int FreeformIndex = 0;

    public static IReadOnlyList<RecordingAspectPreset> All { get; } =
    [
        new("Freeform", null),
        new("1:1", 1.0),
        new("5:4", 5.0 / 4),
        new("4:3", 4.0 / 3),
        new("7:5", 7.0 / 5),
        new("3:2", 3.0 / 2),
        new("16:10", 16.0 / 10),
        new("16:9", 16.0 / 9),
        new("1.85:1", 1.85),
        new("2.35:1", 2.35),
        new("2:3", 2.0 / 3),
        new("9:16", 9.0 / 16),
    ];

    /// <summary>
    /// The region corrected to <paramref name="ratio"/> about its own centre, then fitted
    /// inside <paramref name="bounds"/>. Width is kept where it can be; when the resulting
    /// height will not fit, the height is kept instead — so picking 9:16 on a wide selection
    /// gives a tall one that is still on screen rather than one clipped at the bottom.
    /// </summary>
    public static SD.Rectangle Apply(SD.Rectangle region, double ratio, SD.Rectangle bounds)
    {
        if (!double.IsFinite(ratio) || ratio <= 0 ||
            region.Width <= 0 || region.Height <= 0 ||
            bounds.Width < 2 || bounds.Height < 2)
        {
            return region;
        }

        int centreX = region.Left + (region.Width / 2);
        int centreY = region.Top + (region.Height / 2);

        int width = region.Width;
        int height = (int)Math.Round(width / ratio);
        if (height > bounds.Height || height < 2)
        {
            height = Math.Min(Math.Max(2, region.Height), bounds.Height);
            width = (int)Math.Round(height * ratio);
        }
        if (width > bounds.Width)
        {
            width = bounds.Width;
            height = (int)Math.Round(width / ratio);
        }

        width = Math.Clamp(width, 2, bounds.Width);
        height = Math.Clamp(height, 2, bounds.Height);
        int x = Math.Clamp(centreX - (width / 2), bounds.Left, Math.Max(bounds.Left, bounds.Right - width));
        int y = Math.Clamp(centreY - (height / 2), bounds.Top, Math.Max(bounds.Top, bounds.Bottom - height));
        return new SD.Rectangle(x, y, width, height);
    }
}

/// <summary>Where every part of the HUD sits, in screen pixels.</summary>
public sealed record RecordingHudGeometry(
    SD.Rectangle SettingsCard,
    SD.Rectangle ActionCard,
    IReadOnlyList<(RecordingHudCell Cell, SD.Rectangle Bounds)> Cells)
{
    /// <summary>Everything the HUD covers, for invalidation.</summary>
    public SD.Rectangle Bounds => SD.Rectangle.Union(SettingsCard, ActionCard);

    public RecordingHudCell? HitTest(SD.Point point)
    {
        foreach ((RecordingHudCell cell, SD.Rectangle bounds) in Cells)
        {
            if (bounds.Contains(point))
                return cell;
        }
        return null;
    }

    public SD.Rectangle CellBounds(RecordingHudCell cell)
    {
        foreach ((RecordingHudCell id, SD.Rectangle bounds) in Cells)
        {
            if (id == cell)
                return bounds;
        }
        return SD.Rectangle.Empty;
    }
}

/// <summary>
/// The two-card HUD that replaces the centred modal: a settings card of cells anchored to the
/// pending selection, and a detached action card offering GIF and video. Pure geometry, so
/// paint and hit-test can never disagree and every anchor case is testable without a screen.
/// </summary>
public static class RecordingHudLayout
{
    public const int SettingsHeightDip = 56;
    public const int ActionRowHeightDip = 34;
    public const int CardPadDip = 8;
    public const int CardGapDip = 10;
    public const int DimensionsWidthDip = 108;
    public const int AspectWidthDip = 92;
    public const int ToggleWidthDip = 48;
    public const int MenuRowHeightDip = 26;
    public const int MenuWidthDip = 120;

    /// <summary>Gap between the region and the HUD, and the inset from the monitor edge.</summary>
    public const int RegionGapDip = 12;

    // The HUD's three faces, in DIP. The cards are laid out by hand in device pixels, so the
    // fonts have to be too — a point size would scale a second time on a HiDPI monitor. The
    // label sits on the text scale (TextBody); the readout and the glyphs are deliberately
    // off it, sized to the cell boxes above rather than to a paragraph.
    public const int LabelFontDip = 12;
    public const int ValueFontDip = 15;
    public const int GlyphFontDip = 18;

    private static readonly RecordingHudCell[] Toggles =
    [
        RecordingHudCell.Microphone,
        RecordingHudCell.SystemAudio,
        RecordingHudCell.Webcam,
        RecordingHudCell.Clicks,
        RecordingHudCell.Keystrokes,
    ];

    public static SD.Size SettingsCardSize(double scale) => new(
        Px(CardPadDip, scale) + Px(DimensionsWidthDip, scale) + Px(AspectWidthDip, scale) +
        (Px(ToggleWidthDip, scale) * Toggles.Length) + Px(CardPadDip, scale),
        Px(SettingsHeightDip, scale));

    public static SD.Size BlockSize(double scale)
    {
        SD.Size settings = SettingsCardSize(scale);
        int actionHeight = Px(CardPadDip, scale) + (Px(ActionRowHeightDip, scale) * 2) + Px(CardPadDip, scale);
        return new SD.Size(settings.Width, settings.Height + Px(CardGapDip, scale) + actionHeight);
    }

    public static RecordingHudGeometry Build(SD.Rectangle region, SD.Rectangle monitor, double scale)
    {
        SD.Size block = BlockSize(scale);
        SD.Point origin = Place(region, monitor, block, Px(RegionGapDip, scale));
        return BuildAt(origin, scale);
    }

    /// <summary>Lays the cards out from an explicit origin, so a caller that is animating the
    /// HUD into place can drive the geometry directly.</summary>
    public static RecordingHudGeometry BuildAt(SD.Point origin, double scale)
    {
        SD.Size settingsSize = SettingsCardSize(scale);
        var settings = new SD.Rectangle(origin, settingsSize);
        var cells = new List<(RecordingHudCell, SD.Rectangle)>(9);

        int pad = Px(CardPadDip, scale);
        int x = settings.Left + pad;
        var dimensions = new SD.Rectangle(x, settings.Top, Px(DimensionsWidthDip, scale), settings.Height);
        cells.Add((RecordingHudCell.Dimensions, dimensions));
        x = dimensions.Right;

        var aspect = new SD.Rectangle(x, settings.Top, Px(AspectWidthDip, scale), settings.Height);
        cells.Add((RecordingHudCell.Aspect, aspect));
        x = aspect.Right;

        int toggleWidth = Px(ToggleWidthDip, scale);
        foreach (RecordingHudCell toggle in Toggles)
        {
            cells.Add((toggle, new SD.Rectangle(x, settings.Top, toggleWidth, settings.Height)));
            x += toggleWidth;
        }

        int rowHeight = Px(ActionRowHeightDip, scale);
        var action = new SD.Rectangle(
            settings.Left,
            settings.Bottom + Px(CardGapDip, scale),
            settings.Width,
            pad + (rowHeight * 2) + pad);
        cells.Add((RecordingHudCell.RecordGif,
            new SD.Rectangle(action.Left + pad, action.Top + pad, action.Width - (pad * 2), rowHeight)));
        cells.Add((RecordingHudCell.RecordVideo,
            new SD.Rectangle(action.Left + pad, action.Top + pad + rowHeight, action.Width - (pad * 2), rowHeight)));

        return new RecordingHudGeometry(settings, action, cells);
    }

    /// <summary>
    /// Top-left of the whole block: centred on the region and below it, flipped above when it
    /// would run off the monitor, tucked inside the region when it spans the full height.
    /// Always clamped on-monitor. Same rule as the Cancel/Done bar, so the two never disagree
    /// about where "below the selection" is.
    /// </summary>
    public static SD.Point Place(SD.Rectangle region, SD.Rectangle monitor, SD.Size block, int gap)
    {
        int x = region.Left + ((region.Width - block.Width) / 2);
        int below = region.Bottom + gap;
        int above = region.Top - block.Height - gap;
        int y;
        if (below + block.Height <= monitor.Bottom - gap)
            y = below;
        else if (above >= monitor.Top + gap)
            y = above;
        else
            y = region.Bottom - block.Height - gap;

        x = Math.Clamp(x, monitor.Left + gap, Math.Max(monitor.Left + gap, monitor.Right - block.Width - gap));
        y = Math.Clamp(y, monitor.Top + gap, Math.Max(monitor.Top + gap, monitor.Bottom - block.Height - gap));
        return new SD.Point(x, y);
    }

    /// <summary>
    /// The aspect popover: one row per preset, opening upward from the aspect cell so it never
    /// covers the action card, and flipping below when there is no room above.
    /// </summary>
    public static (SD.Rectangle Card, IReadOnlyList<SD.Rectangle> Rows) AspectMenu(
        SD.Rectangle aspectCell,
        SD.Rectangle monitor,
        double scale,
        int itemCount)
    {
        int pad = Px(6, scale);
        int rowHeight = Px(MenuRowHeightDip, scale);
        int width = Px(MenuWidthDip, scale);
        int height = pad + (rowHeight * Math.Max(1, itemCount)) + pad;

        int x = Math.Clamp(
            aspectCell.Left,
            monitor.Left + Px(4, scale),
            Math.Max(monitor.Left + Px(4, scale), monitor.Right - width - Px(4, scale)));
        int above = aspectCell.Top - Px(6, scale) - height;
        int below = aspectCell.Bottom + Px(6, scale);
        int y = above >= monitor.Top + Px(4, scale)
            ? above
            : Math.Min(below, Math.Max(monitor.Top, monitor.Bottom - height));

        var card = new SD.Rectangle(x, y, width, height);
        var rows = new List<SD.Rectangle>(itemCount);
        for (int i = 0; i < itemCount; i++)
            rows.Add(new SD.Rectangle(card.Left + pad, card.Top + pad + (i * rowHeight), width - (pad * 2), rowHeight));
        return (card, rows);
    }

    private static int Px(int logical, double scale) => RecordingMonitorDpi.Px(logical, scale);

    /// <summary>Glyph for a toggle cell, from the one icon family the app uses.</summary>
    public static string GlyphFor(RecordingHudCell cell) => cell switch
    {
        RecordingHudCell.Microphone => "",
        RecordingHudCell.SystemAudio => "",
        RecordingHudCell.Webcam => "",
        RecordingHudCell.Clicks => "",
        RecordingHudCell.Keystrokes => "",
        _ => "",
    };

    /// <summary>Tooltip / accessible name for a cell.</summary>
    public static string LabelFor(RecordingHudCell cell) => cell switch
    {
        RecordingHudCell.Microphone => "Microphone",
        RecordingHudCell.SystemAudio => "System audio",
        RecordingHudCell.Webcam => "Webcam",
        RecordingHudCell.Clicks => "Highlight clicks",
        RecordingHudCell.Keystrokes => "Show keystrokes",
        RecordingHudCell.Aspect => "Aspect ratio",
        RecordingHudCell.Dimensions => "Selection size",
        RecordingHudCell.RecordGif => "Record GIF",
        RecordingHudCell.RecordVideo => "Record video",
        _ => "",
    };

    /// <summary>The keyboard shortcut shown on an action row.</summary>
    public static string ShortcutFor(RecordingHudCell cell) => cell switch
    {
        RecordingHudCell.RecordGif => "Alt+" + Enter,
        RecordingHudCell.RecordVideo => Enter,
        _ => "",
    };

    private const string Enter = "↵";
}
