using System.IO;
using System.Text;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The shutter. Nothing here plays audio — the hidden-desktop runner has no business
/// making noise — so these pin the routing and the shipped assets instead.
/// </summary>
public class CaptureSoundTests
{
    [Theory]
    [InlineData("default", "shutter-default.wav")]
    [InlineData("classic", "shutter-classic.wav")]
    [InlineData("subtle", "shutter-subtle.wav")]
    [InlineData("silent", null)]
    public void AssetFor_MapsEveryOptionTheComboOffers(string option, string? asset)
    {
        Assert.Equal(asset, CaptureSound.AssetFor(option));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("from-a-newer-build")]
    public void UnknownValues_FallBackToAClick_NotToSilence(string? stored)
    {
        // Silence is a deliberate choice, never an accident of an unreadable value.
        Assert.Equal(CaptureSound.Default, CaptureSound.Normalize(stored));
        Assert.Equal("shutter-default.wav", CaptureSound.AssetFor(stored));
    }

    [Fact]
    public void ShouldPlay_NeedsBothTheToggleAndANonSilentSound()
    {
        Assert.False(CaptureSound.ShouldPlay(new Settings { PlaySounds = false, ShutterSound = "default" }));
        Assert.False(CaptureSound.ShouldPlay(new Settings { PlaySounds = true, ShutterSound = "silent" }));
        Assert.True(CaptureSound.ShouldPlay(new Settings { PlaySounds = true, ShutterSound = "classic" }));
    }

    [Fact]
    public void SoundsAreOffByDefault()
    {
        Assert.False(CaptureSound.ShouldPlay(new Settings()));
    }

    [Theory]
    [InlineData("shutter-default.wav")]
    [InlineData("shutter-classic.wav")]
    [InlineData("shutter-subtle.wav")]
    public void EverySoundShipsAsPlayable16BitMonoPcm(string asset)
    {
        using Stream? stream = typeof(CaptureSound).Assembly
            .GetManifestResourceStream($"WinShot.Assets.sounds.{asset}");
        Assert.NotNull(stream);

        using var reader = new BinaryReader(stream!, Encoding.ASCII);
        Assert.Equal("RIFF", new string(reader.ReadChars(4)));
        reader.ReadUInt32();
        Assert.Equal("WAVE", new string(reader.ReadChars(4)));
        Assert.Equal("fmt ", new string(reader.ReadChars(4)));
        Assert.Equal(16u, reader.ReadUInt32());       // PCM header size
        Assert.Equal(1, reader.ReadUInt16());         // format: PCM
        Assert.Equal(1, reader.ReadUInt16());         // mono
        Assert.Equal(44100u, reader.ReadUInt32());
        reader.ReadUInt32();
        reader.ReadUInt16();
        Assert.Equal(16, reader.ReadUInt16());        // bits per sample

        Assert.Equal("data", new string(reader.ReadChars(4)));
        uint bytes = reader.ReadUInt32();
        double seconds = bytes / (44100.0 * 2);
        // A shutter, not a jingle: long enough to hear, short enough that two captures
        // in a row do not overlap into a rattle.
        Assert.InRange(seconds, 0.04, 0.30);
    }

    [Fact]
    public void PlayIsSilentAndSafeWhenSoundsAreOff()
    {
        // No exception, no audio device touched.
        CaptureSound.Play(new Settings { PlaySounds = false });
        CaptureSound.Play(new Settings { PlaySounds = true, ShutterSound = CaptureSound.Silent });
    }
}
