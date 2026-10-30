using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FocusApp;

/// <summary>One notification from Focus, with buttons for what you can do about it.</summary>
public sealed class Notice
{
    public string Key = "";                 // the same situation is only reported once
    public DateTime Time = DateTime.Now;
    public string Title = "", Text = "";
    public List<(string Label, Action Run)> Actions = new();
}

/// <summary>
/// The notifications panel (the bell in the sidebar): new meetings to fit in, sync conflicts and
/// duplicates, overdue tasks, reminders, the end-of-day review, and website blocking trouble.
/// </summary>
public partial class MainWindow
{
    private readonly List<Notice> _notices = new();
    private readonly HashSet<string> _noticeKeysToday = new();
    private DateTime _lastHousekeeping = DateTime.MinValue;

    /// <summary>Adds a notification (once per situation), updates the bell, and shows a brief toast.</summary>
    private void Notify(string key, string title, string text, params (string Label, Action Run)[] actions)
    {
        if (_noticeKeysToday.Contains(key)) return;
        _noticeKeysToday.Add(key);
        _notices.Insert(0, new Notice { Key = key, Title = title, Text = text, Actions = actions.ToList() });
        UpdateNoticeBell();
        _toast.ShowMessage(title, "Open the bell in the sidebar for options.");
        if (NoticePopup.IsOpen) BuildNoticeList();
    }

    private void Dismiss(Notice n)
    {
        _notices.Remove(n);
        UpdateNoticeBell();
        BuildNoticeList();
    }

    private void UpdateNoticeBell()
    {
        if (NoticeBadge == null) return;
        NoticeBadge.Visibility = _notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticeCount.Text = _notices.Count > 9 ? "9+" : _notices.Count.ToString();
    }

    private void NoticeBell_Click(object sender, RoutedEventArgs e)
    {
        BuildNoticeList();
        NoticePopup.PlacementTarget = (UIElement)sender;
        NoticePopup.IsOpen = true;
    }

    private void ClearNotices_Click(object sender, RoutedEventArgs e)
    {
        _notices.Clear();
        UpdateNoticeBell();
        BuildNoticeList();
    }

