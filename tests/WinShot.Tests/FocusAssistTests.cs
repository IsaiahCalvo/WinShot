using System.Collections.Generic;
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

    [Fact]
    public void Switches_CoverBothTheWindows10AndWindows11Keys()
    {
        Assert.Contains(FocusAssist.Switches, s => s.Value == "ToastEnabled");
        Assert.Contains(FocusAssist.Switches, s => s.Value == "NOC_GLOBAL_SETTING_TOASTS_ENABLED");
        Assert.All(FocusAssist.Switches, s => Assert.StartsWith(@"Software\Microsoft\Windows\", s.Key));
    }
}
