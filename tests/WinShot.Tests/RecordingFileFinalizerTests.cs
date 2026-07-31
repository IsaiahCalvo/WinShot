using System.IO;
using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class RecordingFileFinalizerTests
{
    [Fact]
    public void MoveToUniqueFinalPath_UsesAtomicMoveWhenAvailable()
    {
        using var scope = new TempScope();
        string temp = scope.Write("recording.tmp", "video");

        string finalPath = RecordingFileFinalizer.MoveToUniqueFinalPath(temp, scope.Path, "capture.mp4");

        Assert.Equal(System.IO.Path.Combine(scope.Path, "capture.mp4"), finalPath);
        Assert.True(File.Exists(finalPath));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void MoveToUniqueFinalPath_AppendsNumberWhenNameExists()
    {
        using var scope = new TempScope();
        scope.Write("capture.mp4", "existing");
        string temp = scope.Write("recording.tmp", "video");

        string finalPath = RecordingFileFinalizer.MoveToUniqueFinalPath(temp, scope.Path, "capture.mp4");

        Assert.Equal(System.IO.Path.Combine(scope.Path, "capture (2).mp4"), finalPath);
        Assert.Equal("existing", File.ReadAllText(System.IO.Path.Combine(scope.Path, "capture.mp4")));
    }

    [Fact]
    public void CrossVolumeFallback_CopiesFlushesCommitsAndDeletesTemp()
    {
        using var scope = new TempScope();
        string temp = scope.Write("recording.tmp", "video-data");
        var files = new TestFileOperations { TreatAsCrossVolume = true };

        RecordingFinalizationResult result = RecordingFileFinalizer.FinalizeToUniqueFinalPath(
            temp, scope.Path, "capture.mp4", files);

        Assert.Equal("video-data", File.ReadAllText(result.FinalPath));
        Assert.False(result.TempFileRetained);
        Assert.False(File.Exists(temp));
        Assert.Equal(1, files.FlushCount);
        Assert.Empty(Directory.GetFiles(scope.Path, "*.winshot-partial"));
    }

    [Fact]
    public void CrossVolumeFallback_RetriesWhenFinalNameIsClaimedDuringCommit()
    {
        using var scope = new TempScope();
        string temp = scope.Write("recording.tmp", "video-data");
        var files = new TestFileOperations
        {
            TreatAsCrossVolume = true,
            ClaimFirstPartialDestination = true,
        };

        RecordingFinalizationResult result = RecordingFileFinalizer.FinalizeToUniqueFinalPath(
            temp, scope.Path, "capture.mp4", files);

        Assert.Equal(System.IO.Path.Combine(scope.Path, "capture (2).mp4"), result.FinalPath);
        Assert.Equal("claimed", File.ReadAllText(System.IO.Path.Combine(scope.Path, "capture.mp4")));
        Assert.Equal("video-data", File.ReadAllText(result.FinalPath));
        Assert.Empty(Directory.GetFiles(scope.Path, "*.winshot-partial"));
    }

    [Fact]
    public void CrossVolumeFallback_CopyFailurePreservesTempAndCleansPartial()
    {
        using var scope = new TempScope();
        string temp = scope.Write("recording.tmp", "video-data");
        var files = new TestFileOperations
        {
            TreatAsCrossVolume = true,
            FailDestinationWrite = true,
        };

        Assert.Throws<IOException>(() => RecordingFileFinalizer.FinalizeToUniqueFinalPath(
            temp, scope.Path, "capture.mp4", files));

        Assert.Equal("video-data", File.ReadAllText(temp));
        Assert.False(File.Exists(System.IO.Path.Combine(scope.Path, "capture.mp4")));
        Assert.Empty(Directory.GetFiles(scope.Path, "*.winshot-partial"));
    }

    [Fact]
    public void CrossVolumeFallback_CancellationPreservesTempAndCleansPartial()
    {
        using var scope = new TempScope();
        string temp = System.IO.Path.Combine(scope.Path, "recording.tmp");
        File.WriteAllBytes(temp, new byte[300_000]);
        using var cancellation = new CancellationTokenSource();
        var files = new TestFileOperations
        {
            TreatAsCrossVolume = true,
            CancelAfterFirstRead = cancellation,
        };

        Assert.Throws<OperationCanceledException>(() => RecordingFileFinalizer.FinalizeToUniqueFinalPath(
            temp, scope.Path, "capture.mp4", files, cancellation.Token));

        Assert.True(File.Exists(temp));
        Assert.False(File.Exists(System.IO.Path.Combine(scope.Path, "capture.mp4")));
        Assert.Empty(Directory.GetFiles(scope.Path, "*.winshot-partial"));
    }

    [Fact]
    public void CrossVolumeFallback_SourceDeleteFailureReturnsCommittedFileAndRetainsTemp()
    {
        using var scope = new TempScope();
        string temp = scope.Write("recording.tmp", "video-data");
        var files = new TestFileOperations
        {
            TreatAsCrossVolume = true,
            FailSourceDelete = temp,
        };

        RecordingFinalizationResult result = RecordingFileFinalizer.FinalizeToUniqueFinalPath(
            temp, scope.Path, "capture.mp4", files);

        Assert.True(result.TempFileRetained);
        Assert.True(File.Exists(temp));
        Assert.Equal("video-data", File.ReadAllText(result.FinalPath));
    }

    [Fact]
    public void MoveToUniqueFinalPath_RejectsDirectoryInFileNameAndPreservesTemp()
    {
        using var scope = new TempScope();
        string temp = scope.Write("recording.tmp", "video");

        Assert.Throws<ArgumentException>(() => RecordingFileFinalizer.MoveToUniqueFinalPath(
            temp, scope.Path, System.IO.Path.Combine("other", "capture.mp4")));

        Assert.True(File.Exists(temp));
    }

    private sealed class TestFileOperations : IRecordingFileOperations
    {
        private bool _claimedDestination;

        public bool TreatAsCrossVolume { get; init; }
        public bool ClaimFirstPartialDestination { get; init; }
        public bool FailDestinationWrite { get; init; }
        public string? FailSourceDelete { get; init; }
        public CancellationTokenSource? CancelAfterFirstRead { get; init; }
        public int FlushCount { get; private set; }

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public bool FileExists(string path) => File.Exists(path);
        public bool AreSameVolume(string sourcePath, string destinationDirectory) => !TreatAsCrossVolume;

        public void Move(string sourcePath, string destinationPath)
        {
            if (ClaimFirstPartialDestination && !_claimedDestination &&
                sourcePath.EndsWith(".winshot-partial", StringComparison.Ordinal))
            {
                _claimedDestination = true;
                File.WriteAllText(destinationPath, "claimed");
                throw new IOException("Destination was claimed.");
            }

            File.Move(sourcePath, destinationPath);
        }

        public Stream OpenRead(string path)
        {
            Stream stream = File.OpenRead(path);
            return CancelAfterFirstRead is null
                ? stream
                : new CancelAfterReadStream(stream, CancelAfterFirstRead);
        }

        public Stream CreateNew(string path)
        {
            Stream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return FailDestinationWrite ? new ThrowingWriteStream(stream) : stream;
        }

        public void FlushToDisk(Stream stream)
        {
            FlushCount++;
            stream.Flush();
        }

        public void Delete(string path)
        {
            if (string.Equals(path, FailSourceDelete, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Source is locked.");
            File.Delete(path);
        }

        public bool IsCrossVolumeMoveError(IOException exception) => false;
    }

    private sealed class CancelAfterReadStream(Stream inner, CancellationTokenSource cancellation) : Stream
    {
        private bool _cancelled;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            if (!_cancelled)
            {
                _cancelled = true;
                cancellation.Cancel();
            }
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingWriteStream(Stream inner) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Disk full.");
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
    }

    private sealed class TempScope : IDisposable
    {
        public TempScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"winshot-recording-finalize-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