    private void BuildNoticeList()
    {
        NoticeList.Children.Clear();
        NoticeEmpty.Visibility = _notices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var n in _notices.ToList())
        {
            var stack = new StackPanel();
            var head = new DockPanel();
            var close = new Button { Content = "✕", FontSize = 10, Padding = new Thickness(6, 1, 6, 1), ToolTip = "Dismiss" };
            close.SetResourceReference(StyleProperty, "IconButton");
            close.Click += (_, _) => Dismiss(n);
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            head.Children.Add(new TextBlock { Text = n.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(head);
            var body = new TextBlock { Text = n.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            body.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            stack.Children.Add(body);
            var when = new TextBlock { Text = Ago(n.Time), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
            when.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            stack.Children.Add(when);
            if (n.Actions.Count > 0)
            {
                var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                bool first = true;
                foreach (var (label, run) in n.Actions)
                {
                    var b = new Button { Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 4) };
                    if (first) b.SetResourceReference(StyleProperty, "PrimaryButton");
                    first = false;
                    b.Click += (_, _) =>
                    {
                        NoticePopup.IsOpen = false;
                        Dismiss(n);
                        try { using (TaskStores.Use(TaskStores.Main)) run(); } // notifications are about your work tasks
                        catch (Exception ex) { Log.Error("Notice action", ex); Info("That didn't work: " + ex.Message, "Notifications"); }
                    };
                    buttons.Children.Add(b);
                }
                stack.Children.Add(buttons);
            }
            var card = new Border { Child = stack, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 6, 10), Margin = new Thickness(0, 0, 0, 8) };
            card.SetResourceReference(Border.BackgroundProperty, "Panel2");
            NoticeList.Children.Add(card);
        }
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "just now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago" : d.TotalHours < 24 ? $"{(int)d.TotalHours} h ago" : t.ToString("ddd MMM d");
    }

    // ---------- Checks that create notifications (every few minutes) ----------

    private void CheckNotices()
    {
        if (DateTime.Now - _lastHousekeeping < TimeSpan.FromMinutes(5)) return;
        _lastHousekeeping = DateTime.Now;
        if (_noticeKeysToday.Count > 0 && !_noticeKeysToday.Contains("day:" + DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))
            _noticeKeysToday.RemoveWhere(k => k.StartsWith("overdue:")); // a new day: overdue is reported afresh
        _noticeKeysToday.Add("day:" + DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        try { CheckOverdue(); } catch (Exception ex) { Log.Error("CheckOverdue", ex); }
        try { CheckSyncConflicts(); } catch (Exception ex) { Log.Error("CheckSyncConflicts", ex); }
        try { CheckDuplicateTasks(); } catch (Exception ex) { Log.Error("CheckDuplicateTasks", ex); }
    }

    private void CheckOverdue()
    {
        int n = TaskBin.All.Count(t => t.IsOverdue && !t.IsSnoozed);
        if (n == 0) return;
        Notify("overdue:" + DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            n == 1 ? "1 overdue task" : $"{n} overdue tasks",
            "Give them a new day, do them today, or let them go.",
            ("Show them", () => { NavTasks.IsChecked = true; BinTodoView.IsChecked = true; _binFilter = "overdue"; RefreshTasksTab(); }),
            ("Review today", OpenReview));
    }

    /// <summary>The files Focus syncs that are lists of items with IDs, so conflict copies can be merged in.</summary>
    private static readonly string[] MergeableFiles =
    {
        "task-bin.json", "resolved-tasks.json", "projects.json", "task-removed.json",
        "personal-tasks.json", "personal-finished.json", "personal-projects.json", "personal-removed.json",
        "templates.json", "vocab.json", "alphabet.json", "concepts.json", "layouts.json", "blocklists.json"
    };

    /// <summary>
    /// OneDrive keeps both versions when two computers change a file at once, saving one as a copy (like
    /// "task-bin-DESKTOP-1.json"). Focus's own merging already brings each computer's changes across, so a copy is
    /// mostly an old snapshot: Focus takes only what's genuinely new from it, by itself (never tasks finished or
    /// deleted anywhere), files the copy away in "Merged conflicts", and carries on. Nothing to click.
    /// </summary>
    private void CheckSyncConflicts()
    {
        string folder = Storage.SharedFolder;
        if (string.IsNullOrEmpty(folder) || folder == Storage.Folder || !Directory.Exists(folder)) return;
        int handled = 0, added = 0;
        foreach (var name in MergeableFiles)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            var copies = Directory.GetFiles(folder, stem + "*.json")
                .Where(f => Regex.IsMatch(Path.GetFileName(f), "^" + Regex.Escape(stem) + @"(-[^.]+| \(\d+\))\.json$", RegexOptions.IgnoreCase))
                .ToList();
            foreach (var copy in copies)
            {
                try { added += MergeConflict(name, copy); handled++; }
                catch (Exception ex) { Log.Error("Conflict copy " + Path.GetFileName(copy), ex); }
            }
        }
        if (handled > 0)
        {
            ReloadSharedData(force: true);
            Log.Write($"Sync: tidied {handled} OneDrive conflict cop{(handled == 1 ? "y" : "ies")} ({added} new or newer items kept).");
            if (added > 0) _toast.ShowMessage("Synced", added == 1 ? "1 change from your other computer was kept." : $"{added} changes from your other computer were kept.");
        }
        // Two versions of a day plan can't be combined safely: you choose.
        var planCopies = Directory.Exists(Path.Combine(folder, "plans"))
            ? Directory.GetFiles(Path.Combine(folder, "plans"), "*.json").Where(f => !Regex.IsMatch(Path.GetFileName(f), @"^\d{4}-\d{2}-\d{2}\.json$")).ToList()
            : new List<string>();
        foreach (var copy in planCopies)
            Notify("conflict:" + copy, "A day plan has two versions",
                $"OneDrive kept two versions of a day plan ({Path.GetFileName(copy)}). Open the folder to keep the one you want.",
                ("Show in folder", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{copy}\"")));
    }

