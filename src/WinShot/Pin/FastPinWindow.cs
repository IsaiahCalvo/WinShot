using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinShot.Core;
using WinShot.Overlay;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Pin;

public sealed class FastPinWindow : WF.Form
{
    /// <summary>The pin wants to be reopened in the editor. Raised with a private copy of the
    /// image, so the pin stays on screen and keeps its own bitmap; App routes it through the
    /// same handoff the quick-actions card's Annotate uses.</summary>
    public event Action<SD.Bitmap>? EditRequested;

    /// <summary>
    /// How a pin opens the editor when nothing subscribed to <see cref="EditRequested"/>.
    /// The pencil chip and the Edit menu row are added to every pin unconditionally, and not
    /// every pin is built by App -- the recording completion card raises its own -- so the
    /// handler lives here once instead of at each call site, where one of them forgot it and
    /// shipped a chip that did nothing.
    /// </summary>
    public static Action<SD.Bitmap>? DefaultEditHandler { get; set; }

    private const int WmNclbuttondown = 0x00A1;
    private const int WmNchittest = 0x0084;
    private const int WmSizing = 0x0214;
    private static readonly IntPtr HtCaption = new(2);
    private const int CsDropShadow = 0x00020000;

    // WM_NCHITTEST results for the resize border / interior.
    /// <summary>"Not mine" -- the message is re-sent to whatever is underneath. A locked pin
    /// answers this for every point except its own lock and close chips.</summary>
    private const int HtTransparent = -1;
    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    // WM_SIZING wParam edge codes.
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;

    private const int ResizeBorderLogical = 6;
    private const int ToolbarButtonSizeLogical = 28;
    private const int ToolbarButtonGapLogical = 4;
    private const int ToolbarPadLogical = 6;
    private const int ToolbarTopLogical = 4;
    private const int CascadeOffsetLogical = 24;
    private const int ReadoutDurationMs = 800;

    private const int ToolbarIconSizeLogical = 16;

    private static readonly List<FastPinWindow> OpenPins = new();
    private static int _openCount;

    private readonly SD.Bitmap _image;
    private readonly SettingsService? _settings;
    private readonly int _naturalWidth;
    private readonly int _naturalHeight;
    private readonly WF.ContextMenuStrip _menu;
    private readonly WF.ToolStripMenuItem _lockItem;
    private readonly List<ToolbarButton> _toolbarButtons = new();
    private readonly WF.Timer _readoutTimer = new() { Interval = ReadoutDurationMs };
    private readonly bool _roundedCorners;
    private readonly bool _shadow;
    private readonly bool _border;
    private readonly QuickAccessOverlayVisuals _visuals =
        QuickAccessOverlayThemePalette.For(QuickAccessOverlayThemePalette.Current);
    private readonly WF.Timer _tooltipTimer = new() { Interval = 300 };
    private QuickAccessTooltipWindow? _tooltipWindow;
    private double _scale;
    private bool _locked;
    private bool _mouseInside;
    private int _hoverButton = -1;
    private int _pressedButton = -1;
    private int _focusButton = -1;
    private string? _readoutText;
    private SD.Point _readoutPoint;
    /// <summary>How far the hover chrome has come up: 0 = gone, 1 = fully drawn. The chips
    /// used to appear at full alpha on the next paint after MouseEnter, which is the one
    /// state change on this window and the only thing that tells you it is interactive.</summary>
    private double _chromeAlpha;
    private IDisposable? _chromeFade;
    /// <summary>Alpha the pinned image is painted at. 1 at rest; a locked pin dims toward
    /// PinInteraction.LockedOpacity while the pointer is over it, so a locked pin says so
    /// under the pointer instead of wearing a permanent penalty, and it comes straight back
    /// when you move away. It darkens the image, not the window: the chips are drawn over the
    /// top and must stay legible, so this is not see-through.</summary>
    private double _imageAlpha = 1d;
    private IDisposable? _imageFade;
    /// <summary>How far through a lock/unlock toolbar swap we are: 1 = settled. Locking takes
    /// the row from five chips to two, and that used to be a hard cut -- three chips gone and
    /// a different bar width in one frame -- next to an image dim that eased. The bar now
    /// glides between the two widths and the chips cross-dissolve over the glide.</summary>
    private double _lockSwap = 1d;
    /// <summary>The button row the swap is leaving, or null when none is running.</summary>
    private int[]? _lockSwapFrom;
    private IDisposable? _lockFade;
    /// <summary>An extra alpha multiplier for the chips alone, so the ones leaving or arriving
    /// in a lock swap fade without taking the bar they sit in with them.</summary>
    /// <summary>The ramp behind Toggle pins visibility. The window's own opacity is what
    /// moves -- a pin can carry a user-chosen opacity from Ctrl+wheel, so the value to come
    /// back to is held rather than assumed.</summary>
    private IDisposable? _visibilityFade;
    private double _visibleOpacity = 1d;
    /// <summary>Where the in-flight visibility ramp is heading, or null when none is running.
    /// A pin is still Visible for the whole 140 ms of its fade-out, so both "hide or show?"
    /// and "is this pin already leaving?" have to read the intent rather than the frame --
    /// otherwise a second press inside the ramp (the ordinary way a user says "no, bring them
    /// back") hides again, and re-samples a half-faded Opacity as the value to come back to.</summary>
    private bool? _visibilityTarget;
    /// <summary>The pinned image already resampled to the client area, and the size it was
    /// built for. A pin can be a 9,000 px scrolling capture, and resampling one down at
    /// HighQualityBicubic costs ~32 ms -- so the locked-pin dim, which ramps over ~18 frames
    /// with an 8 ms budget each, was running at 15 fps and holding the UI thread while it did
    /// it. The resample now happens once per size and every frame blits the copy.</summary>
    private SD.Bitmap? _scaled;
    private SD.Size _scaledFor;
    private SD.Rectangle _scaledBand;

    /// <summary>A locked pin answers HTTRANSPARENT, so it gets no WM_MOUSELEAVE. This runs
    /// only while the pointer is over a locked pin and stops as soon as it is not.</summary>
    private readonly WF.Timer _lockedHoverTimer = new() { Interval = 120 };

    public FastPinWindow(SD.Bitmap image, SettingsService? settings = null)
    {
        _image = image;
        _settings = settings;
        // The fallbacks match Settings' own defaults. They used to be false, so any
        // settings-less caller got a hard-square, shadowless pin that looked nothing like
        // the one users actually see.
        _roundedCorners = settings?.Current.PinnedRoundedCorners ?? true;
        _shadow = settings?.Current.PinnedShadow ?? true;
        _border = settings?.Current.PinnedBorder ?? false;
        _naturalWidth = image.Width;
        _naturalHeight = image.Height;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = SD.Color.Black;
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        AccessibleName = "Pinned screenshot";
        AccessibleDescription = "Pinned screenshot. Drag anywhere to move it. Press Tab to reach Edit, Copy, Save, Lock, and Close. Use the arrow keys to move the pin, or middle-click to close it.";

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        _menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkDropDown.DarkMenuRenderer(),
            ShowImageMargin = false,
        };
        var editItem = new WF.ToolStripMenuItem("Edit", null, (_, _) => RaiseEditRequested())
        {
            ToolTipText = "Open the pinned screenshot in the editor",
        };
        _menu.Items.Add(editItem);
        var copyItem = new WF.ToolStripMenuItem("Copy", null, async (_, _) => await CopyAsync())
        {
            ToolTipText = "Copy the pinned screenshot to the clipboard",
        };
        _menu.Items.Add(copyItem);
        var saveItem = new WF.ToolStripMenuItem("Save...", null, async (_, _) => await SaveAsync())
        {
            ToolTipText = "Save the pinned screenshot to a local file",
        };
        _menu.Items.Add(saveItem);
        _lockItem = new WF.ToolStripMenuItem("Lock (Ctrl+L)", null, (_, _) => SetLocked(!_locked));
        _lockItem.ToolTipText = "Toggle click-through mode for this pinned screenshot";
        _menu.Items.Add(_lockItem);
        _menu.Items.Add(new WF.ToolStripSeparator());
        var closeItem = new WF.ToolStripMenuItem("Close", null, (_, _) => Close())
        {
            ToolTipText = "Close this pinned screenshot",
        };
        _menu.Items.Add(closeItem);
        ContextMenuStrip = _menu;

