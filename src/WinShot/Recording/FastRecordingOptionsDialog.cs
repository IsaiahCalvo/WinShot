using System.Runtime.InteropServices;
using ScreenRecorderLib;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastRecordingOptionsDialog : WF.Form
{
    private static readonly SD.Color Back = SD.Color.FromArgb(43, 43, 43);
    private static readonly SD.Color FieldBack = SD.Color.FromArgb(58, 58, 58);
    private static readonly SD.Color TextColor = SD.Color.White;
    private static readonly SD.Color MutedText = SD.Color.FromArgb(220, 220, 220);
    private static readonly SD.Color HintText = SD.Color.FromArgb(170, 170, 170);
    private static readonly SD.Color Accent = ThemePalette.Accent;
    // Translucent light hairline for the CleanShot panel look.
    private static readonly SD.Color Border = SD.Color.FromArgb(54, 255, 255, 255);

    // Design-unit layout grid (96-DPI logical pixels, scaled by _scale everywhere).
    private const int DialogWidth = 286;
    private const int PadX = 18;
    private const int LabelWidth = 96;
    private const int FieldX = 120;
    private const int FieldWidth = 148;

    private readonly WF.RadioButton _mp4Radio;
    private readonly WF.RadioButton _gifRadio;
    private readonly WF.CheckBox _audioCheck;
    private readonly WF.Label _micDeviceLabel;
    private readonly WF.ComboBox _micDeviceCombo;
    private readonly WF.CheckBox _systemAudioCheck;
    private readonly WF.Label _webcamLabel;
    private readonly WF.ComboBox _webcamCombo;
    private readonly WF.Label _webcamDeviceLabel;
    private readonly WF.ComboBox _webcamDeviceCombo;
    private readonly WF.Label _webcamSizeLabel;
    private readonly WF.TextBox _webcamSizeBox;
    private readonly WF.Label _webcamSizeHint;

    // DeviceName values parallel to the combo items (index 0 = "Default", which
    // maps to the first real device). Populated lazily from ScreenRecorderLib.
    private readonly List<string?> _micDeviceNames = new();
    private readonly List<string?> _webcamDeviceNames = new();
    private readonly WF.CheckBox _cursorCheck;
    private readonly WF.CheckBox _clickHighlightCheck;
    private readonly WF.CheckBox _keystrokeCheck;
    private readonly WF.TextBox _countdownBox;
    private readonly WF.ComboBox _fpsCombo;
    private readonly WF.ComboBox _qualityCombo;
    private readonly WF.Label _fpsLabel;
    private readonly WF.Label _qualityLabel;
    private readonly WF.Label _gifFpsLabel;
    private readonly WF.ComboBox _gifFpsCombo;
    private static FastRecordingOptionsDialog? _cached;
    private TaskCompletionSource<WF.DialogResult>? _completion;

    /// <summary>One vertical slot in the dialog. A row whose predicate is false is
    /// skipped entirely, so toggling options never leaves dead gaps. (A predicate,
    /// not Control.Visible: that getter reads false for everything while the form
    /// itself is still hidden.) Offsets and heights are design units.</summary>
    private readonly record struct Row(
        int GapBefore,
        int Height,
        Func<bool>? IsShown,
        (WF.Control Control, int OffsetY)[] Controls);

    private readonly List<Row> _rows = new();

    // Point-based fonts render at the monitor's DPI, but this layout is designed in
    // 96-DPI pixels — so every coordinate is multiplied by the cursor monitor's scale
    // or labels truncate on 125%/150% displays. (AutoScaleMode.Dpi can't do this: it
    // only reacts to DPI *changes*, never the monitor the form first appears on.)
    private readonly double _scale;

    private int S(int logical) => (int)Math.Round(logical * _scale);

    public FastRecordingOptionsDialog(Settings settings)
    {
        _scale = RecordingMonitorDpi.ScaleFor(WF.Screen.FromPoint(WF.Cursor.Position).Bounds);
        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        ClientSize = new SD.Size(S(DialogWidth), S(400)); // height is owned by Reflow()
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.CenterScreen;
        TopMost = true;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        var title = Label("Record screen", PadX, 250, bold: true, size: 11);

        _mp4Radio = Radio("MP4 video", true);
        _gifRadio = Radio("GIF animation", false);

        // FPS + Quality. The GIF FPS row takes Quality's slot when GIF is selected
        // (Quality is meaningless for GIF here).
        _fpsLabel = Label("Frame rate", PadX, LabelWidth);
        _fpsCombo = Combo();
        _fpsCombo.Items.AddRange(["60 fps", "30 fps", "15 fps"]);
        _fpsCombo.SelectedIndex = 1;

        _qualityLabel = Label("Quality", PadX, LabelWidth);
        _qualityCombo = Combo();
        _qualityCombo.Items.AddRange(["High", "Medium", "Low"]);
        _qualityCombo.SelectedIndex = 1;

        _gifFpsLabel = Label("GIF frame rate", PadX, LabelWidth);
        _gifFpsCombo = Combo();
        _gifFpsCombo.Items.AddRange(["20 fps", "15 fps", "12 fps", "8 fps"]);
        _gifFpsCombo.SelectedIndex = 2;

        _audioCheck = Check("Record microphone", isChecked: false);
        _micDeviceLabel = Label("Mic device", PadX, LabelWidth);
        _micDeviceCombo = Combo();
        _systemAudioCheck = Check("Record system audio", isChecked: false);

        _webcamLabel = Label("Webcam", PadX, LabelWidth);
        _webcamCombo = Combo();
        _webcamCombo.Items.AddRange(["Off", "Top left", "Top right", "Bottom left", "Bottom right", "Fullscreen"]);
        _webcamCombo.SelectedIndex = 0;
        _webcamCombo.SelectedIndexChanged += (_, _) => UpdateMp4DependentState();

        _webcamDeviceLabel = Label("Camera", PadX, LabelWidth);
        _webcamDeviceCombo = Combo();

        _webcamSizeLabel = Label("Webcam size", PadX, LabelWidth);
        _webcamSizeBox = NumberBox(RecordingOptions.DefaultWebcamSizePercent.ToString());
        _webcamSizeHint = Label("10–45%", FieldX + 56, 90, color: HintText, size: 8);

        _cursorCheck = Check("Capture cursor", isChecked: false);
        _clickHighlightCheck = Check("Highlight mouse clicks", isChecked: false);
        _keystrokeCheck = Check("Show keystrokes", isChecked: false);

        var countdownLabel = Label("Countdown (s)", PadX, LabelWidth);
        _countdownBox = NumberBox("0");
        var countdownHint = Label("0 = off", FieldX + 56, 90, color: HintText, size: 8);

        var start = ActionButton("Start", Accent);
        start.Click += (_, _) => Complete(WF.DialogResult.OK);
        var cancel = ActionButton("Cancel", FieldBack);
        cancel.Click += (_, _) => Complete(WF.DialogResult.Cancel);
        AcceptButton = start;
        CancelButton = cancel;

        // Rows in visual order. Labels sit 4 design px below their field's top so
        // their baselines align with combo text.
        bool Mp4() => _mp4Radio.Checked;
        bool MicOn() => _mp4Radio.Checked && _audioCheck.Checked;
        bool WebcamOn() => _mp4Radio.Checked && _webcamCombo.SelectedIndex > 0;
        AddRow(0, 24, null, (title, 0));
        AddRow(14, 22, null, (_mp4Radio, 0));
        AddRow(4, 22, null, (_gifRadio, 0));
        AddRow(16, 26, Mp4, (_fpsLabel, 4), (_fpsCombo, 0));
        AddRow(8, 26, Mp4, (_qualityLabel, 4), (_qualityCombo, 0));
        AddRow(16, 26, () => !_mp4Radio.Checked, (_gifFpsLabel, 4), (_gifFpsCombo, 0));
        AddRow(16, 22, Mp4, (_audioCheck, 0));
        AddRow(8, 26, MicOn, (_micDeviceLabel, 4), (_micDeviceCombo, 0));
        AddRow(8, 22, Mp4, (_systemAudioCheck, 0));
        AddRow(16, 26, Mp4, (_webcamLabel, 4), (_webcamCombo, 0));
        AddRow(8, 26, WebcamOn, (_webcamDeviceLabel, 4), (_webcamDeviceCombo, 0));
        AddRow(8, 26, WebcamOn, (_webcamSizeLabel, 4), (_webcamSizeBox, 0), (_webcamSizeHint, 5));
        AddRow(16, 22, null, (_cursorCheck, 0));
        AddRow(4, 22, null, (_clickHighlightCheck, 0));
        AddRow(4, 22, null, (_keystrokeCheck, 0));
        AddRow(16, 26, null, (countdownLabel, 4), (_countdownBox, 0), (countdownHint, 5));
        AddRow(20, 30, null, (cancel, 0), (start, 0));

        // Right-align the action pair: Start on the edge, Cancel beside it.
        start.Left = S(DialogWidth - PadX - 78);
        cancel.Left = start.Left - S(8) - cancel.Width;

        _audioCheck.CheckedChanged += (_, _) => UpdateMp4DependentState();
        _mp4Radio.CheckedChanged += (_, _) => UpdateMp4DependentState();
        _gifRadio.CheckedChanged += (_, _) => UpdateMp4DependentState();
        LoadDevices();
        ApplySettings(settings);

        MouseDown += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
                Native.ReleaseCaptureAndDrag(Handle);
        };
    }

    private void AddRow(int gapBefore, int height, Func<bool>? isShown, params (WF.Control, int)[] controls)
    {
        _rows.Add(new Row(gapBefore, height, isShown, controls));
        foreach ((WF.Control control, _) in controls)
            Controls.Add(control);
    }

    /// <summary>
    /// Stacks the visible rows top to bottom and sizes the dialog to fit, so the
    /// action row always sits a full padding above the bottom edge and hidden
    /// option rows collapse instead of leaving holes.
    /// </summary>
    private void Reflow()
    {
        int y = 18;
        foreach (Row row in _rows)
        {
            bool shown = row.IsShown?.Invoke() ?? true;
            if (shown)
                y += row.GapBefore;
            foreach ((WF.Control control, int offset) in row.Controls)
            {
                control.Visible = shown;
                if (shown)
                    control.Top = S(y + offset);
            }
            if (shown)
                y += row.Height;
        }

        int height = S(y + 18);
        if (ClientSize.Height != height)
            ClientSize = new SD.Size(S(DialogWidth), height);
    }

    /// <summary>
    /// Populates the microphone and webcam device pickers from ScreenRecorderLib.
    /// Item 0 of each combo is "Default" (mapped to a null DeviceName so the
    /// recorder uses the system default); real devices follow. Best-effort: if
    /// enumeration fails the combos simply show "Default" only.
    /// </summary>
    private void LoadDevices()
    {
        _micDeviceCombo.Items.Clear();
        _micDeviceNames.Clear();
        _micDeviceCombo.Items.Add("Default");
        _micDeviceNames.Add(null);
        try
        {
            var inputs = Recorder.GetSystemAudioDevices(AudioDeviceSource.InputDevices);
            if (inputs is not null)
            {
                foreach (var device in inputs)
                {
                    if (string.IsNullOrEmpty(device.DeviceName))
                        continue;
                    _micDeviceCombo.Items.Add(Truncate(device.FriendlyName ?? device.DeviceName));
                    _micDeviceNames.Add(device.DeviceName);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to enumerate microphone devices", ex);
        }
        // When exactly one real device exists, default to it; otherwise leave on
        // "Default" so the recorder picks the system default microphone.
        _micDeviceCombo.SelectedIndex = _micDeviceCombo.Items.Count == 2 ? 1 : 0;

        _webcamDeviceCombo.Items.Clear();
        _webcamDeviceNames.Clear();
        try
        {
            var cameras = Recorder.GetSystemVideoCaptureDevices();
            if (cameras is not null)
            {
                foreach (var camera in cameras)
                {
                    if (string.IsNullOrEmpty(camera.DeviceName))
                        continue;
                    _webcamDeviceCombo.Items.Add(Truncate(camera.FriendlyName ?? camera.DeviceName));
                    _webcamDeviceNames.Add(camera.DeviceName);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to enumerate webcam devices", ex);
        }
        if (_webcamDeviceCombo.Items.Count == 0)
        {
            _webcamDeviceCombo.Items.Add("No camera found");
            _webcamDeviceNames.Add(null);
        }
        _webcamDeviceCombo.SelectedIndex = 0;
    }

    private static string Truncate(string text) =>
        text.Length <= 28 ? text : text[..27] + "…";

    public static FastRecordingOptionsDialog Create(Settings settings)
    {
        var dialog = Interlocked.Exchange(ref _cached, null);
        // A cached dialog keeps the layout scale of the monitor it was built for;
        // rebuild when the cursor has moved to a monitor with a different scale.
        if (dialog is { IsDisposed: false } &&
            Math.Abs(dialog._scale - RecordingMonitorDpi.ScaleFor(WF.Screen.FromPoint(WF.Cursor.Position).Bounds)) > 0.01)
        {
            dialog.Dispose();
            dialog = null;
        }
        if (dialog is { IsDisposed: false })
        {
            dialog.ApplySettings(settings);
            dialog.Opacity = 1;
            dialog.ShowInTaskbar = false;
            dialog.CenterOnCurrentScreen();
            return dialog;
        }

        return new FastRecordingOptionsDialog(settings);
    }

    public static void Return(FastRecordingOptionsDialog dialog)
    {
        if (dialog.IsDisposed)
            return;

        dialog.DialogResult = WF.DialogResult.None;
        dialog._completion = null;
        dialog.Opacity = 1;
        dialog.Hide();
        if (_cached is null)
            _cached = dialog;
        else
            dialog.Dispose();
    }

    public Task<WF.DialogResult> ShowAsync()
    {
        _completion = new TaskCompletionSource<WF.DialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Show();
        Activate();
        Focus();
        return _completion.Task;
    }

    public bool IsGif => _gifRadio.Checked;

    public bool RecordMicrophone => _audioCheck.Checked && _mp4Radio.Checked;

    public bool RecordSystemAudio => _systemAudioCheck.Checked && _mp4Radio.Checked;

    public bool CaptureCursor => _cursorCheck.Checked;

    public bool ShowClickHighlights => _clickHighlightCheck.Checked;

    public bool ShowKeystrokes => _keystrokeCheck.Checked;

    public int CountdownSeconds =>
        int.TryParse(_countdownBox.Text.Trim(), out int seconds)
            ? RecordingOptions.ClampCountdownSeconds(seconds)
            : RecordingOptions.MinCountdownSeconds;

    public string WebcamPosition => _mp4Radio.Checked
        ? _webcamCombo.SelectedIndex switch
        {
            1 => "top-left",
            2 => "top-right",
            3 => "bottom-left",
            4 => "bottom-right",
            5 => "fullscreen",
            _ => "off",
        }
        : "off";

    public int WebcamSizePercent =>
        int.TryParse(_webcamSizeBox.Text.Trim(), out int percent)
            ? RecordingOptions.ClampWebcamSizePercent(percent)
            : RecordingOptions.DefaultWebcamSizePercent;

    /// <summary>
    /// DeviceName of the chosen microphone, or <c>null</c> to use the system
    /// default. Only meaningful when <see cref="RecordMicrophone"/> is true.
    /// </summary>
    public string? MicrophoneDeviceName =>
        SelectedDeviceName(_micDeviceCombo, _micDeviceNames);

    /// <summary>
    /// DeviceName of the chosen webcam, or <c>null</c> when none is available.
    /// Only meaningful when <see cref="WebcamPosition"/> is not "off".
    /// </summary>
    public string? WebcamDeviceName =>
        SelectedDeviceName(_webcamDeviceCombo, _webcamDeviceNames);

    private static string? SelectedDeviceName(WF.ComboBox combo, List<string?> names)
    {
        int index = combo.SelectedIndex;
        return index >= 0 && index < names.Count ? names[index] : null;
    }

    /// <summary>Chosen MP4 frame rate (fps). Applies to the H.264 recorder.</summary>
    public int RecordingFps => _fpsCombo.SelectedIndex switch
    {
        0 => 60,
        2 => 15,
        _ => 30,
    };

    /// <summary>Chosen GIF frame rate (fps). Only meaningful when <see cref="IsGif"/>.</summary>
    public int GifFps => _gifFpsCombo.SelectedIndex switch
    {
        0 => 20,
        1 => 15,
        3 => 8,
        _ => 12,
    };

    /// <summary>H.264 quality (0–100) mapped from the High/Medium/Low picker.</summary>
    public int VideoQuality => _qualityCombo.SelectedIndex switch
    {
        0 => 85,
        2 => 50,
        _ => 70,
    };

    protected override void OnKeyDown(WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Escape)
        {
            Complete(WF.DialogResult.Cancel);
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(WF.FormClosingEventArgs e)
    {
        if (_completion is not null)
        {
            e.Cancel = true;
            Complete(WF.DialogResult.Cancel);
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateWindowRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new SD.Pen(Border, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        IntPtr regionHandle = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, S(20), S(20));
        Region = SD.Region.FromHrgn(regionHandle);
        DeleteObject(regionHandle);
    }

    private void Complete(WF.DialogResult result)
    {
        DialogResult = result;
        Hide();
        _completion?.TrySetResult(result);
        _completion = null;
    }

    private void ApplySettings(Settings settings)
    {
        _mp4Radio.Checked = true;
        _gifRadio.Checked = false;
        _audioCheck.Checked = settings.RecordAudio;
        _systemAudioCheck.Checked = settings.RecordSystemAudio;
        _cursorCheck.Checked = settings.CaptureCursor;
        _clickHighlightCheck.Checked = settings.ShowClickHighlights;
        _keystrokeCheck.Checked = settings.ShowKeystrokes;
        _countdownBox.Text = RecordingOptions.ClampCountdownSeconds(settings.RecordingCountdownSeconds).ToString();
        _webcamSizeBox.Text = RecordingOptions.ClampWebcamSizePercent(settings.WebcamOverlaySizePercent).ToString();
        _fpsCombo.SelectedIndex = settings.RecordingFps switch
        {
            >= 60 => 0,
            <= 15 => 2,
            _ => 1,
        };
        _gifFpsCombo.SelectedIndex = settings.GifFps switch
        {
            >= 20 => 0,
            >= 14 => 1,
            <= 9 => 3,
            _ => 2,
        };
        _qualityCombo.SelectedIndex = 1;
        _webcamCombo.SelectedIndex = RecordingOptions.NormalizeWebcamPosition(settings.WebcamOverlayPosition) switch
        {
            "top-left" => 1,
            "top-right" => 2,
            "bottom-left" => 3,
            "bottom-right" => 4,
            "fullscreen" => 5,
            _ => 0,
        };
        UpdateMp4DependentState();
    }

    private void CenterOnCurrentScreen()
    {
        SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        Location = new SD.Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    // Row predicates own which options apply (MP4 vs GIF, mic on, webcam on);
    // Reflow applies them and restacks in one pass.
    private void UpdateMp4DependentState() => Reflow();

    private WF.Label Label(
        string text,
        int x,
        int width,
        bool bold = false,
        float size = 9,
        SD.Color? color = null) =>
        new()
        {
            AutoSize = false,
            Font = new SD.Font("Segoe UI", size, bold ? SD.FontStyle.Bold : SD.FontStyle.Regular),
            ForeColor = color ?? MutedText,
            Location = new SD.Point(S(x), 0),
            Size = new SD.Size(S(width), S(22)),
            Text = text,
        };

    private WF.ComboBox Combo() =>
        new()
        {
            BackColor = FieldBack,
            DropDownStyle = WF.ComboBoxStyle.DropDownList,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Location = new SD.Point(S(FieldX), 0),
            Size = new SD.Size(S(FieldWidth), S(24)),
        };

    private WF.TextBox NumberBox(string text) =>
        new()
        {
            BackColor = FieldBack,
            BorderStyle = WF.BorderStyle.FixedSingle,
            ForeColor = TextColor,
            Location = new SD.Point(S(FieldX), 0),
            Size = new SD.Size(S(44), S(24)),
            Text = text,
            TextAlign = WF.HorizontalAlignment.Center,
        };

    private WF.RadioButton Radio(string text, bool isChecked) =>
        new()
        {
            AutoSize = true,
            Checked = isChecked,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Location = new SD.Point(S(PadX), 0),
            Text = text,
        };

    private WF.CheckBox Check(string text, bool isChecked) =>
        new()
        {
            AutoSize = true,
            Checked = isChecked,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Location = new SD.Point(S(PadX), 0),
            Text = text,
        };

    private WF.Button ActionButton(string text, SD.Color backColor)
    {
        var button = new WF.Button
        {
            BackColor = backColor,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Location = new SD.Point(0, 0),
            Size = new SD.Size(S(78), S(30)),
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    private static class Native
    {
        private const int WmNclbuttondown = 0x00A1;
        private static readonly IntPtr HtCaption = new(2);

        public static void ReleaseCaptureAndDrag(IntPtr handle)
        {
            ReleaseCapture();
            SendMessage(handle, WmNclbuttondown, HtCaption, IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
