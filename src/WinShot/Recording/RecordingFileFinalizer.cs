using System.IO;

namespace WinShot.Recording;

public static class RecordingFileFinalizer
{
    public static string MoveToUniqueFinalPath(string tempPath, string folder, string fileName)
    {
        Directory.CreateDirectory(folder);

        string finalPath = Path.Combine(folder, fileName);
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int n = 2; File.Exists(finalPath); n++)
            finalPath = Path.Combine(folder, $"{nameWithoutExtension} ({n}){extension}");

        File.Move(tempPath, finalPath);
        return finalPath;
    }
}