        // The hover toolbar reuses the exact actions the context menu already exposes.
        _toolbarButtons.Add(new ToolbarButton("quick-access-edit.svg", "Edit", RaiseEditRequested));
        _toolbarButtons.Add(new ToolbarButton("quick-access-copy.svg", "Copy", () => _ = CopyAsync()));
        _toolbarButtons.Add(new ToolbarButton("quick-access-save.svg", "Save", () => _ = SaveAsync()));
        _toolbarButtons.Add(new ToolbarButton("quick-access-unlock.svg", "Lock", () => SetLocked(!_locked)));
        _toolbarButtons.Add(new ToolbarButton("quick-access-close.svg", "Close", Close));

        _allButtons = Enumerable.Range(0, _toolbarButtons.Count).ToArray();
        _lockedButtons = _allButtons
            .Where(i => _toolbarButtons[i].Tip is "Lock" or "Close")
            .ToArray();

        var area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        _scale = Math.Min(1.0, Math.Min(area.Width * 0.6 / _naturalWidth, area.Height * 0.6 / _naturalHeight));
        ApplyScale();

        int cascadeIndex = _openCount++ % 8;
        Location = PinInteraction.CascadeLocation(
            area,
            Size,
            cascadeIndex,
            CascadeOffsetLogical,
            DeviceDpi);

