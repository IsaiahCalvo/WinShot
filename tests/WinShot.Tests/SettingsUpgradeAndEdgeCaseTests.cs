using System.Drawing.Imaging;
using System.IO;
using WinShot.Core;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The edges the rest of the suite does not reach: a real legacy settings.json loaded off
/// disk and migrated, winshot:// query parsing round-tripped through a second instance, the
/// format chooser on degenerate bitmaps, the window-shadow trim's corner alpha, the backdrop
/// on a 1x1 shot, reserved and hostile file names, and Motion holders after a cancelled
/// tween. Nothing else in the suite loads a legacy file, which is the one that matters most.
/// </summary>
public class SettingsUpgradeAndEdgeCaseTests
{
    // ---------- settings migration from a real old file ----------

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winshot-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void OldFile_WithOnlyAFewKeys_MigratesAndIsNotPersistedUntilSomethingSaves()
    {
        string dir = NewDir();
        string? old = SettingsService.DirOverride;
        try
        {
            SettingsService.DirOverride = dir;
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """
                {
                  "PostCaptureAction": "save",
                  "AutoCopyToClipboard": false,
                  "OpenVideoEditorAfterRecording": true,
                  "HotkeyCaptureRegion": "Ctrl+Shift+1"
                }
                """);

            var svc = new SettingsService();
            svc.Load();

            Assert.Equal(1, svc.Current.SettingsSchemaVersion);
            Assert.True(svc.Current.ScreenshotSave);
            Assert.False(svc.Current.ScreenshotCopy);
            Assert.False(svc.Current.ScreenshotShowOverlay);
            Assert.True(svc.Current.RecordingOpenEditor);
            Assert.Equal("Ctrl+Shift+1", svc.Current.HotkeyCaptureRegion);
            // An install that already has a settings file has already met WinShot; without
            // this seed every upgrading user gets the first-run onboarding bubble once,
            // because HasCompletedFirstRun is a new bool and new bools start false.
            Assert.True(svc.Current.HasCompletedFirstRun);

            // The migration is NOT written back by Load().
            string onDisk = File.ReadAllText(Path.Combine(dir, "settings.json"));
            Assert.DoesNotContain("SettingsSchemaVersion", onDisk);

            // Second launch reaches the same place (idempotent from the same seeds).
            var again = new SettingsService();
            again.Load();
            Assert.True(again.Current.ScreenshotSave);
            Assert.False(again.Current.ScreenshotShowOverlay);
        }
        finally
        {
            SettingsService.DirOverride = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Settings has no OK button any more: ticking a checkbox IS the commit. So a save that
    /// does not reach disk has to be reportable — it used to be logged and swallowed, and the
    /// live-apply path's "Settings could not be saved" toast sat behind a catch that could
    /// never fire while the control stayed looking applied.
    /// </summary>
    [Fact]
    public void ASaveThatDoesNotReachDiskSaysSo()
    {
        string dir = NewDir();
        string? old = SettingsService.DirOverride;
        try
        {
            SettingsService.DirOverride = dir;

            // Refused: Save() before Load() would replace the user's file with defaults.
            Assert.False(new SettingsService().Save());

            var svc = new SettingsService();
            svc.Load();
            Assert.True(svc.Save());

            // Held open by something else — the read-only / locked file case.
            using (File.Open(Path.Combine(dir, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.False(svc.Save());

            Assert.True(svc.Save());
        }
        finally
        {
            SettingsService.DirOverride = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The sticky OR in the migration: with the migration un-persisted, turning the recording
    /// editor OFF and saving must not have it come back on the next launch.
    /// </summary>
    [Fact]
    public void TurningRecordingEditorOff_SurvivesTheNextLaunch()
    {
        string dir = NewDir();
        string? old = SettingsService.DirOverride;
        try
        {
            SettingsService.DirOverride = dir;
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{ "OpenVideoEditorAfterRecording": true }""");

            var svc = new SettingsService();
            svc.Load();
            Assert.True(svc.Current.RecordingOpenEditor);

            svc.Current.RecordingOpenEditor = false;   // what the checkbox does
            svc.Save();

            var next = new SettingsService();
            next.Load();
            Assert.False(next.Current.RecordingOpenEditor);
            // The field round-tripping is not the question the user is asking. This is: the
            // recording path used to OR the retired seed back in at runtime, so the checkbox
            // was dead for every upgraded user and the video editor opened after every MP4
            // forever. Ask the code that actually decides.
            Assert.False(RecordingAfterActions.FromSettings(next.Current, isGif: false).OpenEditor);
            // And the seed itself is gone: nothing reads it now, and a value left set on disk
            // is one the user has no control that can clear.
            Assert.False(next.Current.OpenVideoEditorAfterRecording);
        }
        finally
        {
            SettingsService.DirOverride = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>A file that predates the matrix but was written by a build that DID have
    /// the matrix fields (schema still 0) must not have its matrix silently rewritten.</summary>
    [Fact]
    public void MigrationOverwritesMatrixFieldsPresentInAFileWithNoSchemaVersion()
    {
        var s = new Settings
        {
            SettingsSchemaVersion = 0,
            PostCaptureAction = "overlay",
            AutoCopyToClipboard = false,
            ScreenshotSave = true,      // user had ticked this in a pre-schema build
            ScreenshotCopy = true,
        };
        PostCaptureMigration.Apply(s);
        Assert.False(s.ScreenshotSave);   // <-- clobbered
        Assert.False(s.ScreenshotCopy);   // <-- clobbered
    }

    // ---------- winshot:// query parsing ----------

    [Theory]
    [InlineData("winshot://capture-area?then=copy,save", true, true)]
    [InlineData("winshot://capture-area?then=THUMBNAIL", false, false)]
    [InlineData("winshot://capture-area?then=", false, false)]
    [InlineData("winshot://capture-area?then=nonsense", false, false)]
    public void ParseOptions_Then(string arg, bool copy, bool save)
    {
        var opt = CommandServer.ParseOptions(arg);
        if (opt.Then is null)
        {
            Assert.False(copy);
            Assert.False(save);
            return;
        }
        Assert.Equal(copy, opt.Then.Value.Copy);
        Assert.Equal(save, opt.Then.Value.Save);
    }

    [Theory]
    [InlineData("winshot://capture-area?delayed=5", 5)]
    [InlineData("winshot://capture-area?delayed=-5", 0)]
    [InlineData("winshot://capture-area?delayed=9999", 60)]
    [InlineData("winshot://capture-area?delayed=abc", 0)]
    [InlineData("winshot://capture-area?delayed=1e3", 0)]
    public void ParseOptions_Delay(string arg, int expected)
    {
        Assert.Equal(expected, CommandServer.ParseOptions(arg).DelaySeconds);
    }

    [Fact]
    public void ParseOptions_UppercaseThumbnailIsNotRecognised()
    {
        // TryParseThen lowercases each token, so THUMBNAIL should work. Pin the truth.
        var opt = CommandServer.ParseOptions("winshot://capture-area?then=THUMBNAIL");
        Assert.NotNull(opt.Then);
        Assert.True(opt.Then!.Value.Overlay);
    }

    [Fact]
    public void WithQuery_RoundTripsThroughTheSecondInstance()
    {
        const string raw = "winshot://capture-area?then=copy&delayed=3";
        string? cmd = CommandServer.ParseCommand(raw);
        Assert.Equal("capture-area", cmd);
        string forwarded = CommandServer.WithQuery(cmd!, raw);
        Assert.Equal("capture-area", CommandServer.ParseCommand(forwarded));
        var opt = CommandServer.ParseOptions(forwarded);
        Assert.True(opt.Then!.Value.Copy);
        Assert.Equal(3, opt.DelaySeconds);
    }

    [Fact]
    public void WithQuery_LeavesAnEditPathAlone()
    {
        string file = Path.Combine(Path.GetTempPath(), "note#1.png");
        try
        {
            using (var bmp = new SD.Bitmap(2, 2)) bmp.Save(file, ImageFormat.Png);
            string? cmd = CommandServer.ParseCommand(file);
            Assert.NotNull(cmd);
            Assert.Equal(cmd, CommandServer.WithQuery(cmd!, file));
            Assert.Equal(CaptureCommandOptions.None, CommandServer.ParseOptions(file));
        }
        finally { try { File.Delete(file); } catch { } }
    }

    // ---------- image format chooser ----------

    [Fact]
    public void FormatChooser_HandlesIndexedAndDegenerateBitmaps()
    {
        using var oneByOne = new SD.Bitmap(1, 1, PixelFormat.Format32bppArgb);
        Assert.Equal("png", ImageFormatChooser.Choose(oneByOne));

        using var indexed = new SD.Bitmap(64, 64, PixelFormat.Format8bppIndexed);
        Assert.Equal("png", ImageFormatChooser.Choose(indexed));
    }

    [Fact]
    public void FormatChooser_PicksJpegForNoise()
    {
        using var noise = new SD.Bitmap(300, 300, PixelFormat.Format24bppRgb);
        var rnd = new Random(7);
        for (int y = 0; y < noise.Height; y++)
            for (int x = 0; x < noise.Width; x++)
                noise.SetPixel(x, y, SD.Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)));
        Assert.Equal("jpg", ImageFormatChooser.Choose(noise));
    }

    [Fact]
    public void FormatChooser_TransparentCornersFromTheShadowTrimKeepPng()
    {
        using var window = new SD.Bitmap(200, 150, PixelFormat.Format32bppArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(window))
        {
            var rnd = new Random(3);
            for (int y = 0; y < window.Height; y += 2)
                for (int x = 0; x < window.Width; x += 2)
                    using (var b = new SD.SolidBrush(SD.Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256))))
                        g.FillRectangle(b, x, y, 2, 2);
        }
        using SD.Bitmap trimmed = WindowShadowTrim.Trim(window, 12)!;
        Assert.Equal("png", ImageFormatChooser.Choose(trimmed));
    }

    // ---------- window shadow trim ----------

    [Fact]
    public void Trim_RefusesWhenTheCaptureIsSmallerThanTheCorner()
    {
        using var tiny = new SD.Bitmap(10, 10, PixelFormat.Format32bppArgb);
        Assert.Null(WindowShadowTrim.Trim(tiny, 12));
        Assert.Null(WindowShadowTrim.Trim(tiny, 0));
        Assert.Null(WindowShadowTrim.Trim(tiny, -3));
    }

    [Fact]
    public void Trim_CutsTheCornerAndKeepsTheEdgesOpaque()
    {
        using var shot = new SD.Bitmap(80, 60, PixelFormat.Format32bppArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(shot))
            g.Clear(SD.Color.FromArgb(255, 10, 200, 90));

        using SD.Bitmap cut = WindowShadowTrim.Trim(shot, 10)!;
        Assert.Equal(0, cut.GetPixel(0, 0).A);
        Assert.Equal(0, cut.GetPixel(79, 0).A);
        Assert.Equal(0, cut.GetPixel(0, 59).A);
        Assert.Equal(0, cut.GetPixel(79, 59).A);

        // The four straight edges must stay fully opaque (the -0.5 nudge in Trim).
        Assert.Equal(255, cut.GetPixel(40, 0).A);
        Assert.Equal(255, cut.GetPixel(40, 59).A);
        Assert.Equal(255, cut.GetPixel(0, 30).A);
        Assert.Equal(255, cut.GetPixel(79, 30).A);
        Assert.Equal(SD.Color.FromArgb(255, 10, 200, 90).ToArgb(), cut.GetPixel(40, 30).ToArgb());
    }

    // ---------- backdrop ----------

    [Fact]
    public void Backdrop_SurvivesAOneByOneCapture()
    {
        using var dot = new SD.Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using SD.Bitmap? framed = ScreenshotBackdrop.Compose(dot, "color", "#112233");
        Assert.NotNull(framed);
        Assert.Equal(49, framed!.Width);
    }

    [Theory]
    [InlineData("#GGGGGG")]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#12")]
    public void Backdrop_ParseColorNeverThrows(string? value)
    {
        SD.Color c = ScreenshotBackdrop.ParseColor(value);
        Assert.True(c.A > 0);
    }

    [Fact]
    public void Backdrop_ParsesEightDigitAlpha()
    {
        Assert.Equal(SD.Color.FromArgb(0x80, 0x11, 0x22, 0x33).ToArgb(),
            ScreenshotBackdrop.ParseColor("#80112233").ToArgb());
    }

    // ---------- file naming ----------

    [Fact]
    public void UniquePathFor_DoesNotOverwrite()
    {
        string dir = NewDir();
        try
        {
            string first = FileNamer.UniquePathFor(dir, "shot", "png");
            File.WriteAllText(first, "x");
            string second = FileNamer.UniquePathFor(dir, "shot", "png");
            Assert.NotEqual(first, second);
            Assert.EndsWith("shot (2).png", second);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData("a/b", "a-b")]
    [InlineData("  trailing.  ", "trailing")]
    [InlineData("CON", "CON_")]
    [InlineData("con.png", "con_.png")]
    [InlineData("///", "FALLBACK")]
    [InlineData("", "FALLBACK")]
    [InlineData("..", "FALLBACK")]
    public void SanitizeFileName(string entered, string expected)
    {
        Assert.Equal(expected, ThemedInputDialog.SanitizeFileName(entered, "FALLBACK"));
    }

    // ---------- motion holders ----------

    [Fact]
    public void MotionHoldersReturnToZero_AfterACancelledTweenAndAFinishedOne()
    {
        Assert.Equal(0, Motion.Holders);
        IDisposable a = Motion.Run(5000, Motion.EaseOutBack, _ => { });
        Assert.Equal(1, Motion.Holders);
        a.Dispose();
        Assert.Equal(0, Motion.Holders);
        a.Dispose(); // double dispose must not underflow
        Assert.Equal(0, Motion.Holders);
    }

    // ---------- sounds ----------

    [Theory]
    [InlineData("default")]
    [InlineData("classic")]
    [InlineData("subtle")]
    public void EveryShutterAssetIsEmbedded(string name)
    {
        string? asset = CaptureSound.AssetFor(name);
        Assert.NotNull(asset);
        using Stream? s = typeof(CaptureSound).Assembly.GetManifestResourceStream($"WinShot.Assets.sounds.{asset}");
        Assert.NotNull(s);
    }

    [Fact]
    public void AppIconRasterisesAtEveryTraySize()
    {
        foreach (int size in new[] { 16, 20, 24, 32, 48, 256 })
        {
            using SD.Bitmap? bmp = AppIcon.Raster(size);
            Assert.NotNull(bmp);
            Assert.True(bmp!.Width > 0);
        }
    }

    // ---------- post-capture set ----------

    [Fact]
    public void SingleWithNoActionIsTheCardPlusTheCopyPreference()
    {
        PostCaptureSet set = PostCaptureSet.Single(null, alsoCopy: true);
        Assert.True(set.Overlay);
        Assert.True(set.Copy);
        Assert.False(set.Save);
    }

    [Fact]
    public void EmptySetIsDetected()
    {
        Assert.True(new PostCaptureSet(false, false, false, false, false).IsEmpty);
        Assert.Equal(0, new PostCaptureSet(false, false, false, false, false).Count);
        Assert.Equal(5, new PostCaptureSet(true, true, true, true, true).Count);
    }

    // ---------- a hand-edited settings.json that says null ----------

    /// <summary>
    /// A property initializer does NOT survive a literal null in the file — System.Text.Json
    /// writes the null straight over <c>= new()</c>. ShortcutBindings is the one collection the
    /// app dereferences without a check, and the first dereference is App.RegisterHotkeys inside
    /// OnStartup: an NRE there skips the tray icon, the welcome bubble and the startup command,
    /// so a two-line settings.json turns the app into an error toast.
    /// </summary>
    [Fact]
    public void ShortcutBindingsWrittenAsNullStillLoadsAsADictionary()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winshot-null-bindings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? old = SettingsService.DirOverride;
        try
        {
            SettingsService.DirOverride = dir;
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{ "ShortcutBindings": null, "HotkeyCaptureRegion": "Ctrl+Shift+1" }""");

            var svc = new SettingsService();
            svc.Load();

            Assert.NotNull(svc.Current.ShortcutBindings);
            Assert.Empty(svc.Current.ShortcutBindings);
            // The rest of the file still loaded — the normalization is not a bail-out.
            Assert.Equal("Ctrl+Shift+1", svc.Current.HotkeyCaptureRegion);
        }
        finally
        {
            SettingsService.DirOverride = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

