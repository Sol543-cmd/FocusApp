using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FocusApp;

/// <summary>
/// Three-way merging for synced lists (tasks, completed tasks, Greek words). Each computer remembers the
/// version it last loaded or saved (the "base"). When saving, it compares that base with what it has now
/// (local) and what's in OneDrive now (remote, which may hold the other computer's or the phone's changes):
///   changed on one side only: that change wins;  deleted on one side and untouched on the other: stays deleted;
///   new on either side: kept;  changed on both: the local version (the one you just made) wins.
/// So an old computer that starts up with stale files can never bring back tasks finished elsewhere.
/// </summary>
/// <summary>Something synced that records when it was last changed (UTC), so the newest version can win.</summary>
public interface IStamped
{
    DateTime? Modified { get; set; }
}

public static class SyncMerge
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Key<T>(T item) => JsonSerializer.Serialize(item, Json);

    /// <summary>A deep copy (for remembering the base).</summary>
    public static List<T> Copy<T>(IEnumerable<T> items) =>
        JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(items.ToList(), Json), Json) ?? new List<T>();

    public static bool Same<T>(IEnumerable<T> a, IEnumerable<T> b) => Key(a.ToList()) == Key(b.ToList());

    /// <summary>When an item was last changed: its own stamp, or (for items saved without one, like by the phone) the file's time.</summary>
    private static DateTime When<T>(T item, DateTime fileTime) => item is IStamped s && s.Modified is DateTime m ? m : fileTime;

    public static List<T> Merge<T>(List<T> baseList, List<T> local, List<T> remote, Func<T, string> id, DateTime? remoteFileTime = null)
    {
        DateTime remoteTime = remoteFileTime ?? DateTime.MinValue;
        var b = baseList.GroupBy(id).ToDictionary(g => g.Key, g => Key(g.First()));
        var l = local.GroupBy(id).ToDictionary(g => g.Key, g => g.First());
        var r = remote.GroupBy(id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<T>();
        var done = new HashSet<string>();

        // Keep your own order first, then anything new from the other side.
        foreach (var item in local.Concat(remote))
        {
            string k = id(item);
            if (!done.Add(k)) continue;
            bool inB = b.TryGetValue(k, out var bk), inL = l.TryGetValue(k, out var lv), inR = r.TryGetValue(k, out var rv);
            if (inL && inR && Key(lv) == Key(rv)) { result.Add(lv!); continue; } // same content: keep this computer's object
            if (inL && inR)
            {
                bool localChanged = !inB || Key(lv) != bk, remoteChanged = !inB || Key(rv) != bk;
                if (localChanged && remoteChanged) result.Add(When(lv!, DateTime.MinValue) >= When(rv!, remoteTime) ? lv! : rv!); // both changed: newest wins
                else result.Add(localChanged ? lv! : rv!);
            }
            else if (inL) // not in OneDrive's copy
            {
                if (!inB) result.Add(lv!);                       // new here
                else if (Key(lv) != bk && When(lv!, DateTime.MinValue) > remoteTime) result.Add(lv!); // changed here after it was removed there: keep yours
                // else: finished or deleted on the other computer, untouched here: stays gone
            }
            else if (inR) // not in this computer's list
            {
                if (!inB) result.Add(rv!);                       // new from the other computer or the phone
                else if (Key(rv) != bk) result.Add(rv!);         // deleted here, but changed there: keep theirs
                // else: you finished or deleted it here: stays gone
            }
        }
        return result;
    }

    /// <summary>Marks the items changed (or added) here since the last save with the time of the change.</summary>
    public static void Stamp<T>(List<T> baseList, List<T> local, Func<T, string> id)
    {
        var b = baseList.GroupBy(id).ToDictionary(g => g.Key, g => Key(g.First()));
        var now = DateTime.UtcNow;
        foreach (var item in local)
        {
            if (item is not IStamped s) continue;
            if (!b.TryGetValue(id(item), out var before)) { s.Modified ??= now; continue; } // new here
            if (Key(item) == before) continue;                                              // unchanged
            var old = s.Modified;
            s.Modified = null;                                       // compare without the stamp itself
            bool changed = Key(item) != StripStamp(before);
            // Changed since the last save: stamp it now, unless it already carries a newer time of its own.
            bool alreadyStamped = old != null && StampIn(before) != old;
            s.Modified = changed && !alreadyStamped ? now : old;
        }
    }

    private static DateTime? StampIn(string json)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"Modified\":\"([^\"]*)\"");
        return m.Success && DateTime.TryParse(m.Groups[1].Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }

    private static string StripStamp(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"Modified\":(\"[^\"]*\"|null)", "\"Modified\":null");

    /// <summary>Lists whose save had to wait (OneDrive's copy couldn't be read); retried every 30 seconds.</summary>
    private static readonly HashSet<string> Waiting = new();
    public static event Action? RetryNeeded;
    public static bool AnyWaiting => Waiting.Count > 0;

    /// <summary>
    /// Saves a synced list safely: reads OneDrive's copy, merges (three-way), and writes only if something
    /// changed. Returns the merged list, or null if it had to wait.
    ///   No file and nothing here before: genuinely empty, merge as usual.
    ///   File gone while this computer had items: no deletions are taken from a missing file; yours is written back.
    ///   File there but unreadable (OneDrive busy): nothing is written or lost; it tries again shortly.
    /// </summary>
    public static List<T>? SaveMerged<T>(string name, List<T> baseList, List<T> local, Func<T, string> id, Func<T, bool>? keep = null) where T : new()
    {
        string path = Storage.PathFor(name);
        List<T> remote;
        if (!File.Exists(path))
        {
            if (baseList.Count > 0 && local.Count > 0)
            {
                // The file vanished (moved or deleted): don't read that as "everything was deleted".
                Storage.Save(name, local);
                Waiting.Remove(name);
                return local;
            }
            remote = new List<T>();
        }
        else if (!Storage.TryLoad(name, out remote))
        {
            Log.Write($"Sync: {name} couldn't be read (OneDrive busy?); saving again shortly.");
            Waiting.Add(name);
            RetryNeeded?.Invoke();
            return null;
        }
        Stamp(baseList, local, id);
        var merged = Merge(baseList, local, remote, id, File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue);
        if (keep != null) merged = merged.Where(keep).ToList();
        if (!Same(merged, remote)) Storage.Save(name, merged);
        Waiting.Remove(name);
        return merged;
    }
}
