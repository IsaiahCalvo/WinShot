using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Ocr;

/// <summary>
/// A small themed in-place confirmation HUD shown near the OCR selection after text is
/// copied — CleanShot-style instant feedback instead of the slow, Focus-Assist-throttled
/// Windows tray balloon. Auto-dismisses; shows an "Open" action for a decoded URL.
/// </summary>
public sealed class OcrToastWindow : WF.Form
{
    private static readonly SD.Color Back = ThemePalette.ToolbarBg;
    private static readonly SD.Color BorderColor = SD.Color.FromArgb(54, 255, 255, 255);
    private readonly WF.Timer _dismiss = new();
    private readonly SD.Point? _anchor;

    public OcrToastWindow(string title, string preview, SD.Point? anchorScreen, Action? onOpen)
    {
        _anchor = anchorScreen;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        Opacity = 0.97;
        Padding = new WF.Padding(14);
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        const int width = 300;
        bool hasOpen = onOpen is not null;
        int height = hasOpen ? 96 : 70;
        ClientSize = new SD.Size(width, height);

        var titleLabel = Label(title, 14, 14, width - 28, 11f, ThemePalette.AccentHover, bold: true);
        Controls.Add(titleLabel);

        var previewLabel = Label(Trim(preview), 14, 38, width - 28, 9.5f, ThemePalette.TextSecondary);
        previewLabel.AutoEllipsis = true;
        previewLabel.UseMnemonic = false;
        Controls.Add(previewLabel);

        if (onOpen is not null)
        {
            var open = Button("Open link", width - 14 - 96, 60, 96, 26, ThemePalette.Accent, ThemePalette.AccentHover);
            open.Click += (_, _) => { try { onOpen(); } catch (Exception ex) { Log.Error("OCR toast open failed", ex); } Close(); };
            Controls.Add(open);
        }

        _dismiss.Interval = hasOpen ? 4500 : 1700;
        _dismiss.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _dismiss.Stop();
        MouseLeave += (_, _) => _dismiss.Start();
        foreach (WF.Control c in Controls)
        {
            c.MouseEnter += (_, _) => _dismiss.Stop();
            c.MouseLeave += (_, _) => _dismiss.Start();
        }
        KeyDown += (_, e) => { if (e.KeyCode == WF.Keys.Escape) Close(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateRegion();
        Position();
        _dismiss.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    protected override void OnClosed(EventArgs e)
    {
        _dismiss.Stop();
        _dismiss.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new SD.Pen(BorderColor, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private void Position()
    {
        SD.Rectangle area = WF.Screen.FromPoint(_anchor ?? WF.Cursor.Position).WorkingArea;
        int x, y;
        if (_anchor is SD.Point a)
        {
            // Centered above the selection; flip below if there isn't room.
            x = a.X - Width / 2;
            y = a.Y - Height - 16;
            x = Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
            if (y < area.Top + 8) y = a.Y + 16;
            y = Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - Height - 8));
        }
        else
        {
            x = area.Right - Width - 16;
            y = area.Bottom - Height - 16;
        }
        Location = new SD.Point(x, y);
    }

    private static string Trim(string text)
    {
        text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 90 ? text[..90] + "…" : text;
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 18, 18);
        Region = SD.Region.FromHrgn(rgn);
        DeleteObject(rgn);
    }

    private static WF.Label Label(string text, int x, int y, int width, float size, SD.Color color, bool bold = false) =>
        new()
        {
            AutoSize = false,
            BackColor = SD.Color.Transparent,
            Font = new SD.Font("Segoe UI", size, bold ? SD.FontStyle.Bold : SD.FontStyle.Regular),
            ForeColor = color,
            Location = new SD.Point(x, y),
            Size = new SD.Size(width, 22),
            Text = text,
            TextAlign = SD.ContentAlignment.MiddleLeft,
        };

    private static WF.Button Button(string text, int x, int y, int width, int height, SD.Color back, SD.Color hot)
    {
        var button = new WF.Button
        {
            AutoSize = false,
            BackColor = back,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = SD.Color.White,
            Location = new SD.Point(x, y),
            Size = new SD.Size(width, height),
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hot;
        return button;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
