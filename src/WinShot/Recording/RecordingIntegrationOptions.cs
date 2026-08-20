using WinShot.Capture;
using WinShot.Core;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Recording;

public readonly record struct RecordingAfterActions(
    bool ShowCompletionOverlay,
    bool CopyFile,
    bool OpenEditor)
{
    public static RecordingAfterActions FromSettings(Settings settings, bool isGif) => new(
        settings.RecordingShowOverlay,
        settings.RecordingCopy,
        !isGif && (settings.RecordingOpenEditor || settings.OpenVideoEditorAfterRecording));
}

public readonly record struct RecordingSessionUiOptions(
    bool ShowControls,
    bool ShowTimer,
    bool DimScreen)
{
    public static RecordingSessionUiOptions FromSettings(Settings settings) => new(
        settings.ShowRecordingControls,
        settings.ShowRecordingTimer,
        settings.DimScreenWhileRecording);
}

public static class RecordingAudioInputOptions
{
    public static bool ShouldForceMono(bool recordMicrophone, bool monoSetting) =>
        recordMicrophone && monoSetting;
}

public static class RecordingAreaMemory
{
    public static bool TryResolve(
        SD.Rectangle? selectedVirtualRegion,
        bool rememberLastSelection,
        string? savedScreenRegion,
        SD.Rectangle virtualScreen,
        out RecordingRegionSelection selection)
    {
        if (selectedVirtualRegion is SD.Rectangle selected)
        {
            selection = RecordingRegionSelection.FromVirtualSelection(selected, virtualScreen);
            return selection.IsUsable;
        }

        if (rememberLastSelection && PreviousRegion.TryParse(savedScreenRegion, out SD.Rectangle saved))
        {
            saved.Intersect(virtualScreen);
            selection = RecordingRegionSelection.FromScreenSelection(saved);
            return selection.IsUsable;
        }

        selection = default;
        return false;
    }
}

/// <summary>One display source placed on the recorder's composed canvas.</summary>
public sealed record RecordingDisplaySource(string DeviceName, SD.Rectangle Bounds, SD.Point CanvasPosition);

/// <summary>
/// Lays the displays a selection touches onto one canvas that mirrors their real
/// arrangement (canvas origin = top-left of the union of their bounds), so
/// ScreenRecorderLib can record any rectangle — single display, cross-display, or a
/// chosen subset of displays — with one crop.
/// </summary>
public readonly record struct RecordingDisplayComposition(
    IReadOnlyList<RecordingDisplaySource> Sources,
    SD.Rectangle SourceRect)
{
    public bool IsUsable => Sources.Count > 0 && SourceRect.Width >= 2 && SourceRect.Height >= 2;

    /// <param name="chosenDisplays">When the user explicitly picked displays, only those
    /// become sources; any other display inside the selection union stays black in the
    /// output. Null means every display the selection touches.</param>
    public static RecordingDisplayComposition Create(
        SD.Rectangle selection,
        IReadOnlyList<(string DeviceName, SD.Rectangle Bounds)> displays,
        IReadOnlyList<SD.Rectangle>? chosenDisplays = null)
    {
        var hits = displays
            .Where(d => chosenDisplays is null
                ? !SD.Rectangle.Intersect(d.Bounds, selection).IsEmpty
                : chosenDisplays.Contains(d.Bounds))
            .ToList();
        if (hits.Count == 0)
            return new RecordingDisplayComposition(Array.Empty<RecordingDisplaySource>(), SD.Rectangle.Empty);

        SD.Rectangle union = hits[0].Bounds;
        foreach (var hit in hits.Skip(1))
            union = SD.Rectangle.Union(union, hit.Bounds);

        SD.Rectangle crop = SD.Rectangle.Intersect(selection, union);
        crop.Offset(-union.X, -union.Y);
        crop.Width &= ~1;  // H.264 needs even dimensions
        crop.Height &= ~1;

        var sources = hits
            .Select(d => new RecordingDisplaySource(
                d.DeviceName,
                d.Bounds,
                new SD.Point(d.Bounds.X - union.X, d.Bounds.Y - union.Y)))
            .ToList();
        return new RecordingDisplayComposition(sources, crop);
    }
}

public static class RecordingOutputSize
{
    public static SD.Size CalculateVideo(
        SD.Size source,
        string? maxResolution,
        bool scaleHiDpi,
        double dpiScale)
    {
        double scale = scaleHiDpi && dpiScale > 1 ? 1 / dpiScale : 1;
        (int maxWidth, int maxHeight) = NormalizeMaxResolution(maxResolution);
        if (source.Height > source.Width)
            (maxWidth, maxHeight) = (maxHeight, maxWidth);
        if (maxWidth > 0 && maxHeight > 0)
            scale = Math.Min(scale, Math.Min(maxWidth / (double)source.Width, maxHeight / (double)source.Height));
        return ScaleEven(source, Math.Min(1, scale));
    }

    public static SD.Size CalculateGif(SD.Size source, string? maxWidthSetting)
    {
        if (!int.TryParse(maxWidthSetting, out int maxWidth) || maxWidth < 2 || source.Width <= maxWidth)
            return new SD.Size(Math.Max(2, source.Width), Math.Max(2, source.Height));
        return ScaleEven(source, maxWidth / (double)source.Width);
    }

    public static int GifPaletteColors(int quality, bool optimize)
    {
        if (!optimize)
            return 256;
        int normalized = Math.Clamp(quality, 0, 100);
        return Math.Clamp(16 + (int)Math.Round(normalized * 240 / 100.0), 16, 256);
    }

    private static (int Width, int Height) NormalizeMaxResolution(string? value) => value?.ToLowerInvariant() switch
    {
        "4k" => (3840, 2160),
        "1080p" => (1920, 1080),
        "720p" => (1280, 720),
        _ => (0, 0),
    };

    private static SD.Size ScaleEven(SD.Size source, double scale)
    {
        int width = Math.Max(2, (int)Math.Floor(source.Width * scale)) & ~1;
        int height = Math.Max(2, (int)Math.Floor(source.Height * scale)) & ~1;
        return new SD.Size(width, height);
    }
}

public static class RecordingControlBarPlacement
{
    public static SD.Point BottomCenter(SD.Rectangle workingArea, SD.Size barSize, int margin = 24) => new(
        workingArea.Left + Math.Max(0, (workingArea.Width - barSize.Width) / 2),
        workingArea.Bottom - barSize.Height - margin);
}

public static class RecordingMonitorDpi
{
    public static double ScaleFor(SD.Rectangle screenRect)
    {
        try
        {
            var nativeRect = new NativeRect(screenRect.Left, screenRect.Top, screenRect.Right, screenRect.Bottom);
            IntPtr monitor = MonitorFromRect(ref nativeRect, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 && x >= 96)
                return x / 96.0;
        }
        catch
        {
            // Older Windows builds may not expose per-monitor DPI through shcore.
        }
        return 1;
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
