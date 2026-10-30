using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace FocusApp;

/// <summary>Which folder to sync through. Saved on this computer only, in sync.json.</summary>
public class SyncConfig
{
    public string Folder { get; set; } = "";
}

/// <summary>
/// Keeps plans, layouts, block lists, your task list and focus totals in a shared folder
/// (usually in OneDrive), so two computers see the same data. OneDrive does the copying;
/// Focus just reads and writes files there and notices when the other computer changes them.
/// Things that belong to one computer stay local: the running session, calendar links,
/// settings and the error log.
/// </summary>
public static class Sync
{
    private static FileSystemWatcher? _watcher;
    private static DispatcherTimer? _debounce;
    private static DispatcherTimer? _poll;
    private static readonly Dictionary<string, DateTime> Seen = new(StringComparer.OrdinalIgnoreCase);

    public static string? Folder { get; private set; }
    public static bool IsOn => Folder != null;

    /// <summary>The other computer changed something in the sync folder.</summary>
    public static event Action? RemoteChanged;

    /// <summary>This computer's name, safe to use in a file name.</summary>
    public static string MachineName =>
        new string(Environment.MachineName.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()) is { Length: > 0 } n ? n : "PC";

    /// <summary>The usual place for Focus's folder in OneDrive, or null if OneDrive isn't set up.</summary>
    public static string? SuggestedOneDriveFolder()
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) return Path.Combine(root, "Focus");
        }
        return null;
    }

    /// <summary>Called at startup: uses the saved sync folder, if it's still there.</summary>
    public static void Init()
    {
        var config = Storage.Load<SyncConfig>("sync.json");
        if (config.Folder.Length > 0 && Directory.Exists(config.Folder)) Apply(config.Folder);
        else if (config.Folder.Length > 0) Log.Write($"Sync folder not found: {config.Folder}. Using this computer's data for now.");
    }

    /// <summary>
    /// Turns sync on. This computer's data is merged into the folder (nothing is lost if the
    /// other computer already put data there), then Focus reads and writes there from now on.
    /// </summary>
    public static void Enable(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (string.Equals(folder.TrimEnd('\\'), Storage.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pick a different folder from Focus's own data folder.");
        Directory.CreateDirectory(folder);
        MergeShared(from: Storage.SharedFolder, into: folder);
        Storage.Save("sync.json", new SyncConfig { Folder = folder });
        Apply(folder);
    }

    /// <summary>Turns sync off, keeping a copy of the latest shared data on this computer.</summary>
    public static void Disable()
    {
        if (Folder != null) CopyShared(from: Folder, into: Storage.Folder);
        StopWatcher();
        Folder = null;
        Storage.SetSharedFolder(null);
        Storage.Save("sync.json", new SyncConfig());
    }

    private static void Apply(string folder)
    {
        Folder = folder;
        Storage.SetSharedFolder(folder);
        StartWatcher(folder);
    }

    // ---------- Merging and copying ----------

    /// <summary>Combines this computer's shared data with whatever is already in the folder.</summary>
    private static void MergeShared(string from, string into)
    {
        // Block lists and task list items have IDs, so the same item is never doubled up.
        Storage.TryLoadFrom(from, "blocklists.json", out List<BlockList> localLists);
        Storage.TryLoadFrom(into, "blocklists.json", out List<BlockList> remoteLists);
        var lists = remoteLists.Concat(localLists.Where(l => remoteLists.All(r => r.Id != l.Id))).ToList();
        Storage.SaveTo(into, "blocklists.json", lists);

        Storage.TryLoadFrom(from, "projects.json", out List<Project> localProjects);
        Storage.TryLoadFrom(into, "projects.json", out List<Project> remoteProjects);
        Storage.SaveTo(into, "projects.json", remoteProjects.Concat(localProjects.Where(p => remoteProjects.All(r => r.Id != p.Id))).ToList());

        Storage.TryLoadFrom(from, "templates.json", out List<TaskTemplate> localTemplates);
        Storage.TryLoadFrom(into, "templates.json", out List<TaskTemplate> remoteTemplates);
        Storage.SaveTo(into, "templates.json", remoteTemplates.Concat(localTemplates.Where(t => remoteTemplates.All(r => r.Id != t.Id))).ToList());

        Storage.TryLoadFrom(from, "concepts.json", out List<VocabCard> localConcepts);
        Storage.TryLoadFrom(into, "concepts.json", out List<VocabCard> remoteConcepts);
        Storage.SaveTo(into, "concepts.json", Vocab.Merge(remoteConcepts, localConcepts));

        Storage.TryLoadFrom(from, "alphabet.json", out List<VocabCard> localAlpha);
        Storage.TryLoadFrom(into, "alphabet.json", out List<VocabCard> remoteAlpha);
        Storage.SaveTo(into, "alphabet.json", Vocab.Merge(remoteAlpha, localAlpha));

        Storage.TryLoadFrom(from, "vocab.json", out List<VocabCard> localVocab);
        Storage.TryLoadFrom(into, "vocab.json", out List<VocabCard> remoteVocab);
        Storage.SaveTo(into, "vocab.json", Vocab.Merge(remoteVocab, localVocab));

        Storage.TryLoadFrom(from, "resolved-tasks.json", out List<ResolvedTask> localDone);
        Storage.TryLoadFrom(into, "resolved-tasks.json", out List<ResolvedTask> remoteDone);
        Storage.SaveTo(into, "resolved-tasks.json", remoteDone.Concat(localDone.Where(t => remoteDone.All(r => r.Id != t.Id))).ToList());

        Storage.TryLoadFrom(from, "task-bin.json", out List<BinTask> localBin);
        Storage.TryLoadFrom(into, "task-bin.json", out List<BinTask> remoteBin);
        Storage.SaveTo(into, "task-bin.json", remoteBin.Concat(localBin.Where(t => remoteBin.All(r => r.Id != t.Id))).ToList());

        // Layouts are matched by name; if both computers have one with the same name, the folder's copy wins.
        Storage.TryLoadFrom(from, "layouts.json", out List<DayLayout> localLayouts);
        Storage.TryLoadFrom(into, "layouts.json", out List<DayLayout> remoteLayouts);
        var layouts = remoteLayouts.Concat(localLayouts.Where(l =>
            remoteLayouts.All(r => !string.Equals(r.Name, l.Name, StringComparison.CurrentCultureIgnoreCase)))).ToList();
        Storage.SaveTo(into, "layouts.json", layouts);

        // Day plans: copy any date the folder doesn't have yet.
        string localPlans = Path.Combine(from, "plans");
        if (Directory.Exists(localPlans))
        {
            Directory.CreateDirectory(Path.Combine(into, "plans"));
            foreach (var file in Directory.GetFiles(localPlans, "*.json"))
            {
                string target = Path.Combine(into, "plans", Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target);
            }
        }

        // This computer's focus totals (each computer keeps its own file; they're added together).
        string myStats = $"focus-stats-{MachineName}.json";
        if (!Storage.TryLoadFrom(from, myStats, out FocusStats stats))
            Storage.TryLoadFrom(Storage.Folder, "focus-stats.json", out stats);
        if (Storage.TryLoadFrom(into, myStats, out FocusStats existing))
            foreach (var (day, minutes) in existing.MinutesByDay)
                stats.MinutesByDay[day] = Math.Max(minutes, stats.MinutesByDay.GetValueOrDefault(day));
        Storage.SaveTo(into, myStats, stats);
    }

    /// <summary>Copies the shared files from one folder to another, replacing what's there.</summary>
    private static void CopyShared(string from, string into)
    {
        try
        {
            Directory.CreateDirectory(into);
            foreach (var name in new[] { "layouts.json", "blocklists.json", "task-bin.json", "resolved-tasks.json", "projects.json", "templates.json", "vocab.json", "alphabet.json", "concepts.json", "personal-tasks.json", "personal-finished.json", "personal-projects.json", "task-removed.json", "personal-removed.json" })
            {
                string source = Path.Combine(from, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(into, name), true);
            }
            foreach (var file in Directory.GetFiles(from, "focus-stats-*.json"))
                File.Copy(file, Path.Combine(into, Path.GetFileName(file)), true);
            string plans = Path.Combine(from, "plans");
            if (Directory.Exists(plans))
            {
                Directory.CreateDirectory(Path.Combine(into, "plans"));
                foreach (var file in Directory.GetFiles(plans, "*.json"))
                    File.Copy(file, Path.Combine(into, "plans", Path.GetFileName(file)), true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Sync.CopyShared", ex);
        }
    }

    // ---------- Noticing changes from the other computer ----------

    private static void StartWatcher(string folder)
    {
        StopWatcher();
        _debounce ??= CreateDebounce();

        // Backup check every minute, because file notifications from cloud folders can be missed.
        Seen.Clear();
        CheckForChanges(remember: true);
        if (_poll == null)
        {
            _poll = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _poll.Tick += (_, _) => { if (CheckForChanges(remember: true)) Poke(); };
        }
        _poll.Start();

        try
        {
            _watcher = new FileSystemWatcher(folder, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Error += (_, _) => Poke();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("Sync.StartWatcher", ex);
        }
    }

    /// <summary>True if any shared file changed since last checked and isn't this computer's own save.</summary>
    private static bool CheckForChanges(bool remember)
    {
        if (Folder == null) return false;
        bool changed = false;
        try
        {
            var files = Directory.GetFiles(Folder, "*.json")
                .Concat(Directory.Exists(Path.Combine(Folder, "plans")) ? Directory.GetFiles(Path.Combine(Folder, "plans"), "*.json") : Array.Empty<string>());
            foreach (var path in files)
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                bool known = Seen.TryGetValue(path, out var seenStamp);
                if (known && seenStamp == stamp) continue;
                if (remember) Seen[path] = stamp;
                // New or changed, and not this computer's own save: it came from the other computer.
                if (!Storage.IsOwnVersion(path)) changed = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Sync.CheckForChanges", ex);
        }
        return changed;
    }

    private static void StopWatcher()
    {
        _poll?.Stop();
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private static void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
        if (Storage.IsOwnRecentWrite(e.FullPath) || Storage.IsOwnVersion(e.FullPath)) return; // our own save
        Poke();
    }

    /// <summary>Waits until changes settle for two seconds, then reloads once.</summary>
    private static void Poke()
    {
        var dispatcher = Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(new Action(() =>
        {
            if (_debounce == null) return;
            _debounce.Stop();
            _debounce.Start();
        }));
    }

    private static DispatcherTimer CreateDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { RemoteChanged?.Invoke(); }
            catch (Exception ex) { Log.Error("Sync.RemoteChanged", ex); }
        };
        return timer;
    }
}
