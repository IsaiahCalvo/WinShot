using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Overlay;

/// <summary>
/// One row of the quick-actions card's context menu, drawn end to end rather than by
/// the stock menu-item layout. The stock one parks icons in a gutter to the left of
/// every row's text, so the menu reads as two left edges. Here every row's leading
/// content — a glyph, or the text on the rows that have no glyph — starts on the same
/// left edge, and only an iconed row's text is indented past its glyph.
/// </summary>
internal sealed class QuickActionMenuItem : WF.ToolStripMenuItem
{
    internal const int GlyphSize = 16;
    // The drop-down forces an 8px left inset of its own, so the row's own left pad is
    // small; together they match the right pad and the menu reads evenly inset.
    private const int PadLeft = 4;
    private const int PadRight = 11;
    private const int GlyphGap = 9;
    private const int ShortcutGap = 24;
    private const int MinHeight = 24;

    private readonly Action _action;
    private readonly SD.Bitmap? _glyph;

    internal QuickActionMenuItem(string id, string text, Action action)
        : base(text)
    {
        Id = id;
        _action = action;
        if (QuickActionsMenu.IconFor(id) is { } icon)
            _glyph = SvgIcons.Get(icon, GlyphSize, ThemePalette.TextPrimary);
    }

    internal string Id { get; }

    /// <summary>How far this row's text sits from the menu's left edge.</summary>
    internal int TextLeft => PadLeft + (_glyph is null ? 0 : GlyphSize + GlyphGap);

    private string Shortcut => ShowShortcutKeys ? ShortcutKeyDisplayString ?? string.Empty : string.Empty;

    protected override void OnClick(EventArgs e) => _action();

    public override SD.Size GetPreferredSize(SD.Size constrainingSize)
    {
        SD.Size own = OwnSize();
        // Every row reports the widest row's width. Overriding GetPreferredSize opts out
        // of the drop-down's own equalizing pass, and rows of different widths would put
        // each shortcut at its own right edge instead of in one column.
        int width = own.Width;
        if (Owner is not null)
        {
            foreach (var sibling in Owner.Items)
            {
                if (sibling is QuickActionMenuItem row)
                    width = Math.Max(width, row.OwnSize().Width);
            }
        }
        return new SD.Size(width, own.Height);
    }

    private SD.Size OwnSize()
    {
        SD.Size text = Measure(Text ?? string.Empty);
        int width = TextLeft + text.Width + PadRight;
        if (Shortcut.Length > 0)
            width += ShortcutGap + Measure(Shortcut).Width;
        return new SD.Size(width, Math.Max(MinHeight, text.Height + 8));
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        // Deliberately not base.OnPaint: that is the layout this item exists to replace.
        var g = e.Graphics;
        Owner?.Renderer.DrawMenuItemBackground(new WF.ToolStripItemRenderEventArgs(g, this));

        if (_glyph is not null)
            g.DrawImage(_glyph, PadLeft, (Height - GlyphSize) / 2, GlyphSize, GlyphSize);

        SD.Color color = Enabled ? ThemePalette.TextPrimary : ThemePalette.TextSecondary;
        var flags = WF.TextFormatFlags.NoPrefix | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.Left;
        WF.TextRenderer.DrawText(
            g,
            Text,
            Font,
            new SD.Rectangle(TextLeft, 0, Math.Max(0, Width - TextLeft - PadRight), Height),
            color,
            flags);

        if (Shortcut.Length > 0)
        {
            WF.TextRenderer.DrawText(
                g,
                Shortcut,
                Font,
                new SD.Rectangle(TextLeft, 0, Math.Max(0, Width - TextLeft - PadRight), Height),
                ThemePalette.TextSecondary,
                WF.TextFormatFlags.NoPrefix | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.Right);
        }
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
    internal QuickActionsContextMenu()
    {
        // The rows own their left inset; the drop-down must not add one of its own.
        ShowImageMargin = false;
        Padding = new WF.Padding(0, 2, 0, 2);
    }

    public override SD.Size GetPreferredSize(SD.Size proposedSize)
    {
        SD.Size size = base.GetPreferredSize(proposedSize);
        int rows = 0;
        foreach (WF.ToolStripItem item in Items)
        {
            if (item is QuickActionMenuItem row)
                rows = Math.Max(rows, row.GetPreferredSize(SD.Size.Empty).Width);
        }
        size.Width = Math.Max(size.Width, rows + Padding.Horizontal);
        return size;
    }
}
