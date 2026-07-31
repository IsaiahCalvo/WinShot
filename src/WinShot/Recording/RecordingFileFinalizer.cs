using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinShot.Recording;

internal sealed record RecordingFinalizationResult(string FinalPath, bool TempFileRetained);

/// <summary>
/// Commits a completed recording to its final local path. A same-volume move is
/// atomic. When the save folder is on another volume, the source is copied to a
/// private partial file, flushed to disk, and atomically renamed in the target
/// folder before the source is removed.
/// </summary>
public static class RecordingFileFinalizer
{
    private const int CopyBufferSize = 128 * 1024;

    public static string MoveToUniqueFinalPath(string tempPath, string folder, string fileName) =>
        FinalizeToUniqueFinalPath(tempPath, folder, fileName).FinalPath;

    internal static RecordingFinalizationResult FinalizeToUniqueFinalPath(
        string tempPath,
        string folder,
        string fileName,
        CancellationToken cancellationToken = default) =>
        FinalizeToUniqueFinalPath(
            tempPath,
            folder,
            fileName,
            FileSystemRecordingFileOperations.Instance,
            cancellationToken);

    internal static RecordingFinalizationResult FinalizeToUniqueFinalPath(
        string tempPath,
        string folder,
        string fileName,
        IRecordingFileOperations files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(files);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new ArgumentException("The recording file name must not contain a directory.", nameof(fileName));
        if (!files.FileExists(tempPath))
            throw new FileNotFoundException("The completed recording temp file was not found.", tempPath);

        cancellationToken.ThrowIfCancellationRequested();
        files.CreateDirectory(folder);

        string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        bool useCopyFallback = !files.AreSameVolume(tempPath, folder);

        for (int number = 1; ; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string finalPath = Path.Combine(
                folder,
                number == 1 ? fileName : $"{nameWithoutExtension} ({number}){extension}");
            if (files.FileExists(finalPath))
                continue;

            if (!useCopyFallback)
            {
                try
                {
                    files.Move(tempPath, finalPath);
                    return new RecordingFinalizationResult(finalPath, TempFileRetained: false);
                }
                catch (IOException ex) when (files.IsCrossVolumeMoveError(ex))
                {
                    useCopyFallback = true;
                }
                catch (IOException) when (files.FileExists(finalPath))
                {
                    // Another capture won the name between our existence check and move.
                    continue;
                }
            }

            RecordingFinalizationResult? copied = TryCopyAcrossVolumes(
                tempPath,
                finalPath,
                files,
                cancellationToken);
            if (copied is not null)
                return copied;
        }
    }

    private static RecordingFinalizationResult? TryCopyAcrossVolumes(
        string tempPath,
        string finalPath,
        IRecordingFileOperations files,
        CancellationToken cancellationToken)
    {
        string partialPath = Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.winshot-partial");
        bool partialCreated = false;

        try
        {
            using (Stream source = files.OpenRead(tempPath))
            {
                using Stream destination = files.CreateNew(partialPath);
                partialCreated = true;
                byte[] buffer = new byte[CopyBufferSize];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                        break;
                    destination.Write(buffer, 0, read);
                }

                files.FlushToDisk(destination);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Both files are now in the destination folder, so this commit is atomic.
                files.Move(partialPath, finalPath);
                partialCreated = false;
            }
            catch (IOException) when (files.FileExists(finalPath))
            {
                TryDeletePartial(files, partialPath);
                return null;
            }

            bool tempRetained = false;
            try
            {
                files.Delete(tempPath);
            }
            catch (Exception)
            {
                // The final file is safely committed. Keeping the source duplicate is safer
                // than reporting a failed save and risking a later destructive retry.
                tempRetained = true;
            }

            return new RecordingFinalizationResult(finalPath, tempRetained);
        }
        catch
        {
            if (partialCreated)
                TryDeletePartial(files, partialPath);
            throw;
        }
    }

    private static void TryDeletePartial(IRecordingFileOperations files, string partialPath)
    {
        try
        {
            if (files.FileExists(partialPath))
                files.Delete(partialPath);
        }
        catch
        {
            // Preserve the original exception. Recovery discovery ignores partial files.
        }
    }
}

internal interface IRecordingFileOperations
{
    void CreateDirectory(string path);
    bool FileExists(string path);
    bool AreSameVolume(string sourcePath, string destinationDirectory);
    void Move(string sourcePath, string destinationPath);
    Stream OpenRead(string path);
    Stream CreateNew(string path);
    void FlushToDisk(Stream stream);
    void Delete(string path);
    bool IsCrossVolumeMoveError(IOException exception);
}

internal sealed class FileSystemRecordingFileOperations : IRecordingFileOperations
{
    private const int ErrorNotSameDevice = 17;

    public static FileSystemRecordingFileOperations Instance { get; } = new();

    private FileSystemRecordingFileOperations() { }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public bool FileExists(string path) => File.Exists(path);

    public bool AreSameVolume(string sourcePath, string destinationDirectory)
    {
        string? sourceVolume = TryGetVolumePath(sourcePath);
        string? destinationVolume = TryGetVolumePath(destinationDirectory);
        return sourceVolume is not null && destinationVolume is not null
            ? string.Equals(sourceVolume, destinationVolume, StringComparison.OrdinalIgnoreCase)
            : string.Equals(
                Path.GetPathRoot(Path.GetFullPath(sourcePath)),
                Path.GetPathRoot(Path.GetFullPath(destinationDirectory)),
                StringComparison.OrdinalIgnoreCase);
    }

    public void Move(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath);
    public Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        CopyBufferSize,
        FileOptions.SequentialScan);
    public Stream CreateNew(string path) => new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        CopyBufferSize,
        FileOptions.SequentialScan | FileOptions.WriteThrough);

    public void FlushToDisk(Stream stream)
    {
        if (stream is FileStream fileStream)
            fileStream.Flush(flushToDisk: true);
        else
            stream.Flush();
    }

    public void Delete(string path) => File.Delete(path);

    public bool IsCrossVolumeMoveError(IOException exception) =>
        (exception.HResult & 0xFFFF) == ErrorNotSameDevice;

    private static string? TryGetVolumePath(string path)
    {
        var volumePath = new StringBuilder(260);
        return GetVolumePathName(Path.GetFullPath(path), volumePath, volumePath.Capacity)
            ? volumePath.ToString()
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    private const int CopyBufferSize = 128 * 1024;
}
