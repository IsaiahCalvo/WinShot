using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Core;

/// <summary>
/// Dark tray/context menu chrome (design 4h): charcoal panel, 6px-radius
/// white-alpha row hover, hairline separators, mono muted shortcut text.
/// Apply with <see cref="Apply"/>; it sets the renderer and per-item fonts.
/// </summary>
public static class DarkToolStrip
{
    public static void Apply(WF.ContextMenuStrip menu)
    {
        menu.Renderer = new Renderer();
        menu.BackColor = ThemePalette.ToolbarBg;
        menu.ForeColor = ThemePalette.TextPrimary;
        menu.ShowImageMargin = false;
        menu.Font = ThemePalette.UiFont(9f);
    }

    private sealed class Renderer : WF.ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderMenuItemBackground(WF.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
                return;
            var bounds = new SD.Rectangle(2, 0, e.Item.Width - 4, e.Item.Height - 1);
            e.Graphics.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            using var path = GdiPaths.RoundedRect(bounds, 6);
            using var fill = new SD.SolidBrush(SD.Color.FromArgb(0x3C, 0x3C, 0x3E));
            e.Graphics.FillPath(fill, path);
        }

        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
        {
            bool isShortcut = (e.TextFormat & WF.TextFormatFlags.Right) == WF.TextFormatFlags.Right;
            if (isShortcut)
            {
                e.TextColor = ThemePalette.TextMuted;
                e.TextFont = ThemePalette.MonoFont(7.5f);
            }
            else
            {
                e.TextColor = e.Item.Enabled ? ThemePalette.TextPrimary : ThemePalette.TextMuted;
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(WF.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new SD.Pen(SD.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    private sealed class Colors : WF.ProfessionalColorTable
    {
        public override SD.Color ToolStripDropDownBackground => ThemePalette.ToolbarBg;
        public override SD.Color ImageMarginGradientBegin => ThemePalette.ToolbarBg;
        public override SD.Color ImageMarginGradientMiddle => ThemePalette.ToolbarBg;
        public override SD.Color ImageMarginGradientEnd => ThemePalette.ToolbarBg;
        public override SD.Color MenuBorder => SD.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
        public override SD.Color MenuItemBorder => SD.Color.Transparent;
        public override SD.Color SeparatorDark => SD.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
        public override SD.Color SeparatorLight => SD.Color.Transparent;
    }
}
