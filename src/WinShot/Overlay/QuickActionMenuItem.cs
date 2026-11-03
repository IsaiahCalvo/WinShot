using System.Drawing.Drawing2D;
using WinShot.Core;
using WinShot.Recording;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Overlay;

/// <summary>
/// One row of the quick-actions card's context menu, drawn end to end rather than by
/// the stock menu-item layout. The stock one parks icons in a gutter to the left of
/// every row's text, so the menu reads as two left edges. Here every row's leading
/// content — a glyph, or the text on the rows that have no glyph — starts on the same
/// left edge, and only an iconed row's text is indented past its glyph.
///
/// The sizes below are the 100% sizes. Every one is multiplied by the menu's
/// <see cref="QuickActionsContextMenu.UiScale"/>, so on a 150% or 200% display the glyph,
/// insets and row height grow with the text instead of leaving 17 px icons and 19 px rows
/// around text twice that size.
/// </summary>
internal sealed class QuickActionMenuItem : WF.ToolStripMenuItem
{
    internal const int GlyphSize = 17;
    private const int PadLeft = 8;
    private const int PadRight = 8;
    private const int GlyphGap = 7;
    private const int ShortcutGap = 10;
    private const int ArrowWidth = 12;
    private const int MinHeight = 19;

    private readonly Action _action;
    private readonly string? _icon;
    private SD.Size? _ownSize;

    internal QuickActionMenuItem(string id, string text, Action action)
        : base(text)
    {
        Id = id;
        _action = action;
        _icon = QuickActionsMenu.IconFor(id);
    }

    internal string Id { get; }

    private double Scale => Owner is QuickActionsContextMenu menu ? menu.UiScale : 1d;

    private int Px(int logical) => RecordingMonitorDpi.Px(logical, Scale);

    private int GlyphPx => Px(GlyphSize);

    /// <summary>The glyph at this row's size. SvgIcons caches by size, so this is a lookup.</summary>
    private SD.Bitmap? Glyph => _icon is null ? null : SvgIcons.Get(_icon, GlyphPx, ThemePalette.TextPrimary);

    /// <summary>How far this row's text sits from the menu's left edge.</summary>
    internal int TextLeft => Px(PadLeft) + (_icon is null ? 0 : GlyphPx + Px(GlyphGap));

    /// <summary>Drops the measured size; the menu calls this when its scale changes.</summary>
    internal void Rescale() => _ownSize = null;

    private string Shortcut => ShowShortcutKeys ? ShortcutKeyDisplayString ?? string.Empty : string.Empty;

    protected override void OnClick(EventArgs e)
    {
        // A submenu row has nothing to run; let the base open its drop-down.
        if (HasDropDownItems)
        {
            base.OnClick(e);
            return;
        }
        _action();
    }

    /// <summary>
    /// Every row reports the widest row's width: overriding this opts out of the
    /// drop-down's own equalizing pass, and rows of different widths would put each
    /// shortcut at its own right edge instead of in one column. Both this and
    /// <see cref="OwnSize"/> are cached — layout asks repeatedly, and measuring every
    /// sibling on every ask made opening the menu visibly slow.
    /// </summary>
    public override SD.Size GetPreferredSize(SD.Size constrainingSize)
    {
        SD.Size own = OwnSize();
        int width = Owner is QuickActionsContextMenu menu ? menu.TargetRowWidth : own.Width;
        return new SD.Size(Math.Max(width, own.Width), own.Height);
    }

    internal SD.Size OwnSize()
    {
        if (_ownSize is { } cached)
            return cached;
        SD.Size text = Measure(Text ?? string.Empty);
        int width = TextLeft + text.Width + Px(PadRight);
        if (Shortcut.Length > 0)
            width += Px(ShortcutGap) + Measure(Shortcut).Width;
        if (HasDropDownItems)
            width += Px(ShortcutGap) + Px(ArrowWidth);
        _ownSize = new SD.Size(width, Math.Max(Px(MinHeight), text.Height + Px(2)));
        return _ownSize.Value;
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        // Deliberately not base.OnPaint: that is the layout this item exists to replace.
        var g = e.Graphics;
        DrawHighlight(g);

        if (Glyph is { } glyph)
        {
            int size = GlyphPx;
            g.DrawImage(glyph, Px(PadLeft), (Height - size) / 2, size, size);
        }

        SD.Color color = Enabled ? ThemePalette.TextPrimary : ThemePalette.TextSecondary;
        var flags = WF.TextFormatFlags.NoPrefix | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.Left;
        WF.TextRenderer.DrawText(
            g,
            Text,
            Font,
            new SD.Rectangle(TextLeft, 0, Math.Max(0, Width - TextLeft - Px(PadRight)), Height),
            color,
            flags);

        if (HasDropDownItems)
        {
            DrawSubmenuArrow(g, color);
        }
        else if (Shortcut.Length > 0)
        {
            WF.TextRenderer.DrawText(
                g,
                Shortcut,
                Font,
                new SD.Rectangle(TextLeft, 0, Math.Max(0, Width - TextLeft - Px(PadRight)), Height),
                ThemePalette.TextSecondary,
                WF.TextFormatFlags.NoPrefix | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.Right);
        }
    }

