using System;
using System.IO;

namespace FocusApp;

/// <summary>A small error log in %AppData%\FocusApp\log.txt, so problems can be tracked down later.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static void Error(string where, Exception ex) => Write($"ERROR in {where}: {ex}");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Storage.Folder);
                string path = Path.Combine(Storage.Folder, "log.txt");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
