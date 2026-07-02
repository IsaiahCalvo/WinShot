using System.IO;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Regression tests for the user-settings data-loss class: hotkeys and preferences must
/// survive upgrades, double-launches and crashes. The historical wipe: a second WinShot
/// instance (command forwarding) called Shutdown() before Load(), and OnExit's Save()
/// wrote pristine defaults over the user's file — then a second save rotated the backup
/// past the good copy too. These tests pin the guards that make that impossible.
/// Not parallel-safe with each other (shared static DirOverride), hence one collection.
/// </summary>
[Collection("SettingsPersistence")]
public class SettingsPersistenceTests : IDisposable
{
    private readonly string _dir;

    public SettingsPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "winshot-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        SettingsService.DirOverride = _dir;
    }

    public void Dispose()
    {
        SettingsService.DirOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private void WriteUserSettings(string hotkey)
    {
        var svc = new SettingsService();
        svc.Load();
        svc.Current.HotkeyCaptureRegion = hotkey;
        svc.Save();
    }

    [Fact]
    public void SaveBeforeLoad_IsRefused_AndNeverTouchesTheFile()
    {
        WriteUserSettings("Ctrl+Alt+F9");
        var before = File.ReadAllText(SettingsPath);

        // The second-instance path: a fresh service that never loaded tries to save on exit.
        var secondInstance = new SettingsService();
        secondInstance.Save();

        Assert.Equal(before, File.ReadAllText(SettingsPath)); // custom hotkey intact
    }

    [Fact]
    public void CustomHotkey_SurvivesLoadSaveCycle()
    {
        WriteUserSettings("Ctrl+Alt+F9");

        var reopened = new SettingsService();
        reopened.Load();
        Assert.Equal("Ctrl+Alt+F9", reopened.Current.HotkeyCaptureRegion);
        reopened.Save(); // e.g. exit save

        var again = new SettingsService();
        again.Load();
        Assert.Equal("Ctrl+Alt+F9", again.Current.HotkeyCaptureRegion);
    }

    [Fact]
    public void TwoBackupGenerations_KeepTheOldestGoodCopy()
    {
        WriteUserSettings("Ctrl+Alt+F9"); // save 1: user data in settings.json

        var svc = new SettingsService();
        svc.Load();
        svc.Current.ImageFormat = "jpg";
        svc.Save();                        // save 2: rotates user data into .bak
        svc.Current.ImageFormat = "png";
        svc.Save();                        // save 3: rotates into .bak2

        Assert.True(File.Exists(SettingsPath + ".bak"));
        Assert.True(File.Exists(SettingsPath + ".bak2"));
        // Parse rather than grep: the serializer escapes '+' as + in the raw JSON.
        var oldest = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath + ".bak2"));
        Assert.Equal("Ctrl+Alt+F9", oldest!.HotkeyCaptureRegion);
    }

    [Fact]
    public void CorruptPrimary_FallsBackToBackup()
    {
        WriteUserSettings("Ctrl+Alt+F9");
        var svc = new SettingsService();
        svc.Load();
        svc.Save(); // ensures .bak holds the user data
        File.WriteAllText(SettingsPath, "{ this is not json");

        var reopened = new SettingsService();
        reopened.Load();

        Assert.Equal("Ctrl+Alt+F9", reopened.Current.HotkeyCaptureRegion);
    }

    [Fact]
    public void UnreadablePrimaryWithNoBackups_IsPreservedBeforeFirstSave()
    {
        File.WriteAllText(SettingsPath, "{ this is not json"); // no backups exist

        var svc = new SettingsService();
        svc.Load();           // falls to defaults, arms preservation
        svc.Save();           // must preserve the corrupt file first

        Assert.NotEmpty(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
        Assert.Contains("not json", File.ReadAllText(Directory.GetFiles(_dir, "settings.json.corrupt-*")[0]));
    }
}