    /// <summary>
    /// The hover/keyboard highlight: a rounded bar spanning the row, which is the full
    /// width the drop-down gives its items, so it insets evenly from both edges instead
    /// of the stock block that ran to one border and stopped short of the other.
    /// </summary>
    private void DrawHighlight(SD.Graphics g)
    {
        if (!Selected && !Pressed && !(HasDropDownItems && DropDown.Visible))
            return;

        SD.Rectangle bounds = HighlightBounds();
        if (bounds.Width <= 2 || bounds.Height <= 2)
            return;

        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = GdiPaths.RoundedRect(bounds, Math.Min(Px(6), bounds.Height / 2));
        using var fill = new SD.SolidBrush(SD.Color.FromArgb(66, 66, 70));
        g.FillPath(fill, path);
        g.SmoothingMode = previous;
    }

    /// <summary>
    /// The highlight, inset from the row's own edges. That only reads as even because the
    /// drop-down gives its rows the full client width (see
    /// <see cref="QuickActionsContextMenu.DefaultPadding"/>) — with horizontal padding the
    /// rows sit at X=0 inside a wider client, and the bar came out 1px off the left border
    /// and clipped flat against the row's right edge.
    /// </summary>
    private SD.Rectangle HighlightBounds()
    {
        int gutter = Px(QuickActionsContextMenu.HighlightGutter);
        int inset = Px(1);
        return new SD.Rectangle(gutter, inset, Math.Max(0, Width - gutter * 2), Height - inset * 2);
    }

    /// <summary>The chevron that says this row expands. Drawn here because the row opts out
    /// of the stock layout, which would otherwise have drawn it.</summary>
    private void DrawSubmenuArrow(SD.Graphics g, SD.Color color)
    {
        float arm = (float)(4 * Scale);
        int x = Width - Px(PadRight) - Px(ArrowWidth) / 2;
        int y = Height / 2;
        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new SD.Pen(color, (float)(1.4 * Scale))
        {
            StartCap = SD.Drawing2D.LineCap.Round,
            EndCap = SD.Drawing2D.LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen, new[]
        {
            new SD.PointF(x - (arm / 2f), y - arm),
            new SD.PointF(x + (arm / 2f), y),
            new SD.PointF(x - (arm / 2f), y + arm),
        });
        g.SmoothingMode = previous;
    }

    private SD.Size Measure(string value) =>
        WF.TextRenderer.MeasureText(value, Font, SD.Size.Empty, WF.TextFormatFlags.NoPrefix);
}

/// <summary>
/// The drop-down that hosts <see cref="QuickActionMenuItem"/>. It exists to size itself
/// to those rows: <see cref="WF.ToolStripDropDownMenu"/> re-derives its width from the
/// stock text/image/check metrics, so a row that draws its own glyph measures short and
/// the shortcut column gets clipped off the right edge.
/// </summary>
internal sealed class QuickActionsContextMenu : WF.ContextMenuStrip
{
    private const int CornerRadius = 8;
    /// <summary>Inset of the row highlight from both menu borders.</summary>
    internal const int HighlightGutter = 3;

    private int? _rowWidth;
    private bool _stretchingRows;
    private SD.Font? _scaledFont;

    internal QuickActionsContextMenu()
    {
        // The rows own their left inset; the drop-down must not add one of its own.
        ShowImageMargin = false;
        Renderer = new RoundedMenuRenderer();
        // Pixels, not points: a point font resolves against the system DPI, so on a 150%
        // primary display a "100%" menu came out with 150% text in 100% rows.
        _scaledFont = ThemePalette.ScaledUiFont(ThemePalette.PinnedMenuRowPt, 1d);
        Font = _scaledFont;
    }

    /// <summary>
    /// The display scale the menu is drawn at: 1 at 100%, 2 at 200%. The card sets it from
    /// the monitor it sits on before the menu opens. A point-sized font alone does not get
    /// there: under PerMonitorV2 it resolves against whichever DC it was created on, and the
    /// rows' glyphs and insets are pixels that never scaled at all, so on a 200% display the
    /// menu came out as 100% rows with their text pressed against the edges.
    /// </summary>
    internal double UiScale { get; private set; } = 1d;

