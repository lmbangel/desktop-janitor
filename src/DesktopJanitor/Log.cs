using System.IO;

namespace DesktopJanitor;

/// <summary>Plain-text log of everything the janitor suggests or does, so nothing happens silently.</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopJanitor");

    public static string FilePath { get; } = Path.Combine(Folder, "janitor.log");

    public static void Info(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the janitor down.
            }
        }
    }
}
