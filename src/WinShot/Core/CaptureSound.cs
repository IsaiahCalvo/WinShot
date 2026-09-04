using System.IO;
using System.Media;

namespace WinShot.Core;

/// <summary>
/// The shutter. Three synthesised clicks embedded in the binary (see
/// <c>tools\make-sounds.ps1</c>) plus "Silent", played fire-and-forget so a capture
/// never waits on audio.
/// </summary>
public static class CaptureSound
{
    /// <summary>The combo's four options, in the order the Settings pane lists them.</summary>
    public const string Default = "default";
    public const string Classic = "classic";
    public const string Subtle = "subtle";
    public const string Silent = "silent";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SoundPlayer?> Players = new();

    /// <summary>Maps a stored <c>ShutterSound</c> value to its asset, or null for silence.
    /// Unknown values fall back to the default click rather than to silence — a settings
    /// file from a newer build should still make a noise.</summary>
    public static string? AssetFor(string? shutterSound) => Normalize(shutterSound) switch
    {
        Silent => null,
        Classic => "shutter-classic.wav",
        Subtle => "shutter-subtle.wav",
        _ => "shutter-default.wav",
    };

    public static string Normalize(string? shutterSound)
    {
        string value = (shutterSound ?? "").Trim().ToLowerInvariant();
        return value is Silent or Classic or Subtle ? value : Default;
    }

    /// <summary>True when this settings state should make a sound at all.</summary>
    public static bool ShouldPlay(Settings settings) =>
        settings.PlaySounds && AssetFor(settings.ShutterSound) is not null;

    /// <summary>Plays the configured shutter, if any. Never throws and never blocks:
    /// a missing audio device or a busy mixer must not cost the user a capture.</summary>
    public static void Play(Settings settings)
    {
        if (!ShouldPlay(settings))
            return;
        try
        {
            Load(AssetFor(settings.ShutterSound)!)?.Play();
        }
        catch (Exception ex)
        {
            Log.Error("Capture sound failed to play", ex);
        }
    }

    /// <summary>Decodes each asset once. A SoundPlayer keeps its stream, so re-reading the
    /// resource per capture would allocate on the shutter path for nothing.</summary>
    private static SoundPlayer? Load(string asset) => Players.GetOrAdd(asset, static name =>
    {
        try
        {
            using Stream? stream = typeof(CaptureSound).Assembly.GetManifestResourceStream(
                $"WinShot.Assets.sounds.{name}");
            if (stream is null)
            {
                Log.Error($"Capture sound '{name}' is missing from the build");
                return null;
            }
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            var player = new SoundPlayer(buffer);
            player.Load();
            return player;
        }
        catch (Exception ex)
        {
            Log.Error($"Capture sound '{name}' could not be loaded", ex);
            return null;
        }
    });
}
