using System.IO;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// SettingsPersistenceTests covers corruption and concurrent writes; nothing covered the
/// other way a settings.json goes wrong, which is age. Every upgrade reads a file written by
/// an older WinShot, and the failure mode is silent — the user's hotkeys quietly become the
/// defaults and only they can tell.
/// </summary>
[Collection("SettingsPersistence")]
public class SettingsMigrationTests : IDisposable
{
    private readonly string _dir;

    public SettingsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "winshot-settings-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        SettingsService.DirOverride = _dir;
    }

    public void Dispose()
    {
        SettingsService.DirOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    /// <summary>An early-version file: three keys, everything else absent.</summary>
    [Fact]
    public void AnAncientFileKeepsItsValuesAndDefaultsTheRest()
    {
        File.WriteAllText(SettingsPath, """
        {
          "PostCaptureAction": "copy",
          "HotkeyCaptureRegion": "Ctrl+Alt+F9",
          "SaveFolder": "C:\\Old\\Shots"
        }
        """);

        var settings = new SettingsService();
        settings.Load();

        Assert.Equal("copy", settings.Current.PostCaptureAction);
        Assert.Equal("Ctrl+Alt+F9", settings.Current.HotkeyCaptureRegion);
        Assert.Equal(@"C:\Old\Shots", settings.Current.SaveFolder);
        // A key the old file never had arrives at its default, not at null.
        Assert.False(string.IsNullOrEmpty(settings.Current.HotkeyCaptureFullscreen));
    }

    /// <summary>A key the app has since retired. System.Text.Json ignores unknown members by
    /// default; if that ever stopped being true, every upgrading user's file would fall to
    /// defaults in one release.</summary>
    [Fact]
    public void ARetiredKeyDoesNotWipeTheFile()
    {
        File.WriteAllText(SettingsPath, """
        {
          "HotkeyCaptureRegion": "Ctrl+Alt+F9",
          "LegacyThingThatNoLongerExists": { "a": 1, "b": [2, 3] },
          "AnotherRetiredFlag": true
        }
        """);

        var settings = new SettingsService();
        settings.Load();

        Assert.Equal("Ctrl+Alt+F9", settings.Current.HotkeyCaptureRegion);
    }

    /// <summary>
    /// The case with teeth, recorded as it actually behaves rather than as it should: a value
    /// whose TYPE changed — PostCaptureAction serialized as the enum number it once was —
    /// throws during deserialization, the load falls through to the backups, and the entire
    /// file is abandoned for defaults, every hotkey in it included. Nothing in the app writes
    /// that shape today, so this is a characterisation test: if a future release ever does
    /// change a property's type, this is the cost, and it should be paid deliberately.
    /// </summary>
    [Fact]
    public void AValueWhoseTypeChangedCostsTheWholeFile()
    {
        File.WriteAllText(SettingsPath, """
        {
          "PostCaptureAction": 1,
          "HotkeyCaptureRegion": "Ctrl+Alt+F9"
        }
        """);

        var settings = new SettingsService();
        settings.Load();

        Assert.NotEqual("Ctrl+Alt+F9", settings.Current.HotkeyCaptureRegion);
    }

    /// <summary>A torn write, with the good copy still in the .bak beside it.</summary>
    [Fact]
    public void ATornPrimaryFallsBackToTheBackup()
    {
        var seed = new SettingsService();
        seed.Load();
        seed.Current.HotkeyCaptureRegion = "Ctrl+Alt+F9";
        seed.Save();
        seed.Current.HotkeyCaptureRegion = "Ctrl+Alt+F8";
        seed.Save();   // rotates the previous good copy into .bak

        File.WriteAllText(SettingsPath, "{ \"HotkeyCaptureRegion\": ");

        var settings = new SettingsService();
        settings.Load();

        Assert.StartsWith("Ctrl+Alt+F", settings.Current.HotkeyCaptureRegion);
    }
}