    /// <summary>Sets <see cref="UiScale"/> on this menu and every submenu under it, and lays
    /// the rows out again at the new size. Cheap when the scale has not changed.</summary>
    internal void ApplyScale(double scale)
    {
        if (scale <= 0)
            scale = 1d;
        if (Math.Abs(scale - UiScale) < 0.001)
            return;
        UiScale = scale;

        SD.Font? previous = _scaledFont;
        _scaledFont = ThemePalette.ScaledUiFont(ThemePalette.PinnedMenuRowPt, scale);
        Font = _scaledFont;
        previous?.Dispose();

        int pad = RecordingMonitorDpi.Px(2, scale);
        Padding = new WF.Padding(0, pad, 0, pad);
        _rowWidth = null;
        foreach (WF.ToolStripItem item in Items)
        {
            if (item is QuickActionMenuItem row)
            {
                row.Rescale();
                if (row.HasDropDownItems && row.DropDown is QuickActionsContextMenu child)
                    child.ApplyScale(scale);
            }
        }
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _scaledFont?.Dispose();
            _scaledFont = null;
        }
    }

    /// <summary>
    /// Even gutters on both sides. ToolStripDropDownMenu otherwise recomputes its own
    /// padding during layout — 8 on the left, 1 on the right — which left the highlight
    /// touching one border and short of the other.
    /// </summary>
    /// <summary>
    /// No horizontal padding, so a row spans the whole menu and its highlight can inset
    /// evenly from both borders. ToolStripDropDownMenu lays rows out at X=0 regardless, so
    /// any left padding becomes dead space on the right and clips the bar there.
    /// </summary>
    protected override WF.Padding DefaultPadding => new(0, 2, 0, 2);

    /// <summary>
    /// The width every row is given: the widest row's own width, or the drop-down's inner
    /// width when that turned out wider. Without the second half a row can end up narrower
    /// than the menu, and the row's highlight then stops short of the right edge — which is
    /// exactly how it looked before.
    /// </summary>
    internal int TargetRowWidth => Math.Max(RowWidth, ClientSize.Width - Padding.Horizontal);



    /// <summary>The width every row reports, measured once. Layout asks constantly.</summary>
    internal int RowWidth
    {
        get
        {
            if (_rowWidth is { } cached)
                return cached;
            int width = 0;
            foreach (WF.ToolStripItem item in Items)
            {
                if (item is QuickActionMenuItem row)
                    width = Math.Max(width, row.OwnSize().Width);
            }
            _rowWidth = width;
            return width;
        }
    }

    // The cache must not outlive the row list: layout runs while rows are still being
    // added, and a width measured then is short of the finished menu's.
    protected override void OnItemAdded(WF.ToolStripItemEventArgs e)
    {
        _rowWidth = null;
        base.OnItemAdded(e);
    }

    protected override void OnItemRemoved(WF.ToolStripItemEventArgs e)
    {
        _rowWidth = null;
        base.OnItemRemoved(e);
    }

    /// <summary>Creates the window and lays the rows out now, so the first right-click
    /// does not pay for it. Called while the card is idle.</summary>
    internal void Warm()
    {
        _ = RowWidth;
        _ = Handle;
        PerformLayout();
        foreach (WF.ToolStripItem item in Items)
        {
            if (item is WF.ToolStripMenuItem { HasDropDownItems: true, DropDown: QuickActionsContextMenu child })
                child.Warm();
        }
    }

    private int Radius => RecordingMonitorDpi.Px(CornerRadius, UiScale);

    public override SD.Size GetPreferredSize(SD.Size proposedSize)
    {
        SD.Size size = base.GetPreferredSize(proposedSize);
        size.Width = Math.Max(size.Width, RowWidth + Padding.Horizontal);
        return size;
    }

    protected override void OnLayoutCompleted(EventArgs e)
    {
        base.OnLayoutCompleted(e);
        PopupChrome.ApplyRegion(this, Radius);

        // The drop-down can settle wider than the rows asked for; one more pass hands them
        // that width so every highlight spans the menu. Guarded: this re-enters layout.
        if (_stretchingRows)
            return;
        int target = TargetRowWidth;
        bool shortRow = false;
        foreach (WF.ToolStripItem item in Items)
            shortRow |= item is QuickActionMenuItem && item.Width != target;
        if (!shortRow)
            return;

        _stretchingRows = true;
        try
        {
            PerformLayout();
        }
        finally
        {
            _stretchingRows = false;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PopupChrome.ApplyRegion(this, Radius);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        // Drawn last so the hairline follows the curve the region cut, matching the
        // capture card and the app's other popups.
        PopupChrome.DrawBorder(e.Graphics, ClientSize, Radius, SD.Color.FromArgb(70, 70, 74));
    }

    /// <summary>The dark menu look, minus its square border — the rounded one is painted
    /// over the region instead, and a rectangle would lose its corners to the clip.</summary>
    private sealed class RoundedMenuRenderer : DarkDropDown.DarkMenuRenderer
    {
        /// <summary>The card's plate is pinned by hand and stays where it is; every other menu in
        /// the app moved to the palette's Elevated.</summary>
        internal RoundedMenuRenderer() : base(SD.Color.FromArgb(35, 35, 38))
        {
        }

        protected override void OnRenderToolStripBorder(WF.ToolStripRenderEventArgs e)
        {
        }

        /// <summary>The rows draw their own rounded highlight.</summary>
        protected override void OnRenderMenuItemBackground(WF.ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not QuickActionMenuItem)
                base.OnRenderMenuItemBackground(e);
        }
    }
}
