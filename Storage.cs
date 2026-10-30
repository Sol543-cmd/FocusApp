using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace FocusApp;

/// <summary>
/// Saves and loads app data as JSON files. Most files live in %AppData%\FocusApp on this PC.
/// Files that sync between computers (plans, layouts, block lists, your task list, focus totals)
/// live in the sync folder when sync is turned on (see <see cref="Sync"/>), otherwise here too.
/// </summary>
public static class Storage
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusApp");

    /// <summary>Where shared files go: the sync folder, or <see cref="Folder"/> when not syncing.</summary>
    public static string SharedFolder { get; private set; } = Folder;

    private static readonly string[] SharedNames =
        { "layouts.json", "blocklists.json", "task-bin.json", "resolved-tasks.json", "projects.json", "templates.json", "vocab.json", "alphabet.json", "concepts.json", "personal-tasks.json", "personal-finished.json", "personal-projects.json", "task-removed.json", "personal-removed.json" };

    // When Focus itself last wrote each file, and the file's timestamp after that write,
    // so sync can tell its own saves apart from changes made on the other computer.
    private static readonly ConcurrentDictionary<string, DateTime> OwnWrites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> OwnStamps = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void SetSharedFolder(string? folder) => SharedFolder = string.IsNullOrEmpty(folder) ? Folder : folder;

    /// <summary>True for files that sync between computers.</summary>
    public static bool IsShared(string fileName)
    {
        string name = fileName.Replace('/', '\\');
        return SharedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
               || name.StartsWith("plans\\", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("focus-stats-", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("session-logs\\", StringComparison.OrdinalIgnoreCase)
               || name.Equals("calendar-share.json", StringComparison.OrdinalIgnoreCase);
    }

    public static string PathFor(string fileName) =>
        Path.Combine(IsShared(fileName) ? SharedFolder : Folder, fileName);

    public static T Load<T>(string fileName) where T : new()
    {
        string path = PathFor(fileName);
        if (!File.Exists(path)) return new T();
        if (TryRead(path, out T value)) return value;

        // Keep a copy of an unreadable file so nothing is silently lost.
        try { File.Copy(path, path + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch { }
        return new T();
    }

    /// <summary>Reads a file without any side effects.</summary>
    public static bool TryLoad<T>(string fileName, out T value) where T : new() => TryRead(PathFor(fileName), out value);

    /// <summary>Reads a file from a specific folder (used when turning sync on or off).</summary>
    public static bool TryLoadFrom<T>(string folder, string fileName, out T value) where T : new() =>
        TryRead(Path.Combine(folder, fileName), out value);

    /// <summary>
    /// Reads a file. If it exists but can't be read (OneDrive may be busy downloading or
    /// uploading it), waits briefly and tries again a few times.
    /// </summary>
    private static bool TryRead<T>(string path, out T value) where T : new()
    {
        value = new T();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var loaded = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
                if (loaded == null) return false;
                value = loaded;
                return true;
            }
            catch (Exception) when (attempt < 3)
            {
                Thread.Sleep(250 * (attempt + 1));
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    public static void Delete(string fileName)
    {
        try { File.Delete(PathFor(fileName)); } catch { }
    }

    public static void Save<T>(string fileName, T data) => SaveTo(IsShared(fileName) ? SharedFolder : Folder, fileName, data);

    public static void SaveTo<T>(string folder, string fileName, T data)
    {
        string path = Path.Combine(folder, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? folder);
        string temp = path + ".tmp";
        // Write to a temp file first, then swap, so a crash mid-save can't corrupt the real file.
        string json = JsonSerializer.Serialize(data, Options);

        // OneDrive can hold a file for a moment while uploading it; retry instead of failing.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, path, true);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
        string full = Path.GetFullPath(path);
        OwnWrites[full] = DateTime.UtcNow;
        try { OwnStamps[full] = File.GetLastWriteTimeUtc(full); } catch { }
    }

    /// <summary>True if the file is exactly as Focus on this computer last saved it.</summary>
    public static bool IsOwnVersion(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return OwnStamps.TryGetValue(full, out var stamp) && File.GetLastWriteTimeUtc(full) == stamp;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True if Focus on this computer saved this file in the last few seconds.</summary>
    public static bool IsOwnRecentWrite(string path) =>
        OwnWrites.TryGetValue(Path.GetFullPath(path), out var when) && DateTime.UtcNow - when < TimeSpan.FromSeconds(4);
}
