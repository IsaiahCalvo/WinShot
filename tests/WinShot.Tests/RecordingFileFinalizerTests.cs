using System.IO;
using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class RecordingFileFinalizerTests
{
    [Fact]
    public void MoveToUniqueFinalPath_UsesAvailableName()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"winshot-recording-finalize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, "recording.tmp");
        File.WriteAllText(temp, "video");

        try
        {
            string finalPath = RecordingFileFinalizer.MoveToUniqueFinalPath(temp, dir, "capture.mp4");

            Assert.Equal(Path.Combine(dir, "capture.mp4"), finalPath);
            Assert.True(File.Exists(finalPath));
            Assert.False(File.Exists(temp));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void MoveToUniqueFinalPath_AppendsNumberWhenNameExists()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"winshot-recording-finalize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "capture.mp4"), "existing");
        string temp = Path.Combine(dir, "recording.tmp");
        File.WriteAllText(temp, "video");

        try
        {
            string finalPath = RecordingFileFinalizer.MoveToUniqueFinalPath(temp, dir, "capture.mp4");

            Assert.Equal(Path.Combine(dir, "capture (2).mp4"), finalPath);
            Assert.True(File.Exists(finalPath));
            Assert.Equal("existing", File.ReadAllText(Path.Combine(dir, "capture.mp4")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }
}
