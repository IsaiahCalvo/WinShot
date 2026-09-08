namespace WinShot.Recording;

/// <summary>
/// How big the export will be, said in bytes before it runs and in real bytes after. The
/// editor used to print dimensions and settings and never once mentioned size, so the only way
/// to find out was to export and look in Explorer.
/// </summary>
public static class VideoExportEstimate
{
    /// <summary>
    /// AAC bitrate that <c>MediaEncodingProfile.CreateMp4</c> picks for the profiles this
    /// editor uses. Only the estimate reads it — the real encode uses whatever the profile
    /// says — so an approximation here is a slightly-off estimate, never a wrong file.
    /// </summary>
    public const uint AudioBitrate = 192_000;

    public static long Bytes(uint videoBitrate, uint audioBitrate, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return 0;
        double bits = ((double)videoBitrate + audioBitrate) * seconds;
        return (long)Math.Round(bits / 8);
    }

    /// <summary>
    /// A size a person can read: two significant figures below 10 units, none above, and never
    /// "0.0 MB" for a file that exists.
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 KB";

        double kb = bytes / 1024d;
        if (kb < 1)
            return "1 KB";
        if (kb < 1024)
            return $"{Math.Round(kb):0} KB";

        double mb = kb / 1024d;
        if (mb < 1024)
            return mb < 10 ? $"{mb:0.0} MB" : $"{Math.Round(mb):0} MB";

        double gb = mb / 1024d;
        return gb < 10 ? $"{gb:0.00} GB" : $"{gb:0.0} GB";
    }

    /// <summary>
    /// "61% of the original", or null when there is nothing meaningful to compare against —
    /// a missing source size, or an output that grew, which is not a saving worth boasting of.
    /// </summary>
    public static string? ComparedToSource(long outputBytes, long sourceBytes)
    {
        if (outputBytes <= 0 || sourceBytes <= 0)
            return null;

        int percent = (int)Math.Round(outputBytes * 100d / sourceBytes);
        return percent >= 100 ? null : $"{percent}% of the original";
    }
}

/// <summary>One entry in the export resolution picker, already resolved to real pixels.</summary>
public readonly record struct VideoResolutionPreset(string Label, uint Width, uint Height);

/// <summary>
/// The export resolution choices, resolved against the source this project actually holds, so
/// the picker says "1440 × 810 (75%)" rather than a bare percentage that means nothing until
/// you know what it is a percentage of.
/// </summary>
public static class VideoResolutionPresets
{
    /// <param name="sourceWidth">Natural width; zero falls back to 1920 × 1080, matching the
    /// export path's own fallback so the labels never disagree with the file.</param>
    public static IReadOnlyList<VideoResolutionPreset> For(uint sourceWidth, uint sourceHeight)
    {
        uint width = sourceWidth == 0 ? 1920u : sourceWidth;
        uint height = sourceHeight == 0 ? 1080u : sourceHeight;

        var presets = new List<VideoResolutionPreset>(5);
        // Even-dimension rounding can collapse two percentages onto the same size on a tiny
        // source, so the dedupe covers the whole list rather than only the named tiers.
        foreach ((string label, double scale) in new[] { ("Original", 1.0), ("75%", 0.75), ("50%", 0.5) })
        {
            VideoResolutionPreset preset = Scaled(label, width, height, scale);
            if (!presets.Any(p => p.Width == preset.Width && p.Height == preset.Height))
                presets.Add(preset);
        }

        // Named tiers are resolved against the real aspect, not assumed to be 16:9, and only
        // offered when they are actually smaller than the source — upscaling a recording adds
        // bytes and no detail.
        foreach ((string label, uint tierHeight) in new[] { ("1080p", 1080u), ("720p", 720u) })
        {
            if (tierHeight >= height)
                continue;
            double scale = tierHeight / (double)height;
            VideoResolutionPreset tier = Scaled(label, width, height, scale);
            if (presets.Any(p => p.Width == tier.Width && p.Height == tier.Height))
                continue;
            presets.Add(tier);
        }

        return presets;
    }

    private static VideoResolutionPreset Scaled(string label, uint width, uint height, double scale)
    {
        // H.264 needs even dimensions; the same rounding the export profile applies, so the
        // label cannot promise a size the encoder will not produce.
        uint outWidth = (uint)Math.Max(2, (int)Math.Round(width * scale) & ~1);
        uint outHeight = (uint)Math.Max(2, (int)Math.Round(height * scale) & ~1);
        return new VideoResolutionPreset($"{outWidth} × {outHeight} ({label})", outWidth, outHeight);
    }
}
