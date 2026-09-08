using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The save/restore contract for silencing notifications during a take. Driven through a fake
/// store so nothing here touches the real registry.
/// </summary>
public class FocusAssistTests
{
    private sealed class FakeStore
    {
        public readonly Dictionary<(string, string), int?> Values = new();
        public readonly List<(string Key, string Value, int? Data)> Writes = new();

        public int? Read(string key, string value) =>
            Values.TryGetValue((key, value), out int? existing) ? existing : null;

        public void Write(string key, string value, int? data)
        {
            Writes.Add((key, value, data));
            if (data is null)
                Values.Remove((key, value));
            else
                Values[(key, value)] = data;
        }
    }

    private static (FocusAssist.Session Session, FakeStore Store) NewSession()
    {
        var store = new FakeStore();
        return (new FocusAssist.Session(store.Read, store.Write), store);
    }

    [Fact]
    public void Suppress_TurnsEverySwitchOff()
    {
        (FocusAssist.Session session, FakeStore store) = NewSession();
        foreach ((string key, string value) in FocusAssist.Switches)
            store.Values[(key, value)] = 1;

        session.Suppress();

        Assert.True(session.Active);
        Assert.All(FocusAssist.Switches, s => Assert.Equal(0, store.Values[(s.Key, s.Value)]));
    }

    [Fact]
    public void Restore_PutsTheUsersOwnValuesBack()
    {
        (FocusAssist.Session session, FakeStore store) = NewSession();
        (string key, string value) first = FocusAssist.Switches[0];
        store.Values[(first.key, first.value)] = 1;

        session.Suppress();
        session.Restore();

        Assert.False(session.Active);
        Assert.Equal(1, store.Values[(first.key, first.value)]);
    }

    /// <summary>A machine that never wrote the value must not come back with a zero it never
    /// chose — restoring "nothing was here" means removing the value again.</summary>
    [Fact]
    public void Restore_RemovesValuesThatDidNotExistBefore()
    {
        (FocusAssist.Session session, FakeStore store) = NewSession();

        session.Suppress();
        session.Restore();

        Assert.All(FocusAssist.Switches, s => Assert.False(store.Values.ContainsKey((s.Key, s.Value))));
    }

    /// <summary>A restart re-enters Suppress while it is already held. Saving again would
    /// capture the zeros this class just wrote and restore notifications off forever.</summary>
    [Fact]
    public void Suppress_Twice_DoesNotOverwriteTheSavedState()
    {
        (FocusAssist.Session session, FakeStore store) = NewSession();
        (string key, string value) first = FocusAssist.Switches[0];
        store.Values[(first.key, first.value)] = 1;

        session.Suppress();
        session.Suppress();
        session.Restore();

        Assert.Equal(1, store.Values[(first.key, first.value)]);
    }

    [Fact]
    public void Restore_WithoutSuppress_WritesNothing()
    {
        (FocusAssist.Session session, FakeStore store) = NewSession();

        session.Restore();

        Assert.Empty(store.Writes);
    }

    /// <summary>
    /// The in-memory copy dies with the process, so the saved values go to disk BEFORE the
    /// switches are touched. Without that breadcrumb a crash mid-take left the user's
    /// notification banners off for good.
    /// </summary>
    [Fact]
    public void Suppress_PersistsTheSavedValuesBeforeTouchingTheSwitches()
    {
        var store = new FakeStore();
        List<(string Key, string Value, int? Data)>? persisted = null;
        var session = new FocusAssist.Session(
            store.Read,
            store.Write,
            saved => persisted = saved.ToList(),
            () => persisted = null);
        foreach ((string key, string value) in FocusAssist.Switches)
            store.Values[(key, value)] = 1;

        session.Suppress();

        Assert.NotNull(persisted);
        Assert.Equal(FocusAssist.Switches.Count, persisted!.Count);
        Assert.All(persisted, entry => Assert.Equal(1, entry.Data));
        // ...and it is written before the first switch moves, so a crash between the two
        // writes is still recoverable.
        Assert.All(FocusAssist.Switches, s => Assert.Equal(0, store.Values[(s.Key, s.Value)]));
    }

    [Fact]
    public void Restore_ClearsThePersistedCopy()
    {
        var store = new FakeStore();
        bool cleared = false;
        var session = new FocusAssist.Session(store.Read, store.Write, _ => { }, () => cleared = true);

        session.Suppress();
        Assert.False(cleared);
        session.Restore();

        Assert.True(cleared);
    }

    /// <summary>A crash means the breadcrumb is never cleared, which is exactly what the next
    /// launch reads to put the switches back.</summary>
    [Fact]
    public void Crash_LeavesThePersistedCopyBehindForTheNextLaunch()
    {
        var store = new FakeStore();
        List<(string Key, string Value, int? Data)>? persisted = null;
        var dying = new FocusAssist.Session(
            store.Read, store.Write, saved => persisted = saved.ToList(), () => persisted = null);
        foreach ((string key, string value) in FocusAssist.Switches)
            store.Values[(key, value)] = 1;

        dying.Suppress();
        // ...the process dies here: no Restore, so the breadcrumb survives.

        Assert.NotNull(persisted);
        foreach ((string key, string value, int? data) in persisted!)
            store.Write(key, value, data);

        Assert.All(FocusAssist.Switches, s => Assert.Equal(1, store.Values[(s.Key, s.Value)]));
    }

    /// <summary>File.WriteAllText is not atomic and the breadcrumb is written during a take
    /// that can be killed mid-write, so a truncated one is reachable. It used to throw before
    /// the delete that follows it, so the same unreadable file failed again on every launch.</summary>
    [Fact]
    public void ACorruptBreadcrumbIsDeletedRatherThanFailingOnEveryLaunch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winshot-focus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? previous = SettingsService.DirOverride;
        SettingsService.DirOverride = dir;
        try
        {
            string breadcrumb = Path.Combine(dir, "focus-assist.json");
            File.WriteAllText(breadcrumb, "{not json");

            Assert.False(FocusAssist.RecoverFromCrash());
            Assert.False(
                File.Exists(breadcrumb),
                "the unreadable breadcrumb was left on disk and will fail again every launch");
        }
        finally
        {
            SettingsService.DirOverride = previous;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Switches_CoverBothTheWindows10AndWindows11Keys()
    {
        Assert.Contains(FocusAssist.Switches, s => s.Value == "ToastEnabled");
        Assert.Contains(FocusAssist.Switches, s => s.Value == "NOC_GLOBAL_SETTING_TOASTS_ENABLED");
        Assert.All(FocusAssist.Switches, s => Assert.StartsWith(@"Software\Microsoft\Windows\", s.Key));
    }
}