    /// <summary>Takes what's new from one conflict copy, then files the copy away (kept, just in case). Returns how many items were kept.</summary>
    private int MergeConflict(string name, string copyPath)
    {
        int TaskCopy(TaskStore set)
        {
            if (!Storage.TryLoadFrom(Path.GetDirectoryName(copyPath)!, Path.GetFileName(copyPath), out List<BinTask> theirs)) return 0;
            using (TaskStores.Use(set)) return TaskBin.MergeConflictCopy(theirs);
        }
        int kept = name switch
        {
            "task-bin.json" => TaskCopy(TaskStores.Main),
            "personal-tasks.json" => TaskCopy(TaskStores.Personal),
            "resolved-tasks.json" or "personal-finished.json" => MergeInto<ResolvedTask>(name, copyPath, t => t.Id),
            "projects.json" or "personal-projects.json" => MergeInto<Project>(name, copyPath, p => p.Id),
            "task-removed.json" or "personal-removed.json" => MergeInto<RemovedTask>(name, copyPath, r => r.Id),
            "templates.json" => MergeInto<TaskTemplate>(name, copyPath, t => t.Id),
            "vocab.json" or "alphabet.json" or "concepts.json" => MergeInto<VocabCard>(name, copyPath, c => c.Id),
            "layouts.json" => MergeInto<DayLayout>(name, copyPath, l => l.Name),
            "blocklists.json" => MergeInto<BlockList>(name, copyPath, l => l.Id),
            _ => 0
        };
        string aside = Path.Combine(Path.GetDirectoryName(copyPath)!, "Merged conflicts");
        Directory.CreateDirectory(aside);
        File.Move(copyPath, Path.Combine(aside, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + " " + Path.GetFileName(copyPath)), true);
        return kept;
    }

    private static int MergeInto<T>(string name, string copyPath, Func<T, string> id, Func<T, bool>? keep = null) where T : new()
    {
        if (!Storage.TryLoadFrom(Path.GetDirectoryName(copyPath)!, Path.GetFileName(copyPath), out List<T> theirs)) return 0;
        var mine = Storage.Load<List<T>>(name);
        var byId = mine.Select((t, i) => (t, i)).GroupBy(x => id(x.t)).ToDictionary(g => g.Key, g => g.First().i);
        int changed = 0;
        foreach (var t in theirs.Where(t => keep == null || keep(t)))
        {
            if (!byId.TryGetValue(id(t), out int at)) { mine.Add(t); changed++; continue; }        // missing here: add it
            // In both: the newer version wins (by when each was last changed).
            if (t is IStamped theirsStamp && mine[at] is IStamped mineStamp &&
                (theirsStamp.Modified ?? DateTime.MinValue) > (mineStamp.Modified ?? DateTime.MinValue)) { mine[at] = t; changed++; }
        }
        if (keep != null) mine = mine.Where(keep).ToList();
        if (changed == 0) return 0;
        Storage.Save(name, mine);
        return changed;
    }

    /// <summary>The same task added twice (same text, same project, created within two minutes): offers to tidy up.</summary>
    private void CheckDuplicateTasks()
    {
        var groups = TaskBin.All
            // A true duplicate matches in everything you'd set: text, project, dates and notes.
            .GroupBy(t => (t.Text.Trim().ToLowerInvariant(), t.Project, t.Due, t.PlannedFor, t.Notes.Trim(), t.Repeat))
            .Where(g => g.Count() > 1)
            .Select(g => g.OrderBy(t => t.CreatedAt ?? DateTime.MinValue).ToList())
            .Where(g => (g.Last().CreatedAt ?? DateTime.MinValue) - (g.First().CreatedAt ?? DateTime.MinValue) < TimeSpan.FromMinutes(2))
            .ToList();
        int extra = groups.Sum(g => g.Count - 1);
        if (extra == 0) return;
        string sample = groups[0][0].Text;
        Notify("dupes:" + string.Join(",", groups.SelectMany(g => g.Skip(1)).Select(t => t.Id)),
            extra == 1 ? "A task appears twice" : $"{extra} tasks appear twice",
            $"For example \"{sample}\". This can happen when two computers sync at once.",
            ("Remove the extra copies", () =>
            {
                PushUndo();
                foreach (var g in groups)
                {
                    // Keep the copy with the most details filled in.
                    var keep = g.OrderByDescending(t => t.Details.Length).ThenBy(t => t.CreatedAt).First();
                    foreach (var t in g.Where(t => !ReferenceEquals(t, keep))) TaskBin.All.Remove(t);
                }
                TaskBin.Save();
                _toast.ShowMessage("Duplicates removed", "Ctrl+Z undoes it.");
            }),
            ("Show them", () => { NavTasks.IsChecked = true; BinTodoView.IsChecked = true; ShowSearch(); SearchBox.Text = sample; }));
    }
}