        MouseEnter += (_, _) => { _mouseInside = true; UpdateChromeAlpha(); Invalidate(); };
        MouseLeave += (_, _) => { _mouseInside = false; SetHoverButton(-1); SetPressed(-1); UpdateChromeAlpha(); Invalidate(); };
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left && HitTestButton(e.Location) < 0)
                Close();
        };
        MouseWheel += OnPinMouseWheel;
        KeyDown += OnPinKeyDown;
        GotFocus += (_, _) => { UpdateChromeAlpha(); Invalidate(ToolbarBounds()); };
        LostFocus += (_, _) => { UpdateChromeAlpha(); Invalidate(ToolbarBounds()); };
        _readoutTimer.Tick += (_, _) =>
        {
            _readoutTimer.Stop();
            _readoutText = null;
            Invalidate();
        };
        _tooltipTimer.Tick += (_, _) => ShowActionTooltip();
        _lockedHoverTimer.Tick += (_, _) =>
        {
            if (!_locked || !Bounds.Contains(WF.Cursor.Position))
            {
                _lockedHoverTimer.Stop();
                if (!_locked) return;
                _mouseInside = false;
                SetHoverButton(-1);
                SetPressed(-1);
                UpdateChromeAlpha();
                UpdateImageAlpha();
                Invalidate();
            }
        };
        Closed += (_, _) => Teardown();
        OpenPins.Add(this);
    }

    protected override WF.CreateParams CreateParams
    {
        get
        {
            WF.CreateParams parameters = base.CreateParams;
            if (_shadow)
                parameters.ClassStyle |= CsDropShadow;
            return parameters;
        }
    }

    /// <summary>
    /// Everything this pin owns outside the Controls tree, released once. Closed and
    /// Dispose both come here: a pin disposed without being closed never raises Closed (so
    /// its in-flight tweens would hold the process-wide high-resolution clock for the rest of
    /// the process, and it would sit in OpenPins forever), and a pin that is closed but not
    /// yet disposed still has to let go of its menu, its timers and its bitmap.
    /// </summary>
    private void Teardown()
    {
        if (_tornDown)
            return;
        _tornDown = true;

        _chromeFade?.Dispose();
        _chromeFade = null;
        _imageFade?.Dispose();
        _imageFade = null;
        _visibilityFade?.Dispose();
        _visibilityFade = null;
        _lockFade?.Dispose();
        _lockFade = null;
        _lockedHoverTimer.Stop();
        _lockedHoverTimer.Dispose();
        _readoutTimer.Dispose();
        HideActionTooltip();
        _tooltipTimer.Dispose();
        _menu.Dispose(); // not parented into Controls, so Form disposal never reaches it
        _scaled?.Dispose();
        _scaled = null;
        OpenPins.Remove(this);
        DisposeImageWhenCopyDone();
        MemoryCleanup.Request();
    }

    private bool _tornDown;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Teardown();
        base.Dispose(disposing);
    }

    protected override WF.AccessibleObject CreateAccessibilityInstance() =>
        new FastPinAccessibleObject(this);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedRegion();
    }

    public static void UnlockAllPins()
    {
        foreach (var pin in LivePins())
            pin.SetLocked(false);
    }

    /// <summary>
    /// The pins still alive, pruning any that were disposed without being closed. A form
    /// disposed rather than closed never raises Closed, so without this the list grows dead
    /// entries and the commands below act on windows that are gone.
    /// </summary>
    private static List<FastPinWindow> LivePins()
    {
        OpenPins.RemoveAll(pin => pin.IsDisposed);
        return OpenPins.ToList();
    }

    /// <summary>Whether any pin is on screen; the tray rows that act on all of them are
    /// pointless without one.</summary>
    public static bool AnyOpen => LivePins().Count > 0;

    /// <summary>
    /// Hides every pin, or brings them all back. One gesture, because the reason to hide
    /// pins is that you need the screen for a moment -- and that moment ends.
    /// </summary>
    public static void ToggleAllVisibility()
    {
        List<FastPinWindow> pins = LivePins();
        if (pins.Count == 0)
            return;

        // Any visible pin means "hide"; the alternative -- toggling each pin on its own --
        // leaves a mixed set alternating forever. Read where the pins are heading, not where
        // they are: one still mid-fade-out is already answered.
        bool hide = pins.Any(pin => pin.HeadedForVisible);
        foreach (FastPinWindow pin in pins)
        {
            try
            {
                if (hide) pin.FadeOutAndHide();
                else pin.ShowFadingIn();
            }
            catch (Exception ex)
            {
                Log.Error("Could not toggle a pinned window's visibility (non-fatal)", ex);
            }
        }
    }

    /// <summary>Takes this pin off screen over QuickMs. A pin leaving in one frame is the
    /// one thing the rest of this window does not do -- its chrome, its dim and its zoom
    /// chip all ramp -- and vanishing without a transition reads as a glitch, not a
    /// command.</summary>
    private void FadeOutAndHide()
    {
        if (!HeadedForVisible)
            return;
        // Only sampled when nothing is in flight: mid-ramp, Opacity is a frame of the fade,
        // and taking it as the resting value ratcheted every pin dimmer per double-tap.
        if (_visibilityTarget is null)
            _visibleOpacity = Opacity;
        _visibilityTarget = false;
        RampOpacity(Opacity, 0d, () =>
        {
            Hide();
            Opacity = _visibleOpacity; // restored while hidden, so Show() has nothing to fix
        });
    }

    /// <summary>Brings this pin back at the opacity it left with, over the mirror ramp.</summary>
    private void ShowFadingIn()
    {
        if (HeadedForVisible)
            return;
        _visibilityTarget = true;
        double from = Visible ? Opacity : 0d; // reversing a fade-out picks up where it is
        if (!Visible)
        {
            Opacity = 0d;
            Show();
        }
        RampOpacity(from, _visibleOpacity, null);
    }

    /// <summary>Where this pin will be once any in-flight visibility ramp lands.</summary>
    private bool HeadedForVisible => _visibilityTarget ?? Visible;

    private void RampOpacity(double from, double target, Action? onDone)
    {
        _visibilityFade?.Dispose();
        _visibilityFade = Motion.Run(Motion.QuickMs, ExitAware(from, target),
            p =>
            {
                if (IsDisposed) return;
                Opacity = Math.Clamp(from + ((target - from) * p), 0d, 1d);
            },
            () =>
            {
                _visibilityFade?.Dispose();
                _visibilityFade = null;
                _visibilityTarget = null;
                if (!IsDisposed)
                    onDone?.Invoke();
            });
    }

    public static void CloseAll()
    {
        foreach (FastPinWindow pin in LivePins())
        {
            try { pin.Close(); pin.Dispose(); }
            catch (Exception ex) { Log.Error("Could not close a pinned window (non-fatal)", ex); }
        }
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
        // Full bleed to the rounded edge. The image used to be inset by 1 px over a black
        // BackColor, which baked a dark hairline around every pin -- visible on a light
        // screenshot even with PinnedBorder off. The optional border is
        // PinWindowGeometry.DrawBorder's job, and the shadow is what separates a pin from
        // the desktop.
        var imageRect = new SD.Rectangle(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        SD.Rectangle band = ResampleBand(imageRect, e.ClipRectangle);
        SD.Bitmap surface = ScaledImage(imageRect.Size, band);
        if (_imageAlpha >= 0.999d)
        {
            e.Graphics.DrawImageUnscaled(surface, band.Location);
        }
        else
        {
            // Alpha matrix, not Form.Opacity: the whole point is that the chips painted over
            // this stay fully opaque, and window opacity would take them down with it.
            DrawWithAlpha(e.Graphics, surface, band, _imageAlpha);
        }

        if (_border)
        {
            using var border = new SD.Pen(ThemePalette.BorderStrong, Ui(1));
            DrawWindowBorder(e.Graphics, border);
        }

        // No accent outline on hover: the appearing toolbar itself is the hover signal,
        // matching the Quick Access card's restrained hover language.
        if (_chromeAlpha > 0.004d)
            DrawToolbar(e.Graphics);

        if (_locked)
            DrawLockBadge(e.Graphics);

        if (_readoutText is not null)
            DrawReadout(e.Graphics, _readoutText, _readoutPoint);

        base.OnPaint(e);
    }

    // ----- Hover toolbar -----------------------------------------------------

    /// <summary>Whether the chrome should be up. The drawn amount follows it through
    /// <see cref="_chromeAlpha"/>, so leaving fades instead of cutting.</summary>
    private bool ToolbarVisible => _mouseInside || (_focusButton >= 0 && ContainsFocus);

    /// <summary>
    /// Runs the hover chrome toward whatever <see cref="ToolbarVisible"/> now says, over
    /// QuickMs. Only the toolbar's own rect is invalidated per frame: the pin underneath can
    /// be a 6,000 px capture and repainting all of it 15 times would stutter the fade.
    /// </summary>
    private void UpdateChromeAlpha()
    {
        double target = ToolbarVisible ? 1d : 0d;
        if (Math.Abs(_chromeAlpha - target) < 0.001d)
            return;

        double from = _chromeAlpha;
        _chromeFade?.Dispose();
        // Leaving is the entrance played backwards, so it takes EaseInCubic. Running
        // EaseOutCubic over 1 -> 0 dumps the opacity in the first third and then holds an
        // invisible chrome for the rest of the tween.
        _chromeFade = Motion.Run(Motion.QuickMs, ExitAware(from, target),
            p =>
            {
                if (IsDisposed) return;
                _chromeAlpha = from + ((target - from) * p);
                Invalidate(ChromeInvalidationBounds());
            },
            () => { _chromeFade?.Dispose(); _chromeFade = null; });
    }

    /// <summary>The toolbar rect plus the lock badge's corner. The badge does NOT fade with
    /// the chrome -- it is drawn at full alpha whenever the pin is locked, because at rest it
    /// is the only thing saying so -- but it shares this rect so a chrome repaint restores it.</summary>
    private SD.Rectangle ChromeInvalidationBounds()
    {
        SD.Rectangle bar = ToolbarBounds();
        // Mid-swap the bar is between two widths; the wider of the two is what has to be
        // repainted or the frames that shrink leave the old bar's tail on screen.
        if (_lockSwapFrom is not null)
        {
            bar = SD.Rectangle.Union(bar, BarRect(Math.Max(
                BarWidth(VisibleButtons.Length),
                BarWidth(FitToWidth(_lockSwapFrom).Length))));
        }

        var badge = new SD.Rectangle(0, 0, Ui(34), Ui(34));
        SD.Rectangle rect = SD.Rectangle.Union(SD.Rectangle.Inflate(bar, Ui(4), Ui(4)), badge);
        // The zoom chip hangs below the bar. It used to sit entirely outside this rect, so
        // every fade frame drew it into a clipped-away area and it froze at whatever alpha
        // the one full repaint happened to catch.
        SD.Rectangle chip = ZoomChipBounds(bar);
        return chip.IsEmpty ? rect : SD.Rectangle.Union(rect, SD.Rectangle.Inflate(chip, Ui(2), Ui(2)));
    }

    /// <summary>The ease for a ramp between two amounts: EaseOutCubic on the way up,
    /// its mirror EaseInCubic on the way down, so the two directions are symmetric.</summary>
    private static Func<double, double> ExitAware(double from, double target) =>
        target >= from ? Motion.EaseOutCubic : Motion.EaseInCubic;

    /// <summary>Scales a chrome colour by the hover fade, times <paramref name="chip"/> --
    /// the per-chip multiplier a lock swap dissolves with. Passed down rather than parked in a
    /// field for the length of a paint: a throw mid-toolbar used to leave the field stale for
    /// every later frame.</summary>
    private SD.Color Fade(SD.Color color, double chip = 1d) =>
        SD.Color.FromArgb((int)Math.Clamp(Math.Round(color.A * _chromeAlpha * chip), 0d, 255d), color);

    private int Ui(int logicalPixels) => PinInteraction.ScaleLogical(logicalPixels, DeviceDpi);

    /// <summary>
    /// Which toolbar buttons are on offer. A locked pin shows only Lock and Close: everything
    /// else it could do would need the click that lock exists to pass through.
    ///
    /// The row is then trimmed to what the pin is actually wide enough to hold. A pin of a
    /// small UI element can be narrower than five chips (168 logical px, and 252 device px at
    /// 150 %), and the bar was only ever clamped on its left, so the right-hand chips were
    /// painted off the form -- unclickable, because HitTestButton reads the same rects. Chips
    /// are dropped from the left, so Lock and Close, the pin's only ways out, are the last to
    /// go.
    /// </summary>
    private int[] VisibleButtons => FitToWidth(_locked ? _lockedButtons : _allButtons);

    /// <summary>The widest tail of <paramref name="offered"/> whose bar fits the client, never
    /// fewer than one chip (a 1 px pin has room for nothing and still has to draw something).</summary>
    private int[] FitToWidth(int[] offered)
    {
        int fits = offered.Length;
        while (fits > 1 && BarWidth(fits) > ClientSize.Width)
            fits--;
        return fits == offered.Length ? offered : offered[(offered.Length - fits)..];
    }

    private int BarWidth(int count)
    {
        BarMetrics m = Metrics;
        return (count * m.Button) + ((count - 1) * m.Gap) + (m.Pad * 2);
    }

    /// <summary>The chip size, gaps and insets the bar is drawn with right now.</summary>
    private readonly record struct BarMetrics(int Button, int Gap, int Pad, int Top);

    /// <summary>
    /// The bar's metrics for the pin's current size. A pin can be smaller than the bar's
    /// natural footprint -- a pinned status-bar strip is 20 px tall, and the bar is 40 device
    /// px at 96 dpi, 60 at 150 % -- and the bar used to be laid out at its full size anyway,
    /// so every chip was painted below the client and clipped away. Unclickable too, because
    /// HitTestButton reads the same rects, which on a locked pin (HTTRANSPARENT everywhere a
    /// chip is not) left no way out of the pin at all.
    ///
    /// So the bar shrinks the way it trims: the top inset goes first, then the padding, then
    /// the chip itself, down to whatever square the client can hold. The single chip
    /// <see cref="FitToWidth"/> always keeps is then guaranteed to fit -- the floor it stops
    /// at is only meaningful if that chip is reachable.
    /// </summary>
    private BarMetrics Metrics
    {
        get
        {
            int button = Ui(ToolbarButtonSizeLogical);
            int gap = Ui(ToolbarButtonGapLogical);
            int pad = Ui(ToolbarPadLogical);
            int top = Ui(ToolbarTopLogical);
            int height = Math.Max(1, ClientSize.Height);
            // One chip plus its padding has to fit both ways: the bar is as tall as it is
            // deep, so the largest square the client holds is the ceiling for both.
            int box = Math.Max(1, Math.Min(Math.Max(1, ClientSize.Width), height));

            if (button + (pad * 2) > box)
                pad = Math.Max(0, (box - button) / 2);
            if (button + (pad * 2) > box)
            {
                pad = 0;
                button = box;
            }
            if (top + button + (pad * 2) > height)
                top = Math.Max(0, height - button - (pad * 2));

            return new BarMetrics(button, gap, pad, top);
        }
    }

    // Derived from the button list rather than written out, so inserting an action cannot
    // silently turn "Lock and Close" into something else.
    private int[] _allButtons = Array.Empty<int>();
    private int[] _lockedButtons = Array.Empty<int>();

    private SD.Rectangle ToolbarBounds() => BarRect(CurrentBarWidth());

    /// <summary>The bar's width right now: the settled width of the current row, or a point on
    /// the glide between the two rows while a lock swap runs.</summary>
    private int CurrentBarWidth()
    {
        int width = BarWidth(VisibleButtons.Length);
        if (_lockSwapFrom is null)
            return width;

        int from = BarWidth(FitToWidth(_lockSwapFrom).Length);
        return (int)Math.Round(from + ((width - from) * _lockSwap));
    }

    private SD.Rectangle BarRect(int width)
    {
        BarMetrics m = Metrics;
        int height = m.Button + (m.Pad * 2);
        // Clamped both ways, on both axes: centred when it fits, and never started so far
        // left or so far down that its far edge leaves the client.
        int slack = Math.Max(0, ClientSize.Width - width);
        int x = Math.Clamp((ClientSize.Width - width) / 2, 0, slack);
        int y = Math.Clamp(m.Top, 0, Math.Max(0, ClientSize.Height - height));
        return new SD.Rectangle(x, y, width, height);
    }

    /// <summary>The rect of a toolbar button by its index in _toolbarButtons, or Empty when
    /// the current state does not offer it.</summary>
    private SD.Rectangle ButtonBounds(int index) =>
        VisibleButtons.Contains(index) ? ChipBounds(ToolbarBounds(), index) : SD.Rectangle.Empty;

    /// <summary>
    /// Where a chip sits in a given bar. Every row this window can show -- all five, the
    /// locked pair, and anything the width trim leaves -- is a tail of the same button list,
    /// so a chip's distance from the bar's right edge is identical in all of them. Measuring
    /// from the right is what lets the bar glide between two widths with Lock and Close
    /// holding their place while the chips that are leaving slide out of the end that closes.
    /// </summary>
    private SD.Rectangle ChipBounds(SD.Rectangle bar, int index)
    {
        BarMetrics m = Metrics;
        int fromEnd = _toolbarButtons.Count - 1 - index;
        int x = bar.Right - m.Pad - m.Button - (fromEnd * (m.Button + m.Gap));
        return new SD.Rectangle(x, bar.Top + m.Pad, m.Button, m.Button);
    }

    private void DrawToolbar(SD.Graphics g)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        SD.Rectangle bar = ToolbarBounds();

        using (var bg = new SD.SolidBrush(Fade(SD.Color.FromArgb(235, ThemePalette.ToolbarBg))))
        using (var border = new SD.Pen(Fade(ThemePalette.Border)))
        {
            FillRoundedRect(g, bg, bar, Ui(8));
            DrawRoundedRect(g, border, bar, Ui(8));
        }

        // A lock swap cross-dissolves inside the gliding bar: the row that is leaving is gone
        // by the halfway point, the chips that are new come up over the second half, and the
        // two that survive the swap never flicker.
        int[] current = VisibleButtons;
        double arriving = _lockSwapFrom is null ? 1d : Math.Max(0d, (_lockSwap * 2d) - 1d);
        foreach (int i in current)
        {
            double chip = _lockSwapFrom is null || _lockSwapFrom.Contains(i) ? 1d : arriving;
            if (chip <= 0.004d)
                continue;
            DrawButton(
                g,
                ChipBounds(bar, i),
                IconAssetFor(i),
                i == _hoverButton,
                i == _pressedButton,
                i == _focusButton && ContainsFocus,
                chip);
        }

        if (_lockSwapFrom is not null)
        {
            double leavingAlpha = 1d - Math.Min(1d, _lockSwap * 2d);
            if (leavingAlpha > 0.004d)
            {
                // Clipped to the bar: the chips on their way out are anchored to an edge that
                // is moving past them, and without this they would spill onto the image.
                SD.Drawing2D.GraphicsState state = g.Save();
                g.IntersectClip(bar);
                foreach (int i in _lockSwapFrom)
                {
                    if (!current.Contains(i))
                        DrawButton(g, ChipBounds(bar, i), IconAssetFor(i), false, false, false, leavingAlpha);
                }
                g.Restore(state);
            }
        }

        DrawZoomChip(g, bar);
    }

    /// <summary>
    /// The pin's scale, as a chip under the toolbar, for as long as the chrome is up. It used
    /// to be knowable only in the 800 ms after a wheel tick, so "is this pin at 100 %?" -- the
    /// question you ask before reading pixels off it -- had no answer at rest.
    /// </summary>
    private void DrawZoomChip(SD.Graphics g, SD.Rectangle bar)
    {
        SD.Rectangle chip = ZoomChipBounds(bar);
        if (chip.IsEmpty)
            return;

        string text = ZoomChipText(_scale);
        using var font = ThemePalette.UiFont(ThemePalette.TextCaptionPt, SD.FontStyle.Bold);
        int h = chip.Height;
        using var bg = new SD.SolidBrush(Fade(SD.Color.FromArgb(235, ThemePalette.ToolbarBg)));
        using var border = new SD.Pen(Fade(ThemePalette.Border));
        FillRoundedRect(g, bg, chip, h / 2);
        DrawRoundedRect(g, border, chip, h / 2);
        WF.TextRenderer.DrawText(g, text, font, chip, Fade(ThemePalette.TextPrimary),
            WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
            WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// Where the zoom chip goes, or Empty when the pin has no room for it. Shared with
    /// <see cref="ChromeInvalidationBounds"/> so the fade repaints exactly what it draws.
    /// </summary>
    private SD.Rectangle ZoomChipBounds(SD.Rectangle bar)
    {
        SD.Size size = ZoomChipTextSize(ZoomChipText(_scale));
        int w = size.Width + Ui(14);
        int h = size.Height + Ui(6);
        var chip = new SD.Rectangle(bar.Left + ((bar.Width - w) / 2), bar.Bottom + Ui(4), w, h);
        // A tiny pin has no room under (or beside) the bar; the chip is not worth clipping
        // the image for.
        return chip.Bottom > ClientSize.Height - Ui(2) || chip.Left < 0 || chip.Right > ClientSize.Width
            ? SD.Rectangle.Empty
            : chip;
    }

    /// <summary>
    /// The chip's measured size, cached against the string and the DPI. This is read from
    /// ChromeInvalidationBounds, which runs on every frame of the hover fade -- a Font plus a
    /// MeasureText per frame for a string that only changes on a wheel tick.
    /// </summary>
    private SD.Size ZoomChipTextSize(string text)
    {
        if (_zoomChipTextFor == text && _zoomChipTextDpi == DeviceDpi)
            return _zoomChipTextSize;

        using var font = ThemePalette.UiFont(ThemePalette.TextCaptionPt, SD.FontStyle.Bold);
        _zoomChipTextSize = WF.TextRenderer.MeasureText(text, font);
        _zoomChipTextFor = text;
        _zoomChipTextDpi = DeviceDpi;
        return _zoomChipTextSize;
    }

    private SD.Size _zoomChipTextSize;
    private string? _zoomChipTextFor;
    private int _zoomChipTextDpi;

    /// <summary>
    /// The chip's label. Rounds to whole percent, and never says "0%" for a scale that is
    /// merely small -- a 32,000 px scrolling capture fits the screen at about 0.4 %.
    /// </summary>
    internal static string ZoomChipText(double scale)
    {
        double percent = scale * 100d;
        if (percent >= 10d) return $"{Math.Round(percent):0}%";
        if (percent >= 1d) return $"{percent:0.#}%";
        return $"{Math.Max(percent, 0.1d):0.0}%";
    }

    private string IconAssetFor(int index)
    {
        // The lock toggle reflects current state: closed padlock when locked, open when unlocked.
        if (string.Equals(_toolbarButtons[index].Tip, "Lock", StringComparison.Ordinal))
            return _locked ? "quick-access-lock.svg" : "quick-access-unlock.svg";
        return _toolbarButtons[index].IconAsset;
    }

    private void DrawButton(SD.Graphics g, SD.Rectangle bounds, string iconAsset, bool hot,
        bool pressed, bool focused, double chip = 1d)
    {
        // Rest = dim glyph, hover = HoverFill circle, pressed = that same wash with the press
        // shade stacked over it — the order Theme.xaml's Hover/Press layers composite in, so
        // the chips press the same direction as every other control.
        if (hot || pressed)
        {
            using var hover = new SD.SolidBrush(Fade(ThemePalette.HoverFill, chip));
            g.FillEllipse(hover, bounds);
        }
        if (pressed)
        {
            using var press = new SD.SolidBrush(Fade(ThemePalette.PressedShade, chip));
            g.FillEllipse(press, bounds);
        }

        SD.Color glyphColor = hot || pressed ? ThemePalette.TextPrimary : ThemePalette.TextSecondary;
        // Never wider than the chip that holds it: on a pin too small for a full-size bar the
        // chip shrinks, and a fixed 16 px glyph would spill over its own hover circle.
        int iconSize = Math.Max(1, Math.Min(Ui(ToolbarIconSizeLogical), bounds.Width - Ui(4)));
        SD.Bitmap? icon = SvgIcons.Get(iconAsset, iconSize, glyphColor);
        if (icon is not null)
        {
            var at = new SD.Rectangle(
                bounds.Left + (bounds.Width - icon.Width) / 2,
                bounds.Top + (bounds.Height - icon.Height) / 2,
                icon.Width,
                icon.Height);
            DrawFaded(g, icon, at, chip);
        }

        if (focused)
        {
            // Dotted ring following the circular button, like the card's DrawFocus.
            using var pen = new SD.Pen(Fade(ThemePalette.TextPrimary, chip), 1)
            {
                DashStyle = SD.Drawing2D.DashStyle.Dot,
            };
            g.DrawEllipse(pen, SD.Rectangle.Inflate(bounds, -2, -2));
        }
    }

    private void DrawLockBadge(SD.Graphics g)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        var badge = new SD.Rectangle(Ui(6), Ui(6), Ui(22), Ui(22));
        using var bg = new SD.SolidBrush(SD.Color.FromArgb(217, ThemePalette.ToolbarBg));
        using var border = new SD.Pen(ThemePalette.Accent);
        g.FillEllipse(bg, badge);
        g.DrawEllipse(border, badge);

        SD.Bitmap? icon = SvgIcons.Get("quick-access-lock.svg", Ui(12), ThemePalette.TextPrimary);
        if (icon is not null)
        {
            g.DrawImageUnscaled(
                icon,
                badge.Left + (badge.Width - icon.Width) / 2,
                badge.Top + (badge.Height - icon.Height) / 2);
        }
    }

    /// <summary>Draws a glyph at the chrome's current fade amount. TextureBrush and
    /// DrawImageUnscaled have no alpha knob, so the multiply goes through a colour matrix.</summary>
    private void DrawFaded(SD.Graphics g, SD.Bitmap icon, SD.Rectangle at, double chip = 1d)
    {
        double amount = _chromeAlpha * chip;
        if (amount >= 0.999d)
        {
            g.DrawImageUnscaled(icon, at.Left, at.Top);
            return;
        }

        DrawWithAlpha(g, icon, at, amount);
    }

    /// <summary>Roughly a 4K screen. A pin never shows more than a monitor's worth of itself
    /// at once, so this is the most a resample can ever be worth.</summary>
    private const int ResampleBudgetPx = 8_000_000;

    /// <summary>
    /// The slice of the client worth resampling for this paint. The client is unbounded --
    /// PinInteraction.MaxScale over a 32,000 px scrolling capture is a 96,000 px tall window,
    /// a 1.1 GB copy at 32bpp -- while the part of it on screen is never more than a monitor.
    /// Windows already clips the update region to the visible part, so the paint's own clip
    /// rect is that slice; it is snapped out to a 64 px grid so the small chrome repaints
    /// land inside the band the last full paint built instead of resampling a new one every
    /// frame of the hover fade.
    /// </summary>
    private static SD.Rectangle ResampleBand(SD.Rectangle client, SD.Rectangle clip)
    {
        if ((long)client.Width * client.Height <= ResampleBudgetPx)
            return client;

        static int Down(int v) => Math.Max(0, (v / 64) * 64);
        static int Up(int v) => ((v + 63) / 64) * 64;

        int left = Down(clip.Left);
        int top = Down(clip.Top);
        SD.Rectangle band = SD.Rectangle.Intersect(
            client, new SD.Rectangle(left, top, Up(clip.Right) - left, Up(clip.Bottom) - top));
        if (band.Width <= 0 || band.Height <= 0)
            band = SD.Rectangle.Intersect(client, new SD.Rectangle(0, 0, 64, 64));
        if ((long)band.Width * band.Height > ResampleBudgetPx)
            band.Height = Math.Max(1, ResampleBudgetPx / Math.Max(1, band.Width));
        return band;
    }

    /// <summary>
    /// <paramref name="band"/> of the pinned image drawn at the client size, resampled at most
    /// once per (size, band). Returns _image itself at 1:1, so a pin at natural size holds no
    /// second copy of its bitmap. The band is positioned by translating the full-size draw, not
    /// by scaling a crop, so it samples exactly as a whole-client resample would and moving the
    /// band cannot leave a seam.
    /// </summary>
    private SD.Bitmap ScaledImage(SD.Size size, SD.Rectangle band)
    {
        if (size == _image.Size && band.Location == SD.Point.Empty && band.Size == size)
            return _image;
        if (_scaled is not null && _scaledFor == size && _scaledBand == band)
            return _scaled;

        var copy = new SD.Bitmap(band.Width, band.Height, SD.Imaging.PixelFormat.Format32bppPArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(copy))
        {
            g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(_image, new SD.Rectangle(-band.X, -band.Y, size.Width, size.Height));
        }

        _scaled?.Dispose();
        _scaled = copy;
        _scaledFor = size;
        _scaledBand = band;
        return copy;
    }

    private static void DrawWithAlpha(SD.Graphics g, SD.Bitmap image, SD.Rectangle at, double alpha)
    {
        var matrix = new SD.Imaging.ColorMatrix { Matrix33 = (float)Math.Clamp(alpha, 0d, 1d) };
        using var attributes = new SD.Imaging.ImageAttributes();
        attributes.SetColorMatrix(matrix);
        g.DrawImage(image, at, 0, 0, image.Width, image.Height, SD.GraphicsUnit.Pixel, attributes);
    }

    /// <summary>
    /// Runs the pinned image toward its target alpha: full at rest, and
    /// PinInteraction.LockedOpacity while a locked pin is hovered, so the dim is something
    /// you cause by pointing at it rather than a permanent penalty for locking.
    /// </summary>
    private void UpdateImageAlpha()
    {
        double target = _locked && _mouseInside ? PinInteraction.LockedOpacity(1d) : 1d;
        if (Math.Abs(_imageAlpha - target) < 0.001d)
            return;

        double from = _imageAlpha;
        _imageFade?.Dispose();
        // Same mirror as the chrome, read on the dim rather than on the alpha: the dim
        // arriving is alpha falling, so the arguments are the other way round.
        _imageFade = Motion.Run(Motion.QuickMs, ExitAware(target, from),
            p =>
            {
                if (IsDisposed) return;
                _imageAlpha = from + ((target - from) * p);
                Invalidate();
            },
            () => { _imageFade?.Dispose(); _imageFade = null; });
    }

    /// <summary>
    /// The pointer is over a locked pin. HTTRANSPARENT means no WM_MOUSEMOVE and so no
    /// WM_MOUSELEAVE either, so hover is noticed here (WM_NCHITTEST still arrives) and the
    /// leaving is watched for by a timer that runs only while it matters.
    /// </summary>
    private void NoteLockedHover()
    {
        if (!_mouseInside)
        {
            _mouseInside = true;
            UpdateChromeAlpha();
            UpdateImageAlpha();
            Invalidate();
        }
        _lockedHoverTimer.Start();
    }

    private void DrawReadout(SD.Graphics g, string text, SD.Point near)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using var font = ThemePalette.UiFont(ThemePalette.TextBodyPt);
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        int w = size.Width + 16;
        int h = size.Height + 8;
        int x = Math.Clamp(near.X + 12, 2, Math.Max(2, ClientSize.Width - w - 2));
        int y = Math.Clamp(near.Y + 12, 2, Math.Max(2, ClientSize.Height - h - 2));
        var pill = new SD.Rectangle(x, y, w, h);

        using var bg = new SD.SolidBrush(SD.Color.FromArgb(235, ThemePalette.ToolbarBg));
        using var border = new SD.Pen(ThemePalette.Border);
        FillRoundedRect(g, bg, pill, h / 2);
        DrawRoundedRect(g, border, pill, h / 2);

        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(g, text, font, pill, ThemePalette.TextPrimary, flags);
    }

    private void ShowReadout(string text, SD.Point at)
    {
        _readoutText = text;
        _readoutPoint = at;
        _readoutTimer.Stop();
        _readoutTimer.Start();
        Invalidate();
    }

    private int HitTestButton(SD.Point point)
    {
        if (!_mouseInside)
            return -1;
        foreach (int i in VisibleButtons)
        {
            if (ButtonBounds(i).Contains(point))
                return i;
        }
        return -1;
    }

    private void SetHoverButton(int index)
    {
        if (_hoverButton == index)
            return;
        _hoverButton = index;
        _tooltipTimer.Stop();
        HideActionTooltip();
        if (index >= 0)
            _tooltipTimer.Start();
        Invalidate(ToolbarBounds());
    }

    private void SetPressed(int index)
    {
        if (_pressedButton == index)
            return;
        _pressedButton = index;
        Invalidate(ToolbarBounds());
    }

    private string TooltipTextFor(int index) => _toolbarButtons[index].Tip switch
    {
        "Copy" => "Copy (Ctrl+C)",
        "Save" => "Save (Ctrl+S)",
        "Lock" => _locked ? "Unlock — Ctrl+L" : "Lock (click-through) — Ctrl+L",
        _ => _toolbarButtons[index].Tip,
    };

    private void ShowActionTooltip()
    {
        // Same 300ms labeling pattern as FastQuickActionsWindow.ShowActionTooltip.
        _tooltipTimer.Stop();
        if (IsDisposed || !Visible || !ToolbarVisible || _hoverButton < 0 || _hoverButton >= _toolbarButtons.Count)
            return;

        var tooltip = new QuickAccessTooltipWindow(TooltipTextFor(_hoverButton), _visuals, DeviceDpi);
        _tooltipWindow = tooltip;
        tooltip.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_tooltipWindow, tooltip))
                _tooltipWindow = null;
        };
        tooltip.ShowBelow(this, RectangleToScreen(ButtonBounds(_hoverButton)));
    }

    private void HideActionTooltip()
    {
        QuickAccessTooltipWindow? tooltip = _tooltipWindow;
        _tooltipWindow = null;
        if (tooltip is null || tooltip.IsDisposed)
            return;

        tooltip.Close();
        tooltip.Dispose();
    }

    // ----- Scale / layout ----------------------------------------------------

    private void ApplyScale()
    {
        // No +2: the image fills the client area now, so the window is exactly the scaled
        // image. The two extra pixels existed only to make room for the baked hairline.
        ClientSize = new SD.Size(
            Math.Max(1, (int)Math.Round(_naturalWidth * _scale)),
            Math.Max(1, (int)Math.Round(_naturalHeight * _scale)));
    }

    private void SyncScaleFromClientSize()
    {
        // Resize via the border edges drives ClientSize; derive _scale back from it so
        // wheel-resize and keyboard nudges keep working against the new size.
        // No MinScale floor here: the constructor's FIT scale for a huge image (a tall
        // scrolling capture) is legitimately below MinScale, and clamping it up
        // desynced _scale from the actual window — the first wheel notch then jumped
        // the pin ~3x. Interactive paths (AdjustScale, WM_SIZING) enforce the minimum.
        double sx = ClientSize.Width / (double)_naturalWidth;
        double sy = ClientSize.Height / (double)_naturalHeight;
        _scale = Math.Min(Math.Max(sx, sy), PinInteraction.MaxScale);
    }

    // ----- Mouse -------------------------------------------------------------

    private void OnMouseDown(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Middle)
        {
            Close();
            return;
        }

        if (e.Button != WF.MouseButtons.Left)
            return;

        // A click on a toolbar button must not start a window drag; it fires on MouseUp.
        int button = HitTestButton(e.Location);
        if (button >= 0)
        {
            FocusToolbarButton(button);
            SetPressed(button);
            return;
        }

        _focusButton = -1;

        ReleaseCapture();
        SendMessage(Handle, WmNclbuttondown, HtCaption, IntPtr.Zero);
    }

    private void OnMouseMove(object? sender, WF.MouseEventArgs e)
    {
        SetHoverButton(HitTestButton(e.Location));
    }

    private void OnMouseUp(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left)
            return;

        SetPressed(-1);
        int index = HitTestButton(e.Location);
        if (index >= 0)
            _toolbarButtons[index].Action();
    }

    private void OnPinMouseWheel(object? sender, WF.MouseEventArgs e)
    {
        if ((ModifierKeys & WF.Keys.Control) == WF.Keys.Control)
        {
            Opacity = PinInteraction.AdjustOpacity(Opacity, e.Delta);
            // The transient pill, not the chip: opacity has no resting readout, and the chip
            // under the bar is the zoom's. Two gestures, but the pill is now only ever the
            // answer to the one that has nowhere else to report.
            ShowReadout($"Opacity {(int)Math.Round(Opacity * 100)}%", PointToClient(WF.Cursor.Position));
            return;
        }

        double newScale = PinInteraction.AdjustScale(_scale, e.Delta);
        if (Math.Abs(newScale - _scale) < 0.0001)
            return;

        double factor = newScale / _scale;
        Left -= (int)Math.Round(e.X * (factor - 1));
        Top -= (int)Math.Round(e.Y * (factor - 1));
        _scale = newScale;
        // No transient readout for zoom any more: the chip under the toolbar is showing the
        // scale the whole time the chrome is up, which is whenever a wheel tick can happen.
        ApplyScale();
        Invalidate();
    }

    private void OnPinKeyDown(object? sender, WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Escape)
        {
            Close();
            return;
        }

        if (e.KeyCode == WF.Keys.L && e.Control)
        {
            SetLocked(!_locked);
            e.Handled = true;
            return;
        }

        if (e.KeyCode == WF.Keys.C && e.Control)
        {
            _ = CopyAsync();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == WF.Keys.S && e.Control)
        {
            _ = SaveAsync();
            e.Handled = true;
            return;
        }

        // Reset scale (and opacity) to 100% — keeps double-click reserved for close.
        if (e.KeyCode == WF.Keys.D0 && e.Control)
        {
            ResetToNatural();
            e.Handled = true;
            return;
        }

        int step = PinInteraction.NudgeStep(e.Shift);
        switch (e.KeyCode)
        {
            case WF.Keys.Left: Left -= step; e.Handled = true; break;
            case WF.Keys.Right: Left += step; e.Handled = true; break;
            case WF.Keys.Up: Top -= step; e.Handled = true; break;
            case WF.Keys.Down: Top += step; e.Handled = true; break;
        }
    }

    protected override bool ProcessCmdKey(ref WF.Message msg, WF.Keys keyData)
    {
        WF.Keys keyCode = keyData & WF.Keys.KeyCode;
        WF.Keys modifiers = keyData & WF.Keys.Modifiers;

        if (keyCode == WF.Keys.Tab && modifiers is WF.Keys.None or WF.Keys.Shift)
        {
            // Cycles the buttons this state actually offers, so Tab on a locked pin walks
            // Lock and Close rather than stopping on two chips that are not drawn.
            int[] offered = VisibleButtons;
            int direction = modifiers == WF.Keys.Shift ? -1 : 1;
            int slot = Array.IndexOf(offered, _focusButton);
            int nextSlot = slot < 0
                ? (direction > 0 ? 0 : offered.Length - 1)
                : (slot + direction + offered.Length) % offered.Length;
            FocusToolbarButton(offered[nextSlot]);
            return true;
        }

        if (_focusButton >= 0 && modifiers == WF.Keys.None &&
            keyCode is WF.Keys.Enter or WF.Keys.Space)
        {
            _toolbarButtons[_focusButton].Action();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void FocusToolbarButton(int index)
    {
        if (index < 0 || index >= _toolbarButtons.Count || !VisibleButtons.Contains(index))
            return;

        _focusButton = index;
        Focus();
        // Child ids are 1-based positions in the accessible tree, which walks VisibleButtons.
        AccessibilityNotifyClients(WF.AccessibleEvents.Focus, Array.IndexOf(VisibleButtons, index) + 1);
        UpdateChromeAlpha();
        Invalidate(ToolbarBounds());
    }

    private void ResetToNatural()
    {
        Opacity = 1.0;
        _visibleOpacity = 1.0;
        _scale = 1.0;
        ApplyScale();
        // The zoom chip covers the scale half of this, but only while the chrome is up --
        // and it never covered the opacity half at all, so Ctrl+0 with the pointer off the
        // pin used to report nothing about either.
        ShowReadout("100%", new SD.Point(ClientSize.Width / 2, ClientSize.Height / 2));
        Invalidate();
    }

    /// <summary>
    /// Hands the editor its own copy and leaves the pin where it is. Editing a pinned
    /// reference is a second look at it, not a replacement for it -- and the pin's bitmap is
    /// still being painted from.
    /// </summary>
    private void RaiseEditRequested()
    {
        Action<SD.Bitmap>? handler = EditRequested ?? DefaultEditHandler;
        if (handler is null)
        {
            // Clicking the pencil and getting nothing, with nothing said, is the one failure
            // mode this chip has; every other new action in the app pairs success with a line.
            // Through the toast, not the zoom readout: that pill is neutral chrome held for
            // 800 ms, which is the confirmation dwell, and an error gets the error colour and
            // the long one.
            App.Toast("Pin", "The editor is not available.", severity: ToastSeverity.Error);
            Log.Error("A pin's Edit chip fired with no editor handler registered");
            return;
        }

        try
        {
            handler(CaptureService.CloneBitmap(_image));
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the pinned screenshot in the editor", ex);
            App.Toast("Pin", "Couldn't open the editor. The pin is unchanged.",
                severity: ToastSeverity.Error);
        }
    }

    /// <summary>
    /// Locking used to be a one-way trip: WS_EX_TRANSPARENT on the whole window meant the pin
    /// received no mouse messages at all, so its own controls were gone and the tray's
    /// "Unlock pinned windows" was the only way back. Click-through is now answered per point
    /// in WM_NCHITTEST instead, which lets the lock and close chips stay hit-testable while
    /// every other pixel passes clicks through exactly as before. The dim is on the image
    /// (<see cref="UpdateImageAlpha"/>), not on Form.Opacity, so the chips stay legible.
    /// </summary>
    private void SetLocked(bool locked)
    {
        if (_locked == locked)
            return;

        int[] leaving = VisibleButtons; // read before the flip; it is what the swap dissolves
        _locked = locked;
        if (locked)
        {
            _focusButton = -1;
            SetHoverButton(-1);
            SetPressed(-1);
        }
        else
        {
            _lockedHoverTimer.Stop();
        }

        _lockItem.Text = locked ? "Unlock (Ctrl+L)" : "Lock (Ctrl+L)";
        StartLockSwap(leaving);
        UpdateChromeAlpha();
        UpdateImageAlpha();
        Invalidate();
    }

    /// <summary>
    /// Glides the toolbar from the row it had to the row lock leaves it with. Lock is always
    /// toggled with the pointer on the pin, so the bar is up and ToolbarVisible does not
    /// change -- UpdateChromeAlpha returns early and nothing else would move the bar at all.
    /// </summary>
    private void StartLockSwap(int[] leaving)
    {
        _lockFade?.Dispose();
        _lockFade = null;
        _lockSwapFrom = null;
        _lockSwap = 1d;

        // Nothing is on screen to transition -- Ctrl+L from the keyboard with the chrome
        // down. The next hover simply draws the row lock left behind.
        if (_chromeAlpha <= 0.004d || leaving.SequenceEqual(VisibleButtons))
            return;

        _lockSwapFrom = leaving;
        _lockSwap = 0d;
        _lockFade = Motion.Run(Motion.SwapMs, Motion.EaseOutCubic,
            p =>
            {
                if (IsDisposed) return;
                _lockSwap = p;
                Invalidate(ChromeInvalidationBounds());
            },
            () =>
            {
                _lockFade?.Dispose();
                _lockFade = null;
                SD.Rectangle dirty = ChromeInvalidationBounds(); // still knows both widths
                _lockSwapFrom = null;
                _lockSwap = 1d;
                if (!IsDisposed)
                    Invalidate(dirty);
            });
    }

    // ----- Resize border + aspect ratio --------------------------------------

    protected override void WndProc(ref WF.Message m)
    {
        if (m.Msg == WmNchittest && _locked)
        {
            // Click-through everywhere except the pin's own escape hatches. The pointer being
            // here is also the only hover signal a locked pin gets.
            NoteLockedHover();
            m.Result = HitTestButton(PointToClient(WF.Cursor.Position)) >= 0
                ? (IntPtr)HtClient
                : (IntPtr)HtTransparent;
            return;
        }

        if (m.Msg == WmNchittest && !_locked)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)HtClient)
            {
                int hit = HitTestResizeBorder();
                if (hit != HtClient)
                    m.Result = (IntPtr)hit;
            }
            return;
        }

        if (m.Msg == WmSizing && !_locked)
        {
            base.WndProc(ref m);
            ConstrainAspectRatio(ref m);
            return;
        }

        base.WndProc(ref m);
    }

    private int HitTestResizeBorder()
    {
        SD.Point p = PointToClient(WF.Cursor.Position);
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        int resizeBorder = Ui(ResizeBorderLogical);
        bool left = p.X <= resizeBorder;
        bool right = p.X >= w - resizeBorder;
        bool top = p.Y <= resizeBorder;
        bool bottom = p.Y >= h - resizeBorder;

        // Don't steal the top edge from the hover toolbar buttons.
        if (top && HitTestButton(p) >= 0)
            return HtClient;

        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return HtClient;
    }

    private void ConstrainAspectRatio(ref WF.Message m)
    {
        var rc = Marshal.PtrToStructure<Rect>(m.LParam);
        int edge = m.WParam.ToInt32();

        // Non-client chrome is zero here (borderless form) and the image fills the client
        // area, so the window rect IS the image rect.
        double aspect = _naturalWidth / (double)_naturalHeight;
        int newWidth = rc.Right - rc.Left;
        int newHeight = rc.Bottom - rc.Top;

        // Floor matches PinInteraction.MinScale so a drag can't shrink the pin to nothing.
        int minImgW = Math.Max(16, (int)Math.Round(_naturalWidth * PinInteraction.MinScale));
        int minImgH = Math.Max(16, (int)Math.Round(_naturalHeight * PinInteraction.MinScale));
        int imgW = Math.Max(minImgW, newWidth);
        int imgH = Math.Max(minImgH, newHeight);

        bool horizontalDrag = edge is WmszLeft or WmszRight;
        bool verticalDrag = edge is WmszTop or WmszBottom;

        int targetImgW;
        int targetImgH;
        if (horizontalDrag)
        {
            targetImgW = imgW;
            targetImgH = (int)Math.Round(targetImgW / aspect);
        }
        else if (verticalDrag)
        {
            targetImgH = imgH;
            targetImgW = (int)Math.Round(targetImgH * aspect);
        }
        else
        {
            // Corner drag: let width lead, derive height.
            targetImgW = imgW;
            targetImgH = (int)Math.Round(targetImgW / aspect);
        }

        int targetW = targetImgW;
        int targetH = targetImgH;

        // Apply the constrained size against the anchored edge so the opposite side stays put.
        switch (edge)
        {
            case WmszLeft:
            case WmszTopLeft:
            case WmszBottomLeft:
                rc.Left = rc.Right - targetW;
                break;
            default:
                rc.Right = rc.Left + targetW;
                break;
        }

        switch (edge)
        {
            case WmszTop:
            case WmszTopLeft:
            case WmszTopRight:
                rc.Top = rc.Bottom - targetH;
                break;
            default:
                rc.Bottom = rc.Top + targetH;
                break;
        }

        Marshal.StructureToPtr(rc, m.LParam, false);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_locked)
            SyncScaleFromClientSize();
        ApplyRoundedRegion();
        Invalidate();
    }

    protected override void OnDpiChanged(WF.DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyRoundedRegion();
        Invalidate();
    }

    private void ApplyRoundedRegion()
    {
        SD.Region? next = null;
        if (_roundedCorners && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            using var path = PinWindowGeometry.RoundedClipPath(ClientSize, DeviceDpi);
            next = new SD.Region(path);
        }

        SD.Region? previous = Region;
        Region = next;
        previous?.Dispose();
    }

    private void DrawWindowBorder(SD.Graphics graphics, SD.Pen pen) =>
        PinWindowGeometry.DrawBorder(
            graphics,
            pen,
            ClientSize,
            DeviceDpi,
            _roundedCorners);

    // ----- Actions (reused by toolbar + context menu) ------------------------

    private Task? _copyTask;

    private async Task CopyAsync()
    {
        try
        {
            // Tracked so Close never disposes _image while the copy's own STA thread
            // is still cloning it (which silently dropped the copy).
            _copyTask = CaptureService.CopyToClipboardAsync(_image);
            await _copyTask;
        }
        catch (Exception ex)
        {
            Log.Error("Pin copy failed", ex);
            // A pin has no button label to flip and no status line of its own, so the toast
            // is its feedback channel — without it a busy clipboard left the pin looking as
            // though the copy had worked.
            App.Toast(
                "Copy failed",
                FriendlyError.For(ex),
                new SD.Point(Left, Top),
                severity: ToastSeverity.Error);
        }
    }

    private void DisposeImageWhenCopyDone()
    {
        Task? pending = _copyTask;
        if (pending is null || pending.IsCompleted)
        {
            _image.Dispose();
            return;
        }

        _ = pending.ContinueWith(
            _ => _image.Dispose(),
            TaskContinuationOptions.ExecuteSynchronously);
    }

    private async Task SaveAsync()
    {
        try
        {
            string fallbackFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinShot");
            string folder = PinInteraction.ResolveSaveFolder(_settings?.Current.SaveFolder, fallbackFolder);
            System.IO.Directory.CreateDirectory(folder);
            using var dialog = new WF.SaveFileDialog
            {
                FileName = _settings is null
                    ? CaptureService.DefaultFileName("png")
                    : FileNamer.Next(_settings, "png"),
                InitialDirectory = folder,
                Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image|*.webp",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            var copy = CaptureService.CloneBitmap(_image);
            await Task.Run(() =>
            {
                using (copy)
                    ImageSaver.Save(copy, dialog.FileName);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Pin save failed", ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static void FillRoundedRect(SD.Graphics g, SD.Brush brush, SD.Rectangle bounds, int radius)
    {
        using var path = GdiPaths.RoundedRect(bounds, radius);
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRect(SD.Graphics g, SD.Pen pen, SD.Rectangle bounds, int radius)
    {
        using var path = GdiPaths.RoundedRect(new SD.Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), radius);
        g.DrawPath(pen, path);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private sealed class FastPinAccessibleObject(FastPinWindow owner)
        : WF.Control.ControlAccessibleObject(owner)
    {
        public override string? Name
        {
            get => owner.AccessibleName;
            set => owner.AccessibleName = value;
        }

        public override string? Description => owner.AccessibleDescription;
        public override WF.AccessibleRole Role => WF.AccessibleRole.Window;

        // Children are the chips this state actually draws, in drawn order -- the same
        // VisibleButtons list ToolbarBounds, DrawToolbar, HitTestButton and Tab follow. A
        // locked pin used to offer a screen reader five buttons where two are painted, and
        // the three phantoms reported an empty rect.
        public override int GetChildCount() => owner.VisibleButtons.Length;

        public override WF.AccessibleObject? GetChild(int slot)
        {
            int[] offered = owner.VisibleButtons;
            return slot >= 0 && slot < offered.Length
                ? new ToolbarButtonAccessibleObject(owner, offered[slot], this)
                : null;
        }

        public override WF.AccessibleObject? GetFocused() =>
            GetChild(Array.IndexOf(owner.VisibleButtons, owner._focusButton)) ?? base.GetFocused();

        public override WF.AccessibleObject? HitTest(int x, int y)
        {
            SD.Point client = owner.PointToClient(new SD.Point(x, y));
            int button = owner.HitTestButton(client);
            return button >= 0
                ? GetChild(Array.IndexOf(owner.VisibleButtons, button))
                : base.HitTest(x, y);
        }
    }

    private sealed class ToolbarButtonAccessibleObject(
        FastPinWindow owner,
        int index,
        WF.AccessibleObject parent) : WF.AccessibleObject
    {
        public override string? Name
        {
            get => owner._toolbarButtons[index].Tip;
            set { }
        }

        public override string? Description => owner._toolbarButtons[index].Help;
        public override string? Help => owner._toolbarButtons[index].Help;
        public override string? DefaultAction => "Press";
        public override WF.AccessibleRole Role => WF.AccessibleRole.PushButton;
        public override WF.AccessibleObject? Parent => parent;
        public override SD.Rectangle Bounds => owner.RectangleToScreen(owner.ButtonBounds(index));

        public override WF.AccessibleStates State
        {
            get
            {
                WF.AccessibleStates state = WF.AccessibleStates.Focusable |
                                            WF.AccessibleStates.Selectable;
                if (owner._focusButton == index && owner.ContainsFocus)
                    state |= WF.AccessibleStates.Focused | WF.AccessibleStates.Selected;
                // Not "unavailable while locked" any more: a locked pin keeps Lock and Close
                // hit-testable, and those two are its only way out. What is genuinely gone is
                // a chip this state does not offer at all.
                if (!owner.VisibleButtons.Contains(index))
                    state |= WF.AccessibleStates.Invisible | WF.AccessibleStates.Unavailable;
                return state;
            }
        }

        public override void DoDefaultAction()
        {
            if (owner.VisibleButtons.Contains(index))
                owner._toolbarButtons[index].Action();
        }

        public override void Select(WF.AccessibleSelection flags)
        {
            if ((flags & (WF.AccessibleSelection.TakeFocus | WF.AccessibleSelection.TakeSelection)) != 0)
                owner.FocusToolbarButton(index);
        }

        public override WF.AccessibleObject? Navigate(WF.AccessibleNavigation navdir) => navdir switch
        {
            WF.AccessibleNavigation.Next or WF.AccessibleNavigation.Right => Sibling(1),
            WF.AccessibleNavigation.Previous or WF.AccessibleNavigation.Left => Sibling(-1),
            WF.AccessibleNavigation.Up => parent,
            _ => base.Navigate(navdir),
        };

        /// <summary>The next chip round the offered row, so navigation matches Tab.</summary>
        private WF.AccessibleObject? Sibling(int direction)
        {
            int[] offered = owner.VisibleButtons;
            int slot = Array.IndexOf(offered, index);
            return slot < 0 || offered.Length == 0
                ? null
                : parent.GetChild((slot + direction + offered.Length) % offered.Length);
        }
    }

    private sealed class ToolbarButton(string iconAsset, string tip, Action action)
    {
        public string IconAsset { get; } = iconAsset;
        public string Tip { get; } = tip;
        public string Help { get; } = tip switch
        {
            "Copy" => "Copy the pinned screenshot to the clipboard",
            "Save" => "Save the pinned screenshot to a local file",
            "Edit" => "Open a copy of the pinned screenshot in the editor",
            "Lock" => "Toggle click-through mode for this pinned screenshot",
            "Close" => "Close this pinned screenshot",
            _ => tip,
        };
        public Action Action { get; } = action;
    }
}
