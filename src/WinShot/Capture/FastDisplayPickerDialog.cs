using WinShot.Core;
using WinShot.Recording;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

public sealed class FastDisplayPickerDialog : WF.Form
{
    private static readonly SD.Color Back = SD.Color.FromArgb(43, 43, 43);
    private static readonly SD.Color ButtonBack = SD.Color.FromArgb(58, 58, 58);
    private static readonly SD.Color ButtonHot = SD.Color.FromArgb(79, 79, 79);
    private static readonly SD.Color Accent = ThemePalette.Accent;
    private static readonly SD.Color TextColor = SD.Color.White;

    // Point fonts render at the monitor's DPI, so the fixed-pixel layout scales with
    // the cursor monitor or labels truncate on 125%/150% displays.
    private readonly double _scale;

    private int S(int logical) => (int)Math.Round(logical * _scale);

    private FastDisplayPickerDialog()
    {
        _scale = RecordingMonitorDpi.ScaleFor(WF.Screen.FromPoint(WF.Cursor.Position).Bounds);
        AutoScaleMode = WF.AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = WF.AutoSizeMode.GrowAndShrink;
        BackColor = Back;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        Padding = new WF.Padding(S(18));
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.CenterScreen;
        TopMost = true;

        var panel = new WF.FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
            BackColor = Back,
            FlowDirection = WF.FlowDirection.TopDown,
            Margin = WF.Padding.Empty,
            Padding = WF.Padding.Empty,
            WrapContents = false,
        };

        panel.Controls.Add(new WF.Label
        {
            AutoSize = false,
            Font = new SD.Font("Segoe UI", 10f, SD.FontStyle.Bold),
            ForeColor = TextColor,
            Height = S(24),
            Margin = new WF.Padding(0, 0, 0, S(8)),
            Text = "Choose displays to record",
            Width = S(260),
        });

        var screens = WF.Screen.AllScreens;
        var selected = new HashSet<int>();
        var displayButtons = new WF.Button[screens.Length];
        WF.Button record = Button("Record", 74, ButtonBack);
        record.Enabled = false;
        record.EnabledChanged += (_, _) => record.BackColor = record.Enabled ? Accent : ButtonBack;

        for (int i = 0; i < screens.Length; i++)
        {
            int index = i;
            var screen = screens[i];
            var bounds = screen.Bounds;
            var button = Button(
                $"Display {i + 1}{(screen.Primary ? " (primary)" : "")} - {bounds.Width}x{bounds.Height}",
                260,
                ButtonBack);
            button.Click += (_, _) =>
            {
                if (!selected.Add(index))
                    selected.Remove(index);
                button.BackColor = selected.Contains(index) ? Accent : ButtonBack;
                record.Enabled = selected.Count > 0;
            };
            displayButtons[i] = button;
            panel.Controls.Add(button);
        }

        var all = Button("Record all displays", 260, ButtonBack);
        all.Click += (_, _) =>
        {
            SelectedDisplays = screens.Select(s => s.Bounds).ToArray();
            DialogResult = WF.DialogResult.OK;
        };
        panel.Controls.Add(all);

        var bottom = new WF.FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
            BackColor = Back,
            FlowDirection = WF.FlowDirection.RightToLeft,
            Margin = new WF.Padding(0, S(10), 0, 0),
            Padding = WF.Padding.Empty,
            Width = S(260),
            WrapContents = false,
        };

        var cancel = Button("Cancel", 74, ButtonBack);
        cancel.Click += (_, _) => DialogResult = WF.DialogResult.Cancel;
        // The action pair reads as buttons, not list rows: centered labels, a gap between.
        record.TextAlign = SD.ContentAlignment.MiddleCenter;
        record.Padding = WF.Padding.Empty;
        cancel.TextAlign = SD.ContentAlignment.MiddleCenter;
        cancel.Padding = WF.Padding.Empty;
        cancel.Margin = new WF.Padding(S(8), 0, 0, S(6));
        record.Click += (_, _) =>
        {
            if (selected.Count == 0)
                return;
            SelectedDisplays = selected.OrderBy(i => i).Select(i => screens[i].Bounds).ToArray();
            DialogResult = WF.DialogResult.OK;
        };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(record);
        panel.Controls.Add(bottom);

        Controls.Add(panel);
        AcceptButton = record;
        CancelButton = cancel;

        MouseDown += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
                Native.ReleaseCaptureAndDrag(Handle);
        };
        panel.MouseDown += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
                Native.ReleaseCaptureAndDrag(Handle);
        };

        UpdateWindowRegion();
    }

    public SD.Rectangle[]? SelectedDisplays { get; private set; }

    /// <summary>Bounds of the displays to record (any subset, or all), or null on cancel.</summary>
    public static SD.Rectangle[]? ChooseDisplays()
    {
        var screens = WF.Screen.AllScreens;
        if (screens.Length == 1)
            return [screens[0].Bounds];

        using var dialog = new FastDisplayPickerDialog();
        PerfLog.TrackFirstShown(dialog, "display picker");
        return dialog.ShowDialog() == WF.DialogResult.OK ? dialog.SelectedDisplays : null;
    }

    protected override void OnKeyDown(WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Escape)
        {
            DialogResult = WF.DialogResult.Cancel;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateWindowRegion();
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        IntPtr regionHandle = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, S(20), S(20));
        Region = SD.Region.FromHrgn(regionHandle);
        Native.DeleteObject(regionHandle);
    }

    private WF.Button Button(string text, int width, SD.Color backColor)
    {
        var button = new WF.Button
        {
            AutoSize = false,
            BackColor = backColor,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Height = S(30),
            Margin = new WF.Padding(0, 0, 0, S(6)),
            Padding = new WF.Padding(S(8), 0, 0, 0),
            Text = text,
            TextAlign = SD.ContentAlignment.MiddleLeft,
            UseVisualStyleBackColor = false,
            Width = S(width),
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ButtonHot;
        return button;
    }

    private static class Native
    {
        private const int WmNclbuttondown = 0x00A1;
        private static readonly IntPtr HtCaption = new(2);

        public static void ReleaseCaptureAndDrag(IntPtr handle)
        {
            ReleaseCapture();
            SendMessage(handle, WmNclbuttondown, HtCaption, IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
